namespace Sushi.Application.Console;

using Sushi.Transpilation;

static class DiagnosticPrinter
{
    public static void Print(IEnumerable<Diagnostic> diagnostics, bool includeWarnings)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (!includeWarnings && diagnostic.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            var severity = diagnostic.Severity.ToString().ToUpperInvariant();
            var location = $"{diagnostic.Span.SourcePath}:{diagnostic.Span.Line}:{diagnostic.Span.Column}";
            System.Console.Error.WriteLine($"[{severity}] {diagnostic.Code} {location}: {diagnostic.Message}");
        }
    }
}
