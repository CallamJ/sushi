namespace Sushi.Transpilation.Backends;

using Sushi.Application;

public sealed class EmitContext
{
    private readonly List<Diagnostic> _diagnostics;

    public string SourcePath { get; }
    public TargetProfile TargetProfile { get; }

    public EmitContext(string sourcePath, List<Diagnostic> diagnostics, TargetProfile? targetProfile = null)
    {
        SourcePath = sourcePath;
        _diagnostics = diagnostics;
        TargetProfile = targetProfile ?? TargetProfile.Host();
    }

    public void Error(string code, string message, int line = 1, int column = 1)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, new SourceSpan(SourcePath, line, column)));
    }

    public string ErrorAndReturn(string code, string message, string fallback = "''", int line = 1, int column = 1)
    {
        Error(code, message, line, column);
        return fallback;
    }
}
