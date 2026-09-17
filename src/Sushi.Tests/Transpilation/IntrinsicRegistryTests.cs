namespace Sushi.Tests.Transpilation;

using Sushi.Transpilation.Intrinsics;
using Xunit;

public class IntrinsicRegistryTests
{
    [Fact]
    public void Resolve_KnownIntrinsic_Succeeds()
    {
        var registry = IntrinsicRegistry.CreateDefault();

        var ok = registry.TryResolve("std.fs.readText", out var signature);

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
    public void Resolve_RemovedIoAliases_Fail()
    {
        var registry = IntrinsicRegistry.CreateDefault();

        Assert.False(registry.TryResolve("std.io.readText", out _));
        Assert.False(registry.TryResolve("std.io.writeText", out _));
        Assert.False(registry.TryResolve("std.io.exists", out _));
    }

    [Fact]
    public void Resolve_Milestone3Intrinsics_Succeed()
    {
        var registry = IntrinsicRegistry.CreateDefault();

        Assert.True(registry.TryResolve("std.process.run", out var processRun));
        Assert.False(registry.TryResolve("std.json.parse", out _));
        Assert.False(registry.TryResolve("std.fs.glob", out _));
        Assert.False(registry.TryResolve("std.fs.size", out _));
        Assert.True(registry.TryResolve("std.fs.fileSize", out var fsFileSize));
        Assert.True(registry.TryResolve("std.fs.directorySize", out var fsDirectorySize));
        Assert.True(registry.TryResolve("std.http.get", out var httpGet));

        Assert.Equal(IntrinsicId.ProcessRun, processRun.Id);
        Assert.Equal(IntrinsicId.FsFileSize, fsFileSize.Id);
        Assert.Equal(IntrinsicId.FsDirectorySize, fsDirectorySize.Id);
        Assert.Equal("int", fsFileSize.ReturnType.Name);
        Assert.Equal("int", fsDirectorySize.ReturnType.Name);
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

    [Fact]
    public void Resolve_Round_HasOptionalPrecisionAndFloatReturnType()
    {
        var registry = IntrinsicRegistry.CreateDefault();

        Assert.True(registry.TryResolve("std.math.round", out var round));
        Assert.Equal("float", round.ReturnType.Name);
        Assert.Equal(2, round.Parameters.Count);
        Assert.Equal("precision", round.Parameters[1].Name);
        Assert.Equal("int", round.Parameters[1].TypeName);
        Assert.True(round.Parameters[1].HasDefaultValue);
        Assert.Equal(0, round.Parameters[1].DefaultValue);
    }

    [Fact]
    public void StandardLibraryCatalog_ProvidesCompleteMarkdownForEveryActiveMember()
    {
        var functions = StandardLibraryCatalog.CreateDefault().Functions;

        Assert.Equal(57, functions.Count);
        foreach (var function in functions)
        {
            Assert.Contains("## Usage", function.Documentation);
            Assert.Contains("## Parameters", function.Documentation);
            Assert.Contains("## Returns", function.Documentation);
            Assert.Contains("## Examples", function.Documentation);
            Assert.Contains("## Errors and portability", function.Documentation);
        }
    }

    [Fact]
    public void StandardLibraryCatalog_UsesPreciseGlobSignatureTypes()
    {
        var catalog = StandardLibraryCatalog.CreateDefault();

        Assert.True(catalog.TryGetFunction("std.fs.query", out var query));
        Assert.Equal("FileQuery", query.ReturnType);
        Assert.Equal("string", query.Parameters[0].TypeName);
        Assert.True(query.Parameters[0].HasDefaultValue);
    }

    [Theory]
    [InlineData("std.fs.isFile")]
    [InlineData("std.fs.isDirectory")]
    public void Resolve_FileSystemPredicates_HasBooleanReturnType(string name)
    {
        var registry = IntrinsicRegistry.CreateDefault();

        Assert.True(registry.TryResolve(name, out var signature));
        Assert.Equal("bool", signature.ReturnType.Name);
    }

    [Theory]
    [InlineData("std.os.cwd")]
    [InlineData("std.console.readLine")]
    public void Resolve_StringReturningIntrinsics_HasStringReturnType(string name)
    {
        var registry = IntrinsicRegistry.CreateDefault();

        Assert.True(registry.TryResolve(name, out var signature));
        Assert.Equal("string", signature.ReturnType.Name);
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
