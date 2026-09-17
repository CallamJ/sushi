namespace Sushi.Application.LanguageServer;

using Sushi.Build;
using Sushi.Build.SyntaxTree;
using Sushi.Transpilation.Intrinsics;

/// <summary>
/// A lossless, editor-facing view of a Sushi document. The compiler lexer deliberately
/// discards trivia before parsing; language tooling must retain it for highlighting,
/// formatting, and exact LSP ranges.
/// </summary>
internal sealed class SushiSemanticModel
{
    private static readonly StandardLibraryCatalog StandardLibrary = StandardLibraryCatalog.CreateDefault();
    private readonly Dictionary<int, SushiSymbol> _symbolsByTokenStart;

    private SushiSemanticModel(
        string text,
        IReadOnlyList<UnclassifiedToken> rawTokens,
        IReadOnlyList<ClassifiedToken> tokens,
        IReadOnlyList<SushiSymbol> symbols,
        Dictionary<int, SushiSymbol> symbolsByTokenStart)
    {
        Text = text;
        RawTokens = rawTokens;
        Tokens = tokens;
        Symbols = symbols;
        _symbolsByTokenStart = symbolsByTokenStart;
    }

    public string Text { get; }
    public IReadOnlyList<UnclassifiedToken> RawTokens { get; }
    public IReadOnlyList<ClassifiedToken> Tokens { get; }
    public IReadOnlyList<SushiSymbol> Symbols { get; }

