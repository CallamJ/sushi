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
        Assert.Contains("printf '%s\\n'", result.EmittedCode);
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

    [Fact]
    public void Transpile_Bash_StdIntrinsicScript_Succeeds()
    {
        const string source = """
            var cwd = std.os.cwd()
            println(cwd)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "intrinsic.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("pwd", result.EmittedCode);
    }

    [Fact]
    public void Transpile_PowerShell_StdIntrinsicScript_Succeeds()
    {
        const string source = """
            var content = std.io.readText("a.txt")
            print(content)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "intrinsic.sushi",
            TargetLanguage = TargetLanguage.Powershell7
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("Get-Content -Raw -LiteralPath", result.EmittedCode);
    }

    [Fact]
    public void Transpile_Milestone3Apis_Succeeds()
    {
        const string source = """
            var payload = { hello: "world" }
            var text = std.json.stringify(payload)
            var parsed = std.json.parse(text)
            var files = std.fs.glob("*.sushi")
            var response = std.http.get("https://example.com")
            var result = std.process.run("pwsh", ["-NoProfile", "-Command", "Write-Output hi"], allowFailure: true)
            println(parsed.hello)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m3.sushi",
            TargetLanguage = TargetLanguage.Powershell7
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("__sushi_json_stringify", result.EmittedCode);
        Assert.Contains("__sushi_http_get", result.EmittedCode);
        Assert.Contains("__sushi_process_run", result.EmittedCode);
    }
}
