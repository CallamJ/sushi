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
        Assert.Contains("jq -Rsc", script);
        Assert.Empty(diagnostics);
    }
}
