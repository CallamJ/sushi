namespace Sushi.Application.LanguageServer;

using System.Text;
using System.Text.Json;
using Sushi.Application;
using Sushi.Build;
using Sushi.Build.SyntaxTree;
using Sushi.Transpilation;

/// <summary>
/// Small, dependency-free LSP 3.17 endpoint. Keeping the protocol boundary here makes
/// Sushi usable from any editor without pulling editor SDKs into the compiler.
/// </summary>
internal sealed class SushiLanguageServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly TextWriter _log;
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
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
                    initialization.ValueKind == JsonValueKind.Object && initialization.TryGetProperty("targetProfile", out var targetProfile) &&
                    TargetProfile.TryParse(targetProfile.GetString(), out var selectedProfile))
                    _targetProfile = selectedProfile;
                await ReplyAsync(id, new
                {
                    capabilities = new
                    {
                        positionEncoding = "utf-16",
                        textDocumentSync = 1,
                        completionProvider = new { triggerCharacters = new[] { "." } },
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
    }

    private async Task PublishDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        if (!_documents.TryGetValue(uri, out var document)) return;
        var path = PathForUri(uri);
        var result = new Transpiler().Transpile(new TranspileRequest { SourcePath = path, SourceText = document.Text, TargetLanguage = _targetProfile.Shell, TargetProfile = _targetProfile });
        var diagnostics = result.Diagnostics.Select(d => new
        {
            range = Range(document.Text, d.Span.StartOffset, d.Span.EndOffset > d.Span.StartOffset ? d.Span.EndOffset : d.Span.StartOffset + 1, d.Span.Line, d.Span.Column),
            severity = d.Severity == DiagnosticSeverity.Error ? 1 : 2,
            code = d.Code,
            source = "sushi",
            message = d.Message
        });
        await NotifyAsync("textDocument/publishDiagnostics", new { uri, version = document.Version, diagnostics }, cancellationToken);
    }

    private object Completion(JsonElement parameters)
    {
        var document = Document(parameters);
        var items = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
        foreach (var symbol in AnalyzeAll().Where(symbol => symbol.Uri == document.Uri || symbol.Exported))
            items[symbol.Name] = new CompletionItem(symbol.Name, CompletionKind(symbol.Kind), symbol.Kind);
        foreach (var keyword in Keywords)
            items.TryAdd(keyword, new CompletionItem(keyword, 14, "keyword"));
        foreach (var builtIn in StandardLibrary)
            items.TryAdd(builtIn, new CompletionItem(builtIn, builtIn.StartsWith("std.", StringComparison.Ordinal) ? 9 : 3, builtIn.StartsWith("std.", StringComparison.Ordinal) ? "module" : "function"));

        return new
        {
            isIncomplete = false,
            items = items.Values.OrderBy(item => item.Label, StringComparer.Ordinal)
                .Select(item => new { label = item.Label, kind = item.Kind, detail = item.Detail }).ToArray()
        };
    }

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
        var model = SushiSemanticModel.Create(document.Text);
        var offset = Offset(document.Text, parameters.GetProperty("position"));
        var token = model.TokenAt(offset);
        if (token is null) return null;
        var symbol = model.SymbolAt(offset);
        var text = symbol is null ? $"`{token.Text}`" : HoverText(symbol);
        return new { contents = new { kind = "markdown", value = text }, range = TokenRange(document.Text, token) };
    }

    private static string HoverText(SushiSymbol symbol)
    {
        var type = symbol.DeclaredType is null ? "" : $": {symbol.DeclaredType}";
        return $"`{symbol.Kind.ToString().ToLowerInvariant()} {symbol.Name}{type}`";
    }

    private object Definitions(JsonElement parameters) => LocationsForToken(parameters, declarationsOnly: true);
    private object References(JsonElement parameters) => LocationsForToken(parameters, declarationsOnly: false);

    private object LocationsForToken(JsonElement parameters, bool declarationsOnly)
    {
        var document = Document(parameters);
        var model = SushiSemanticModel.Create(document.Text);
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
        var model = SushiSemanticModel.Create(document.Text);
        return model.Symbols
            .Select(symbol => new { name = symbol.Name, kind = SymbolKind(symbol.Kind), range = TokenRange(document.Text, symbol.Token), selectionRange = TokenRange(document.Text, symbol.Token) }).ToArray();
    }

    private object DocumentHighlights(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = SushiSemanticModel.Create(document.Text);
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
            .SelectMany(document => SushiSemanticModel.Create(document.Text).Symbols.Select(symbol => new { document, symbol }))
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
        var model = SushiSemanticModel.Create(document.Text);
        var offset = Offset(document.Text, parameters.GetProperty("position"));
        var open = OpenCallParenthesis(model.Tokens, offset);
        if (open is null) return null;
        var name = model.Tokens.Where(token => token.End <= open.Start && token.Kind == ClassifiedTokenKind.Identifier).LastOrDefault();
        if (name is null) return null;
        var symbol = model.SymbolFor(name);
        if (symbol is null || symbol.Kind is not (SushiSymbolKind.Function or SushiSymbolKind.Method))
            symbol = model.Symbols.FirstOrDefault(candidate => candidate.Name == name.Text && candidate.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method);

        var signature = symbol is null ? BuiltInSignature(name.Text) : SignatureFor(model, symbol);
        if (signature is null) return null;
        var activeParameter = ActiveParameter(model.Tokens, open, offset);
        return new
        {
            signatures = new[] { new { label = signature.Label, documentation = new { kind = "markdown", value = signature.Documentation }, parameters = signature.Parameters.Select(parameter => new { label = parameter }).ToArray() } },
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

    private static SignatureInformation? SignatureFor(SushiSemanticModel model, SushiSymbol symbol)
    {
        var parameters = ParametersFor(model, symbol)
            .Select(parameter => parameter.DeclaredType is null ? parameter.Name : $"{parameter.DeclaredType} {parameter.Name}")
            .ToArray();
        return new SignatureInformation($"{symbol.Name}({string.Join(", ", parameters)})", parameters, $"{symbol.Kind}: `{symbol.Name}`");
    }

    private static SignatureInformation? BuiltInSignature(string name) => name switch
    {
        "println" => new SignatureInformation("println(object value)", ["object value"], "Writes a value followed by a newline."),
        "print" => new SignatureInformation("print(object value)", ["object value"], "Writes a value without a newline."),
        "string" => new SignatureInformation("string(object value)", ["object value"], "Converts a value to its string representation."),
        _ => null
    };

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
        var model = SushiSemanticModel.Create(document.Text);
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
        var model = SushiSemanticModel.Create(document.Text);
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

    private object CodeActions(JsonElement parameters)
    {
        var document = Document(parameters);
        var range = parameters.GetProperty("range");
        var start = Offset(document.Text, range.GetProperty("start"));
        var end = Offset(document.Text, range.GetProperty("end"));
        var model = SushiSemanticModel.Create(document.Text);
        var actions = new List<object>();

        if (TryInlineVariable(document, model, start, out var inlineEdit, out var inlineName))
            actions.Add(new { title = $"Inline '{inlineName}'", kind = "refactor.inline", edit = inlineEdit, isPreferred = true });
        if (TryExtractVariable(document, model, start, end, out var extractEdit, out var extractName))
            actions.Add(new { title = $"Extract variable '{extractName}'", kind = "refactor.extract", edit = extractEdit, isPreferred = true });
        return actions;
    }

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

    private object SemanticTokens(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = SushiSemanticModel.Create(document.Text);
        var classifiedByStart = model.Tokens.ToDictionary(token => token.Start);
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
            if (type < 0) continue;
            var modifiers = raw.Kind != TokenKind.Comment && classifiedByStart.TryGetValue(raw.Start, out var identifier) &&
                model.SymbolFor(identifier) is { } symbol && symbol.Token.Start == identifier.Start
                ? DeclarationModifier : 0;
            AddSemanticToken(data, document.Text, raw.Start, raw.Length, type, modifiers, ref previousLine, ref previousColumn);
        }
        return new { data };
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
        var model = SushiSemanticModel.Create(document.Text);
        var offset = Offset(document.Text, parameters.GetProperty("position"));
        var token = model.TokenAt(offset);
        return token is null || model.SymbolAt(offset) is null || IsKeyword(token.Text) || StandardLibrary.Contains(token.Text) ? null : TokenRange(document.Text, token);
    }

    private object? Rename(JsonElement parameters)
    {
        var document = Document(parameters);
        var model = SushiSemanticModel.Create(document.Text);
        var token = model.TokenAt(Offset(document.Text, parameters.GetProperty("position")));
        var newName = parameters.GetProperty("newName").GetString() ?? "";
        var symbol = token is null ? null : model.SymbolFor(token);
        if (token is null || symbol is null || !IsIdentifier(newName) || IsKeyword(token.Text) || StandardLibrary.Contains(token.Text)) return null;
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
            yield return new SymbolOccurrence(document.Uri, token, token.Text, kind, declaration, declaration && IsTopLevel(tokens, index));
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
    private static int SymbolKind(string kind) => kind switch { "class" => 5, "enum" => 10, "function" => 12, "module" => 2, _ => 13 };
    private static int SymbolKind(SushiSymbolKind kind) => kind switch
    {
        SushiSymbolKind.Namespace or SushiSymbolKind.Module => 2,
        SushiSymbolKind.Class => 5,
        SushiSymbolKind.Enum => 10,
        SushiSymbolKind.EnumMember => 22,
        SushiSymbolKind.Function => 12,
        SushiSymbolKind.Method => 6,
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
    private sealed record SignatureInformation(string Label, string[] Parameters, string Documentation);
    private sealed record CompletionItem(string Label, int Kind, string Detail);
    private sealed record SymbolOccurrence(string Uri, ClassifiedToken Token, string Name, string Kind, bool Declaration, bool Exported);
    private static readonly string[] Keywords = ["box", "use", "class", "new", "return", "this", "if", "else", "while", "for", "break", "continue", "true", "false", "null", "var", "switch", "case", "default", "also", "do", "step", "enum", "in", "export", "as"];
    private static readonly string[] StandardLibrary = ["println", "print", "string", "std.fs", "std.archive", "std.http", "std.string"];
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
    private const int DeclarationModifier = 1;
    private static readonly string[] SemanticTokenTypes = ["namespace", "class", "enum", "enumMember", "function", "method", "property", "parameter", "variable", "keyword", "operator", "string", "number", "comment"];
    private static readonly string[] SemanticTokenModifiers = ["declaration"];
}
