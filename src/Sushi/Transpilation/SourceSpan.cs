namespace Sushi.Transpilation;

public sealed class SourceSpan
{
    public string SourcePath { get; }
    public int Line { get; }
    public int Column { get; }

    public SourceSpan(string sourcePath, int line, int column)
    {
        SourcePath = sourcePath;
        Line = line;
        Column = column;
    }

    public static SourceSpan Unknown(string sourcePath)
    {
        return new SourceSpan(sourcePath, 1, 1);
    }
}
