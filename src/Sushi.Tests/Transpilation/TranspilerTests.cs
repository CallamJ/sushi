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
    public void Transpile_Zsh_BasicScript_Succeeds()
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
            TargetLanguage = TargetLanguage.Zsh
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("#!/usr/bin/env zsh", result.EmittedCode);
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

    [Fact]
    public void Transpile_UserFunction_DefaultAndNamedArguments_Succeeds()
    {
        const string source = """
            add(a, b = 5) {
                return a + b
            }

            var x = add(2)
            var y = add(b: 7, a: 3)
            println(x)
            println(y)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase2_defaults.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("add 2 5", result.EmittedCode);
        Assert.Contains("add 3 7", result.EmittedCode);
    }

    [Fact]
    public void Transpile_UserFunction_Varargs_Succeeds()
    {
        const string source = """
            first(head, string... rest) {
                return rest[0]
            }

            var x = first("a", "b", "c")
            println(x)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase2_varargs.sushi",
            TargetLanguage = TargetLanguage.Zsh
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("local -a __sushi_varargs_rest", result.EmittedCode);
        Assert.Contains("local rest=\"$(__sushi_json_array", result.EmittedCode);
    }

    [Fact]
    public void Transpile_UserFunction_UnknownNamedArgument_Fails()
    {
        const string source = """
            add(a, b) {
                return a + b
            }

            var x = add(a: 1, c: 2)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase2_unknown_named.sushi",
            TargetLanguage = TargetLanguage.Powershell7
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1011");
    }

    [Fact]
    public void Transpile_UserFunction_DuplicateArgumentBinding_Fails()
    {
        const string source = """
            add(a, b) {
                return a + b
            }

            var x = add(1, a: 2)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase2_duplicate.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1010");
    }

    [Fact]
    public void Transpile_UserFunction_MissingRequiredArgument_Fails()
    {
        const string source = """
            add(a, b) {
                return a + b
            }

            var x = add(1)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase2_missing_required.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1012");
    }

    [Fact]
    public void Transpile_UserFunction_TooManyArgumentsWithoutVarargs_Fails()
    {
        const string source = """
            add(a) {
                return a
            }

            var x = add(1, 2)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase2_too_many.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1013");
    }

    [Fact]
    public void Transpile_UserFunction_NamedBindingToVarargs_Fails()
    {
        const string source = """
            collect(string... rest) {
                return rest
            }

            var x = collect(rest: "a")
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase2_named_varargs.sushi",
            TargetLanguage = TargetLanguage.Zsh
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1014");
    }

    [Fact]
    public void Transpile_UnresolvedFunction_WithNamedArguments_Fails()
    {
        const string source = """
            var x = missing(flag: true)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase2_unresolved_named.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1017");
    }
}
