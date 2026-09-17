namespace Sushi.Tests.Transpilation;

using System.Linq;
using Sushi.Application;
using Sushi.Transpilation;
using Xunit;

public sealed class FileQueryTests
{
    [Theory]
    [InlineData(TargetLanguage.Bash, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Zsh, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Powershell51, TargetPlatform.Windows)]
    public void ReusableDynamicQuery_LowersWithoutAGlobRuntime(TargetLanguage target, TargetPlatform platform)
    {
        const string source = """
            use std.fs as fs
            var root = "src"
            var extension = "cs"
            var baseQuery = fs.query(root).recursive().includingHidden()
            var sourceQuery = baseQuery.matching("*." + extension).excluding("*Test.cs")
            string[] files = sourceQuery.files()
            string[] directories = baseQuery.directories()
            """;

        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "query.sushi",
            TargetLanguage = target,
            TargetProfile = new TargetProfile(target, platform)
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.DoesNotContain("__sushi_fs_glob", result.EmittedCode);
        Assert.DoesNotContain("__sushi_glob_regex", result.EmittedCode);
        Assert.Contains(target == TargetLanguage.Powershell51 ? "Get-ChildItem" : "find -P", result.EmittedCode);
    }

    [Fact]
    public void LegacyGlob_IsNotAPublicStandardLibraryMember()
    {
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = "use std.fs.glob\nvar files = glob(\"*.sushi\")",
            SourcePath = "legacy-glob.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.DoesNotContain("__sushi_fs_glob", result.EmittedCode);
    }

    [Theory]
    [InlineData(TargetLanguage.Bash, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Zsh, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Powershell51, TargetPlatform.Windows)]
    public void FileQuery_CanCrossATypedFunctionBoundary(TargetLanguage target, TargetPlatform platform)
    {
        const string source = """
            use std.fs as fs
            string[] list(FileQuery input) {
                return input.files()
            }
            var sourceQuery = fs.query("src").recursive().matching("*.cs")
            string[] files = list(sourceQuery)
            """;
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "query-function.sushi",
            TargetLanguage = target,
            TargetProfile = new TargetProfile(target, platform)
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.DoesNotContain("__sushi_fs_glob", result.EmittedCode);
    }
}
