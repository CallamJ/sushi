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
        Assert.True(registry.TryResolve("std.json.parse", out var jsonParse));
        Assert.True(registry.TryResolve("std.fs.glob", out var fsGlob));
        Assert.True(registry.TryResolve("std.http.get", out var httpGet));

        Assert.Equal(IntrinsicId.ProcessRun, processRun.Id);
        Assert.Equal(IntrinsicId.JsonParse, jsonParse.Id);
        Assert.Equal(IntrinsicId.FsGlob, fsGlob.Id);
        Assert.Equal(IntrinsicId.HttpGet, httpGet.Id);
    }
}
