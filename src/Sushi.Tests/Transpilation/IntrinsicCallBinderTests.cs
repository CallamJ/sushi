namespace Sushi.Tests.Transpilation;

using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;
using Xunit;

public class IntrinsicCallBinderTests
{
    [Fact]
    public void Bind_DefaultArgument_IsInjected()
    {
        var signature = new IntrinsicSignature(
            "std.io.writeText",
            IntrinsicId.IoWriteText,
            new[]
            {
                new IntrinsicParameter("path"),
                new IntrinsicParameter("text"),
                new IntrinsicParameter("append", hasDefaultValue: true, defaultValue: false)
            });

        var arguments = new[]
        {
            new IntrinsicCallArgument(null, new IrLiteralExpression("a.txt"), 1, 1),
            new IntrinsicCallArgument(null, new IrLiteralExpression("data"), 1, 10)
        };

        var result = IntrinsicCallBinder.Bind(signature, arguments, "test.sushi", 1, 1);

        Assert.True(result.Success);
        Assert.Equal(3, result.OrderedArguments.Count);
        var defaultArg = Assert.IsType<IrLiteralExpression>(result.OrderedArguments[2]);
        Assert.Equal(false, defaultArg.Value);
    }

    [Fact]
    public void Bind_NamedArguments_AreReordered()
    {
        var signature = new IntrinsicSignature(
            "std.io.writeText",
            IntrinsicId.IoWriteText,
            new[]
            {
                new IntrinsicParameter("path"),
                new IntrinsicParameter("text"),
                new IntrinsicParameter("append", hasDefaultValue: true, defaultValue: false)
            });

        var arguments = new[]
        {
            new IntrinsicCallArgument("text", new IrLiteralExpression("data"), 1, 1),
            new IntrinsicCallArgument("path", new IrLiteralExpression("a.txt"), 1, 10),
            new IntrinsicCallArgument("append", new IrLiteralExpression(true), 1, 20)
        };

        var result = IntrinsicCallBinder.Bind(signature, arguments, "test.sushi", 1, 1);

        Assert.True(result.Success);
        var pathArg = Assert.IsType<IrLiteralExpression>(result.OrderedArguments[0]);
        var textArg = Assert.IsType<IrLiteralExpression>(result.OrderedArguments[1]);
        var appendArg = Assert.IsType<IrLiteralExpression>(result.OrderedArguments[2]);
        Assert.Equal("a.txt", pathArg.Value);
        Assert.Equal("data", textArg.Value);
        Assert.Equal(true, appendArg.Value);
    }

    [Fact]
    public void Bind_UnknownNamedArgument_ReturnsDiagnostic()
    {
        var signature = new IntrinsicSignature(
            "std.os.chdir",
            IntrinsicId.OsChdir,
            new[] { new IntrinsicParameter("path") });

        var arguments = new[]
        {
            new IntrinsicCallArgument("missing", new IrLiteralExpression("x"), 1, 1)
        };

        var result = IntrinsicCallBinder.Bind(signature, arguments, "test.sushi", 1, 1);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == IntrinsicDiagnosticCodes.UnknownNamedArgument);
    }
}