    public static SushiSemanticModel Create(string text)
    {
        var raw = new Tokenizer(text).Tokenize().ToArray();
        var tokens = new Lexer(raw).Lex().Where(token => token.Kind != ClassifiedTokenKind.EndOfFile).ToArray();
        var scopes = BuildScopes(tokens);
        var symbols = CollectDeclarations(tokens, scopes);
        AttachDocumentation(raw, tokens, symbols);
        var byStart = new Dictionary<int, SushiSymbol>();

        foreach (var symbol in symbols)
            byStart[symbol.Token.Start] = symbol;

        // Bind remaining identifiers to the closest visible declaration. This deliberately
        // handles incomplete source too: declarations collected before a parser recovery
        // point are still useful to highlighting and navigation.
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Kind != ClassifiedTokenKind.Identifier || byStart.ContainsKey(token.Start)) continue;
            var resolved = Resolve(token, index, scopes[index], symbols, tokens);
            if (resolved is not null) byStart[token.Start] = resolved;
        }

        return new SushiSemanticModel(text, raw, tokens, symbols, byStart);
    }

    private static void AttachDocumentation(IReadOnlyList<UnclassifiedToken> raw, ClassifiedToken[] tokens, List<SushiSymbol> symbols)
    {
        var documentation = DocumentationParser.Parse(string.Concat(raw.Select(token => token.Text)));
        foreach (var comment in documentation)
        {
            var index = Array.FindIndex(tokens, token => token.Start == comment.TargetOffset);
            if (index < 0) continue;
            if (tokens[index].IsKeyword("export")) index++;
            if (index >= tokens.Length) continue;
            var declaration = tokens[index];
            if (declaration.IsKeyword("class") || declaration.IsKeyword("enum") || declaration.IsKeyword("var")) index++;
            else if (declaration.Kind == ClassifiedTokenKind.Identifier && index + 1 < tokens.Length &&
                     tokens[index + 1].Kind == ClassifiedTokenKind.Identifier) index++;
            if (index >= tokens.Length) continue;
            var symbol = symbols.FirstOrDefault(candidate => candidate.Token.Start == tokens[index].Start);
            if (symbol is not null) symbol.Documentation = comment;
        }
    }

    public ClassifiedToken? TokenAt(int offset) => Tokens.FirstOrDefault(token =>
        token.Start <= offset && offset < token.End && (token.Kind == ClassifiedTokenKind.Identifier || token.IsKeyword("new")));

    public SushiSymbol? SymbolAt(int offset)
    {
        var token = TokenAt(offset);
        return token is not null && _symbolsByTokenStart.TryGetValue(token.Start, out var symbol) ? symbol : null;
    }

    public SushiSymbol? SymbolFor(ClassifiedToken token) =>
        _symbolsByTokenStart.TryGetValue(token.Start, out var symbol) ? symbol : null;

    /// <summary>Resolves the semantic type of an expression ending at <paramref name="offset"/>.
    /// This is intentionally centralized so completion, hover and signature help do not each
    /// invent a different receiver inference rule.</summary>
    public string? TypeAt(int offset)
    {
        var token = Tokens.FirstOrDefault(candidate => candidate.Start <= offset &&
            offset < candidate.End && candidate.Kind != ClassifiedTokenKind.EndOfFile)
            ?? Tokens.LastOrDefault(candidate => candidate.End <= offset &&
                candidate.Kind != ClassifiedTokenKind.EndOfFile);
        return token is null ? null : TypeOf(token);
    }

    public string? TypeOf(ClassifiedToken token)
    {
        if (token.Kind is ClassifiedTokenKind.StringLiteral or ClassifiedTokenKind.InterpolatedString) return "string";
        if (token.Kind == ClassifiedTokenKind.IntegerLiteral) return "int";
        if (token.Kind == ClassifiedTokenKind.FloatLiteral) return "float";
        if (token.IsKeyword("true") || token.IsKeyword("false")) return "bool";
        if (token.IsKeyword("null")) return "any";
        var symbol = SymbolFor(token);
        if (symbol is not null)
        {
            if (symbol.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method)
                return symbol.DeclaredType ?? InferReturnType(symbol);
            if (symbol.DeclaredType is not null) return symbol.DeclaredType;
            if (symbol.Kind is SushiSymbolKind.Variable or SushiSymbolKind.Field)
            {
                var equals = symbol.DeclarationIndex + 1 < Tokens.Count && Tokens[symbol.DeclarationIndex + 1].IsOperator("=")
                    ? symbol.DeclarationIndex + 2 : -1;
                if (equals >= 0 && Tokens.Skip(equals).TakeWhile(candidate => candidate.Kind is not ClassifiedTokenKind.Semicolon && candidate.Line == Tokens[equals].Line)
                    .Any(candidate => candidate.Kind == ClassifiedTokenKind.Identifier && candidate.Text == "query"))
                    return "FileQuery";
                if (equals + 1 < Tokens.Count && Tokens[equals].IsKeyword("new") && Tokens[equals + 1].Kind == ClassifiedTokenKind.Identifier)
                    return Tokens[equals + 1].Text;
                if (equals >= 0 && equals < Tokens.Count) return TypeOf(Tokens[equals]);
            }
            return null;
        }

        // A call may be incomplete while the user is typing. Resolve its callee through
        // the same symbol table rather than treating every parenthesized expression alike.
        var index = Array.FindIndex(Tokens.ToArray(), candidate => candidate.Start == token.Start);
        if (index >= 0 && index + 1 < Tokens.Count && Tokens[index + 1].Kind == ClassifiedTokenKind.LeftParen)
        {
            var callable = Symbols.FirstOrDefault(candidate => candidate.Name == token.Text &&
                candidate.Kind is SushiSymbolKind.Function or SushiSymbolKind.Method);
            if (callable is not null) return callable.DeclaredType ?? InferReturnType(callable);
            if (StandardLibrary.TryGetFunction(QualifiedNameAt(token), out var qualifiedIntrinsic)) return qualifiedIntrinsic.ReturnType;
            if (TryFileQueryMemberAt(index, out var fileQueryMember)) return fileQueryMember.ReturnType;
            if (StandardLibrary.TryGetFunction(token.Text, out var intrinsic)) return intrinsic.ReturnType;
        }
        return null;
    }

    public string ReturnTypeOf(SushiSymbol callable) => callable.DeclaredType ?? InferReturnType(callable) ?? "void";

    private string? InferReturnType(SushiSymbol callable)
    {
        var index = Array.FindIndex(Tokens.ToArray(), candidate => candidate.Start == callable.Token.Start);
        if (index < 0) return null;
        var open = Enumerable.Range(index + 1, Tokens.Count - index - 1)
            .FirstOrDefault(i => Tokens[i].Kind == ClassifiedTokenKind.LeftBrace);
        if (open <= index) return null;
        var close = FindMatching(Tokens.ToArray(), open, ClassifiedTokenKind.LeftBrace, ClassifiedTokenKind.RightBrace);
        if (close < 0) return null;
        string? inferred = null;
        for (var i = open + 1; i < close; i++)
        {
            if (!Tokens[i].IsKeyword("return") || i + 1 >= close) continue;
            var current = TypeOf(Tokens[i + 1]);
            if (current is null) continue;
            if (inferred is null) inferred = current;
            else if (!String.Equals(inferred, current, StringComparison.Ordinal)) return "any";
        }
        return inferred ?? "void";
    }

    public string QualifiedNameAt(ClassifiedToken token)
    {
        var tokens = Tokens.ToList();
        var index = tokens.FindIndex(candidate => candidate.Start == token.Start);
        if (index < 0) return token.Text;
        var parts = new List<string> { token.Text };
        for (var cursor = index - 1; cursor >= 1 && tokens[cursor].Kind == ClassifiedTokenKind.Dot &&
             tokens[cursor - 1].Kind == ClassifiedTokenKind.Identifier; cursor -= 2)
            parts.Insert(0, tokens[cursor - 1].Text);
        var name = string.Join('.', parts);
        if (StandardLibrary.TryGetFunction(name, out _)) return name;

        for (var useIndex = 0; useIndex < tokens.Count; useIndex++)
        {
            if (!tokens[useIndex].IsKeyword("use")) continue;
            var cursor = useIndex + 1;
            var importParts = new List<string>();
            while (cursor < tokens.Count && tokens[cursor].Kind == ClassifiedTokenKind.Identifier)
            {
                importParts.Add(tokens[cursor].Text);
                cursor++;
                if (cursor >= tokens.Count || tokens[cursor].Kind != ClassifiedTokenKind.Dot) break;
                cursor++;
            }
            if (importParts.Count < 2 || importParts[0] != "std") continue;

            var importedName = string.Join('.', importParts);
            if (cursor < tokens.Count && tokens[cursor].IsKeyword("as") && cursor + 1 < tokens.Count &&
                tokens[cursor + 1].Kind == ClassifiedTokenKind.Identifier)
            {
                var alias = tokens[cursor + 1].Text;
                if (parts[0] == alias)
                {
                    var candidate = importedName + (parts.Count == 1 ? "" : "." + string.Join('.', parts.Skip(1)));
                    if (StandardLibrary.TryGetFunction(candidate, out _)) return candidate;
                }
                continue;
            }

            if (StandardLibrary.TryGetFunction(importedName, out _) && parts.Count == 1 && parts[0] == importParts[^1])
                return importedName;

            // `use std.fs` exposes `fs.member`; expand that familiar module form.
            if (parts.Count > 1 && parts[0] == importParts[^1])
            {
                var candidate = "std." + name;
                if (StandardLibrary.TryGetFunction(candidate, out _)) return candidate;
            }
        }
        return name;
    }

    public IEnumerable<SushiSymbol> MembersFor(string? type)
    {
        if (String.IsNullOrWhiteSpace(type)) yield break;
        var normalized = type.EndsWith("[]", StringComparison.Ordinal) ? "array" : type;
        if (normalized.Equals("string", StringComparison.OrdinalIgnoreCase) || normalized.Equals("str", StringComparison.OrdinalIgnoreCase))
            yield break; // intrinsic string members are supplied by the catalog layer
        foreach (var symbol in Symbols.Where(symbol => symbol.Kind is SushiSymbolKind.Field or SushiSymbolKind.Method))
        {
            if (MemberBelongsTo(normalized, symbol))
                yield return symbol;
        }
    }

    public bool MemberBelongsTo(string? type, SushiSymbol symbol)
    {
        if (symbol.Kind is not (SushiSymbolKind.Field or SushiSymbolKind.Method) || String.IsNullOrWhiteSpace(type)) return false;
        return string.Equals(ContainingClassName(symbol.DeclarationIndex), type, StringComparison.Ordinal);
    }

    private bool TryFileQueryMemberAt(int index, out StandardLibraryFunction function)
    {
        function = null!;
        if (index < 2 || Tokens[index - 1].Kind != ClassifiedTokenKind.Dot) return false;
        var receiver = Tokens[index - 2];
        var receiverType = TypeOf(receiver);
        if (receiver.Kind == ClassifiedTokenKind.RightParen)
        {
            var open = FindOpeningParenthesis(index - 2);
            if (open > 0) receiverType = TypeOf(Tokens[open - 1]);
        }
        return string.Equals(receiverType, "FileQuery", StringComparison.Ordinal) &&
               StandardLibrary.TryGetFileQueryMember($"FileQuery.{Tokens[index].Text}", out function);
    }

    private int FindOpeningParenthesis(int close)
    {
        var depth = 0;
        for (var index = close; index >= 0; index--)
        {
            if (Tokens[index].Kind == ClassifiedTokenKind.RightParen) depth++;
            else if (Tokens[index].Kind == ClassifiedTokenKind.LeftParen && --depth == 0) return index;
        }
        return -1;
    }

    private string? ContainingClassName(int declarationIndex)
    {
        var depth = 0;
        for (var index = declarationIndex; index >= 0; index--)
        {
            if (Tokens[index].Kind == ClassifiedTokenKind.RightBrace) { depth++; continue; }
            if (Tokens[index].Kind != ClassifiedTokenKind.LeftBrace) continue;
            if (depth > 0) { depth--; continue; }
            if (index >= 2 && Tokens[index - 2].IsKeyword("class") && Tokens[index - 1].Kind == ClassifiedTokenKind.Identifier)
                return Tokens[index - 1].Text;
        }
        return null;
    }

    public IEnumerable<ClassifiedToken> ReferencesOf(SushiSymbol symbol) => Tokens.Where(token =>
        token.Kind == ClassifiedTokenKind.Identifier &&
        _symbolsByTokenStart.TryGetValue(token.Start, out var candidate) && candidate.Id == symbol.Id);

    private static Scope[] BuildScopes(ClassifiedToken[] tokens)
    {
        var result = new Scope[tokens.Length];
        var root = new Scope(null, 0);
        var current = root;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Kind == ClassifiedTokenKind.RightBrace && current.Parent is not null)
                current = current.Parent;
            result[index] = current;
            if (token.Kind == ClassifiedTokenKind.LeftBrace)
                current = new Scope(current, index + 1);
        }
        return result;
    }

    private static List<SushiSymbol> CollectDeclarations(ClassifiedToken[] tokens, Scope[] scopes)
    {
        var symbols = new List<SushiSymbol>();
        var nextId = 1;
        var declarationIndexes = new HashSet<int>();

        void Add(int index, SushiSymbolKind kind, Scope? scope = null, string? declaredType = null, bool exported = false)
        {
            if (index < 0 || index >= tokens.Length ||
                (tokens[index].Kind != ClassifiedTokenKind.Identifier && !(kind == SushiSymbolKind.Constructor && tokens[index].IsKeyword("new"))) ||
                !declarationIndexes.Add(index)) return;
            symbols.Add(new SushiSymbol(nextId++, tokens[index], kind, scope ?? scopes[index], index, declaredType, exported));
        }

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.IsKeyword("class") && index + 1 < tokens.Length) Add(index + 1, SushiSymbolKind.Class);
            else if (token.IsKeyword("enum") && index + 1 < tokens.Length) Add(index + 1, SushiSymbolKind.Enum);
            else if (token.IsKeyword("box") && index + 1 < tokens.Length) Add(index + 1, SushiSymbolKind.Namespace);
            else if (token.IsKeyword("use"))
            {
                var asIndex = FindBeforeStatementEnd(tokens, index, candidate => candidate.IsKeyword("as"));
                if (asIndex >= 0 && asIndex + 1 < tokens.Length) Add(asIndex + 1, SushiSymbolKind.Module);
            }
            else if (token.IsKeyword("var"))
            {
                var name = NextIdentifier(tokens, index + 1);
                if (name >= 0) Add(name, IsInClassBody(tokens, index) ? SushiSymbolKind.Field : SushiSymbolKind.Variable);
            }
        }

        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].IsKeyword("new") && IsInClassBody(tokens, index) && index + 1 < tokens.Length && tokens[index + 1].Kind == ClassifiedTokenKind.LeftParen)
            {
                var close = FindMatching(tokens, index + 1, ClassifiedTokenKind.LeftParen, ClassifiedTokenKind.RightParen);
                Add(index, SushiSymbolKind.Constructor);
                if (close > index)
                {
                    var bodyScope = close + 1 < scopes.Length && tokens[close + 1].Kind == ClassifiedTokenKind.LeftBrace ? scopes[close + 1] : scopes[index];
                    CollectParameters(tokens, index + 2, close, bodyScope, Add);
                }
                continue;
            }
            if (tokens[index].Kind != ClassifiedTokenKind.Identifier) continue;
            var openParen = index + 1 < tokens.Length && tokens[index + 1].Kind == ClassifiedTokenKind.LeftParen ? index + 1 : -1;
            var closeParen = openParen >= 0 ? FindMatching(tokens, openParen, ClassifiedTokenKind.LeftParen, ClassifiedTokenKind.RightParen) : -1;
            var isFunction = closeParen >= 0 && closeParen + 1 < tokens.Length &&
                (tokens[closeParen + 1].Kind == ClassifiedTokenKind.LeftBrace || tokens[closeParen + 1].IsOperator("->"));
            if (isFunction)
            {
                var kind = IsInClassBody(tokens, index) ? SushiSymbolKind.Method : SushiSymbolKind.Function;
                // Keep an omitted return type distinct from an explicit `void`; the semantic
                // layer can then infer a concrete type from return expressions.
                var returnType = index > 0 && tokens[index - 1].Kind == ClassifiedTokenKind.Identifier && IsTypeName(tokens[index - 1].Text)
                    ? tokens[index - 1].Text
                    : null;
                Add(index, kind, declaredType: returnType);
                var bodyScope = closeParen + 1 < scopes.Length && tokens[closeParen + 1].Kind == ClassifiedTokenKind.LeftBrace
                    ? scopes[closeParen + 1] : scopes[index];
                CollectParameters(tokens, openParen + 1, closeParen, bodyScope, Add);
                continue;
            }

            // Typed variables and fields use the form "Type name". A class-body
            // declaration is a field; elsewhere it is a variable.
            var nameIndex = index + 1;
            var declaredType = tokens[index].Text;
            if (nameIndex + 1 < tokens.Length && tokens[nameIndex].Kind == ClassifiedTokenKind.LeftBracket &&
                tokens[nameIndex + 1].Kind == ClassifiedTokenKind.RightBracket)
            {
                nameIndex += 2;
                declaredType += "[]";
            }
            if (nameIndex < tokens.Length && tokens[nameIndex].Kind == ClassifiedTokenKind.Identifier &&
                IsTypeName(tokens[index].Text) && IsDeclarationTerminator(tokens, nameIndex + 1))
            {
                var kind = IsInClassBody(tokens, index) ? SushiSymbolKind.Field : SushiSymbolKind.Variable;
                Add(nameIndex, kind, declaredType: declaredType);
                CollectSharedTypedDeclarations(tokens, nameIndex + 1, scopes, kind, declaredType, Add);
            }
        }

        CollectEnumValues(tokens, scopes, Add);
        return symbols;
    }

    /// <summary>
    /// A typed declaration may introduce several names, for example
    /// <c>string first = "a", second = "b"</c>. The parser expands this for
    /// compilation, but the editor model is token-based and must do the same
    /// work so every name can retain the shared type in hover and navigation.
    /// </summary>
    private static void CollectSharedTypedDeclarations(
        ClassifiedToken[] tokens,
        int start,
        Scope[] scopes,
        SushiSymbolKind kind,
        string declaredType,
        Action<int, SushiSymbolKind, Scope?, string?, bool> add)
    {
        var parentheses = 0;
        var brackets = 0;
        var braces = 0;
        var expectName = false;

        for (var index = start; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Kind == ClassifiedTokenKind.LeftParen) { parentheses++; continue; }
            if (token.Kind == ClassifiedTokenKind.RightParen) { if (parentheses > 0) parentheses--; continue; }
            if (token.Kind == ClassifiedTokenKind.LeftBracket) { brackets++; continue; }
            if (token.Kind == ClassifiedTokenKind.RightBracket) { if (brackets > 0) brackets--; continue; }
            if (token.Kind == ClassifiedTokenKind.LeftBrace) { braces++; continue; }
            if (token.Kind == ClassifiedTokenKind.RightBrace)
            {
                if (braces > 0) { braces--; continue; }
                return;
            }
            if (parentheses != 0 || brackets != 0 || braces != 0) continue;
            if (token.Kind == ClassifiedTokenKind.Semicolon) return;

            if (token.Kind == ClassifiedTokenKind.Comma)
            {
                expectName = true;
                continue;
            }

            if (expectName && token.Kind == ClassifiedTokenKind.Identifier)
            {
                add(index, kind, scopes[index], declaredType, false);
                expectName = false;
            }
        }
    }

    private static void CollectParameters(ClassifiedToken[] tokens, int start, int end, Scope scope, Action<int, SushiSymbolKind, Scope?, string?, bool> add)
    {
        // Parse comma-delimited parameter segments rather than guessing from
        // neighbouring tokens. The old rule missed the final untyped parameter
        // in forms such as `new(first, second)` because it looked beyond the
        // supplied parameter range for the closing parenthesis.
        for (var segmentStart = start; segmentStart < end;)
        {
            var segmentEnd = segmentStart;
            while (segmentEnd < end && tokens[segmentEnd].Kind != ClassifiedTokenKind.Comma) segmentEnd++;

            var identifiers = Enumerable.Range(segmentStart, segmentEnd - segmentStart)
                .Where(index => tokens[index].Kind == ClassifiedTokenKind.Identifier)
                .ToArray();
            if (identifiers.Length > 0)
            {
                // A typed parameter has a type followed by its name (including
                // arbitrary user-defined types); otherwise the first identifier
                // is the untyped parameter name.
                var name = identifiers.Length > 1 ? identifiers[1] : identifiers[0];
                var type = identifiers.Length > 1 ? tokens[identifiers[0]].Text : null;
                add(name, SushiSymbolKind.Parameter, scope, type, false);
            }

            segmentStart = segmentEnd + 1;
        }
    }

    private static void CollectEnumValues(ClassifiedToken[] tokens, Scope[] scopes, Action<int, SushiSymbolKind, Scope?, string?, bool> add)
    {
        var enumDepth = -1;
        var braceDepth = 0;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Kind == ClassifiedTokenKind.LeftBrace) braceDepth++;
            if (tokens[index].IsKeyword("enum")) enumDepth = braceDepth + 1;
            if (enumDepth == braceDepth && tokens[index].Kind == ClassifiedTokenKind.Identifier &&
                (index == 0 || tokens[index - 1].Kind is ClassifiedTokenKind.LeftBrace or ClassifiedTokenKind.Comma or ClassifiedTokenKind.Semicolon))
                add(index, SushiSymbolKind.EnumMember, scopes[index], null, false);
            if (tokens[index].Kind == ClassifiedTokenKind.RightBrace)
            {
                if (enumDepth == braceDepth) enumDepth = -1;
                braceDepth--;
            }
        }
    }

    private static SushiSymbol? Resolve(ClassifiedToken token, int index, Scope scope, IEnumerable<SushiSymbol> symbols, IReadOnlyList<ClassifiedToken> tokens)
    {
        // Member declarations live in their class scope, not the scope where an
        // arbitrary object is used. Keep their documentation usable for the
        // common `object.member` form even when receiver type inference is not
        // available in an incomplete editor buffer.
        if (index > 0 && tokens[index - 1].Kind == ClassifiedTokenKind.Dot)
        {
            var member = symbols.FirstOrDefault(candidate => candidate.Name == token.Text &&
                candidate.Kind is SushiSymbolKind.Field or SushiSymbolKind.Method && candidate.DeclarationIndex <= index);
            if (member is not null) return member;
        }
        SushiSymbol? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in symbols)
        {
            if (!String.Equals(candidate.Name, token.Text, StringComparison.Ordinal)) continue;
            if (!IsVisible(candidate, scope)) continue;
            if (candidate.DeclarationIndex > index && candidate.Kind is SushiSymbolKind.Variable or SushiSymbolKind.Parameter or SushiSymbolKind.Field) continue;
            var distance = ScopeDistance(scope, (Scope)candidate.Scope);
            if (distance < bestDistance) { best = candidate; bestDistance = distance; }
        }
        return best;
    }

    private static bool IsVisible(SushiSymbol symbol, Scope scope) => ScopeDistance(scope, (Scope)symbol.Scope) != int.MaxValue;
    private static int ScopeDistance(Scope scope, Scope candidate)
    {
        var distance = 0;
        for (var current = scope; current is not null; current = current.Parent, distance++)
            if (ReferenceEquals(current, candidate)) return distance;
        return int.MaxValue;
    }

    private static int FindMatching(ClassifiedToken[] tokens, int open, ClassifiedTokenKind left, ClassifiedTokenKind right)
    {
        var depth = 0;
        for (var index = open; index < tokens.Length; index++)
        {
            if (tokens[index].Kind == left) depth++;
            else if (tokens[index].Kind == right && --depth == 0) return index;
        }
        return -1;
    }

    private static int NextIdentifier(ClassifiedToken[] tokens, int start)
    {
        for (var index = start; index < tokens.Length && index < start + 8; index++)
        {
            if (tokens[index].Kind == ClassifiedTokenKind.Identifier) return index;
            if (tokens[index].Kind is ClassifiedTokenKind.Semicolon or ClassifiedTokenKind.Operator) break;
        }
        return -1;
    }

    private static int FindBeforeStatementEnd(ClassifiedToken[] tokens, int start, Func<ClassifiedToken, bool> predicate)
    {
        for (var index = start; index < tokens.Length && tokens[index].Kind != ClassifiedTokenKind.Semicolon; index++)
            if (predicate(tokens[index])) return index;
        return -1;
    }

    private static bool IsDeclarationTerminator(ClassifiedToken[] tokens, int index) => index >= tokens.Length ||
        tokens[index].Kind is ClassifiedTokenKind.Semicolon or ClassifiedTokenKind.RightBrace or ClassifiedTokenKind.Operator or ClassifiedTokenKind.Comma;
    internal static bool IsTypeName(string name) => name.EndsWith("[]", StringComparison.Ordinal) ||
        name is "string" or "int" or "float" or "bool" or "object" or "any" or "void" ||
        (name.Length > 0 && char.IsUpper(name[0]));
    private static bool IsInClassBody(ClassifiedToken[] tokens, int index)
    {
        var depth = 0;
        for (var cursor = index; cursor >= 0; cursor--)
        {
            if (tokens[cursor].Kind == ClassifiedTokenKind.RightBrace) depth++;
            else if (tokens[cursor].Kind == ClassifiedTokenKind.LeftBrace)
            {
                if (depth-- > 0) continue;
                return cursor > 1 && tokens[cursor - 2].IsKeyword("class");
            }
        }
        return false;
    }

    private sealed class Scope
    {
        public Scope(Scope? parent, int start) { Parent = parent; Start = start; }
        public Scope? Parent { get; }
        public int Start { get; }
    }
}

