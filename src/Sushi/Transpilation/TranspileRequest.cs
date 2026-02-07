namespace Sushi.Transpilation;

using Sushi.Application;

public sealed class TranspileRequest
{
    public required string SourceText { get; init; }
    public required string SourcePath { get; init; }
    public required TargetLanguage TargetLanguage { get; init; }
}
