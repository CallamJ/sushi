namespace Sushi.Tests.Transpilation;

using System.Collections.Generic;
using Sushi.Transpilation;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;
using Xunit;

public class EmitterTests
{
    [Fact]
    public void BashEmitter_EmitsBasicScript()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("x", new IrLiteralExpression(1)),
            new IrExpressionStatement(new IrIntrinsicCallExpression(
                "println",
                IntrinsicId.Println,
                new IrExpression[]
            {
                new IrIdentifierExpression("x")
            }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("#!/usr/bin/env bash", script);
        Assert.Contains("x=1", script);
        Assert.Contains("printf '%s\\n' \"${x:-}\"", script);
        Assert.DoesNotContain("__sushi_j_reset", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_EmitsBasicScript()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("x", new IrLiteralExpression(1)),
            new IrExpressionStatement(new IrIntrinsicCallExpression(
                "println",
                IntrinsicId.Println,
                new IrExpression[]
            {
                new IrIdentifierExpression("x")
            }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("Set-StrictMode -Version Latest", script);
        Assert.Contains("$x = 1", script);
        Assert.Contains("Write-Host $x", script);
        Assert.DoesNotContain("function __sushi_member", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_EmitsStdIoExistsIntrinsic()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("ok", new IrIntrinsicCallExpression(
                "std.io.exists",
                IntrinsicId.IoExists,
                new IrExpression[] { new IrLiteralExpression("a.txt") }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("[[ -e", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_EmitsStdIoReadTextIntrinsic()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("content", new IrIntrinsicCallExpression(
                "std.io.readText",
                IntrinsicId.IoReadText,
                new IrExpression[] { new IrLiteralExpression("a.txt") }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("Get-Content -Raw -LiteralPath", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_LowersStringIntrinsicsDirectly()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("matched", new IrIntrinsicCallExpression(
                "std.string.match",
                IntrinsicId.StringMatch,
                new IrExpression[]
                {
                    new IrLiteralExpression("abc123"),
                    new IrLiteralExpression("\\d+")
                })),
            new IrVariableDeclarationStatement("parts", new IrIntrinsicCallExpression(
                "std.string.split",
                IntrinsicId.StringSplit,
                new IrExpression[]
                {
                    new IrLiteralExpression("a,b,c"),
                    new IrLiteralExpression(","),
                    new IrLiteralExpression(2)
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("strings.sushi", diagnostics));

        Assert.Contains("[regex]::Match", script);
        Assert.Contains(".Split(", script);
        Assert.DoesNotContain("function __sushi_", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_LowersStringIntrinsicsDirectly()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("ok", new IrIntrinsicCallExpression(
                "std.string.contains",
                IntrinsicId.StringContains,
                new IrExpression[]
                {
                    new IrLiteralExpression("hello"),
                    new IrLiteralExpression("ell")
                })),
            new IrVariableDeclarationStatement("match", new IrIntrinsicCallExpression(
                "std.string.match",
                IntrinsicId.StringMatch,
                new IrExpression[]
                {
                    new IrLiteralExpression("abc123"),
                    new IrLiteralExpression("[0-9]+")
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("strings.sushi", diagnostics));

        Assert.Contains("[[", script);
        Assert.Contains("BASH_REMATCH", script);
        Assert.DoesNotContain("__sushi_string_", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_UsesNativeAssociativeArraysForObjects()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("obj", new IrObjectLiteralExpression(new IrObjectProperty[]
            {
                new("name", new IrLiteralExpression("sushi")),
                new("count", new IrLiteralExpression(2))
            })),
            new IrVariableDeclarationStatement("name", new IrMemberAccessExpression(
                new IrIdentifierExpression("obj"),
                "name"))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("objects.sushi", diagnostics));

        Assert.Contains("declare -A obj=", script);
        Assert.Contains("${obj['name']-}", script);
        Assert.DoesNotContain("__sushi_native_obj", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_WriteText_CreatesParentDirectory()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrExpressionStatement(new IrIntrinsicCallExpression(
                "std.io.writeText",
                IntrinsicId.IoWriteText,
                new IrExpression[]
                {
                    new IrLiteralExpression("tmp/notes.txt"),
                    new IrLiteralExpression("hello"),
                    new IrLiteralExpression(false)
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("New-Item -ItemType Directory", script);
        Assert.Contains("Set-Content -LiteralPath $__sushi_path", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_WriteText_CreatesParentDirectory()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrExpressionStatement(new IrIntrinsicCallExpression(
                "std.io.writeText",
                IntrinsicId.IoWriteText,
                new IrExpression[]
                {
                    new IrLiteralExpression("tmp/notes.txt"),
                    new IrLiteralExpression("hello"),
                    new IrLiteralExpression(false)
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("mkdir -p --", script);
        Assert.Contains("printf '%s' 'hello' > \"$__sushi_path\"", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_LowersProcessRunDirectly()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("result", new IrIntrinsicCallExpression(
                "std.process.run",
                IntrinsicId.ProcessRun,
                new IrExpression[]
                {
                    new IrLiteralExpression("pwsh"),
                    new IrArrayLiteralExpression(new IrExpression[]
                    {
                        new IrLiteralExpression("-NoProfile"),
                        new IrLiteralExpression("-Command"),
                        new IrLiteralExpression("Write-Output hi")
                    }),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(0),
                    new IrLiteralExpression(true),
                    new IrLiteralExpression(false)
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("Start-Process -FilePath", script);
        Assert.Contains("[pscustomobject]@{ code=", script);
        Assert.DoesNotContain("function __sushi_", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_LowersHttpAndGlobDirectly()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("response", new IrIntrinsicCallExpression(
                "std.http.get",
                IntrinsicId.HttpGet,
                new IrExpression[]
                {
                    new IrLiteralExpression("https://example.com"),
                    new IrLiteralExpression(null)
                })),
            new IrVariableDeclarationStatement("files", new IrIntrinsicCallExpression(
                "std.fs.glob",
                IntrinsicId.FsGlob,
                new IrExpression[]
                {
                    new IrLiteralExpression("*.sushi"),
                    new IrLiteralExpression(null)
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("curl -sS", script);
        Assert.Contains("mapfile -t files", script);
        Assert.DoesNotContain("__sushi_http_", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ZshEmitter_EmitsZshScript()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("x", new IrLiteralExpression(1)),
            new IrExpressionStatement(new IrIntrinsicCallExpression(
                "println",
                IntrinsicId.Println,
                new IrExpression[]
                {
                    new IrIdentifierExpression("x")
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new ZshEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("#!/usr/bin/env zsh", script);
        Assert.Contains("set -eu", script);
        Assert.Contains("set -o pipefail", script);
        Assert.DoesNotContain("setopt ksharrays", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_EmitsTimeoutHandlingInProcessRun()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("result", new IrIntrinsicCallExpression(
                "std.process.run",
                IntrinsicId.ProcessRun,
                new IrExpression[]
                {
                    new IrLiteralExpression("echo"),
                    new IrArrayLiteralExpression(new IrExpression[] { new IrLiteralExpression("hi") }),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(250),
                    new IrLiteralExpression(true),
                    new IrLiteralExpression(false)
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("timeout.sushi", diagnostics));

        Assert.Contains("timeout '0.25s'", script);
        Assert.Contains("result_timedOut=", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_EmitsTimeoutHandlingInProcessRun()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("result", new IrIntrinsicCallExpression(
                "std.process.run",
                IntrinsicId.ProcessRun,
                new IrExpression[]
                {
                    new IrLiteralExpression("echo"),
                    new IrArrayLiteralExpression(new IrExpression[] { new IrLiteralExpression("hi") }),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(null),
                    new IrLiteralExpression(250),
                    new IrLiteralExpression(true),
                    new IrLiteralExpression(false)
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("timeout.sushi", diagnostics));

        Assert.Contains("WaitForExit(250)", script);
        Assert.Contains("$result_timedOut = $true", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_EmitsVarargsFunctionBinding()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrFunctionDeclarationStatement(
                "collect",
                new[]
                {
                    new IrFunctionParameter("head", isVarargs: false, defaultValue: null),
                    new IrFunctionParameter("rest", isVarargs: true, defaultValue: null)
                },
                new IrBlockStatement(new IrStatement[]
                {
                    new IrReturnStatement(new IrIdentifierExpression("rest"))
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("varargs.sushi", diagnostics));

        Assert.Contains("local head=\"$1\"", script);
        Assert.Contains("local -a rest=(\"${@:2}\")", script);
        Assert.DoesNotContain("__sushi_array_new", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_EmitsVarargsFunctionBinding()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrFunctionDeclarationStatement(
                "collect",
                new[]
                {
                    new IrFunctionParameter("head", isVarargs: false, defaultValue: null),
                    new IrFunctionParameter("rest", isVarargs: true, defaultValue: null)
                },
                new IrBlockStatement(new IrStatement[]
                {
                    new IrReturnStatement(new IrIdentifierExpression("rest"))
                }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("varargs.sushi", diagnostics));

        Assert.Contains("param($head)", script);
        Assert.Contains("$rest = @()", script);
        Assert.Contains("foreach ($__sushi_vararg in @($args))", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_EmitsFunctionTypeContracts()
    {
        var structuralType = IrTypeRef.Structural(new[]
        {
            new IrStructuralField("name", IrTypeRef.Primitive("string"), optional: false),
            new IrStructuralField("age", IrTypeRef.Primitive("int"), optional: false)
        });

        var program = new IrProgram(new IrStatement[]
        {
            new IrFunctionDeclarationStatement(
                "checkUser",
                new[]
                {
                    new IrFunctionParameter("user", isVarargs: false, defaultValue: null, structuralType),
                    new IrFunctionParameter("count", isVarargs: false, defaultValue: null, IrTypeRef.Primitive("int"))
                },
                new IrBlockStatement(new IrStatement[]
                {
                    new IrReturnStatement(new IrIdentifierExpression("count"))
                }),
                IrTypeRef.Primitive("int"))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("contracts.sushi", diagnostics));

        Assert.Contains("local user_name=\"$1\"", script);
        Assert.Contains("local -i user_age=\"$2\"", script);
        Assert.Contains("local -i count=\"$3\"", script);
        Assert.Contains("__sushi_result=", script);
        Assert.DoesNotContain("__sushi_type_check", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_EmitsFunctionTypeContracts()
    {
        var structuralType = IrTypeRef.Structural(new[]
        {
            new IrStructuralField("name", IrTypeRef.Primitive("string"), optional: false)
        });

        var program = new IrProgram(new IrStatement[]
        {
            new IrFunctionDeclarationStatement(
                "checkUser",
                new[]
                {
                    new IrFunctionParameter("user", isVarargs: false, defaultValue: null, structuralType),
                    new IrFunctionParameter("count", isVarargs: false, defaultValue: null, IrTypeRef.Primitive("int"))
                },
                new IrBlockStatement(new IrStatement[]
                {
                    new IrReturnStatement(new IrIdentifierExpression("count"))
                }),
                IrTypeRef.Primitive("int"))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("contracts.sushi", diagnostics));

        Assert.Contains("param([pscustomobject]$user, [int]$count)", script);
        Assert.Contains("$__sushi_return_value =", script);
        Assert.DoesNotContain("function __sushi_", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_ArithmeticIndexExpression_UsesStrictNumericCheck()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("total", new IrLiteralExpression(0)),
            new IrExpressionStatement(
                new IrAssignmentExpression(
                    new IrIdentifierExpression("total"),
                    "+=",
                    new IrIndexExpression(
                        new IrIdentifierExpression("values"),
                        new IrIdentifierExpression("i"))))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("arith.sushi", diagnostics));

        Assert.DoesNotContain("__sushi_validate_integer", script);
        Assert.DoesNotContain("__sushi_json_index", script);
        Assert.DoesNotContain("${total:-0}", script);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "SUSHI1030");
    }

    [Fact]
    public void BashEmitter_UsesNativeArrayStorageWithoutFullRuntimeForLiteralIndexing()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("values", new IrArrayLiteralExpression(new IrExpression[]
            {
                new IrLiteralExpression("first"),
                new IrLiteralExpression("second")
            })),
            new IrExpressionStatement(new IrIntrinsicCallExpression(
                "println",
                IntrinsicId.Println,
                new[] { new IrIndexExpression(new IrIdentifierExpression("values"), new IrLiteralExpression(0)) }))
        });

        var diagnostics = new List<Diagnostic>();
        var script = new BashEmitter().Emit(program, new EmitContext("array.sushi", diagnostics));

        Assert.Contains("declare -a values=('first' 'second')", script);
        Assert.Contains("${values[0]-}", script);
        Assert.DoesNotContain("__sushi_array_", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_ReassignsArraysWithoutRevivingRuntimeHelpers()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("values", new IrArrayLiteralExpression(new IrExpression[]
            {
                new IrLiteralExpression("first")
            })),
            new IrExpressionStatement(new IrAssignmentExpression(
                new IrIdentifierExpression("values"),
                "=",
                new IrArrayLiteralExpression(new IrExpression[]
                {
                    new IrLiteralExpression("second")
                }))),
            new IrExpressionStatement(new IrIntrinsicCallExpression(
                "println",
                IntrinsicId.Println,
                new[] { new IrIndexExpression(new IrIdentifierExpression("values"), new IrLiteralExpression(0)) }))
        });

        var diagnostics = new List<Diagnostic>();
        var script = new BashEmitter().Emit(program, new EmitContext("array.sushi", diagnostics));

        Assert.Contains("values=('second')", script);
        Assert.Contains("${values[0]-}", script);
        Assert.DoesNotContain("__sushi_array_", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_UsesDirectArithmeticForKnownIntegers()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("left", new IrLiteralExpression(1)),
            new IrVariableDeclarationStatement("right", new IrLiteralExpression(2)),
            new IrExpressionStatement(new IrAssignmentExpression(
                new IrIdentifierExpression("left"),
                "=",
                new IrBinaryExpression(
                    new IrIdentifierExpression("left"),
                    "+",
                    new IrIdentifierExpression("right"))))
        });

        var diagnostics = new List<Diagnostic>();
        var script = new PowerShellEmitter().Emit(program, new EmitContext("numeric.sushi", diagnostics));

        Assert.Contains("$left = ($left + $right)", script);
        Assert.DoesNotContain("__sushi_add $left $right", script);
        Assert.Empty(diagnostics);
    }
}
