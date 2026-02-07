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
    public void PowerShellEmitter_EmitsProcessRunAndJsonHelpers()
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
                })),
            new IrVariableDeclarationStatement("obj", new IrIntrinsicCallExpression(
                "std.json.parse",
                IntrinsicId.JsonParse,
                new IrExpression[] { new IrLiteralExpression("{\"x\":1}") }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new PowerShellEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("function __sushi_process_run", script);
        Assert.Contains("function __sushi_json_parse", script);
        Assert.Contains("__sushi_process_run -command", script);
        Assert.Contains("__sushi_json_parse -text", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BashEmitter_EmitsHttpAndGlobHelpers()
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

        Assert.Contains("__sushi_http_get", script);
        Assert.Contains("__sushi_fs_glob", script);
        Assert.Contains("__sushi_http_request()", script);
        Assert.Contains("__sushi_j_parse_value", script);
        Assert.DoesNotContain("perl ", script);
        Assert.DoesNotContain("| jq", script);
        Assert.DoesNotContain("jq -", script);
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
        Assert.Contains("setopt typesetsilent", script);
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

        Assert.Contains("timeout_enabled=true", script);
        Assert.Contains("sleep \"$timeout_seconds\"", script);
        Assert.Contains("exit_code=124", script);
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

        Assert.Contains("if ($timeoutMs -gt 0)", script);
        Assert.Contains("WaitForExit($timeoutMs)", script);
        Assert.Contains("$timedOut = $true", script);
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
        Assert.Contains("local -a __sushi_varargs_rest=(\"${@:2}\")", script);
        Assert.Contains("local rest=\"$(__sushi_json_array", script);
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
        Assert.Contains("$rest = @($args)", script);
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

        Assert.Contains("__sushi_type_check", script);
        Assert.Contains("__sushi_struct_check", script);
        Assert.Contains("__sushi_type_check \"${count:-}\"", script);
        Assert.Contains("local __sushi_return_value=", script);
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

        Assert.Contains("function __sushi_type_check", script);
        Assert.Contains("function __sushi_struct_check", script);
        Assert.Contains("__sushi_type_check -value $count", script);
        Assert.Contains("__sushi_struct_check -value $user", script);
        Assert.Contains("$__sushi_return_value =", script);
        Assert.Contains("__sushi_type_check -value $__sushi_return_value", script);
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

        Assert.Contains("__sushi_require_integer", script);
        Assert.Contains("__sushi_json_index", script);
        Assert.DoesNotContain("${total:-0}", script);
        Assert.Empty(diagnostics);
    }
}
