namespace Sushi.Tests.Transpilation;

using Sushi.Application;
using Sushi.Transpilation;
using Xunit;

public class TranspilerTests
{
    [Fact]
    public void Transpile_PureEnumComparison_PrintsWithoutTemporaryOrStatusWrapper()
    {
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = "enum State { Ready, Done }\nprintln(State.Ready != State.Done)",
            SourcePath = "enum-comparison.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success);
        Assert.Contains("[[", result.EmittedCode);
        Assert.Contains("printf '%s\\n' 'true'", result.EmittedCode);
        Assert.DoesNotContain("_tmp", result.EmittedCode);
        Assert.DoesNotContain("__sushi_status", result.EmittedCode);
        Assert.DoesNotContain("__sushi_enum_", result.EmittedCode);
    }

    [Theory]
    [InlineData(TargetLanguage.Bash)]
    [InlineData(TargetLanguage.Zsh)]
    [InlineData(TargetLanguage.Powershell51)]
    public void Transpile_TruthinessOperator_IsExplicitAndNative(TargetLanguage target)
    {
        const string source = """
            var empty = ""
            var text = "false"
            var zero = 0
            var values = []
            if (?empty) { std.process.exit(1) }
            if (!?text) { std.process.exit(2) }
            if (?zero) { std.process.exit(3) }
            if (!?values) { std.process.exit(4) }
            """;

        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "truthiness.sushi",
            TargetLanguage = target
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.DoesNotContain("__sushi_truthy", result.EmittedCode);
    }

    [Fact]
    public void Transpile_ConditionRequiresBool_AndTruthinessRequiresKnownType()
    {
        const string source = """
            var text = "ready"
            if (text) { println(text) }
            value() { return "ready" }
            if (?value()) { println("never") }
            """;

        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "strict-conditions.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1046");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1047");
    }

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
            TargetLanguage = TargetLanguage.Powershell51
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("$x = 1", result.EmittedCode);
        Assert.Contains("Write-Output", result.EmittedCode);
    }

    [Fact]
    public void Transpile_ClassDeclaration_Succeeds()
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

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SUSHI1001");
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
            TargetLanguage = TargetLanguage.Powershell51
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("Get-Content -Raw -LiteralPath", result.EmittedCode);
    }

    [Fact]
    public void Transpile_Milestone3Apis_Succeeds()
    {
        const string source = """
            var files = std.fs.glob("*.sushi")
            var response = std.http.get("https://example.com")
            var result = std.process.run("pwsh", ["-NoProfile", "-Command", "Write-Output hi"], allowFailure: true)
            println(result.code)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m3.sushi",
            TargetLanguage = TargetLanguage.Powershell51
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("Invoke-WebRequest", result.EmittedCode);
        Assert.Contains("Start-Process", result.EmittedCode);
        Assert.DoesNotContain("function __sushi_", result.EmittedCode);
    }

    [Fact]
    public void Transpile_JsonIntrinsic_IsNoLongerBuiltIn()
    {
        const string source = """
            var parsed = std.json.parse("{\"name\":\"sushi\",\"count\":2}")
            println(parsed.name)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "json_object_runtime.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1301");
    }

    [Theory]
    [InlineData(TargetLanguage.Bash)]
    [InlineData(TargetLanguage.Zsh)]
    public void Transpile_ShellCollections_UseNativeStorageWithoutRuntime(TargetLanguage target)
    {
        const string source = """
            var parsed = { name: "sushi", count: 2 }
            var files = std.fs.glob("src/**/*.cs")
            var stages = [{ command: "printf", args: ["hello"] }]
            var result = std.process.pipeline(stages, allowFailure: true)
            println(parsed.name)
            println(result.code)
            """;

        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "native_boundaries.sushi",
            TargetLanguage = target
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.DoesNotContain("__sushi_json_", result.EmittedCode);
        Assert.DoesNotContain("__sushi_process_pipeline", result.EmittedCode);
        Assert.DoesNotContain("__sushi_native_obj", result.EmittedCode);
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
        Assert.Contains("add 'x' 2 5", result.EmittedCode);
        Assert.Contains("add 'y' 3 7", result.EmittedCode);
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
        Assert.Contains("local -a rest=(\"${@:3}\")", result.EmittedCode);
        Assert.DoesNotContain("__sushi_array_new", result.EmittedCode);
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
            TargetLanguage = TargetLanguage.Powershell51
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

    [Fact]
    public void Transpile_StructuralParameterScript_Succeeds()
    {
        const string source = """
            process(object { string name, int age } user) {
                return user.name
            }

            var value = process({ name: "alice", age: 32 })
            println(value)
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase3_structural_ok.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("user_name", result.EmittedCode);
        Assert.DoesNotContain("__sushi_struct_check", result.EmittedCode);
    }

    [Fact]
    public void Transpile_StaticStructuralFieldMismatch_Fails()
    {
        const string source = """
            process(object { string name, int age } user) {
                return user.name
            }

            var value = process({ name: "alice", age: "bad" })
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase3_structural_bad.sushi",
            TargetLanguage = TargetLanguage.Zsh
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1023");
    }

    [Fact]
    public void Transpile_StaticReturnTypeMismatch_Fails()
    {
        const string source = """
            int value() {
                return "hello"
            }
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase3_return_bad.sushi",
            TargetLanguage = TargetLanguage.Powershell51
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1024");
    }

    [Fact]
    public void Transpile_StaticArgumentTypeMismatch_Fails()
    {
        const string source = """
            int parse(int x) {
                return x
            }

            var value = parse("x")
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_phase3_argument_bad.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1021");
    }

    [Fact]
    public void Transpile_Bash_TypedVarargsIndex_UsesNativeArrayStorage()
    {
        const string source = """
            sum(int... values) {
                var total = 0
                var i = 0
                while (i < 2) {
                    total = total + values[i]
                    i = i + 1
                }
                return total
            }
            println(sum(1, 2))
            """;

        var transpiler = new Transpiler();
        var result = transpiler.Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "m4_numeric_index_math.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success);
        Assert.NotNull(result.EmittedCode);
        Assert.Contains("local -a values=(\"${@:2}\")", result.EmittedCode);
        Assert.DoesNotContain("__sushi_json_index", result.EmittedCode);
    }

    [Fact]
    public void Transpile_UndefinedIdentifier_FailsWithSourceDiagnostic()
    {
        const string source = "println(missingValue)";

        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "undefined.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "SUSHI1002");
        Assert.Equal(1, diagnostic.Span.Line);
        Assert.Contains("missingValue", diagnostic.Message);
    }

    [Fact]
    public void Transpile_FunctionParameter_IsDefined()
    {
        const string source = "identity(value) { return value }";

        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = source,
            SourcePath = "parameter.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SUSHI1002");
    }

    [Fact]
    public void Transpile_BoxDeclaration_Succeeds()
    {
        var result = new Transpiler().Transpile(new TranspileRequest
        {
            SourceText = "box Example",
            SourcePath = "module.sushi",
            TargetLanguage = TargetLanguage.Bash
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "SUSHI1001");
    }
}
