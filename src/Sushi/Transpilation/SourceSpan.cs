namespace Sushi.Transpilation;

public sealed class SourceSpan
{
    public string SourcePath { get; }
    public int Line { get; }
    public int Column { get; }
    /// <summary>Zero-based start offset when it is available; -1 for legacy spans.</summary>
    public int StartOffset { get; }
    /// <summary>Exclusive source offset when it is available; -1 for legacy spans.</summary>
    public int EndOffset { get; }

    public SourceSpan(string sourcePath, int line, int column, int startOffset = -1, int endOffset = -1)
    {
        SourcePath = sourcePath;
        Line = line;
        Column = column;
        StartOffset = startOffset;
        EndOffset = endOffset;
    }

    public static SourceSpan Unknown(string sourcePath)
    {
        return new SourceSpan(sourcePath, 1, 1);
    }
}
