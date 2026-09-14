namespace Sushi.Tests.Transpilation;

using Sushi.Transpilation.Intrinsics;
using Xunit;

public class IntrinsicRegistryTests
{
    [Fact]
    public void Resolve_KnownIntrinsic_Succeeds()
    {
        var registry = IntrinsicRegistry.CreateDefault();

        var ok = registry.TryResolve("std.io.readText", out var signature);

        Assert.True(ok);
        Assert.NotNull(signature);
        Assert.Equal(IntrinsicId.IoReadText, signature.Id);
    }

    [Fact]
    public void Resolve_UnknownIntrinsic_Fails()
    {
        var registry = IntrinsicRegistry.CreateDefault();

        var ok = registry.TryResolve("std.io.missing", out _);

        Assert.False(ok);
    }

    [Fact]
    public void Resolve_Milestone3Intrinsics_Succeed()
    {
        var registry = IntrinsicRegistry.CreateDefault();

        Assert.True(registry.TryResolve("std.process.run", out var processRun));
        Assert.False(registry.TryResolve("std.json.parse", out _));
        Assert.True(registry.TryResolve("std.fs.glob", out var fsGlob));
        Assert.True(registry.TryResolve("std.fs.size", out var fsSize));
        Assert.True(registry.TryResolve("std.http.get", out var httpGet));

        Assert.Equal(IntrinsicId.ProcessRun, processRun.Id);
        Assert.Equal(IntrinsicId.FsGlob, fsGlob.Id);
        Assert.Equal(IntrinsicId.FsSize, fsSize.Id);
        Assert.Equal("int", fsSize.ReturnType.Name);
        Assert.Equal(IntrinsicId.HttpGet, httpGet.Id);
    }

    [Fact]
    public void Resolve_StringIntrinsics_Succeed()
    {
        var registry = IntrinsicRegistry.CreateDefault();

        Assert.True(registry.TryResolve("std.string.trim", out var trim));
        Assert.True(registry.TryResolve("std.string.length", out var length));
        Assert.True(registry.TryResolve("std.string.split", out var split));
        Assert.True(registry.TryResolve("std.string.match", out var match));

        Assert.Equal(IntrinsicId.StringTrim, trim.Id);
        Assert.Equal(IntrinsicId.StringLength, length.Id);
        Assert.Equal("int", length.ReturnType.Name);
        Assert.Equal(IntrinsicId.StringSplit, split.Id);
        Assert.Equal(IntrinsicId.StringMatch, match.Id);
    }

    [Theory]
    [InlineData("print")]
    [InlineData("println")]
    [InlineData("std.fs.writeText")]
    [InlineData("std.process.sleep")]
    public void Resolve_SideEffectingIntrinsic_HasVoidReturnType(string name)
    {
        var registry = IntrinsicRegistry.CreateDefault();

        Assert.True(registry.TryResolve(name, out var signature));
        Assert.Equal("void", signature.ReturnType.Name);
    }
}
