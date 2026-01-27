namespace Sushi.Build;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Sushi.Build.SyntaxTree;

/// <summary>
/// Lexer - Converts unclassified tokens from the Tokenizer into classified tokens
/// Handles:
/// - Keyword identification
/// - Number parsing
/// - String interpolation
/// - Implicit semicolon insertion
/// - Token classification
/// </summary>
public class Lexer
{
    private readonly List<UnclassifiedToken> _tokens;
    private int _position;

    private static readonly HashSet<string> Keywords = new()
    {
        "box", "use", "class", "new", "return", "this",
        "if", "else", "while", "for", "break", "continue",
        "true", "false", "null",
        "var", "switch", "case", "default", "also",
        "do", "step", "enum", "in"
    };

    // Operators that should be classified as operators
    private static readonly HashSet<string> Operators = new()
    {
        "+", "-", "*", "/", "%", "=",
        "==", "!=", "<", ">", "<=", ">=",
        "&&", "||", "!",
        "++", "--", "+=", "-=", "*=", "/=",
        "&", "->"
    };

    public Lexer(IEnumerable<UnclassifiedToken> tokens)
    {
        // Remove comments BEFORE any processing
        _tokens = tokens.Where(t => t.Kind != TokenKind.Comment).ToList();
        _position = 0;
    }

    public IEnumerable<ClassifiedToken> Lex()
    {
        while (!IsEof())
        {
            var token = Current();

            switch (token.Kind)
            {
                case TokenKind.Whitespace:
                    // Check if this whitespace contains newline and should insert semicolon
                    if (ShouldInsertSemicolon(token))
                    {
                        yield return new ClassifiedToken(
                            ClassifiedTokenKind.Semicolon,
                            ";",
                            token.Start,
                            token.Line,
                            token.Column
                        );
                    }
                    // Skip whitespace (unless you want to preserve it)
                    Advance();
                    break;

                case TokenKind.Comment:
                    // Comments should have been filtered in constructor
                    // This case should never be hit, but skip just in case
                    Advance();
                    break;

                case TokenKind.Word:
                    yield return ClassifyWord(token);
                    Advance();
                    break;

                case TokenKind.String:
                    yield return ClassifyString(token);
                    Advance();
                    break;

                case TokenKind.Char:
                    yield return ClassifyChar(token);
                    Advance();
                    break;

                case TokenKind.Symbol:
                    yield return ClassifySymbol(token);
                    Advance();
                    break;

                default:
                    throw new Exception($"Unexpected token kind: {token.Kind}");
            }
        }

        // Emit EOF token
        var last = _tokens.LastOrDefault();
        if (last != null)
        {
            yield return new ClassifiedToken(
                ClassifiedTokenKind.EndOfFile,
                "",
                last.End,
                last.Line,
                last.Column
            );
        }
    }

    /* ───────────── Classification Methods ───────────── */

    private ClassifiedToken ClassifyWord(UnclassifiedToken token)
    {
        string text = token.Text;

        // Check if it's a keyword
        if (Keywords.Contains(text))
        {
            return new ClassifiedToken(
                ClassifiedTokenKind.Keyword,
                text,
                token.Start,
                token.Line,
                token.Column
            );
        }

        // Check if it's a number
        if (TryParseNumber(text, out var numberValue, out bool isFloat))
        {
            return new ClassifiedToken(
                isFloat ? ClassifiedTokenKind.FloatLiteral : ClassifiedTokenKind.IntegerLiteral,
                text,
                token.Start,
                token.Line,
                token.Column,
                numberValue
            );
        }

        // Otherwise it's an identifier
        return new ClassifiedToken(
            ClassifiedTokenKind.Identifier,
            text,
            token.Start,
            token.Line,
            token.Column
        );
    }

