namespace Sushi.Transpilation;

using Sushi.Application;

public sealed class TranspileRequest
{
    public required string SourceText { get; init; }
    public required string SourcePath { get; init; }
    public required TargetLanguage TargetLanguage { get; init; }
    // Retained TargetLanguage keeps the public compiler API source-compatible.
    // New callers should provide the complete target profile.
    public TargetProfile? TargetProfile { get; init; }
}
