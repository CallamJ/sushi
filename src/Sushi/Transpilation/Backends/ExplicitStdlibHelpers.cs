namespace Sushi.Transpilation.Backends;

using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

/// <summary>
/// The sole admission point for generated stdlib helper declarations. A helper
/// is emitted only when its owning API is both explicitly imported and used.
/// </summary>
internal static class ExplicitStdlibHelpers
{
    public static bool RequiresFsGlob(IrProgram program) =>
        EmissionCapabilityAnalyzer.UsesFsGlob(program) &&
        program.Statements.OfType<IrStandardLibraryImportStatement>().Any(IsFsGlobImport);

    private static bool IsFsGlobImport(IrStandardLibraryImportStatement import) =>
        (import.Module.Equals("std.fs", StringComparison.Ordinal) &&
         (import.Members.Count == 0 || import.Members.Contains("glob", StringComparer.Ordinal))) ||
        (import.Module.Equals("std.fs.glob", StringComparison.Ordinal) &&
         (import.Members.Count == 0 || import.Members.Contains("glob", StringComparer.Ordinal)));
}