internal sealed record SushiSymbol(int Id, ClassifiedToken Token, SushiSymbolKind Kind, object ScopeHandle, int DeclarationIndex, string? DeclaredType, bool Exported)
{
    public string Name => Token.Text;
    public object Scope => ScopeHandle;
    public DocumentationComment? Documentation { get; set; }
    public SushiType Type => Kind switch
    {
        SushiSymbolKind.Class => SushiType.Named(Name),
        SushiSymbolKind.Enum => SushiType.Named(Name),
        SushiSymbolKind.Function or SushiSymbolKind.Method or SushiSymbolKind.Constructor => SushiType.Function(DeclaredType ?? "void"),
        _ => SushiType.Parse(DeclaredType)
    };
}

/// <summary>Small, lossless type vocabulary shared by editor features. Unknown means the
/// analyzer lacks information; Any is an explicit dynamic type and must not be treated as
/// proof that every member exists.</summary>
internal sealed record SushiType(SushiTypeKind Kind, string Name, SushiType? Element = null)
{
    public static SushiType Unknown { get; } = new(SushiTypeKind.Unknown, "unknown");
    public static SushiType Any { get; } = new(SushiTypeKind.Any, "any");
    public static SushiType Void { get; } = new(SushiTypeKind.Void, "void");
    public static SushiType Named(string name) => Parse(name);
    public static SushiType Function(string returnType) => new(SushiTypeKind.Function, returnType);
    public static SushiType Parse(string? name)
    {
        if (String.IsNullOrWhiteSpace(name)) return Unknown;
        if (name.EndsWith("[]", StringComparison.Ordinal)) return new(SushiTypeKind.Array, name, Parse(name[..^2]));
        return name.ToLowerInvariant() switch
        {
            "any" or "object" => Any,
            "void" => Void,
            "string" or "str" => new(SushiTypeKind.String, name),
            "int" => new(SushiTypeKind.Int, name),
            "float" => new(SushiTypeKind.Float, name),
            "bool" => new(SushiTypeKind.Bool, name),
            _ => new(SushiTypeKind.Named, name)
        };
    }
}

internal enum SushiTypeKind { Unknown, Any, Void, String, Int, Float, Bool, Array, Named, Function }

internal enum SushiSymbolKind
{
    Namespace,
    Module,
    Class,
    Enum,
    EnumMember,
    Function,
    Method,
    Constructor,
    Field,
    Parameter,
    Variable
}
