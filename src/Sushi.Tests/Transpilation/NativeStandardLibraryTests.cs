namespace Sushi.Tests.Transpilation;

using Sushi.Application;
using Sushi.Transpilation;
using Xunit;

public sealed class NativeStandardLibraryTests
{
    [Fact]
    public void ArchiveZip_LowersDirectlyToBashCommand()
    {
        var result = Transpile("std.archive.zip(\"dist\", \"dist.zip\")", TargetLanguage.Bash, TargetPlatform.Linux);

        Assert.True(result.Success);
        Assert.Contains("zip -qr", result.EmittedCode);
        Assert.DoesNotContain("__sushi_process", result.EmittedCode);
        Assert.DoesNotContain("__sushi_value", result.EmittedCode);
    }

    [Fact]
    public void ArchiveZip_LowersDirectlyToPowerShellCmdlet()
    {
        var result = Transpile("std.archive.zip(\"dist\", \"dist.zip\")", TargetLanguage.Powershell51, TargetPlatform.Windows);

        Assert.True(result.Success);
        Assert.Contains("Compress-Archive", result.EmittedCode);
        Assert.DoesNotContain("__sushi_process", result.EmittedCode);
    }

    [Fact]
    public void FsPredicate_LowersToNativeFileTest()
    {
        var result = Transpile("println(std.fs.isDirectory(\"dist\"))", TargetLanguage.Bash, TargetPlatform.Linux);

        Assert.True(result.Success);
        Assert.Contains("[[ -d", result.EmittedCode);
    }

    [Fact]
    public void HttpDownload_LowersToTheTargetHttpTool()
    {
        var bash = Transpile("std.http.download(\"https://example.test/a\", \"a.txt\")", TargetLanguage.Bash, TargetPlatform.Linux);
        var powershell = Transpile("std.http.download(\"https://example.test/a\", \"a.txt\")", TargetLanguage.Powershell51, TargetPlatform.Windows);

        Assert.True(bash.Success);
        Assert.True(powershell.Success);
        Assert.Contains("curl -fsSL", bash.EmittedCode);
        Assert.Contains("Invoke-WebRequest", powershell.EmittedCode);
        Assert.Contains("-UseBasicParsing", powershell.EmittedCode);
    }

    [Fact]
    public void PathEnvironmentProcessAndConsoleApis_LowerNatively()
    {
        const string source = "println(std.path.extension(\"archive.tar.gz\"))\nstd.env.unset(\"TEMP_VALUE\")\nstd.process.sleep(5)\nstd.console.error(\"failed\")";
        var bash = Transpile(source, TargetLanguage.Bash, TargetPlatform.Linux);
        var powershell = Transpile(source, TargetLanguage.Powershell51, TargetPlatform.Windows);

        Assert.True(bash.Success);
        Assert.Contains("basename --", bash.EmittedCode);
        Assert.Contains("unset", bash.EmittedCode);
        Assert.Contains("sleep", bash.EmittedCode);
        Assert.Contains(">&2", bash.EmittedCode);
        Assert.True(powershell.Success);
        Assert.Contains("[IO.Path]::GetExtension", powershell.EmittedCode);
        Assert.Contains("Remove-Item", powershell.EmittedCode);
        Assert.Contains("Start-Sleep", powershell.EmittedCode);
        Assert.Contains("[Console]::Error", powershell.EmittedCode);
    }

    [Fact]
    public void PowerShellOutput_UsesPowerShell51CompatibleRuntimeApis()
    {
        const string source = "use std.fs\nuse std.process\nuse std.http\nvar files = std.fs.query(\"tmp\").recursive().matching(\"*.txt\").files()\nvar result = std.process.run(\"tool\", [], timeoutMs: 1, allowFailure: true)\nvar response = std.http.get(\"https://example.test\")";
        var result = Transpile(source, TargetLanguage.Powershell51, TargetPlatform.Windows);

        Assert.True(result.Success);
        Assert.Contains(".Kill()", result.EmittedCode);
        Assert.Contains("-UseBasicParsing", result.EmittedCode);
        Assert.Contains("ResponseUri", result.EmittedCode);
        Assert.Contains("SecurityProtocol", result.EmittedCode);
        Assert.DoesNotContain("GetRelativePath", result.EmittedCode);
        Assert.DoesNotContain("Kill($true)", result.EmittedCode);
        Assert.DoesNotContain("__sushi_process_", result.EmittedCode);
        Assert.DoesNotContain("__sushi_http_", result.EmittedCode);
    }

    [Theory]
    [InlineData(TargetLanguage.Bash, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Zsh, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Powershell51, TargetPlatform.Windows)]
    public void FileQuery_IsLoweredWithoutGlobHelpers(TargetLanguage target, TargetPlatform platform)
    {
        var withoutImport = Transpile("var files = std.fs.glob(\"*.sushi\")", target, platform);
        Assert.False(withoutImport.Success);
        Assert.DoesNotContain("__sushi_fs_glob", withoutImport.EmittedCode);

        var query = Transpile("use std.fs as fs\nvar files = fs.query().files()", target, platform);
        Assert.True(query.Success);
        Assert.DoesNotContain("__sushi_fs_glob", query.EmittedCode);
    }

    [Theory]
    [InlineData(TargetLanguage.Bash, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Zsh, TargetPlatform.Linux)]
    [InlineData(TargetLanguage.Powershell51, TargetPlatform.Windows)]
    public void UnsupportedAggregateIntrinsicShape_IsDiagnosedInsteadOfCallingALegacyHelper(TargetLanguage target, TargetPlatform platform)
    {
        var result = Transpile("use std.process\nprintln(process.run(\"printf\", [\"ok\"], allowFailure: true))", target, platform);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1030");
        Assert.DoesNotContain("__sushi_process_", result.EmittedCode);
    }

    [Fact]
    public void PipeExpression_RewritesToTheTargetCallWithoutRuntimeSupport()
    {
        var result = Transpile("println(\"  hello  \" | std.string.trim())", TargetLanguage.Bash, TargetPlatform.Linux);

        Assert.True(result.Success);
        Assert.Contains("hello", result.EmittedCode);
        Assert.DoesNotContain("PipeExpressionNode", result.EmittedCode);
    }

    [Fact]
    public void InterpolatedString_LowersItsEmbeddedExpression()
    {
        var bash = Transpile("var name = \"Sushi\"\nprintln(\"Hello $(name.upper())!\")", TargetLanguage.Bash, TargetPlatform.Linux);
        var powershell = Transpile("var name = \"Sushi\"\nprintln(\"Hello $(name.upper())!\")", TargetLanguage.Powershell51, TargetPlatform.Windows);

        Assert.True(bash.Success);
        Assert.Contains("Hello", bash.EmittedCode);
        Assert.Contains("Sushi", bash.EmittedCode);
        Assert.True(powershell.Success);
        Assert.Contains("ToUpperInvariant", powershell.EmittedCode);
    }

    private static TranspileResult Transpile(string source, TargetLanguage shell, TargetPlatform platform) =>
        new Transpiler().Transpile(new TranspileRequest
        {
            SourcePath = "stdlib.sushi",
            SourceText = source,
            TargetLanguage = shell,
            TargetProfile = new TargetProfile(shell, platform)
        });
}
