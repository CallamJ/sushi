namespace Sushi.Tests.Transpilation;

using Sushi.Application;
using Sushi.Transpilation;
using Xunit;

public class TranspilerTests
{
    [Fact]
    public void Transpile_Bash_BasicScript_Succeeds()
    {
        const string source = """
            var x = 1
            println(x)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "basic.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("x=1", result.EmittedCode);
        Assert.Contains("echo", result.EmittedCode);
    }

    [Fact]
    public void Transpile_PowerShell_BasicScript_Succeeds()
    {
        const string source = """
            var x = 1
            println(x)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "basic.sushi",
            TargetLanguage = TargetLanguage.Powershell7
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("$x = 1", result.EmittedCode);
        Assert.Contains("Write-Host", result.EmittedCode);
    }

    [Fact]
    public void Transpile_UnsupportedFeature_FailsWithDiagnostic()
    {
        const string source = """
            class Person {
                string name
            }
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "unsupported.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "SUSHI1001");
    }
}
