namespace Sushi.Build;

using System;
using System.Collections.Generic;
using System.Text;

using Sushi.Build.SyntaxTree;

public sealed class Tokenizer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    // Symbols must be sorted longest-first
    private static readonly string[] Symbols =
    {
        "==", "!=", "<=", ">=", "&&", "||", "++", "--",
        "+=", "-=", "*=", "/=", "->",
        "+", "-", "*", "/", "%", "=",
        "<", ">", "!", "&", "|",
        "(", ")", "{", "}", "[", "]",
        ";", ",", ".", ":"
    };

    public Tokenizer(string source)
    {
        _src = source ?? throw new ArgumentNullException(nameof(source));
    }

    public IEnumerable<UnclassifiedToken> Tokenize()
    {
        while (!IsEOF())
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

    private UnclassifiedToken ReadLineComment()
    {
        int start = Mark(out int line, out int col);
        Advance(2); // //

        while (!IsEOF() && Peek() != '\n')
            Advance();

        return Make(TokenKind.Comment, start, line, col);
    }

    private UnclassifiedToken ReadBlockComment()
    {
        int start = Mark(out int line, out int col);
        Advance(2); // /*

        while (!IsEOF())
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

        while (!IsEOF() && predicate(Peek()))
            Advance();

        return Make(kind, start, line, col);
    }

    private UnclassifiedToken ReadQuoted(TokenKind kind, char quote)
    {
        int start = Mark(out int line, out int col);
        Advance(); // opening quote

        while (!IsEOF())
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

        for (int i = 0; i < s.Length; i++)
        {
            if (_src[_pos + i] != s[i])
                return false;
        }
        return true;
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

    private bool IsEOF() => _pos >= _src.Length;

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

    public enum TokenKind
    {
        Word,        // identifiers, numbers, keywords (undecided)
        Symbol,      // operators, punctuation
        String,      // "text"
        Char,        // 'c'
        Whitespace,  // spaces, tabs, newlines
        Comment      // // or /* */
    }

}
