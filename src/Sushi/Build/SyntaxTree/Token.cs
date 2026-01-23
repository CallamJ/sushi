namespace Sushi.Build.SyntaxTree;

public class Token
{

}

public enum TokenKind
{
    Word,        // identifiers, numbers, keywords (undecided)
    Symbol,      // operators, punctuation
    String,      // "text"
    Char,        // 'c'
    Whitespace,  // spaces, tabs, newlines
    Comment      // // or /* */
}
