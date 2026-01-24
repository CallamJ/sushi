namespace Sushi.Build.SyntaxTree;

/// <summary>
/// A token after lexical analysis (classification and interpolation parsing)
/// </summary>
public class ClassifiedToken
{
    public ClassifiedTokenKind Kind { get; }
    public string Text { get; }
    public object? Value { get; }  // Parsed value for literals
    public int Start { get; }
    public int Length => Text.Length;
    public int Line { get; }
    public int Column { get; }
    
    // For interpolated strings
    public InterpolatedStringPart[]? InterpolationParts { get; }

    public ClassifiedToken(
        ClassifiedTokenKind kind,
        string text,
        int start,
        int line,
        int column,
        object? value = null,
        InterpolatedStringPart[]? interpolationParts = null)
    {
        Kind = kind;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Start = start;
        Line = line;
        Column = column;
        Value = value;
        InterpolationParts = interpolationParts;
    }

    public bool Is(ClassifiedTokenKind kind) => Kind == kind;

    public bool IsOneOf(params ClassifiedTokenKind[] kinds)
    {
        foreach (var k in kinds)
        {
            if (Kind == k)
                return true;
        }
        return false;
    }

    public bool IsKeyword(string keyword)
    {
        return Kind == ClassifiedTokenKind.Keyword && Text == keyword;
    }

    public bool IsOperator(string op)
    {
        return Kind == ClassifiedTokenKind.Operator && Text == op;
    }

    public int End => Start + Text.Length;

    public override string ToString()
    {
        if (Value != null)
            return $"{Kind} \"{Text}\" = {Value} @ {Line}:{Column}";
        return $"{Kind} \"{Text}\" @ {Line}:{Column}";
    }
}

/// <summary>
/// Represents a part of an interpolated string (literal or expression)
/// </summary>
public class InterpolatedStringPart
{
    public bool IsLiteral { get; }
    public string Content { get; }

    public InterpolatedStringPart(bool isLiteral, string content)
    {
        IsLiteral = isLiteral;
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public static InterpolatedStringPart Literal(string text) => new(true, text);
    public static InterpolatedStringPart Expression(string expr) => new(false, expr);
}