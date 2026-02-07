namespace Sushi.Transpilation;

public sealed class TranspileResult
{
    public bool Success { get; init; }
    public string? EmittedCode { get; init; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
}
