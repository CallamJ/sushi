namespace Sushi.Build.SyntaxTree;

public sealed class UnclassifiedToken
{
    public TokenKind Kind { get; }
    public string Text { get; }
    public int Start { get; }
    public int Length => Text.Length;
    public int Line { get; }
    public int Column { get; }

    public UnclassifiedToken(
        TokenKind kind,
        string text,
        int start,
        int line,
        int column)
    {
        Kind = kind;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Start = start;
        Line = line;
        Column = column;
    }

    /* ───────────── Structural helpers (no language rules) ───────────── */

    public bool Is(TokenKind kind) => Kind == kind;

    public bool IsOneOf(params TokenKind[] kinds)
    {
        foreach (var k in kinds)
        {
            if (Kind == k)
                return true;
        }
        return false;
    }

    public bool IsSymbol(string value)
    {
        return Kind == TokenKind.Symbol && Text == value;
    }

    public bool IsWhitespace() => Kind == TokenKind.Whitespace;
    public bool IsIdentifierLike() => Kind == TokenKind.Word;
    public bool IsStringLiteral() => Kind == TokenKind.String;
    public bool IsCharLiteral() => Kind == TokenKind.Char;
    public bool IsComment() => Kind == TokenKind.Comment;

    /* ───────────── Source helpers ───────────── */

    public int End => Start + Text.Length;

    public string Slice(string source)
    {
        return source.Substring(Start, Text.Length);
    }

    public override string ToString()
    {
        return $"{Kind} \"{Text}\" @ {Line}:{Column}";
    }
}
