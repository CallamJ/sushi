namespace Sushi.Build;

using System;
using System.Collections.Generic;

using Sushi.Build.SyntaxTree;

public sealed class Tokenizer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    // Symbols must be sorted longest-first
    private static readonly string[] Symbols =
    [
        "==", "!=", "<=", ">=", "&&", "||", "++", "--",
        "+=", "-=", "*=", "/=", "->", "...",
        "..", "+", "-", "*", "/", "%", "=",
        "<", ">", "!", "&", "|",
        "(", ")", "{", "}", "[", "]",
        ";", ",", ".", ":", "@", "$", "?"
    ];

    public Tokenizer(string source)
    {
        _src = source ?? throw new ArgumentNullException(nameof(source));
    }

    public IEnumerable<UnclassifiedToken> Tokenize()
    {
        while (!IsEof())
        {
            char c = Peek();

            if (char.IsWhiteSpace(c))
                yield return ReadWhitespace();

            else if (c == '"' )
                yield return ReadString();

            else if (c == '\'')
                yield return ReadChar();

            else if (c == '/' && Peek(1) == '/')
                yield return ReadLineComment();

            else if (c == '/' && Peek(1) == '*')
                yield return ReadBlockComment();

            else if (char.IsDigit(c))
                yield return ReadNumber();

            else if (IsSymbolStart(c))
                yield return ReadSymbol();

            else
                yield return ReadWord();
        }
    }

    /* ───────────── Readers ───────────── */

    private UnclassifiedToken ReadWhitespace()
    {
        return ReadWhile(TokenKind.Whitespace, char.IsWhiteSpace);
    }

    private UnclassifiedToken ReadWord()
    {
        return ReadWhile(TokenKind.Word, c =>
            !char.IsWhiteSpace(c) &&
            !IsSymbolStart(c) &&
            c != '"' &&
            c != '\'' );
    }

    private UnclassifiedToken ReadString()
    {
        return ReadQuoted(TokenKind.String, '"');
    }

    private UnclassifiedToken ReadChar()
    {
        return ReadQuoted(TokenKind.Char, '\'');
    }

    private UnclassifiedToken ReadNumber()
    {
        int start = Mark(out int line, out int col);
        
        // Read digits
        while (!IsEof() && char.IsDigit(Peek()))
            Advance();
        
        // Check for decimal point followed by digits
        if (!IsEof() && Peek() == '.' && Peek(1) != '.')
        {
            // Make sure next char after . is a digit (not another dot or other symbol)
            if (!IsEof() && char.IsDigit(Peek(1)))
            {
                Advance(); // consume '.'
                
                // Read fractional part
                while (!IsEof() && char.IsDigit(Peek()))
                    Advance();
            }
        }
        
        return Make(TokenKind.Word, start, line, col);
    }

    private UnclassifiedToken ReadLineComment()
    {
        int start = Mark(out int line, out int col);
        Advance(2); // //

        while (!IsEof() && Peek() != '\n')
            Advance();

        return Make(TokenKind.Comment, start, line, col);
    }

    private UnclassifiedToken ReadBlockComment()
    {
        int start = Mark(out int line, out int col);
        Advance(2); // /*

        while (!IsEof())
        {
            if (Peek() == '*' && Peek(1) == '/')
            {
                Advance(2);
                break;
            }
            Advance();
        }

        return Make(TokenKind.Comment, start, line, col);
    }

    private UnclassifiedToken ReadSymbol()
    {
        int start = Mark(out int line, out int col);

        foreach (var sym in Symbols)
        {
            if (Matches(sym))
            {
                Advance(sym.Length);
                return Make(TokenKind.Symbol, start, line, col);
            }
        }

        throw new Exception($"Unknown symbol at {_line}:{_col}");
    }

    /* ───────────── Core helpers ───────────── */

    private UnclassifiedToken ReadWhile(TokenKind kind, Func<char, bool> predicate)
    {
        int start = Mark(out int line, out int col);

        while (!IsEof() && predicate(Peek()))
            Advance();

        return Make(kind, start, line, col);
    }

    private UnclassifiedToken ReadQuoted(TokenKind kind, char quote)
    {
        int start = Mark(out int line, out int col);
        Advance(); // opening quote

        while (!IsEof())
        {
            if (Peek() == '\\')
            {
                Advance(2); // escape
                continue;
            }

            if (Peek() == quote)
            {
                Advance();
                break;
            }

            Advance();
        }

        return Make(kind, start, line, col);
    }

    private UnclassifiedToken Make(TokenKind kind, int start, int line, int col)
    {
        string text = _src.Substring(start, _pos - start);
        return new UnclassifiedToken(kind, text, start, line, col);
    }

    private int Mark(out int line, out int col)
    {
        line = _line;
        col = _col;
        return _pos;
    }

    private bool Matches(string s)
    {
        if (_pos + s.Length > _src.Length)
            return false;

        return !s.Where((t, i) => _src[_pos + i] != t).Any();
    }

    private bool IsSymbolStart(char c)
    {
        foreach (var s in Symbols)
        {
            if (s[0] == c)
                return true;
        }
        return false;
    }

    private char Peek(int offset = 0)
    {
        return _src[_pos + offset];
    }

    private bool IsEof() => _pos >= _src.Length;

    private void Advance(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            if (_src[_pos] == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
            _pos++;
        }
    }
}