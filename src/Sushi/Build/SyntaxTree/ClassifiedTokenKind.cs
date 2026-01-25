namespace Sushi.Build.SyntaxTree;

/// <summary>
/// Token types after lexical analysis (classification)
/// </summary>
public enum ClassifiedTokenKind
{
    // Literals
    Identifier,
    Keyword,
    IntegerLiteral,
    FloatLiteral,
    StringLiteral,
    InterpolatedString,  // String with $(..  ) expressions
    CharLiteral,
    
    // Operators
    Operator,
    
    // Punctuation
    LeftParen,      // (
    RightParen,     // )
    LeftBrace,      // {
    RightBrace,     // }
    LeftBracket,    // [
    RightBracket,   // ]
    Semicolon,      // ;
    Comma,          // ,
    Dot,            // .
    Colon,          // :
    Pipe,           // |
    At,             // @ (pipe placeholder)
    Question,       // ? (ternary operator)
    Range,          // .. (exclusive range)
    RangeInclusive, // ... (inclusive range, also used for varargs)
    
    // Special
    Comment,
    Whitespace,
    EndOfFile
}