    private ClassifiedToken ClassifyString(UnclassifiedToken token)
    {
        string text = token.Text;
        
        // Check if string contains interpolation $(...)
        if (text.Contains("$("))
        {
            var parts = ParseStringInterpolation(text);
            return new ClassifiedToken(
                ClassifiedTokenKind.InterpolatedString,
                text,
                token.Start,
                token.Line,
                token.Column,
                null,
                parts
            );
        }

        // Regular string - extract value (remove quotes and handle escapes)
        string value = UnescapeString(text.Substring(1, text.Length - 2));
        
        return new ClassifiedToken(
            ClassifiedTokenKind.StringLiteral,
            text,
            token.Start,
            token.Line,
            token.Column,
            value
        );
    }

    private ClassifiedToken ClassifyChar(UnclassifiedToken token)
    {
        string text = token.Text;
        
        // Extract character value (remove quotes and handle escapes)
        string inner = text.Substring(1, text.Length - 2);
        char value = UnescapeChar(inner);
        
        return new ClassifiedToken(
            ClassifiedTokenKind.CharLiteral,
            text,
            token.Start,
            token.Line,
            token.Column,
            value
        );
    }

    private ClassifiedToken ClassifySymbol(UnclassifiedToken token)
    {
        string text = token.Text;

        // Map symbols to specific token kinds
        ClassifiedTokenKind kind = text switch
        {
            "(" => ClassifiedTokenKind.LeftParen,
            ")" => ClassifiedTokenKind.RightParen,
            "{" => ClassifiedTokenKind.LeftBrace,
            "}" => ClassifiedTokenKind.RightBrace,
            "[" => ClassifiedTokenKind.LeftBracket,
            "]" => ClassifiedTokenKind.RightBracket,
            ";" => ClassifiedTokenKind.Semicolon,
            "," => ClassifiedTokenKind.Comma,
            "." => ClassifiedTokenKind.Dot,
            ":" => ClassifiedTokenKind.Colon,
            "|" => ClassifiedTokenKind.Pipe,
            "@" => ClassifiedTokenKind.At,
            "?" => ClassifiedTokenKind.Question,
            ".." => ClassifiedTokenKind.Range,
            "..." => ClassifiedTokenKind.RangeInclusive,
            _ => Operators.Contains(text) ? ClassifiedTokenKind.Operator : ClassifiedTokenKind.Operator
        };

        return new ClassifiedToken(
            kind,
            text,
            token.Start,
            token.Line,
            token.Column
        );
    }

    /* ───────────── String Interpolation Parsing ───────────── */

    private InterpolatedStringPart[] ParseStringInterpolation(string text)
    {
        var parts = new List<InterpolatedStringPart>();
        
        // Remove outer quotes
        string content = text.Substring(1, text.Length - 2);
        
        int pos = 0;
        var literalBuilder = new StringBuilder();

        while (pos < content.Length)
        {
            // Check for escaped $
            if (pos < content.Length - 1 && content[pos] == '\\' && content[pos + 1] == '$')
            {
                literalBuilder.Append('$');
                pos += 2;
                continue;
            }

            // Check for interpolation start
            if (pos < content.Length - 1 && content[pos] == '$' && content[pos + 1] == '(')
            {
                // Save any literal text accumulated so far
                if (literalBuilder.Length > 0)
                {
                    parts.Add(InterpolatedStringPart.Literal(literalBuilder.ToString()));
                    literalBuilder.Clear();
                }

                // Find matching closing paren
                int start = pos + 2;
                int depth = 1;
                int end = start;
                
                while (end < content.Length && depth > 0)
                {
                    if (content[end] == '(')
                        depth++;
                    else if (content[end] == ')')
                        depth--;
                    
                    if (depth > 0)
                        end++;
                }

                // Extract expression
                string expr = content.Substring(start, end - start);
                parts.Add(InterpolatedStringPart.Expression(expr));
                
                pos = end + 1;
            }
            else
            {
                // Regular character
                if (content[pos] == '\\' && pos < content.Length - 1)
                {
                    // Handle escape sequences
                    literalBuilder.Append(UnescapeCharacter(content[pos + 1]));
                    pos += 2;
                }
                else
                {
                    literalBuilder.Append(content[pos]);
                    pos++;
                }
            }
        }

        // Add any remaining literal text
        if (literalBuilder.Length > 0)
        {
            parts.Add(InterpolatedStringPart.Literal(literalBuilder.ToString()));
        }

        return parts.ToArray();
    }

