namespace Sushi.Tests.Transpilation;

using System.Collections.Generic;
using Sushi.Transpilation;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Xunit;

public class EmitterTests
{
    [Fact]
    public void BashEmitter_EmitsBasicScript()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("x", new IrLiteralExpression(1)),
            new IrExpressionStatement(new IrCallExpression("println", new IrExpression[]
            {
                new IrIdentifierExpression("x")
            }))
        });

        var diagnostics = new List<Diagnostic>();
        var emitter = new BashEmitter();
        var script = emitter.Emit(program, new EmitContext("test.sushi", diagnostics));

        Assert.Contains("#!/usr/bin/env bash", script);
        Assert.Contains("x=1", script);
        Assert.Contains("echo \"${x:-}\"", script);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PowerShellEmitter_EmitsBasicScript()
    {
        var program = new IrProgram(new IrStatement[]
        {
            new IrVariableDeclarationStatement("x", new IrLiteralExpression(1)),
            new IrExpressionStatement(new IrCallExpression("println", new IrExpression[]
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
}
