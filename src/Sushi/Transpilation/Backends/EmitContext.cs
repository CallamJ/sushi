namespace Sushi.Transpilation.Backends;

public sealed class EmitContext
{
    private readonly List<Diagnostic> _diagnostics;

    public string SourcePath { get; }

    public EmitContext(string sourcePath, List<Diagnostic> diagnostics)
    {
        SourcePath = sourcePath;
        _diagnostics = diagnostics;
    }

    public void Error(string code, string message, int line = 1, int column = 1)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, new SourceSpan(SourcePath, line, column)));
    }
}
