namespace Sushi.Transpilation;

public sealed class Diagnostic
{
    public string Code { get; }
    public string Message { get; }
    public DiagnosticSeverity Severity { get; }
    public SourceSpan Span { get; }

    public Diagnostic(string code, string message, DiagnosticSeverity severity, SourceSpan span)
    {
        Code = code;
        Message = message;
        Severity = severity;
        Span = span;
    }

    public static Diagnostic Error(string code, string message, SourceSpan span)
    {
        return new Diagnostic(code, message, DiagnosticSeverity.Error, span);
    }

    public static Diagnostic Warning(string code, string message, SourceSpan span)
    {
        return new Diagnostic(code, message, DiagnosticSeverity.Warning, span);
    }
}