    /* ───────────── Implicit Semicolon Insertion ───────────── */

    private bool ShouldInsertSemicolon(UnclassifiedToken whitespaceToken)
    {
        // Only insert semicolon if whitespace contains newline
        if (!whitespaceToken.Text.Contains('\n'))
            return false;

        // Don't insert if we're at the beginning or end
        if (_position == 0 || _position >= _tokens.Count - 1)
            return false;

        // Find the previous non-whitespace token
        UnclassifiedToken? prev = null;
        for (int i = _position - 1; i >= 0; i--)
        {
            if (_tokens[i].Kind != TokenKind.Whitespace)
            {
                prev = _tokens[i];
                break;
            }
        }
        
        if (prev == null)
            return false;
        
        // Find the next non-whitespace token
        UnclassifiedToken? next = null;
        for (int i = _position + 1; i < _tokens.Count; i++)
        {
            if (_tokens[i].Kind != TokenKind.Whitespace)
            {
                next = _tokens[i];
                break;
            }
        }
        
        if (next == null)
            return false;
        
        // Don't insert semicolon if nothing else is on the same line as prev token
        if(prev.Line != whitespaceToken.Line)
            return false;

        // Don't insert after opening braces/parens or before closing ones
        if (prev.IsSymbol("{") || prev.IsSymbol("(") || prev.IsSymbol("["))
            return false;
        
        if (next.IsSymbol("}") || next.IsSymbol(")") || next.IsSymbol("]"))
            return false;

        // Don't insert if previous token is already a semicolon
        if (prev.IsSymbol(";"))
            return false;

        // Don't insert if previous is an operator (statement continues)
        if (prev.IsSymbol("+") || prev.IsSymbol("-") || prev.IsSymbol("*") || 
            prev.IsSymbol("/") || prev.IsSymbol("=") || prev.IsSymbol("|") ||
            prev.IsSymbol(",") || prev.IsSymbol(".") || prev.IsSymbol(":") ||
            prev.IsSymbol("?") || prev.IsSymbol("&&") || prev.IsSymbol("||"))
            return false;

        // Don't insert if next line starts with an operator continuation
        if (next.IsSymbol("|") || next.IsSymbol(".") || next.IsSymbol(",") ||
            next.IsSymbol(":"))
            return false;

        // Insert semicolon after statements that should end
        return true;
    }

    /* ───────────── Helper Methods ───────────── */

    private bool TryParseNumber(string text, out object value, out bool isFloat)
    {
        value = null!;
        isFloat = false;

        // Try integer first
        if (int.TryParse(text, out int intValue))
        {
            value = intValue;
            return true;
        }

        // Try long
        if (long.TryParse(text, out long longValue))
        {
            value = longValue;
            return true;
        }

        // Try float
        if (double.TryParse(text, out double doubleValue))
        {
            value = doubleValue;
            isFloat = true;
            return true;
        }

        return false;
    }

    private string UnescapeString(string text)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i < text.Length - 1)
            {
                sb.Append(UnescapeCharacter(text[i + 1]));
                i++;
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    private char UnescapeChar(string text)
    {
        if (text.Length == 1)
            return text[0];
        
        if (text.Length == 2 && text[0] == '\\')
            return UnescapeCharacter(text[1]);
        
        throw new Exception($"Invalid character literal: '{text}'");
    }

    private char UnescapeCharacter(char c)
    {
        return c switch
        {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            '0' => '\0',
            _ => c
        };
    }

    private UnclassifiedToken Current()
    {
        if (_position >= _tokens.Count)
            throw new InvalidOperationException("No more tokens");
        return _tokens[_position];
    }

    private void Advance()
    {
        _position++;
    }

    private bool IsEof()
    {
        return _position >= _tokens.Count;
    }
}