namespace Sushi.Application.LanguageServer;

using Sushi.Build;
using Sushi.Build.SyntaxTree;

/// <summary>
/// A lossless, editor-facing view of a Sushi document. The compiler lexer deliberately
/// discards trivia before parsing; language tooling must retain it for highlighting,
/// formatting, and exact LSP ranges.
/// </summary>
internal sealed class SushiSemanticModel
{
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
            var resolved = Resolve(token, index, scopes[index], symbols);
            if (resolved is not null) byStart[token.Start] = resolved;
        }

        return new SushiSemanticModel(text, raw, tokens, symbols, byStart);
    }

    public ClassifiedToken? TokenAt(int offset) => Tokens.FirstOrDefault(token =>
        token.Start <= offset && offset < token.End && token.Kind == ClassifiedTokenKind.Identifier);

    public SushiSymbol? SymbolAt(int offset)
    {
        var token = TokenAt(offset);
        return token is not null && _symbolsByTokenStart.TryGetValue(token.Start, out var symbol) ? symbol : null;
    }

    public SushiSymbol? SymbolFor(ClassifiedToken token) =>
        _symbolsByTokenStart.TryGetValue(token.Start, out var symbol) ? symbol : null;

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
            if (index < 0 || index >= tokens.Length || tokens[index].Kind != ClassifiedTokenKind.Identifier || !declarationIndexes.Add(index)) return;
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
                if (name >= 0) Add(name, SushiSymbolKind.Variable);
            }
        }

        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Kind != ClassifiedTokenKind.Identifier) continue;
            var openParen = index + 1 < tokens.Length && tokens[index + 1].Kind == ClassifiedTokenKind.LeftParen ? index + 1 : -1;
            var closeParen = openParen >= 0 ? FindMatching(tokens, openParen, ClassifiedTokenKind.LeftParen, ClassifiedTokenKind.RightParen) : -1;
            var isFunction = closeParen >= 0 && closeParen + 1 < tokens.Length &&
                (tokens[closeParen + 1].Kind == ClassifiedTokenKind.LeftBrace || tokens[closeParen + 1].IsOperator("->"));
            if (isFunction)
            {
                var kind = IsInClassBody(tokens, index) ? SushiSymbolKind.Method : SushiSymbolKind.Function;
                Add(index, kind);
                var bodyScope = closeParen + 1 < scopes.Length && tokens[closeParen + 1].Kind == ClassifiedTokenKind.LeftBrace
                    ? scopes[closeParen + 1] : scopes[index];
                CollectParameters(tokens, openParen + 1, closeParen, bodyScope, Add);
                continue;
            }

            // Typed variables and fields use the form "Type name". A class-body
            // declaration is a field; elsewhere it is a variable.
            if (index + 1 < tokens.Length && tokens[index + 1].Kind == ClassifiedTokenKind.Identifier &&
                IsTypeName(tokens[index].Text) && IsDeclarationTerminator(tokens, index + 2))
            {
                Add(index + 1, IsInClassBody(tokens, index) ? SushiSymbolKind.Field : SushiSymbolKind.Variable, declaredType: tokens[index].Text);
            }
        }

        CollectEnumValues(tokens, scopes, Add);
        return symbols;
    }

    private static void CollectParameters(ClassifiedToken[] tokens, int start, int end, Scope scope, Action<int, SushiSymbolKind, Scope?, string?, bool> add)
    {
        for (var index = start; index < end; index++)
        {
            if (tokens[index].Kind != ClassifiedTokenKind.Identifier) continue;
            var previous = index > start ? tokens[index - 1] : null;
            var next = index + 1 < end ? tokens[index + 1] : null;
            var name = previous?.Kind == ClassifiedTokenKind.Identifier && IsTypeName(previous.Text) ? index :
                (previous?.Kind is ClassifiedTokenKind.Comma or ClassifiedTokenKind.LeftParen || index == start) &&
                (next?.Kind is ClassifiedTokenKind.Comma or ClassifiedTokenKind.RightParen or ClassifiedTokenKind.Operator) ? index : -1;
            if (name >= 0) add(name, SushiSymbolKind.Parameter, scope, previous?.Kind == ClassifiedTokenKind.Identifier ? previous.Text : null, false);
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

    private static SushiSymbol? Resolve(ClassifiedToken token, int index, Scope scope, IEnumerable<SushiSymbol> symbols)
    {
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
        tokens[index].Kind is ClassifiedTokenKind.Semicolon or ClassifiedTokenKind.Operator or ClassifiedTokenKind.Comma;
    internal static bool IsTypeName(string name) => name is "string" or "int" or "float" or "bool" or "array" or "object" or "any" or "void" ||
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
}

internal enum SushiSymbolKind
{
    Namespace,
    Module,
    Class,
    Enum,
    EnumMember,
    Function,
    Method,
    Field,
    Parameter,
    Variable
}
