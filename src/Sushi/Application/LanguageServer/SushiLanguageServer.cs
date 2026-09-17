namespace Sushi.Application.LanguageServer;

using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Sushi.Application;
using Sushi.Build;
using Sushi.Build.SyntaxTree;
using Sushi.Transpilation;
using Sushi.Transpilation.Intrinsics;

/// <summary>
/// Small, dependency-free LSP 3.17 endpoint. Keeping the protocol boundary here makes
/// Sushi usable from any editor without pulling editor SDKs into the compiler.
/// </summary>
internal sealed class SushiLanguageServer
{
    private static readonly System.Text.RegularExpressions.Regex BareDocumentationLine = new("^\\s*///\\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly TextWriter _log;
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly SemanticWorkspace _semanticWorkspace = new();
    private TargetProfile _targetProfile = TargetProfile.TryParse(Environment.GetEnvironmentVariable("SUSHI_LSP_TARGET"), out var configuredProfile)
        ? configuredProfile
        : TargetProfile.Host();
    private bool _shutdown;
    private bool _exit;

    public SushiLanguageServer(Stream input, Stream output, TextWriter log)
    {
        _input = input;
        _output = output;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(cancellationToken);
            if (message is null) return;
            try
            {
                await DispatchAsync(message.Value, cancellationToken);
                if (_exit) return;
            }
            catch (Exception ex)
            {
                await _log.WriteLineAsync($"sushi-lsp: {ex}");
                if (message.Value.TryGetProperty("id", out var id))
                    await ReplyAsync(id, null, new { code = -32603, message = "Internal Sushi language server error." }, cancellationToken);
            }
        }
    }

