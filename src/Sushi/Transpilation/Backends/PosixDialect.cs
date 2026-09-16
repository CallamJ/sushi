namespace Sushi.Transpilation.Backends;

using Sushi.Application;

/// <summary>
/// Syntax and capability differences between shells in the POSIX-family
/// emitter.  The emitter owns the lowering strategy; the dialect only answers
/// which shell spelling and native features should be used.
/// </summary>
public sealed record PosixDialect(
    TargetLanguage Language,
    string Shebang,
    bool IsZsh,
    bool SupportsKshArrays,
    bool SupportsIndirectNameReferences,
    bool SupportsAssociativeArrays)
{
    public static PosixDialect Bash { get; } = new(
        TargetLanguage.Bash,
        "#!/usr/bin/env bash",
        IsZsh: false,
        SupportsKshArrays: false,
        SupportsIndirectNameReferences: true,
        SupportsAssociativeArrays: true);

    public static PosixDialect Zsh { get; } = new(
        TargetLanguage.Zsh,
        "#!/usr/bin/env zsh",
        IsZsh: true,
        SupportsKshArrays: true,
        SupportsIndirectNameReferences: true,
        SupportsAssociativeArrays: true);
}
