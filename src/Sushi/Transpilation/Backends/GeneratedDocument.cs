namespace Sushi.Transpilation.Backends;

using System.Text;

/// <summary>
/// Target-neutral, indentation-aware output document.  Emitters build code through
/// this object instead of formatting a completed script after the fact.
/// </summary>
internal sealed class GeneratedDocument
{
    private readonly StringBuilder _content = new();

    public int Indent { get; set; }

    public void Clear()
    {
        _content.Clear();
        Indent = 0;
    }

    public void Line(string? text = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            _content.Append('\n');
            return;
        }

        _content.Append(' ', Indent * 4);
        _content.Append(text);
        _content.Append('\n');
    }

    public void BlankLine()
    {
        if (_content.Length == 0 || _content[^1] != '\n' ||
            (_content.Length > 1 && _content[^2] != '\n'))
            _content.Append('\n');
    }

    public void Comment(string text) => Line($"# {text}");

    public void Block(string header, Action body, string closing = "}")
    {
        Line(header);
        Indent++;
        body();
        Indent--;
        Line(closing);
    }

    /// <summary>
    /// Emits pre-authored stdlib helper source at the current indentation, removing
    /// only the template's common left margin.  It deliberately does not inspect or
    /// rewrite target syntax.
    /// </summary>
    public void Template(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var nonEmpty = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        var margin = nonEmpty.Count == 0 ? 0 : nonEmpty.Min(line => line.TakeWhile(char.IsWhiteSpace).Count());
        foreach (var line in lines)
        {
            var normalized = line.Length >= margin ? line[margin..] : line;
            Line(normalized);
        }
    }

    public override string ToString() => _content.ToString();
}
