namespace Sushi.Build.SyntaxTree;

public enum TokenKind
{
    Whitespace,
    Word,
    String,
    Char,
    Comment,
    Symbol,
    EndOfFile
}