    private async Task DispatchAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("method", out var methodElement)) return;
        var method = methodElement.GetString() ?? "";
        var hasId = message.TryGetProperty("id", out var id);
        var parameters = message.TryGetProperty("params", out var value) ? value : default;

        switch (method)
        {
            case "initialize":
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("initializationOptions", out var initialization) &&
                    initialization.ValueKind == JsonValueKind.Object)
                {
                    if (initialization.TryGetProperty("targetProfile", out var targetProfile) &&
                        TargetProfile.TryParse(targetProfile.GetString(), out var selectedProfile))
                        _targetProfile = selectedProfile;
                }
                await ReplyAsync(id, new
                {
                    capabilities = new
                    {
                        positionEncoding = "utf-16",
                        textDocumentSync = 1,
                        diagnosticProvider = new { interFileDependencies = false, workspaceDiagnostics = false },
                        completionProvider = new { triggerCharacters = new[] { ".", "@", "/" } },
                        hoverProvider = true,
                        definitionProvider = true,
                        typeDefinitionProvider = true,
                        referencesProvider = true,
                        documentHighlightProvider = true,
                        renameProvider = new { prepareProvider = true },
                        documentSymbolProvider = true,
                        workspaceSymbolProvider = true,
                        signatureHelpProvider = new { triggerCharacters = new[] { "(", "," } },
                        foldingRangeProvider = true,
                        selectionRangeProvider = true,
                        inlayHintProvider = true,
                        documentFormattingProvider = true,
                        documentRangeFormattingProvider = true,
                        codeActionProvider = new { codeActionKinds = new[] { "quickfix", "refactor.extract", "refactor.inline" } },
                        codeLensProvider = new { resolveProvider = false },
                        executeCommandProvider = new { commands = new[] { "sushi.run", "sushi.check", "sushi.openGenerated" } },
                        documentLinkProvider = new { resolveProvider = false },
                        callHierarchyProvider = true,
                        semanticTokensProvider = new
                        {
                            legend = new { tokenTypes = SemanticTokenTypes, tokenModifiers = SemanticTokenModifiers },
                            full = true
                        }
                    },
                    serverInfo = new { name = "sushi", version = typeof(SushiLanguageServer).Assembly.GetName().Version?.ToString() }
                }, null, cancellationToken);
                break;
            case "initialized":
                break;
            case "shutdown":
                _shutdown = true;
                if (hasId) await ReplyAsync(id, null, null, cancellationToken);
                break;
            case "exit":
                if (!_shutdown) await _log.WriteLineAsync("sushi-lsp: client exited without shutdown request.");
                _exit = true;
                return;
            case "textDocument/didOpen":
                Upsert(parameters.GetProperty("textDocument"));
                await PublishDiagnosticsAsync(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!, cancellationToken);
                break;
            case "textDocument/didChange":
                ApplyChange(parameters);
                await PublishDiagnosticsAsync(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!, cancellationToken);
                break;
            case "textDocument/diagnostic":
                {
                    var diagnosticUri = parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;
                    await ReplyAsync(id, new { kind = "full", items = BuildDiagnostics(diagnosticUri).Select(ToLspDiagnostic).ToArray() }, null, cancellationToken);
                }
                break;
            case "textDocument/didClose":
                {
                    var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;
                    _documents.Remove(uri);
                    await NotifyAsync("textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<object>() }, cancellationToken);
                    break;
                }
            case "textDocument/completion":
                await ReplyAsync(id, Completion(parameters), null, cancellationToken);
                break;
            case "textDocument/hover":
                await ReplyAsync(id, Hover(parameters), null, cancellationToken);
                break;
            case "textDocument/definition":
                await ReplyAsync(id, Definitions(parameters), null, cancellationToken);
                break;
            case "textDocument/documentLink":
                await ReplyAsync(id, DocumentLinks(parameters), null, cancellationToken);
                break;
            case "textDocument/references":
                await ReplyAsync(id, References(parameters), null, cancellationToken);
                break;
            case "textDocument/typeDefinition":
                await ReplyAsync(id, Definitions(parameters), null, cancellationToken);
                break;
            case "textDocument/documentHighlight":
                await ReplyAsync(id, DocumentHighlights(parameters), null, cancellationToken);
                break;
            case "textDocument/documentSymbol":
                await ReplyAsync(id, DocumentSymbols(parameters), null, cancellationToken);
                break;
            case "workspace/symbol":
                await ReplyAsync(id, WorkspaceSymbols(parameters), null, cancellationToken);
                break;
            case "textDocument/signatureHelp":
                await ReplyAsync(id, SignatureHelp(parameters), null, cancellationToken);
                break;
            case "textDocument/foldingRange":
                await ReplyAsync(id, FoldingRanges(parameters), null, cancellationToken);
                break;
            case "textDocument/selectionRange":
                await ReplyAsync(id, SelectionRanges(parameters), null, cancellationToken);
                break;
            case "textDocument/inlayHint":
                await ReplyAsync(id, InlayHints(parameters), null, cancellationToken);
                break;
            case "textDocument/formatting":
            case "textDocument/rangeFormatting":
                await ReplyAsync(id, FormattingEdits(parameters), null, cancellationToken);
                break;
            case "textDocument/codeAction":
                await ReplyAsync(id, CodeActions(parameters), null, cancellationToken);
                break;
            case "textDocument/codeLens":
                await ReplyAsync(id, CodeLenses(parameters), null, cancellationToken);
                break;
            case "textDocument/prepareCallHierarchy":
                await ReplyAsync(id, PrepareCallHierarchy(parameters), null, cancellationToken);
                break;
            case "callHierarchy/incomingCalls":
                await ReplyAsync(id, IncomingCalls(parameters), null, cancellationToken);
                break;
            case "callHierarchy/outgoingCalls":
                await ReplyAsync(id, OutgoingCalls(parameters), null, cancellationToken);
                break;
            // Compiles the current editor buffer without creating an artifact in the workspace.
            case "sushi/transpileDocument":
                await ReplyAsync(id, TranspileDocument(parameters), null, cancellationToken);
                break;
            case "workspace/executeCommand":
                await ReplyAsync(id, await ExecuteCommandAsync(parameters, cancellationToken), null, cancellationToken);
                break;
            case "textDocument/semanticTokens/full":
                await ReplyAsync(id, SemanticTokens(parameters), null, cancellationToken);
                break;
            case "textDocument/prepareRename":
                await ReplyAsync(id, PrepareRename(parameters), null, cancellationToken);
                break;
            case "textDocument/rename":
                await ReplyAsync(id, Rename(parameters), null, cancellationToken);
                break;
            default:
                if (hasId) await ReplyAsync(id, null, new { code = -32601, message = $"Unsupported method: {method}" }, cancellationToken);
                break;
        }
    }

    private void Upsert(JsonElement item)
    {
        var uri = item.GetProperty("uri").GetString()!;
        _documents[uri] = new OpenDocument(uri, item.GetProperty("text").GetString() ?? "", item.TryGetProperty("version", out var version) ? version.GetInt32() : 0);
        _semanticWorkspace.Invalidate(uri);
    }

    private void ApplyChange(JsonElement parameters)
    {
        var document = parameters.GetProperty("textDocument");
        var uri = document.GetProperty("uri").GetString()!;
        if (!_documents.TryGetValue(uri, out var open)) return;
        // The Sushi clients use full synchronization. Gracefully accept incremental clients too.
        var text = open.Text;
        foreach (var change in parameters.GetProperty("contentChanges").EnumerateArray())
        {
            var replacement = change.GetProperty("text").GetString() ?? "";
            if (!change.TryGetProperty("range", out var range)) { text = replacement; continue; }
            var start = Offset(text, range.GetProperty("start"));
            var end = Offset(text, range.GetProperty("end"));
            text = text[..start] + replacement + text[end..];
        }
        _documents[uri] = open with { Text = text, Version = document.TryGetProperty("version", out var version) ? version.GetInt32() : open.Version };
        _semanticWorkspace.Invalidate(uri);
    }

    private async Task PublishDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        if (!_documents.TryGetValue(uri, out var document)) return;
        var sourceDiagnostics = BuildDiagnostics(uri);
        await _log.WriteLineAsync($"sushi-lsp: diagnostics uri={uri} count={sourceDiagnostics.Count}");
        var diagnostics = sourceDiagnostics.Select(ToLspDiagnostic);
        await NotifyAsync("textDocument/publishDiagnostics", new { uri, version = document.Version, diagnostics }, cancellationToken);
    }

    private List<Diagnostic> BuildDiagnostics(string uri)
    {
        if (!_documents.TryGetValue(uri, out var document)) return [];
        var path = PathForUri(uri);
        var result = new Transpiler().Transpile(new TranspileRequest { SourcePath = path, SourceText = document.Text, TargetLanguage = _targetProfile.Shell, TargetProfile = _targetProfile });
        var allDiagnostics = result.Diagnostics.ToList();
        var knownNames = AnalyzeAll().Where(symbol => symbol.Declaration).Select(symbol => symbol.Name).Concat(StandardLibraryNames).Append("string").ToHashSet(StringComparer.Ordinal);
        foreach (var comment in DocumentationParser.Parse(document.Text))
        {
            foreach (var link in DocumentationParser.Links(comment))
            {
                var finalName = link.Target.Split('.').Last();
                if (!knownNames.Contains(finalName))
                    allDiagnostics.Add(Diagnostic.Warning("SUSHI1109", $"Documentation link '{link.Target}' cannot be resolved.", new SourceSpan(path, comment.Line, comment.Column, comment.Start, comment.End)));
            }
        }
        return allDiagnostics;
    }

    private object ToLspDiagnostic(Diagnostic d)
    {
        var uri = new Uri(Path.GetFullPath(d.Span.SourcePath)).AbsoluteUri;
        var text = _documents.TryGetValue(uri, out var document) ? document.Text : string.Empty;
        return new
        {
            range = Range(text, d.Span.StartOffset, d.Span.EndOffset > d.Span.StartOffset ? d.Span.EndOffset : d.Span.StartOffset + 1, d.Span.Line, d.Span.Column),
            severity = d.Severity == DiagnosticSeverity.Error ? 1 : 2,
            code = d.Code,
            source = "sushi",
            message = d.Message
        };
    }

    private object Completion(JsonElement parameters)
    {
        var document = Document(parameters);
        var offset = Offset(document.Text, parameters.GetProperty("position"));
        if (TryDocumentationTemplateCompletion(document, offset, out var documentationTemplate))
            return new { isIncomplete = false, items = new[] { documentationTemplate } };
        if (IsDocumentationTagContext(document.Text, offset))
            return new { isIncomplete = false, items = new[] { "param", "returns", "throws", "deprecated", "example" }.Select(tag => new { label = "@" + tag, kind = 14, detail = "documentation tag" }).ToArray() };
        var items = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
        var localModel = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var memberContext = IsMemberCompletionContext(localModel.Tokens, offset);
        var completionPrefix = document.Text[..Math.Clamp(offset, 0, document.Text.Length)];
        if (System.Text.RegularExpressions.Regex.IsMatch(completionPrefix, @"\.{2,}$"))
            return new { isIncomplete = false, items = Array.Empty<object>() };
        if (!memberContext && IsSwitchExpressionContext(localModel.Tokens, offset))
        {
            return new
            {
                isIncomplete = false,
                items = new object[]
                {
                    new { label = "default ->", kind = 15, detail = "switch expression default arm", insertText = "default -> $0", insertTextFormat = 2 },
                    new { label = "case ->", kind = 15, detail = "switch expression arm", insertText = "$0 -> $1", insertTextFormat = 2 }
                }
            };
        }
        if (!memberContext && !HasIdentifierPrefix(document.Text, offset))
            return new { isIncomplete = false, items = Array.Empty<object>() };
        var prefix = memberContext ? "" : IdentifierPrefix(document.Text, offset);
        if (memberContext)
        {
            foreach (var member in MemberCompletionItems(localModel, offset))
                items[member.Label] = member;
        }
        foreach (var symbol in localModel.Symbols.Where(symbol => !memberContext || symbol.Kind is SushiSymbolKind.Field or SushiSymbolKind.Method or SushiSymbolKind.EnumMember))
            if (!memberContext || ReceiverMayExposeSymbol(localModel, offset, symbol))
                items[symbol.Name] = CompletionForSymbol(localModel, symbol, memberContext);
        foreach (var occurrence in AnalyzeAll().Where(symbol => !memberContext && symbol.Uri != document.Uri && symbol.Exported))
        {
            var model = SushiSemanticModel.Create(DocumentFor(occurrence.Uri).Text);
            var symbol = model.Symbols.FirstOrDefault(candidate => candidate.Token.Start == occurrence.Token.Start);
            items.TryAdd(occurrence.Name, symbol is null
                ? new CompletionItem(occurrence.Name, CompletionKind(occurrence.Kind), occurrence.Kind, null, null)
                : CompletionForSymbol(model, symbol));
        }
        foreach (var keyword in Keywords.Where(_ => !memberContext))
            items.TryAdd(keyword, new CompletionItem(keyword, 14, "keyword", null, null));
        foreach (var builtIn in StandardLibrary.Functions.Where(_ => !memberContext))
            items.TryAdd(builtIn.Name, new CompletionItem(
                builtIn.Name,
                3,
                $"function → {builtIn.ReturnType}",
                builtIn.Documentation,
                null,
                CallableInsertText(builtIn.Name, builtIn.Parameters.Count),
                builtIn.Parameters.Count == 0 ? null : 2));
        if (!memberContext)
            foreach (var snippet in Snippets)
                items[snippet.Label] = new CompletionItem(snippet.Label, 15, snippet.Detail, snippet.Documentation, null, snippet.InsertText, 2);

        return new
        {
            isIncomplete = false,
            items = items.Values
                .Where(item => memberContext || item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Label, StringComparer.Ordinal)
                .Select(item => new { label = item.Label, kind = item.Kind, detail = item.Detail, documentation = item.Documentation is null ? null : new { kind = "markdown", value = item.Documentation }, tags = item.Deprecated is null ? null : new[] { 1 }, insertText = item.InsertText, insertTextFormat = item.InsertTextFormat }).ToArray()
        };
    }

    private static bool TryDocumentationTemplateCompletion(OpenDocument document, int offset, out object item)
    {
        item = null!;
        var lineStart = LineStart(document.Text, offset);
        var line = LineText(document.Text, lineStart);
        if (!BareDocumentationLine.IsMatch(line) || offset < lineStart + line.Length) return false;
        var declarationStart = NextLineStart(document.Text, lineStart);
        if (declarationStart >= document.Text.Length || String.IsNullOrWhiteSpace(LineText(document.Text, declarationStart))) return false;
        var indentation = line[..line.IndexOf('/')];
        if (!TryDocumentationTemplate(LineText(document.Text, declarationStart), indentation, out var template)) return false;
        item = new
        {
            label = "Generate documentation template",
            kind = 15,
            detail = "documentation template",
            documentation = new { kind = "markdown", value = "Generate a summary and documentation tags for the declaration below." },
            // The visible label is descriptive, but the text being completed is
            // `///`. Without this, VS Code filters the item out immediately
            // after the third slash is typed.
            filterText = "///",
            sortText = "000",
            preselect = true,
            textEdit = new { range = Range(document.Text, lineStart, lineStart + line.Length, 1, 1), newText = template },
            insertTextFormat = 1
        };
        return true;
    }

    private static bool IsMemberCompletionContext(IReadOnlyList<ClassifiedToken> tokens, int offset) =>
        tokens.LastOrDefault(token => token.End <= offset)?.Kind == ClassifiedTokenKind.Dot;

    private static bool IsSwitchExpressionContext(IReadOnlyList<ClassifiedToken> tokens, int offset)
    {
        var prior = tokens.Where(token => token.End <= offset).ToArray();
        if (prior.Length > 0 && (prior[^1].Kind == ClassifiedTokenKind.Dot ||
                                prior[^1].IsOperator("..") || prior[^1].IsOperator("...")))
            return false;
        var depth = 0;
        for (var index = prior.Length - 1; index >= 0; index--)
        {
            if (prior[index].Kind == ClassifiedTokenKind.RightBrace) depth++;
            else if (prior[index].Kind == ClassifiedTokenKind.LeftBrace)
            {
                if (depth > 0) { depth--; continue; }
                var switchIndex = prior.Take(index).ToList().FindLastIndex(token => token.IsKeyword("switch"));
                if (switchIndex < 0) return false;
                var previous = switchIndex > 0 ? prior[switchIndex - 1] : null;
                return previous is not null && (previous.IsOperator("=") || previous.IsKeyword("return") ||
                    previous.Kind is ClassifiedTokenKind.LeftParen or ClassifiedTokenKind.Comma);
            }
        }
        return false;
    }

    private static bool HasIdentifierPrefix(string text, int offset)
    {
        var prefix = IdentifierPrefix(text, offset);
        return prefix.Length > 0 && (char.IsLetter(prefix[0]) || prefix[0] == '_');
    }

    private static string IdentifierPrefix(string text, int offset)
    {
        var end = Math.Clamp(offset, 0, text.Length);
        var cursor = end - 1;
        while (cursor >= 0 && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor--;
        return text[(cursor + 1)..end];
    }

    private static bool ReceiverMayExposeSymbol(SushiSemanticModel model, int offset, SushiSymbol symbol)
    {
        var dot = model.Tokens.LastOrDefault(token => token.End <= offset && token.Kind == ClassifiedTokenKind.Dot);
        if (dot is null) return false;
        var preceding = model.Tokens.LastOrDefault(token => token.End <= dot.Start);
        if (preceding?.Kind == ClassifiedTokenKind.Dot)
            return false;
        if (preceding?.Kind is ClassifiedTokenKind.StringLiteral or ClassifiedTokenKind.InterpolatedString or
            ClassifiedTokenKind.IntegerLiteral or ClassifiedTokenKind.FloatLiteral)
            return false;
        if (preceding?.Kind == ClassifiedTokenKind.RightParen)
        {
            var open = model.Tokens.ToList().FindLastIndex(token => token.Kind == ClassifiedTokenKind.LeftParen && token.Start < preceding.Start);
            var callee = open > 0 ? model.Tokens[open - 1].Text : null;
            if (callee is not null && StandardLibrary.TryGetFunction(callee, out var function) &&
                string.Equals(function.ReturnType, "void", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        var receiver = model.Tokens.LastOrDefault(token => token.End <= dot.Start && token.Kind == ClassifiedTokenKind.Identifier);
        if (receiver is null) return false;
        var receiverSymbol = model.SymbolFor(receiver);
        var receiverType = model.TypeOf(receiver);
        if (receiverSymbol?.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method && receiverType == "void")
            return false;
        if (receiverSymbol?.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method && receiverType is null)
            return false;
        // Primitive receivers expose their intrinsic/sugar members, not unrelated
        // user-defined methods that happen to share the document.
        if (receiverType is "string" or "str" or "int" or "float" or "bool" or "array") return false;
        return receiverType is null || symbol.Kind is SushiSymbolKind.Field or SushiSymbolKind.Method;
    }

    private static IEnumerable<CompletionItem> MemberCompletionItems(SushiSemanticModel model, int offset)
    {
        var dot = model.Tokens.LastOrDefault(token => token.End <= offset && token.Kind == ClassifiedTokenKind.Dot);
        var preceding = dot is null ? null : model.Tokens.LastOrDefault(token => token.End <= dot.Start);
        if (preceding?.Kind == ClassifiedTokenKind.Dot)
            yield break;
        if (preceding?.Kind is ClassifiedTokenKind.IntegerLiteral or ClassifiedTokenKind.FloatLiteral)
            yield break;
        if (preceding?.Kind == ClassifiedTokenKind.RightParen)
        {
            var open = model.Tokens.ToList().FindLastIndex(token => token.Kind == ClassifiedTokenKind.LeftParen && token.Start < preceding.Start);
            var callee = open > 0 ? model.Tokens[open - 1].Text : null;
            if (callee is not null && StandardLibrary.TryGetFunction(callee, out var function) &&
                string.Equals(function.ReturnType, "void", StringComparison.OrdinalIgnoreCase))
                yield break;
        }
        var receiver = dot is null ? null : model.Tokens.LastOrDefault(token => token.End <= dot.Start &&
            (token.Kind == ClassifiedTokenKind.Identifier || token.Kind is ClassifiedTokenKind.StringLiteral or ClassifiedTokenKind.InterpolatedString));
        var receiverSymbol = receiver is null ? null : model.SymbolFor(receiver);
        var type = receiver is null ? null : model.TypeOf(receiver);
        // A dot immediately after `new Type(...)` belongs to the constructed
        // object, not to the last identifier inside its arguments.
        var prior = dot is null ? null : model.Tokens.LastOrDefault(token => token.End <= dot.Start);
        if (prior?.Kind == ClassifiedTokenKind.RightParen)
        {
            var open = model.Tokens.ToList().FindLastIndex(token => token.Kind == ClassifiedTokenKind.LeftParen && token.Start < prior.Start);
            if (open > 1 && model.Tokens[open - 1].Kind == ClassifiedTokenKind.Identifier &&
                model.Tokens[open - 2].IsKeyword("new"))
                type = model.Tokens[open - 1].Text;
        }
        if (type == "void" && receiverSymbol is not null && receiver is not null)
            type = model.TypeOf(receiver!);
        var modulePath = receiver?.Kind == ClassifiedTokenKind.Identifier ? ImportedModulePath(model, receiver.Text) : null;
        if (modulePath is not null)
        {
            foreach (var function in StandardLibrary.Functions.Where(function => function.Name.StartsWith(modulePath + ".", StringComparison.Ordinal)))
            {
                var shortName = function.Name[(modulePath.Length + 1)..];
                var parameters = string.Join(", ", function.Parameters.Select(parameter => parameter.DisplayName));
                yield return new CompletionItem($"{shortName}({parameters})", 3, function.ReturnType, function.Documentation, null,
                    CallableInsertText(shortName, function.Parameters.Count), function.Parameters.Count > 0 ? 2 : null);
            }
            yield break;
        }
        var isString = type is not null && (type.Equals("string", StringComparison.OrdinalIgnoreCase) || type.Equals("str", StringComparison.OrdinalIgnoreCase));
        var isArray = type is not null && (type.EndsWith("[]", StringComparison.Ordinal) || type.Equals("array", StringComparison.OrdinalIgnoreCase));
        if (isString)
        {
            foreach (var name in new[] { "trim", "lower", "upper", "length", "split", "contains", "startsWith", "endsWith", "replace", "isMatch", "match" })
            {
                var count = name switch { "length" or "trim" or "lower" or "upper" => 0, _ => 1 };
                var detail = StandardLibrary.TryGetFunction($"std.string.{name}", out var function)
                    ? function.ReturnType
                    : "string";
                // The receiver (`value`) is supplied by the expression before
                // the dot and should not appear as an explicit method argument.
                var parameters = function is null ? "" : string.Join(", ", function.Parameters.Skip(1).Select(parameter => parameter.DisplayName));
                yield return new CompletionItem($"{name}({parameters})", 2, detail, function?.Documentation, null, CallableInsertText(name, count), count > 0 ? 2 : null);
            }
        }
        if (isArray)
        {
            foreach (var name in new[] { "length", "push", "map", "filter", "reduce" })
            {
                var count = name == "length" ? 0 : 1;
                var detail = name switch
                {
                    "length" => "int",
                    "push" => "array",
                    "map" => "array",
                    "filter" => "array",
                    "reduce" => "any",
                    _ => "array"
                };
                var parameters = name switch
                {
                    "length" => "",
                    "push" => "any value",
                    "map" => "function callback",
                    "filter" => "function predicate",
                    "reduce" => "function callback, any initial = null",
                    _ => ""
                };
                yield return new CompletionItem($"{name}({parameters})", 2, detail, null, null, CallableInsertText(name, count), count > 0 ? 2 : null);
            }
        }
    }

    private static string? InferredCallableReturnType(SushiSemanticModel model, SushiSymbol callable)
    {
        var index = model.Tokens.ToList().FindIndex(token => token.Start == callable.Token.Start);
        if (index < 0) return null;
        var open = Enumerable.Range(index + 1, model.Tokens.Count - index - 1)
            .FirstOrDefault(i => model.Tokens[i].Kind == ClassifiedTokenKind.LeftBrace);
        if (open <= index) return null;
        var close = FindMatching(model.Tokens, open, ClassifiedTokenKind.LeftBrace, ClassifiedTokenKind.RightBrace);
        if (close < 0) return null;
        for (var i = open + 1; i + 1 < close; i++)
        {
            if (!model.Tokens[i].IsKeyword("return")) continue;
            var value = model.Tokens[i + 1];
            if (value.Kind is ClassifiedTokenKind.StringLiteral or ClassifiedTokenKind.InterpolatedString) return "string";
            if (value.Kind == ClassifiedTokenKind.IntegerLiteral) return "int";
            if (value.Kind == ClassifiedTokenKind.FloatLiteral) return "float";
            if (value.Kind != ClassifiedTokenKind.Identifier) continue;
            var referenced = model.Symbols.FirstOrDefault(symbol => symbol.Name == value.Text &&
                symbol.Kind is SushiSymbolKind.Field or SushiSymbolKind.Variable or SushiSymbolKind.Parameter);
            if (referenced?.DeclaredType is not null) return referenced.DeclaredType;
        }
        return null;
    }

    private static string? ImportedModulePath(SushiSemanticModel model, string alias)
    {
        for (var index = 0; index + 1 < model.Tokens.Count; index++)
        {
            if (!model.Tokens[index].IsKeyword("use")) continue;
            var path = new List<string>();
            var cursor = index + 1;
            while (cursor < model.Tokens.Count && model.Tokens[cursor].Kind == ClassifiedTokenKind.Identifier)
            {
                path.Add(model.Tokens[cursor].Text);
                cursor++;
                if (cursor >= model.Tokens.Count || model.Tokens[cursor].Kind != ClassifiedTokenKind.Dot) break;
                cursor++;
            }
            if (path.Count == 0) continue;
            var resolvedAlias = path[^1];
            if (cursor + 1 < model.Tokens.Count && model.Tokens[cursor].IsKeyword("as"))
                resolvedAlias = model.Tokens[cursor + 1].Text;
            if (resolvedAlias == alias) return string.Join('.', path);
        }
        return null;
    }

    private static CompletionItem CompletionForSymbol(SushiSemanticModel model, SushiSymbol symbol, bool includeSignatureLabel = false)
    {
        var callable = symbol.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method;
        var parameterCount = callable ? ParametersFor(model, symbol).Count() : 0;
        var detail = callable
            ? includeSignatureLabel
                ? model.ReturnTypeOf(symbol)
                : $"{model.ReturnTypeOf(symbol)} {symbol.Name}({string.Join(", ", ParametersFor(model, symbol).Select(parameter => ParameterSignature(model, parameter)))})"
            : symbol.Kind.ToString().ToLowerInvariant();
        var label = includeSignatureLabel && callable
            ? $"{symbol.Name}({string.Join(", ", ParametersFor(model, symbol).Select(parameter => ParameterSignature(model, parameter)))})"
            : symbol.Name;
        return new CompletionItem(
            label,
            CompletionKind(symbol.Kind.ToString().ToLowerInvariant()),
            detail,
            DocumentationMarkdown(symbol.Documentation),
            symbol.Documentation?.DeprecationMessage,
            callable ? CallableInsertText(symbol.Name, parameterCount) : null,
            callable && parameterCount > 0 ? 2 : null);
    }

    private static string CallableInsertText(string name, int parameterCount) =>
        parameterCount == 0 ? name + "()" : name + "($0)";

    private static int CompletionKind(string kind) => kind switch
    {
        "class" => 7,
        "enum" => 13,
        "function" => 3,
        "method" => 2,
        "field" => 5,
        "property" => 10,
        "parameter" => 6,
        "module" => 9,
        _ => 6
    };

    private object? Hover(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var offset = Offset(document.Text, parameters.GetProperty("position"));
        var token = model.TokenAt(offset);
        if (token is null) return null;
        var symbol = _semanticWorkspace.SymbolAt(document.Uri, document.Text, offset);
        var builtInName = _semanticWorkspace.QualifiedNameAt(document.Uri, document.Text, token);
        var hasBuiltIn = StandardLibrary.TryGetFunction(builtInName, out var builtIn) ||
                         StandardLibrary.TryGetFunction(token.Text, out builtIn);
        var text = SushiSemanticModel.IsTypeName(token.Text) && !IsCallToken(model, token)
            ? SushiCode(token.Text)
            : hasBuiltIn && (symbol is null || IsQualifiedMemberToken(model, token))
            ? SushiCode($"{builtIn.ReturnType} {builtIn.Name}({string.Join(", ", builtIn.Parameters.Select(parameter => parameter.DisplayName))})") + "\n\n" + builtIn.Documentation
            : symbol is null
                ? SushiCode(token.Text)
                : HoverText(model, symbol);
        return new { contents = new { kind = "markdown", value = text }, range = TokenRange(document.Text, token) };
    }

    private static bool IsQualifiedMemberToken(SushiSemanticModel model, ClassifiedToken token)
    {
        var index = model.Tokens.ToList().FindIndex(candidate => candidate.Start == token.Start);
        return index > 0 && model.Tokens[index - 1].Kind == ClassifiedTokenKind.Dot;
    }

    private static bool IsCallToken(SushiSemanticModel model, ClassifiedToken token)
    {
        var index = model.Tokens.ToList().FindIndex(candidate => candidate.Start == token.Start);
        return index >= 0 && index + 1 < model.Tokens.Count && model.Tokens[index + 1].Kind == ClassifiedTokenKind.LeftParen;
    }

    private static string HoverText(SushiSemanticModel model, SushiSymbol symbol)
    {
        var signature = symbol.Kind switch
        {
            SushiSymbolKind.Function or SushiSymbolKind.Method => $"{model.ReturnTypeOf(symbol)} {symbol.Name}({string.Join(", ", ParametersFor(model, symbol).Select(parameter => ParameterSignature(model, parameter)))})",
            SushiSymbolKind.Constructor => $"new({string.Join(", ", ParametersFor(model, symbol).Select(parameter => ParameterSignature(model, parameter)))})",
            SushiSymbolKind.Field or SushiSymbolKind.Variable => $"{symbol.DeclaredType ?? "var"} {symbol.Name}",
            SushiSymbolKind.Parameter => ParameterSignature(model, symbol),
            SushiSymbolKind.Class => $"class {symbol.Name}",
            SushiSymbolKind.Enum => $"enum {symbol.Name}",
            _ => $"{symbol.Kind.ToString().ToLowerInvariant()} {symbol.Name}"
        };
        signature = DeclarationWithInitializer(model, symbol) ?? signature;
        var documentation = symbol.Documentation;
        if (documentation is null && symbol.Kind == SushiSymbolKind.Parameter)
            documentation = model.Symbols
                .Where(candidate => candidate.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method or SushiSymbolKind.Constructor)
                .FirstOrDefault(candidate => ParametersFor(model, candidate).Any(parameter => parameter.Token.Start == symbol.Token.Start))
                ?.Documentation;
        var parameterDocumentation = symbol.Kind == SushiSymbolKind.Parameter
            ? documentation?.ParameterDocumentation(symbol.Name)
            : null;
        return SushiCode(signature) + DocumentationMarkdown(documentation) +
               (String.IsNullOrWhiteSpace(parameterDocumentation) ? "" : $"\n\n**Parameter:** {RenderDocumentation(parameterDocumentation)}");
    }

    private static string ParameterSignature(SushiSemanticModel model, SushiSymbol symbol)
    {
        var signature = $"{symbol.DeclaredType ?? "var"} {symbol.Name}";
        var index = symbol.DeclarationIndex + 1;
        if (index >= model.Tokens.Count || !model.Tokens[index].IsOperator("=")) return signature;

        var valueStart = model.Tokens[index].End;
        var valueEnd = model.Text.Length;
        var parentheses = 0;
        var brackets = 0;
        var braces = 0;
        for (var tokenIndex = index + 1; tokenIndex < model.Tokens.Count; tokenIndex++)
        {
            var token = model.Tokens[tokenIndex];
            switch (token.Kind)
            {
                case ClassifiedTokenKind.LeftParen: parentheses++; break;
                case ClassifiedTokenKind.RightParen:
                    if (parentheses == 0 && brackets == 0 && braces == 0) { valueEnd = token.Start; goto Done; }
                    parentheses--; break;
                case ClassifiedTokenKind.LeftBracket: brackets++; break;
                case ClassifiedTokenKind.RightBracket: brackets--; break;
                case ClassifiedTokenKind.LeftBrace: braces++; break;
                case ClassifiedTokenKind.RightBrace: braces--; break;
                case ClassifiedTokenKind.Comma when parentheses == 0 && brackets == 0 && braces == 0:
                    valueEnd = token.Start; goto Done;
            }
        }

    Done:
        var value = model.Text[valueStart..valueEnd].Trim();
        return value.Length == 0 ? signature : $"{signature} = {value}";
    }

    private static string? DeclarationWithInitializer(SushiSemanticModel model, SushiSymbol symbol)
    {
        if (symbol.Kind is not (SushiSymbolKind.Variable or SushiSymbolKind.Field)) return null;
        var declaration = symbol.DeclarationIndex;
        if (declaration < 0 || declaration + 1 >= model.Tokens.Count || !model.Tokens[declaration + 1].IsOperator("=")) return null;

        var start = symbol.Token.Start;
        var hasSourceTypePrefix = false;
        if (declaration > 0 && (model.Tokens[declaration - 1].IsKeyword("var") ||
                                model.Tokens[declaration - 1].Kind == ClassifiedTokenKind.Identifier && SushiSemanticModel.IsTypeName(model.Tokens[declaration - 1].Text)))
        {
            start = model.Tokens[declaration - 1].Start;
            hasSourceTypePrefix = true;
        }

        var end = DeclarationSegmentEnd(model.Tokens, declaration + 2, model.Text.Length);
        var declarationText = model.Text[start..end].Trim();
        return hasSourceTypePrefix || string.IsNullOrWhiteSpace(symbol.DeclaredType)
            ? declarationText
            : $"{symbol.DeclaredType} {declarationText}";
    }

    private static int DeclarationSegmentEnd(IReadOnlyList<ClassifiedToken> tokens, int start, int fallback)
    {
        var parentheses = 0;
        var brackets = 0;
        var braces = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token.Kind)
            {
                case ClassifiedTokenKind.LeftParen: parentheses++; break;
                case ClassifiedTokenKind.RightParen:
                    if (parentheses > 0) parentheses--;
                    break;
                case ClassifiedTokenKind.LeftBracket: brackets++; break;
                case ClassifiedTokenKind.RightBracket:
                    if (brackets > 0) brackets--;
                    break;
                case ClassifiedTokenKind.LeftBrace: braces++; break;
                case ClassifiedTokenKind.RightBrace:
                    if (braces == 0 && parentheses == 0 && brackets == 0) return token.Start;
                    if (braces > 0) braces--;
                    break;
                case ClassifiedTokenKind.Comma when parentheses == 0 && brackets == 0 && braces == 0:
                case ClassifiedTokenKind.Semicolon when parentheses == 0 && brackets == 0 && braces == 0:
                    return token.Start;
            }
        }
        return fallback;
    }

    private static string SushiCode(string text) => $"```sushi\n{text}\n```";

    private object Definitions(JsonElement parameters) => LocationsForToken(parameters, declarationsOnly: true);
    private object References(JsonElement parameters) => LocationsForToken(parameters, declarationsOnly: false);

    private object DocumentLinks(JsonElement parameters)
    {
        var document = Document(parameters);
        var links = new List<object>();
        foreach (var comment in new Tokenizer(document.Text).Tokenize().Where(token => token.Kind == TokenKind.Comment && token.Text.StartsWith("///", StringComparison.Ordinal)))
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(comment.Text, "\\{@link\\s+(?<target>[A-Za-z_][A-Za-z0-9_.]*)(?:\\s+[^}]+)?\\}"))
            {
                var targetName = match.Groups["target"].Value.Split('.').Last();
                var target = AnalyzeAll().FirstOrDefault(symbol => symbol.Declaration && symbol.Name == targetName);
                if (target is null) continue;
                var destination = $"{target.Uri}#L{target.Token.Line},{target.Token.Column}";
                links.Add(new { range = Range(document.Text, comment.Start + match.Index, comment.Start + match.Index + match.Length, comment.Line, comment.Column), target = destination, tooltip = $"Go to {match.Groups["target"].Value}" });
            }
        }
        return links;
    }

    private object LocationsForToken(JsonElement parameters, bool declarationsOnly)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var token = model.TokenAt(Offset(document.Text, parameters.GetProperty("position")));
        if (token is null) return Array.Empty<object>();
        var localSymbol = model.SymbolFor(token);
        if (localSymbol is not null)
        {
            var tokens = declarationsOnly ? [localSymbol.Token] : model.ReferencesOf(localSymbol).ToArray();
            return tokens.Select(candidate => new { uri = document.Uri, range = TokenRange(document.Text, candidate) }).ToArray();
        }
        return AnalyzeAll().Where(symbol => symbol.Name == token.Text && (!declarationsOnly || symbol.Declaration))
            .Select(symbol => new { uri = symbol.Uri, range = TokenRange(DocumentFor(symbol.Uri).Text, symbol.Token) }).ToArray();
    }

    private object DocumentSymbols(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        return model.Symbols
            .Select(symbol => new { name = symbol.Name, detail = symbol.Documentation?.Summary ?? "", kind = SymbolKind(symbol.Kind), range = TokenRange(document.Text, symbol.Token), selectionRange = TokenRange(document.Text, symbol.Token) }).ToArray();
    }

    private object DocumentHighlights(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var symbol = model.SymbolAt(Offset(document.Text, parameters.GetProperty("position")));
        if (symbol is null) return Array.Empty<object>();
        return model.ReferencesOf(symbol).Select(token => new
        {
            range = TokenRange(document.Text, token),
            kind = token.Start == symbol.Token.Start ? 3 : 2 // write / read
        }).ToArray();
    }

    private object WorkspaceSymbols(JsonElement parameters)
    {
        var query = parameters.TryGetProperty("query", out var queryValue) ? queryValue.GetString() ?? "" : "";
        return WorkspaceDocuments()
            .SelectMany(document => _semanticWorkspace.Analyze(document.Uri, document.Text).Symbols.Select(symbol => new { document, symbol }))
            .Where(item => item.symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.symbol.Name, StringComparer.Ordinal)
            .Select(item => new
            {
                name = item.symbol.Name,
                kind = SymbolKind(item.symbol.Kind),
                location = new { uri = item.document.Uri, range = TokenRange(item.document.Text, item.symbol.Token) },
                containerName = item.symbol.Kind.ToString().ToLowerInvariant()
            }).ToArray();
    }

    private object? SignatureHelp(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var offset = Offset(document.Text, parameters.GetProperty("position"));
        var open = OpenCallParenthesis(model.Tokens, offset);
        if (open is null) return null;
        var name = model.Tokens.Where(token => token.End <= open.Start && token.Kind == ClassifiedTokenKind.Identifier).LastOrDefault();
        if (name is null) return null;
        var symbol = model.SymbolFor(name);
        if (symbol is null || symbol.Kind is not (SushiSymbolKind.Function or SushiSymbolKind.Method))
            symbol = model.Symbols.FirstOrDefault(candidate => candidate.Name == name.Text && candidate.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method);

        var signature = symbol is null ? BuiltInSignature(CallName(model.Tokens, open, name)) : SignatureFor(model, symbol);
        if (signature is null) return null;
        var activeParameter = ActiveParameter(model.Tokens, open, offset);
        return new
        {
            signatures = new[] { new { label = signature.Label, documentation = new { kind = "markdown", value = signature.Documentation }, parameters = signature.Parameters.Select(parameter => parameter.Documentation is null
                ? (object)new { label = parameter.Label }
                : new { label = parameter.Label, documentation = new { kind = "markdown", value = parameter.Documentation } }).ToArray() } },
            activeSignature = 0,
            activeParameter = Math.Min(activeParameter, Math.Max(0, signature.Parameters.Length - 1))
        };
    }

    private static ClassifiedToken? OpenCallParenthesis(IReadOnlyList<ClassifiedToken> tokens, int offset)
    {
        var openParens = new Stack<ClassifiedToken>();
        foreach (var token in tokens)
        {
            if (token.Start >= offset) break;
            if (token.Kind == ClassifiedTokenKind.LeftParen) openParens.Push(token);
            else if (token.Kind == ClassifiedTokenKind.RightParen && openParens.Count > 0) openParens.Pop();
        }
        return openParens.Count > 0 ? openParens.Peek() : null;
    }

    private static int ActiveParameter(IReadOnlyList<ClassifiedToken> tokens, ClassifiedToken open, int offset)
    {
        var depth = 0;
        var commas = 0;
        foreach (var token in tokens)
        {
            if (token.Start <= open.Start || token.Start >= offset) continue;
            if (token.Kind == ClassifiedTokenKind.LeftParen) depth++;
            else if (token.Kind == ClassifiedTokenKind.RightParen && depth > 0) depth--;
            else if (token.Kind == ClassifiedTokenKind.Comma && depth == 0) commas++;
        }
        return commas;
    }

    private static string CallName(IReadOnlyList<ClassifiedToken> tokens, ClassifiedToken open, ClassifiedToken name)
    {
        var index = tokens.ToList().FindLastIndex(token => token.Start == name.Start);
        if (index < 0) return name.Text;
        var parts = new List<string> { name.Text };
        for (var cursor = index - 1; cursor >= 1 && tokens[cursor].Text == "." && tokens[cursor - 1].Kind == ClassifiedTokenKind.Identifier; cursor -= 2)
            parts.Insert(0, tokens[cursor - 1].Text);
        return string.Join(".", parts);
    }

    private static SignatureInformation? SignatureFor(SushiSemanticModel model, SushiSymbol symbol)
    {
        var parameters = ParametersFor(model, symbol)
            .Select(parameter => new SignatureParameter(ParameterSignature(model, parameter), symbol.Documentation?.ParameterDocumentation(parameter.Name)))
            .ToArray();
        return new SignatureInformation($"{symbol.Name}({string.Join(", ", parameters.Select(parameter => parameter.Label))})", parameters, $"{symbol.Kind}: `{symbol.Name}`" + DocumentationMarkdown(symbol.Documentation));
    }

    private static SignatureInformation? BuiltInSignature(string name)
    {
        if (!StandardLibrary.TryGetFunction(name, out var function)) return null;
        var parameters = function.Parameters.Select(parameter => new SignatureParameter(parameter.DisplayName, null)).ToArray();
        return new SignatureInformation(
            $"{function.Name}({string.Join(", ", parameters.Select(parameter => parameter.Label))})",
            parameters,
            $"{function.Documentation}\n\nReturns `{function.ReturnType}`.");
    }

    private static IEnumerable<SushiSymbol> ParametersFor(SushiSemanticModel model, SushiSymbol function)
    {
        var functionIndex = model.Tokens.ToList().FindIndex(token => token.Start == function.Token.Start);
        if (functionIndex < 0 || functionIndex + 1 >= model.Tokens.Count) return Array.Empty<SushiSymbol>();
        var open = model.Tokens[functionIndex + 1];
        var closeIndex = FindMatching(model.Tokens, functionIndex + 1, ClassifiedTokenKind.LeftParen, ClassifiedTokenKind.RightParen);
        if (open.Kind != ClassifiedTokenKind.LeftParen || closeIndex < 0) return Array.Empty<SushiSymbol>();
        var start = open.Start;
        var end = model.Tokens[closeIndex].End;
        return model.Symbols.Where(symbol => symbol.Kind == SushiSymbolKind.Parameter && symbol.Token.Start > start && symbol.Token.End < end);
    }

    private object FoldingRanges(JsonElement parameters)
    {
        var document = Document(parameters);
        var raw = new Tokenizer(document.Text).Tokenize().ToArray();
        var ranges = new List<object>();
        foreach (var comment in raw.Where(token => token.Kind == TokenKind.Comment && token.Text.Contains('\n')))
            ranges.Add(new { startLine = comment.Line - 1, endLine = comment.Line - 1 + comment.Text.Count(character => character == '\n'), kind = "comment" });
        var tokens = new Lexer(raw).Lex().Where(token => token.Kind != ClassifiedTokenKind.EndOfFile).ToArray();
        var opens = new Stack<ClassifiedToken>();
        foreach (var token in tokens)
        {
            if (token.Kind == ClassifiedTokenKind.LeftBrace) opens.Push(token);
            else if (token.Kind == ClassifiedTokenKind.RightBrace && opens.TryPop(out var open) && token.Line > open.Line)
                ranges.Add(new { startLine = open.Line - 1, startCharacter = open.Column - 1, endLine = token.Line - 1, endCharacter = token.Column - 1, kind = "region" });
        }
        return ranges;
    }

    private object SelectionRanges(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        return parameters.GetProperty("positions").EnumerateArray().Select(position =>
        {
            var token = model.TokenAt(Offset(document.Text, position));
            var range = token is null ? Range(document.Text, 0, document.Text.Length, 1, 1) : TokenRange(document.Text, token);
            return new { range, parent = new { range = Range(document.Text, 0, document.Text.Length, 1, 1) } };
        }).ToArray();
    }

    private object InlayHints(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var hints = new List<object>();
        for (var index = 0; index + 2 < model.Tokens.Count; index++)
        {
            if (!model.Tokens[index].IsKeyword("var") || model.Tokens[index + 1].Kind != ClassifiedTokenKind.Identifier || !model.Tokens[index + 2].IsOperator("=")) continue;
            var initializer = index + 3 < model.Tokens.Count ? model.Tokens[index + 3] : null;
            var type = initializer?.Kind switch
            {
                ClassifiedTokenKind.StringLiteral or ClassifiedTokenKind.InterpolatedString => "string",
                ClassifiedTokenKind.IntegerLiteral => "int",
                ClassifiedTokenKind.FloatLiteral => "float",
                _ => null
            };
            if (type is null) continue;
            var position = Position(document.Text, model.Tokens[index + 1].End);
            hints.Add(new { position, label = $": {type}", kind = 1, paddingLeft = true });
        }
        return hints;
    }

    private object FormattingEdits(JsonElement parameters)
    {
        var document = Document(parameters);
        var formatted = Format(document.Text);
        if (formatted == document.Text) return Array.Empty<object>();
        return new[] { new { range = Range(document.Text, 0, document.Text.Length, 1, 1), newText = formatted } };
    }

    private static bool TryDocumentationTemplate(string rawDeclaration, string indentation, out string template)
    {
        template = "";
        var declaration = rawDeclaration.TrimStart();
        var constructor = System.Text.RegularExpressions.Regex.Match(declaration, "^new\\s*\\((?<parameters>[^)]*)\\)");
        if (constructor.Success)
        {
            if (String.IsNullOrWhiteSpace(constructor.Groups["parameters"].Value)) return false;
            template = DocumentationTemplateLines(indentation, "constructor", constructor.Groups["parameters"].Value, returns: false);
            return true;
        }

        var callable = System.Text.RegularExpressions.Regex.Match(declaration, "^(?:export\\s+)?(?:(?<return>[A-Za-z_]\\w*)\\s+)?(?<name>[A-Za-z_]\\w*)\\s*\\((?<parameters>[^)]*)\\)");
        if (callable.Success)
        {
            var returns = !String.Equals(callable.Groups["return"].Value, "void", StringComparison.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(callable.Groups["parameters"].Value) && !returns) return false;
            template = DocumentationTemplateLines(indentation, callable.Groups["name"].Value, callable.Groups["parameters"].Value, returns);
            return true;
        }
        return false;
    }

    private static string DocumentationTemplateLines(string indentation, string name, string parameters, bool returns)
    {
        var lines = new List<string> { $"{indentation}///" };
        foreach (var parameter in parameters.Split(','))
        {
            var parameterName = System.Text.RegularExpressions.Regex.Match(parameter.Split('=')[0].Trim(), "(?<name>[A-Za-z_]\\w*)\\s*$");
            if (parameterName.Success) lines.Add($"{indentation}/// @param {parameterName.Groups["name"].Value}");
        }
        if (returns) lines.Add($"{indentation}/// @returns");
        return String.Join("\n", lines);
    }

    private static string LineText(string text, int start)
    {
        var end = text.IndexOf('\n', start);
        return text[start..(end < 0 ? text.Length : end)].TrimEnd('\r');
    }

    private static int NextLineStart(string text, int start)
    {
        var end = text.IndexOf('\n', start);
        return end < 0 ? text.Length : end + 1;
    }

    private object CodeActions(JsonElement parameters)
    {
        var document = Document(parameters);
        var range = parameters.GetProperty("range");
        var start = Offset(document.Text, range.GetProperty("start"));
        var end = Offset(document.Text, range.GetProperty("end"));
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var actions = new List<object>();

        if (TryInlineVariable(document, model, start, out var inlineEdit, out var inlineName))
            actions.Add(new { title = $"Inline '{inlineName}'", kind = "refactor.inline", edit = inlineEdit, isPreferred = true });
        if (TryExtractVariable(document, model, start, end, out var extractEdit, out var extractName))
            actions.Add(new { title = $"Extract variable '{extractName}'", kind = "refactor.extract", edit = extractEdit, isPreferred = true });
        if (TryGenerateDocumentation(document, model, start, out var documentationEdit, out var documentationName))
            actions.Add(new { title = $"Generate documentation for '{documentationName}'", kind = "quickfix", edit = documentationEdit });
        if (TryAddMissingParameterDocumentation(document, model, start, out var parametersEdit, out var parameterName))
            actions.Add(new { title = $"Document parameter '{parameterName}'", kind = "quickfix", edit = parametersEdit });
        if (TryAddImport(document, model, start, out var importEdit, out var importName))
            actions.Add(new { title = $"Import '{importName}'", kind = "quickfix", edit = importEdit, isPreferred = true });
        return actions;
    }

    private bool TryAddImport(OpenDocument document, SushiSemanticModel model, int offset, out object edit, out string name)
    {
        edit = null!;
        name = "";
        var token = model.TokenAt(offset);
        if (token is null || model.SymbolFor(token) is not null || !IsIdentifier(token.Text)) return false;

        // Offer a direct stdlib import for unresolved short names such as
        // `glob`, `readText`, or `run`.
        var standardMatches = StandardLibrary.Functions
            .Where(function => function.Name.Contains('.', StringComparison.Ordinal) &&
                               function.Name[(function.Name.LastIndexOf('.') + 1)..] == token.Text)
            .ToArray();
        if (standardMatches.Length == 1)
        {
            var canonical = standardMatches[0].Name;
            if (!document.Text.Contains($"use {canonical}", StringComparison.Ordinal) &&
                !document.Text.Contains($"use {canonical[..canonical.LastIndexOf('.')]}{{", StringComparison.Ordinal))
            {
                var standardInsertion = 0;
                while (standardInsertion < document.Text.Length && (document.Text[standardInsertion] == '\n' || document.Text[standardInsertion] == '\r')) standardInsertion++;
                edit = new { changes = new Dictionary<string, object> { [document.Uri] = new[] { new { range = Range(document.Text, standardInsertion, standardInsertion, 1, 1), newText = $"use {canonical}\n" } } } };
                name = token.Text;
                return true;
            }
        }

        var candidate = AnalyzeAll().FirstOrDefault(symbol => symbol.Exported && symbol.Name == token.Text && symbol.Uri != document.Uri);
        if (candidate is null) return false;
        var sourcePath = PathForUri(document.Uri);
        var importedPath = PathForUri(candidate.Uri);
        var basePath = Path.GetDirectoryName(sourcePath);
        if (basePath is null) return false;
        var relative = Path.GetRelativePath(basePath, importedPath).Replace(Path.DirectorySeparatorChar, '/');
        if (!relative.StartsWith(".", StringComparison.Ordinal)) relative = "./" + relative;
        var insertion = 0;
        while (insertion < document.Text.Length && (document.Text[insertion] == '\n' || document.Text[insertion] == '\r')) insertion++;
        edit = new { changes = new Dictionary<string, object> { [document.Uri] = new[] { new { range = Range(document.Text, insertion, insertion, 1, 1), newText = $"use \"{relative}\"\n" } } } };
        name = token.Text;
        return true;
    }

    private object CodeLenses(JsonElement parameters)
    {
        var document = Document(parameters);
        // These commands operate on the entire source buffer. Keep their lens at
        // the file header rather than attaching it to the first declaration.
        var range = Range(document.Text, 0, 0, 0, 0);
        object Lens(string title, string command) => new
        {
            range,
            command = new { title, command, arguments = new[] { document.Uri } }
        };
        return new[]
        {
            Lens("Run Sushi", "sushi.run"),
            Lens("Check Sushi", "sushi.check"),
            Lens("Preview generated output", "sushi.openGenerated")
        };
    }

    private object TranspileDocument(JsonElement parameters)
    {
        var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;
        var document = DocumentFor(uri);
        var text = parameters.TryGetProperty("text", out var suppliedText) && suppliedText.ValueKind == JsonValueKind.String
            ? suppliedText.GetString() ?? document.Text
            : document.Text;
        var target = _targetProfile;
        if (parameters.TryGetProperty("targetProfile", out var targetProperty) && TargetProfile.TryParse(targetProperty.GetString(), out var suppliedTarget))
            target = suppliedTarget;
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourcePath = PathForUri(uri),
            SourceText = text,
            TargetLanguage = target.Shell,
            TargetProfile = target
        });
        return new
        {
            success = result.Success,
            targetProfile = target.Id,
            languageId = target.Shell == TargetLanguage.Powershell51 ? "powershell" : "shellscript",
            fileExtension = target.FileExtension,
            code = result.EmittedCode,
            diagnostics = result.Diagnostics.Select(diagnostic => new
            {
                code = diagnostic.Code,
                message = diagnostic.Message,
                severity = diagnostic.Severity.ToString(),
                range = Range(text, diagnostic.Span.StartOffset, diagnostic.Span.EndOffset, diagnostic.Span.Line, diagnostic.Span.Column)
            }).ToArray()
        };
    }

    private async Task<object> ExecuteCommandAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var command = parameters.TryGetProperty("command", out var commandProperty) ? commandProperty.GetString() ?? "" : "";
        var uri = parameters.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array && arguments.GetArrayLength() > 0
            ? CommandUri(arguments[0]) : null;
        await _log.WriteLineAsync($"sushi-lsp: executeCommand '{command}' for '{uri ?? "<missing-uri>"}'.");
        if (uri is null || !_documents.TryGetValue(uri, out var document))
        {
            await NotifyAsync("window/showMessage", new { type = 1, message = "Sushi: the source document is no longer open." }, cancellationToken);
            return new { success = false, message = "Document is not open." };
        }

        var target = _targetProfile;
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourcePath = PathForUri(uri), SourceText = document.Text, TargetLanguage = target.Shell, TargetProfile = target
        });
        if (!result.Success || result.EmittedCode is null)
        {
            var message = string.Join("\n", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            await NotifyAsync("window/showMessage", new { type = 1, message = "Sushi: " + (message.Length == 0 ? "transpilation failed." : message) }, cancellationToken);
            return new { success = false, diagnostics = result.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray() };
        }

        if (command == "sushi.check")
        {
            await NotifyAsync("window/showMessage", new { type = 3, message = $"Sushi check passed ({target.Id})." }, cancellationToken);
            return new { success = true, targetProfile = target.Id };
        }
        if (command == "sushi.openGenerated")
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"sushi-generated-{Guid.NewGuid():N}{target.FileExtension}");
            await File.WriteAllTextAsync(outputPath, result.EmittedCode, cancellationToken);
            await NotifyAsync("window/showMessage", new { type = 3, message = $"Sushi generated {target.Id} output at {outputPath}." }, cancellationToken);
            return new { success = true, targetProfile = target.Id, path = outputPath, code = result.EmittedCode };
        }
        if (command == "sushi.run")
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"sushi-run-{Guid.NewGuid():N}{target.FileExtension}");
            await File.WriteAllTextAsync(scriptPath, result.EmittedCode, cancellationToken);
            try
            {
                var runner = target.Shell switch
                {
                    TargetLanguage.Zsh => "zsh",
                    TargetLanguage.Powershell51 => OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
                    _ => "bash"
                };
                var process = new Process { StartInfo = new ProcessStartInfo(runner) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
                process.StartInfo.ArgumentList.Add(target.Shell == TargetLanguage.Powershell51 ? "-File" : scriptPath);
                if (target.Shell == TargetLanguage.Powershell51) process.StartInfo.ArgumentList.Add(scriptPath);
                process.Start();
                var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var output = (stdout + stderr).Trim();
                await NotifyAsync("window/showMessage", new { type = process.ExitCode == 0 ? 3 : 1, message = $"Sushi run ({target.Id}) exited {process.ExitCode}." + (output.Length == 0 ? "" : $"\n{output}") }, cancellationToken);
                return new { success = process.ExitCode == 0, exitCode = process.ExitCode, output };
            }
            finally { try { File.Delete(scriptPath); } catch { } }
        }

        await NotifyAsync("window/showMessage", new { type = 2, message = $"Sushi: unsupported command '{command}'." }, cancellationToken);
        return new { success = false, message = "Unsupported command." };
    }

    private static string? CommandUri(JsonElement argument)
    {
        if (argument.ValueKind == JsonValueKind.String) return argument.GetString();
        if (argument.ValueKind == JsonValueKind.Object && argument.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
            return uri.GetString();
        return null;
    }

    private object PrepareCallHierarchy(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var symbol = model.SymbolAt(Offset(document.Text, parameters.GetProperty("position")));
        return symbol is null || symbol.Kind is not (SushiSymbolKind.Function or SushiSymbolKind.Method or SushiSymbolKind.Constructor)
            ? Array.Empty<object>()
            : new[] { CallHierarchyItem(document, model, symbol) };
    }

    private object IncomingCalls(JsonElement parameters)
    {
        if (!TryHierarchyItem(parameters, out var targetDocument, out var targetModel, out var target)) return Array.Empty<object>();
        var calls = new List<object>();
        foreach (var document in WorkspaceDocuments())
        {
            var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
            foreach (var token in model.Tokens.Where(token => token.Kind == ClassifiedTokenKind.Identifier && token.Text == target.Name))
            {
                var index = model.Tokens.ToList().FindIndex(candidate => candidate.Start == token.Start);
                if (index < 0 || index + 1 >= model.Tokens.Count || model.Tokens[index + 1].Kind != ClassifiedTokenKind.LeftParen || model.SymbolFor(token)?.Id == target.Id && document.Uri == targetDocument.Uri) continue;
                var caller = EnclosingCallable(model, index);
                if (caller is null) continue;
                calls.Add(new { from = CallHierarchyItem(document, model, caller), fromRanges = new[] { TokenRange(document.Text, token) } });
            }
        }
        return calls;
    }

    private object OutgoingCalls(JsonElement parameters)
    {
        if (!TryHierarchyItem(parameters, out var document, out var model, out var source)) return Array.Empty<object>();
        var sourceIndex = model.Tokens.ToList().FindIndex(token => token.Start == source.Token.Start);
        var openBrace = sourceIndex < 0 ? -1 : model.Tokens.Skip(sourceIndex).ToList().FindIndex(token => token.Kind == ClassifiedTokenKind.LeftBrace);
        if (openBrace < 0) return Array.Empty<object>();
        openBrace += sourceIndex;
        var closeBrace = FindMatching(model.Tokens, openBrace, ClassifiedTokenKind.LeftBrace, ClassifiedTokenKind.RightBrace);
        if (closeBrace < 0) return Array.Empty<object>();
        var calls = new List<object>();
        for (var index = openBrace + 1; index + 1 < closeBrace; index++)
        {
            var token = model.Tokens[index];
            if (token.Kind != ClassifiedTokenKind.Identifier || model.Tokens[index + 1].Kind != ClassifiedTokenKind.LeftParen) continue;
            var target = model.SymbolFor(token) ?? model.Symbols.FirstOrDefault(symbol => symbol.Name == token.Text && symbol.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method);
            if (target is null || target.Id == source.Id) continue;
            calls.Add(new { to = CallHierarchyItem(document, model, target), fromRanges = new[] { TokenRange(document.Text, token) } });
        }
        return calls;
    }

    private bool TryHierarchyItem(JsonElement parameters, out OpenDocument document, out SushiSemanticModel model, out SushiSymbol symbol)
    {
        document = null!; model = null!; symbol = null!;
        if (!parameters.TryGetProperty("item", out var item) || !item.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("uri", out var uriProperty) || !data.TryGetProperty("start", out var startProperty)) return false;
        document = DocumentFor(uriProperty.GetString()!);
        model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        symbol = model.Symbols.FirstOrDefault(candidate => candidate.Token.Start == startProperty.GetInt32())!;
        return symbol is not null;
    }

    private static SushiSymbol? EnclosingCallable(SushiSemanticModel model, int index) => model.Symbols
        .Where(symbol => symbol.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method or SushiSymbolKind.Constructor)
        .Select(symbol => (Symbol: symbol, Start: model.Tokens.ToList().FindIndex(token => token.Start == symbol.Token.Start)))
        .Where(item => item.Start >= 0)
        .Select(item => (item.Symbol, Open: item.Start + model.Tokens.Skip(item.Start).ToList().FindIndex(token => token.Kind == ClassifiedTokenKind.LeftBrace)))
        .Where(item => item.Open >= item.Symbol.DeclarationIndex)
        .Select(item => (item.Symbol, Close: FindMatching(model.Tokens, item.Open, ClassifiedTokenKind.LeftBrace, ClassifiedTokenKind.RightBrace), item.Open))
        .Where(item => item.Close >= index && item.Open < index)
        .OrderBy(item => item.Close - item.Open)
        .Select(item => item.Symbol)
        .FirstOrDefault();

    private object CallHierarchyItem(OpenDocument document, SushiSemanticModel model, SushiSymbol symbol) => new
    {
        name = symbol.Name,
        kind = SymbolKind(symbol.Kind),
        detail = symbol.DeclaredType ?? symbol.Kind.ToString().ToLowerInvariant(),
        uri = document.Uri,
        range = TokenRange(document.Text, symbol.Token),
        selectionRange = TokenRange(document.Text, symbol.Token),
        data = new { uri = document.Uri, start = symbol.Token.Start }
    };

    private static bool TryGenerateDocumentation(OpenDocument document, SushiSemanticModel model, int offset, out object edit, out string name)
    {
        edit = null!;
        name = "";
        var symbol = SymbolAtDocumentationOrDeclaration(model, offset);
        if (symbol is null || symbol.Documentation is not null || symbol.Kind is SushiSymbolKind.Parameter or SushiSymbolKind.Variable or SushiSymbolKind.Namespace or SushiSymbolKind.Module) return false;
        var callable = symbol.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method or SushiSymbolKind.Constructor;
        var parameters = callable ? ParametersFor(model, symbol).ToArray() : Array.Empty<SushiSymbol>();
        var returns = symbol.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method &&
                      !String.Equals(symbol.DeclaredType, "void", StringComparison.OrdinalIgnoreCase);
        if (!callable || (parameters.Length == 0 && !returns)) return false;

        var insertion = LineStart(document.Text, symbol.Token.Start);
        var indentation = IndentationAt(document.Text, insertion);
        var text = new StringBuilder(indentation).Append("///\n");
        foreach (var parameter in parameters) text.Append(indentation).Append("/// @param ").Append(parameter.Name).Append('\n');
        if (returns) text.Append(indentation).Append("/// @returns\n");
        edit = new { changes = new Dictionary<string, object> { [document.Uri] = new[] { new { range = Range(document.Text, insertion, insertion, 1, 1), newText = text.ToString() } } } };
        name = symbol.Name;
        return true;
    }

    private static bool TryAddMissingParameterDocumentation(OpenDocument document, SushiSemanticModel model, int offset, out object edit, out string parameterName)
    {
        edit = null!;
        parameterName = "";
        var symbol = SymbolAtDocumentationOrDeclaration(model, offset);
        if (symbol?.Documentation is null || symbol.Kind is not (SushiSymbolKind.Function or SushiSymbolKind.Method or SushiSymbolKind.Constructor)) return false;
        var missing = ParametersFor(model, symbol).FirstOrDefault(parameter => symbol.Documentation.ParameterDocumentation(parameter.Name) is null);
        if (missing is null) return false;
        var insertion = symbol.Documentation.End;
        var indentation = IndentationAt(document.Text, LineStart(document.Text, symbol.Documentation.Start));
        edit = new { changes = new Dictionary<string, object> { [document.Uri] = new[] { new { range = Range(document.Text, insertion, insertion, 1, 1), newText = $"\n{indentation}/// @param {missing.Name}" } } } };
        parameterName = missing.Name;
        return true;
    }

    private static SushiSymbol? SymbolAtDocumentationOrDeclaration(SushiSemanticModel model, int offset) =>
        model.SymbolAt(offset) ?? model.Symbols.FirstOrDefault(symbol =>
            symbol.Documentation is { } documentation && documentation.Start <= offset && offset <= documentation.End);

    private static bool TryInlineVariable(OpenDocument document, SushiSemanticModel model, int offset, out object edit, out string name)
    {
        edit = null!;
        name = "";
        var symbol = model.SymbolAt(offset);
        if (symbol?.Kind != SushiSymbolKind.Variable) return false;
        var references = model.ReferencesOf(symbol).ToArray();
        if (references.Length != 2) return false;
        var declarationIndex = model.Tokens.ToList().FindIndex(token => token.Start == symbol.Token.Start);
        if (declarationIndex < 1 || !model.Tokens[declarationIndex - 1].IsKeyword("var")) return false;
        var equalsIndex = declarationIndex + 1;
        if (equalsIndex >= model.Tokens.Count || !model.Tokens[equalsIndex].IsOperator("=")) return false;
        var statementEnd = model.Tokens.Skip(equalsIndex + 1).FirstOrDefault(token => token.Kind == ClassifiedTokenKind.Semicolon);
        if (statementEnd is null) return false;
        var initializer = document.Text[model.Tokens[equalsIndex].End..statementEnd.Start].Trim();
        if (!IsSafeInlineExpression(initializer)) return false;
        var use = references.Single(token => token.Start != symbol.Token.Start);
        var declarationStart = LineStart(document.Text, model.Tokens[declarationIndex - 1].Start);
        var declarationEnd = statementEnd.End;
        if (declarationEnd < document.Text.Length && document.Text[declarationEnd] == '\n') declarationEnd++;
        edit = new
        {
            changes = new Dictionary<string, object>
            {
                [document.Uri] = new object[]
                {
                    new { range = Range(document.Text, declarationStart, declarationEnd, 1, 1), newText = "" },
                    new { range = TokenRange(document.Text, use), newText = initializer }
                }
            }
        };
        name = symbol.Name;
        return true;
    }

    private static bool TryExtractVariable(OpenDocument document, SushiSemanticModel model, int start, int end, out object edit, out string name)
    {
        edit = null!;
        name = "";
        if (end <= start || document.Text[start..end].Contains('\n')) return false;
        if (model.SymbolAt(start) is { } selectedSymbol && selectedSymbol.Token.Start >= start && selectedSymbol.Token.End <= end) return false;
        var expression = document.Text[start..end].Trim();
        if (expression.Length == 0 || !IsSafeInlineExpression(expression)) return false;
        var prefix = "extractedValue";
        name = prefix;
        var suffix = 2;
        var names = model.Symbols.Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
        while (names.Contains(name)) name = prefix + suffix++;
        var insertion = LineStart(document.Text, start);
        edit = new
        {
            changes = new Dictionary<string, object>
            {
                [document.Uri] = new object[]
                {
                    new { range = Range(document.Text, insertion, insertion, 1, 1), newText = $"var {name} = {expression}\n" },
                    new { range = Range(document.Text, start, end, 1, 1), newText = name }
                }
            }
        };
        return true;
    }

    private static bool IsSafeInlineExpression(string expression)
    {
        if (expression.Length == 0 || expression.Contains('(') || expression.Contains('[') || expression.Contains('{')) return false;
        if (expression[0] is '"' or '\'' || char.IsDigit(expression[0])) return true;
        if (expression is "true" or "false" or "null") return true;
        return expression.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');
    }

    private static string Format(string text)
    {
        var raw = new Tokenizer(text).Tokenize().ToArray();
        var symbolsByLine = raw.Where(token => token.Kind == TokenKind.Symbol).GroupBy(token => token.Line)
            .ToDictionary(group => group.Key, group => group.Select(token => token.Text).ToArray());
        var lines = text.Split('\n');
        var indentation = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0) { lines[index] = ""; continue; }
            var symbols = symbolsByLine.TryGetValue(lineNumber, out var lineSymbols) ? lineSymbols : Array.Empty<string>();
            var leadingClosures = symbols.FirstOrDefault() == "}" ? 1 : 0;
            if (leadingClosures > 0) indentation = Math.Max(0, indentation - leadingClosures);
            lines[index] = new string(' ', indentation * 4) + trimmed;
            indentation = Math.Max(0, indentation + symbols.Count(symbol => symbol == "{") - (symbols.Count(symbol => symbol == "}") - leadingClosures));
        }
        return string.Join("\n", lines);
    }

    private static int LineStart(string text, int offset)
    {
        var start = Math.Clamp(offset, 0, text.Length);
        while (start > 0 && text[start - 1] != '\n') start--;
        return start;
    }

    private static string IndentationAt(string text, int lineStart)
    {
        var end = lineStart;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
        return text[lineStart..end];
    }

    private object SemanticTokens(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var classifiedByStart = model.Tokens.ToDictionary(token => token.Start);
        var unusedImports = FindUnusedImportTokens(model);
        var data = new List<int>();
        var previousLine = 0;
        var previousColumn = 0;
        foreach (var raw in model.RawTokens.Where(token => token.Text.Length > 0))
        {
            if (raw.Kind == TokenKind.Whitespace) continue;
            var type = raw.Kind == TokenKind.Comment ? SemanticComment :
                !classifiedByStart.TryGetValue(raw.Start, out var token) ? -1 : token.Kind switch
            {
                ClassifiedTokenKind.Keyword => SemanticKeyword,
                ClassifiedTokenKind.StringLiteral or ClassifiedTokenKind.InterpolatedString or ClassifiedTokenKind.CharLiteral => SemanticString,
                ClassifiedTokenKind.IntegerLiteral or ClassifiedTokenKind.FloatLiteral => SemanticNumber,
                ClassifiedTokenKind.Operator => SemanticOperator,
                ClassifiedTokenKind.Identifier => SemanticType(model.SymbolFor(token), token.Text),
                _ => -1
            };
            if (unusedImports.Contains(raw.Start)) type = SemanticUnusedImport;
            if (type < 0) continue;
            var modifiers = raw.Kind != TokenKind.Comment && classifiedByStart.TryGetValue(raw.Start, out var identifier) &&
                model.SymbolFor(identifier) is { } symbol && symbol.Token.Start == identifier.Start
                ? DeclarationModifier : 0;
            AddSemanticToken(data, document.Text, raw.Start, raw.Length, type, modifiers, ref previousLine, ref previousColumn);
        }
        return new { data };
    }

    private static HashSet<int> FindUnusedImportTokens(SushiSemanticModel model)
    {
        var unused = new HashSet<int>();
        var tokens = model.Tokens;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].IsKeyword("use")) continue;
            var end = i + 1;
            while (end < tokens.Count && tokens[end].Kind != ClassifiedTokenKind.Semicolon &&
                   tokens[end].Line == tokens[i].Line) end++;
            var candidates = new List<ClassifiedToken>();
            for (var j = i + 1; j < end; j++)
            {
                if (tokens[j].Kind == ClassifiedTokenKind.Identifier) candidates.Add(tokens[j]);
            }
            var asIndex = candidates.FindIndex(token => token.IsKeyword("as"));
            if (asIndex >= 0 && asIndex + 1 < candidates.Count)
                candidates = new List<ClassifiedToken> { candidates[asIndex + 1] };
            else if (tokens.Skip(i + 1).Take(end - i - 1).Any(token => token.Kind == ClassifiedTokenKind.LeftBrace))
                candidates = candidates.SkipWhile(token => token.Start < tokens.First(t => t.Kind == ClassifiedTokenKind.LeftBrace && t.Start > tokens[i].Start).Start).ToList();
            else if (candidates.Count > 0)
                candidates = new List<ClassifiedToken> { candidates[^1] };

            foreach (var candidate in candidates)
            {
                if (tokens.Any(token => token.Kind == ClassifiedTokenKind.Identifier && token.Text == candidate.Text &&
                                        token.Start != candidate.Start)) continue;
                unused.Add(candidate.Start);
            }
        }
        return unused;
    }

    private static int SemanticType(SushiSymbol? symbol, string tokenText) => symbol?.Kind switch
    {
        SushiSymbolKind.Namespace or SushiSymbolKind.Module => SemanticNamespace,
        SushiSymbolKind.Class => SemanticVariable,
        SushiSymbolKind.Enum => SemanticEnum,
        SushiSymbolKind.EnumMember => SemanticEnumMember,
        SushiSymbolKind.Function => SemanticFunction,
        SushiSymbolKind.Method => SemanticMethod,
        SushiSymbolKind.Field => SemanticProperty,
        SushiSymbolKind.Parameter => SemanticParameter,
        SushiSymbolKind.Variable => SemanticVariable,
        _ => SushiSemanticModel.IsTypeName(tokenText) ? SemanticKeyword : SemanticVariable
    };

    private static void AddSemanticToken(List<int> data, string text, int start, int length, int type, int modifiers, ref int previousLine, ref int previousColumn)
    {
        var end = Math.Min(text.Length, start + length);
        var cursor = start;
        while (cursor < end)
        {
            var newline = text.IndexOf('\n', cursor, end - cursor);
            var segmentEnd = newline < 0 ? end : newline;
            if (segmentEnd > cursor)
            {
                var (line, column) = LineColumn(text, cursor);
                data.Add(line - previousLine);
                data.Add(line == previousLine ? column - previousColumn : column);
                data.Add(segmentEnd - cursor);
                data.Add(type);
                data.Add(modifiers);
                previousLine = line;
                previousColumn = column;
            }
            cursor = newline < 0 ? end : newline + 1;
        }
    }

    private static (int Line, int Column) LineColumn(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
            if (text[index] == '\n') { line++; lineStart = index + 1; }
        return (line, offset - lineStart);
    }

    private object? PrepareRename(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var offset = Offset(document.Text, parameters.GetProperty("position"));
        var token = model.TokenAt(offset);
        return token is null || model.SymbolAt(offset) is null || IsKeyword(token.Text) || StandardLibraryNames.Contains(token.Text) ? null : TokenRange(document.Text, token);
    }

    private object? Rename(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = _semanticWorkspace.Analyze(document.Uri, document.Text);
        var token = model.TokenAt(Offset(document.Text, parameters.GetProperty("position")));
        var newName = parameters.GetProperty("newName").GetString() ?? "";
        var symbol = token is null ? null : model.SymbolFor(token);
        if (token is null || symbol is null || !IsIdentifier(newName) || IsKeyword(token.Text) || StandardLibraryNames.Contains(token.Text)) return null;
        var changes = new Dictionary<string, object>();
        changes[document.Uri] = model.ReferencesOf(symbol)
            .Select(reference => new { range = TokenRange(document.Text, reference), newText = newName }).ToArray();
        return new { changes };
    }

    private IEnumerable<SymbolOccurrence> AnalyzeAll() => WorkspaceDocuments().SelectMany(Analyze);

    private IEnumerable<OpenDocument> WorkspaceDocuments()
    {
        var pending = new Queue<OpenDocument>(_documents.Values);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var document = pending.Dequeue();
            if (!seen.Add(document.Uri)) continue;
            yield return document;
            var basePath = Path.GetDirectoryName(PathForUri(document.Uri));
            if (basePath is null) continue;
            foreach (var use in Classify(document.Text).Select((token, index) => (token, index)).Where(item => item.token.IsKeyword("use")))
            {
                var tokens = Classify(document.Text);
                if (use.index + 1 >= tokens.Count || tokens[use.index + 1].Kind != ClassifiedTokenKind.StringLiteral) continue;
                var relativePath = (string?)tokens[use.index + 1].Value ?? tokens[use.index + 1].Text.Trim('"');
                var importPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
                if (!File.Exists(importPath)) continue;
                var importUri = new Uri(importPath).AbsoluteUri;
                pending.Enqueue(_documents.TryGetValue(importUri, out var open) ? open : new OpenDocument(importUri, File.ReadAllText(importPath), 0));
            }
        }
    }

    private static IEnumerable<SymbolOccurrence> Analyze(OpenDocument document)
    {
        var tokens = Classify(document.Text).Where(token => token.Kind != ClassifiedTokenKind.EndOfFile).ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Kind != ClassifiedTokenKind.Identifier) continue;
            var previous = index > 0 ? tokens[index - 1] : null;
            var next = index + 1 < tokens.Length ? tokens[index + 1] : null;
            var kind = "variable";
            var declaration = previous?.IsKeyword("class") == true || previous?.IsKeyword("enum") == true || previous?.IsKeyword("var") == true || previous?.IsKeyword("as") == true;
            if (previous?.IsKeyword("class") == true) kind = "class";
            else if (previous?.IsKeyword("enum") == true) kind = "enum";
            else if (previous?.IsKeyword("as") == true) kind = "module";
            else if (next?.Kind == ClassifiedTokenKind.LeftParen)
            {
                kind = "function";
                declaration = IsFunctionDeclaration(tokens, index);
            }
            else if (previous?.Kind == ClassifiedTokenKind.Identifier &&
                     (next?.Kind == ClassifiedTokenKind.Operator || next?.Kind == ClassifiedTokenKind.Semicolon))
            {
                declaration = true;
            }
            yield return new SymbolOccurrence(document.Uri, token, token.Text, kind, declaration, declaration && IsTopLevel(tokens, index) && IsExportedDeclaration(tokens, index));
        }
    }

    private static bool IsFunctionDeclaration(ClassifiedToken[] tokens, int nameIndex)
    {
        var depth = 0;
        for (var index = nameIndex + 1; index < tokens.Length && index < nameIndex + 80; index++)
        {
            if (tokens[index].Kind == ClassifiedTokenKind.LeftParen) depth++;
            else if (tokens[index].Kind == ClassifiedTokenKind.RightParen && --depth == 0)
                return index + 1 < tokens.Length &&
                       (tokens[index + 1].Kind == ClassifiedTokenKind.LeftBrace ||
                        (tokens[index + 1].Kind == ClassifiedTokenKind.Operator && tokens[index + 1].Text == "->"));
        }
        return false;
    }

    private static int FindMatching(IReadOnlyList<ClassifiedToken> tokens, int openIndex, ClassifiedTokenKind left, ClassifiedTokenKind right)
    {
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == left) depth++;
            else if (tokens[index].Kind == right && --depth == 0) return index;
        }
        return -1;
    }

    private static bool IsTopLevel(ClassifiedToken[] tokens, int position) => tokens.Take(position).Count(token => token.Kind == ClassifiedTokenKind.LeftBrace) == tokens.Take(position).Count(token => token.Kind == ClassifiedTokenKind.RightBrace);
    private static bool IsExportedDeclaration(ClassifiedToken[] tokens, int position)
    {
        for (var index = position - 1; index >= 0 && tokens[index].Kind is not (ClassifiedTokenKind.Semicolon or ClassifiedTokenKind.LeftBrace or ClassifiedTokenKind.RightBrace); index--)
            if (tokens[index].IsKeyword("export")) return true;
        return false;
    }
    private static IReadOnlyList<ClassifiedToken> Classify(string text) => new Lexer(new Tokenizer(text).Tokenize()).Lex().ToArray();
    private OpenDocument Document(JsonElement parameters) => DocumentFor(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!);
    private OpenDocument DocumentFor(string uri)
    {
        if (_documents.TryGetValue(uri, out var document)) return document;
        var path = PathForUri(uri);
        if (File.Exists(path)) return new OpenDocument(uri, File.ReadAllText(path), 0);
        throw new InvalidOperationException($"Document is not open: {uri}");
    }

    private static ClassifiedToken? TokenAt(OpenDocument document, JsonElement position)
    {
        var offset = Offset(document.Text, position);
        return Classify(document.Text).FirstOrDefault(token => token.Start <= offset && offset <= token.End && token.Kind == ClassifiedTokenKind.Identifier);
    }

    private static int Offset(string text, JsonElement position)
    {
        var line = position.GetProperty("line").GetInt32();
        var character = position.GetProperty("character").GetInt32();
        var offset = 0;
        for (var current = 0; current < line && offset < text.Length; current++)
        {
            var newline = text.IndexOf('\n', offset);
            offset = newline < 0 ? text.Length : newline + 1;
        }
        return Math.Min(text.Length, offset + character);
    }

    private static object Range(string text, int startOffset, int endOffset, int fallbackLine, int fallbackColumn)
    {
        if (startOffset < 0 || startOffset > text.Length) startOffset = OffsetForLineColumn(text, fallbackLine, fallbackColumn);
        endOffset = Math.Clamp(endOffset, startOffset, text.Length);
        return new { start = Position(text, startOffset), end = Position(text, endOffset) };
    }
    private static object TokenRange(string text, ClassifiedToken token) => Range(text, token.Start, token.End, token.Line, token.Column);
    private static object Position(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 0; var lineStart = 0;
        for (var index = 0; index < offset; index++) if (text[index] == '\n') { line++; lineStart = index + 1; }
        return new { line, character = offset - lineStart };
    }
    private static int OffsetForLineColumn(string text, int line, int column)
    {
        var offset = 0;
        for (var current = 1; current < line && offset < text.Length; current++) { var newline = text.IndexOf('\n', offset); offset = newline < 0 ? text.Length : newline + 1; }
        return Math.Min(text.Length, offset + Math.Max(0, column - 1));
    }
    private static string PathForUri(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;
    private static bool IsIdentifier(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(character => char.IsLetterOrDigit(character) || character == '_');
    private static bool IsKeyword(string value) => Keywords.Contains(value);
    private static string DocumentationMarkdown(DocumentationComment? documentation)
    {
        if (documentation is null) return "";
        var text = new StringBuilder();
        if (!String.IsNullOrWhiteSpace(documentation.Body)) text.Append(RenderDocumentation(documentation.Body));
        var parameters = documentation.TagsNamed("param").Where(parameter => !String.IsNullOrWhiteSpace(parameter.Subject)).ToArray();
        if (parameters.Length > 0)
        {
            text.Append("\n\n**Parameters:**");
            foreach (var parameter in parameters)
            {
                text.Append("\n- `").Append(parameter.Subject).Append('`');
                if (!String.IsNullOrWhiteSpace(parameter.Description)) text.Append(": ").Append(RenderDocumentation(parameter.Description));
            }
        }
        if (documentation.ReturnsDocumentation is { Length: > 0 } returns) text.Append("\n\n**Returns:** ").Append(RenderDocumentation(returns));
        if (documentation.DeprecationMessage is { Length: > 0 } deprecated) text.Append("\n\n> **Deprecated:** ").Append(RenderDocumentation(deprecated));
        foreach (var throws in documentation.TagsNamed("throws")) text.Append("\n\n**Throws:** ").Append(RenderDocumentation(throws.Description));
        foreach (var example in documentation.TagsNamed("example")) text.Append("\n\n**Example**\n```sushi\n").Append(example.Description).Append("\n```");
        return text.Length == 0 ? "" : "\n\n" + text;
    }
    private static string RenderDocumentation(string text) => System.Text.RegularExpressions.Regex.Replace(text, "\\{@link\\s+([A-Za-z_][A-Za-z0-9_.]*)(?:\\s+([^}]+))?\\}", match => $"`{(match.Groups[2].Success ? match.Groups[2].Value.Trim() : match.Groups[1].Value)}`");
    private static bool IsDocumentationTagContext(string text, int offset)
    {
        var start = LineStart(text, offset);
        return text[start..Math.Clamp(offset, start, text.Length)].TrimStart().StartsWith("/// @", StringComparison.Ordinal);
    }
    private static int SymbolKind(string kind) => kind switch { "class" => 5, "enum" => 10, "function" => 12, "module" => 2, _ => 13 };
    private static int SymbolKind(SushiSymbolKind kind) => kind switch
    {
        SushiSymbolKind.Namespace or SushiSymbolKind.Module => 2,
        SushiSymbolKind.Class => 5,
        SushiSymbolKind.Enum => 10,
        SushiSymbolKind.EnumMember => 22,
        SushiSymbolKind.Function => 12,
        SushiSymbolKind.Method => 6,
        SushiSymbolKind.Constructor => 9,
        SushiSymbolKind.Field => 8,
        SushiSymbolKind.Parameter => 26,
        _ => 13
    };

    private async Task<JsonElement?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var header = new StringBuilder();
        var lastFour = new Queue<byte>();
        while (true)
        {
            var one = new byte[1];
            if (await _input.ReadAsync(one, cancellationToken) == 0) return null;
            header.Append((char)one[0]); lastFour.Enqueue(one[0]); if (lastFour.Count > 4) lastFour.Dequeue();
            if (lastFour.Count == 4 && lastFour.SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        }
        var lengthLine = header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (lengthLine is null || !int.TryParse(lengthLine["Content-Length:".Length..].Trim(), out var length)) return null;
        var buffer = new byte[length]; var read = 0;
        while (read < length) { var count = await _input.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken); if (count == 0) return null; read += count; }
        return JsonDocument.Parse(buffer).RootElement.Clone();
    }

    private Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) => SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);
    private Task ReplyAsync(JsonElement id, object? result, object? error, CancellationToken cancellationToken) => SendAsync(error is null ? new { jsonrpc = "2.0", id, result } : new { jsonrpc = "2.0", id, error }, cancellationToken);
    private async Task SendAsync(object body, CancellationToken cancellationToken)
    {
        // JSON-RPC responses must always contain either a `result` or an `error`
        // member. In particular, a valid hover/rename request can have a null
        // result when the cursor is not over a symbol. Do not globally omit null
        // properties or the response becomes an invalid JSON-RPC message that
        // clients (notably vscode-jsonrpc) reject.
        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _output.WriteAsync(header, cancellationToken); await _output.WriteAsync(payload, cancellationToken); await _output.FlushAsync(cancellationToken);
    }

    private sealed record OpenDocument(string Uri, string Text, int Version);
    private sealed record SignatureInformation(string Label, SignatureParameter[] Parameters, string Documentation);
    private sealed record SignatureParameter(string Label, string? Documentation);
    private sealed record CompletionItem(string Label, int Kind, string Detail, string? Documentation, string? Deprecated, string? InsertText = null, int? InsertTextFormat = null);
    private sealed record Snippet(string Label, string Detail, string InsertText, string Documentation);
    private sealed record SymbolOccurrence(string Uri, ClassifiedToken Token, string Name, string Kind, bool Declaration, bool Exported);
    private static readonly string[] Keywords = ["box", "use", "class", "new", "return", "this", "if", "else", "while", "for", "break", "continue", "true", "false", "null", "var", "switch", "case", "default", "also", "do", "step", "enum", "in", "export", "as"];
    private static readonly StandardLibraryCatalog StandardLibrary = StandardLibraryCatalog.CreateDefault();
    private static readonly HashSet<string> StandardLibraryNames = StandardLibrary.Functions.Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
    private static readonly Snippet[] Snippets =
    [
        new("if", "conditional block", "if (${1:condition}) {\n    ${0}\n}", "Creates an `if` block."),
        new("if / else", "conditional branches", "if (${1:condition}) {\n    ${2}\n} else {\n    ${0}\n}", "Creates an `if` / `else` block."),
        new("while", "loop", "while (${1:condition}) {\n    ${0}\n}", "Creates a `while` loop."),
        new("for", "collection loop", "for (${1:item} : ${2:items}) {\n    ${0}\n}", "Creates a collection loop."),
        new("function", "function declaration", "${1:void} ${2:name}(${3}) {\n    ${0}\n}", "Creates a typed function."),
        new("class", "class declaration", "class ${1:Name} {\n    new(${2}) {\n        ${0}\n    }\n}", "Creates a class and constructor."),
        new("enum", "enum declaration", "enum ${1:Name} {\n    ${2:Value}\n}", "Creates an enum."),
        new("use", "module import", "use \"${1:module.sushi}\"${0}", "Imports a Sushi module.")
    ];
    // Keep this legend stable: clients cache token indexes for the lifetime of an LSP session.
    private const int SemanticNamespace = 0;
    private const int SemanticClass = 1;
    private const int SemanticEnum = 2;
    private const int SemanticEnumMember = 3;
    private const int SemanticFunction = 4;
    private const int SemanticMethod = 5;
    private const int SemanticProperty = 6;
    private const int SemanticParameter = 7;
    private const int SemanticVariable = 8;
    private const int SemanticKeyword = 9;
    private const int SemanticOperator = 10;
    private const int SemanticString = 11;
    private const int SemanticNumber = 12;
    private const int SemanticComment = 13;
    private const int SemanticUnusedImport = 14;
    private const int DeclarationModifier = 1;
    private static readonly string[] SemanticTokenTypes = ["namespace", "class", "enum", "enumMember", "function", "method", "property", "parameter", "variable", "keyword", "operator", "string", "number", "comment", "unusedImport"];
    private static readonly string[] SemanticTokenModifiers = ["declaration"];
}
