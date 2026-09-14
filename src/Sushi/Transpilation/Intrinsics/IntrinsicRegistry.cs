namespace Sushi.Transpilation.Intrinsics;

public sealed class IntrinsicRegistry
{
    private readonly Dictionary<string, IntrinsicSignature> _signatures;

    private IntrinsicRegistry(IEnumerable<IntrinsicSignature> signatures)
    {
        _signatures = signatures.ToDictionary(s => s.CanonicalName, s => s, StringComparer.Ordinal);
    }

    public static IntrinsicRegistry CreateDefault()
    {
        var signatures = new[]
        {
            new IntrinsicSignature(
                "print",
                IntrinsicId.Print,
                new[] { new IntrinsicParameter("value", hasDefaultValue: true, defaultValue: "") }),
            new IntrinsicSignature(
                "println",
                IntrinsicId.Println,
                new[] { new IntrinsicParameter("value", hasDefaultValue: true, defaultValue: "") }),
            new IntrinsicSignature(
                "std.target.shell",
                IntrinsicId.TargetShell,
                Array.Empty<IntrinsicParameter>()),
            new IntrinsicSignature(
                "std.target.platform",
                IntrinsicId.TargetPlatform,
                Array.Empty<IntrinsicParameter>()),
            new IntrinsicSignature(
                "std.string.trim",
                IntrinsicId.StringTrim,
                new[] { new IntrinsicParameter("value") }),
            new IntrinsicSignature(
                "std.string.lower",
                IntrinsicId.StringLower,
                new[] { new IntrinsicParameter("value") }),
            new IntrinsicSignature(
                "std.string.upper",
                IntrinsicId.StringUpper,
                new[] { new IntrinsicParameter("value") }),
            new IntrinsicSignature(
                "std.string.split",
                IntrinsicId.StringSplit,
                new[]
                {
                    new IntrinsicParameter("value"),
                    new IntrinsicParameter("sep"),
                    new IntrinsicParameter("limit", hasDefaultValue: true, defaultValue: 0)
                }),
            new IntrinsicSignature(
                "std.string.contains",
                IntrinsicId.StringContains,
                new[]
                {
                    new IntrinsicParameter("value"),
                    new IntrinsicParameter("needle")
                }),
            new IntrinsicSignature(
                "std.string.startsWith",
                IntrinsicId.StringStartsWith,
                new[]
                {
                    new IntrinsicParameter("value"),
                    new IntrinsicParameter("prefix")
                }),
            new IntrinsicSignature(
                "std.string.endsWith",
                IntrinsicId.StringEndsWith,
                new[]
                {
                    new IntrinsicParameter("value"),
                    new IntrinsicParameter("suffix")
                }),
            new IntrinsicSignature(
                "std.string.replace",
                IntrinsicId.StringReplace,
                new[]
                {
                    new IntrinsicParameter("value"),
                    new IntrinsicParameter("old"),
                    new IntrinsicParameter("new")
                }),
            new IntrinsicSignature(
                "std.string.isMatch",
                IntrinsicId.StringIsMatch,
                new[]
                {
                    new IntrinsicParameter("value"),
                    new IntrinsicParameter("pattern")
                }),
            new IntrinsicSignature(
                "std.string.match",
                IntrinsicId.StringMatch,
                new[]
                {
                    new IntrinsicParameter("value"),
                    new IntrinsicParameter("pattern")
                }),
            new IntrinsicSignature(
                "std.fs.readText",
                IntrinsicId.IoReadText,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.fs.writeText",
                IntrinsicId.IoWriteText,
                new[]
                {
                    new IntrinsicParameter("path"),
                    new IntrinsicParameter("text"),
                    new IntrinsicParameter("append", hasDefaultValue: true, defaultValue: false)
                }),
            new IntrinsicSignature(
                "std.fs.exists",
                IntrinsicId.IoExists,
                new[] { new IntrinsicParameter("path") }),
            // Compatibility names for the beta. New code should use std.fs.*.
            new IntrinsicSignature(
                "std.io.readText",
                IntrinsicId.IoReadText,
                new[] { new IntrinsicParameter("path") }, "std.io.* is deprecated; use std.fs.* instead."),
            new IntrinsicSignature(
                "std.io.writeText",
                IntrinsicId.IoWriteText,
                new[]
                {
                    new IntrinsicParameter("path"),
                    new IntrinsicParameter("text"),
                    new IntrinsicParameter("append", hasDefaultValue: true, defaultValue: false)
                }, "std.io.* is deprecated; use std.fs.* instead."),
            new IntrinsicSignature(
                "std.io.exists",
                IntrinsicId.IoExists,
                new[] { new IntrinsicParameter("path") }, "std.io.* is deprecated; use std.fs.* instead."),
            new IntrinsicSignature(
                "std.path.join",
                IntrinsicId.PathJoin,
                new[] { new IntrinsicParameter("parts", isVariadic: true) }),
            new IntrinsicSignature(
                "std.path.dirname",
                IntrinsicId.PathDirname,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.path.basename",
                IntrinsicId.PathBasename,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.path.extension",
                IntrinsicId.PathExtension,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.path.stem",
                IntrinsicId.PathStem,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.env.get",
                IntrinsicId.EnvGet,
                new[]
                {
                    new IntrinsicParameter("name"),
                    new IntrinsicParameter("fallback", hasDefaultValue: true, defaultValue: null)
                }),
            new IntrinsicSignature(
                "std.env.set",
                IntrinsicId.EnvSet,
                new[]
                {
                    new IntrinsicParameter("name"),
                    new IntrinsicParameter("value")
                }),
            new IntrinsicSignature(
                "std.env.has",
                IntrinsicId.EnvHas,
                new[] { new IntrinsicParameter("name") }),
            new IntrinsicSignature(
                "std.env.unset",
                IntrinsicId.EnvUnset,
                new[] { new IntrinsicParameter("name") }),
            new IntrinsicSignature(
                "std.process.args",
                IntrinsicId.ProcessArgs,
                Array.Empty<IntrinsicParameter>()),
            new IntrinsicSignature(
                "std.process.exit",
                IntrinsicId.ProcessExit,
                new[] { new IntrinsicParameter("code", hasDefaultValue: true, defaultValue: 0) }),
            new IntrinsicSignature(
                "std.process.which",
                IntrinsicId.ProcessWhich,
                new[] { new IntrinsicParameter("command") }),
            new IntrinsicSignature(
                "std.process.sleep",
                IntrinsicId.ProcessSleep,
                new[] { new IntrinsicParameter("milliseconds") }),
            new IntrinsicSignature(
                "std.process.run",
                IntrinsicId.ProcessRun,
                new[]
                {
                    new IntrinsicParameter("command"),
                    new IntrinsicParameter("args", hasDefaultValue: true, defaultValue: null),
                    new IntrinsicParameter("cwd", hasDefaultValue: true, defaultValue: null),
                    new IntrinsicParameter("env", hasDefaultValue: true, defaultValue: null),
                    new IntrinsicParameter("input", hasDefaultValue: true, defaultValue: null),
                    new IntrinsicParameter("timeoutMs", hasDefaultValue: true, defaultValue: 0),
                    new IntrinsicParameter("allowFailure", hasDefaultValue: true, defaultValue: false),
                    new IntrinsicParameter("stream", hasDefaultValue: true, defaultValue: false)
                }),
            new IntrinsicSignature(
                "std.process.pipeline",
                IntrinsicId.ProcessPipeline,
                new[]
                {
                    new IntrinsicParameter("stages"),
                    new IntrinsicParameter("cwd", hasDefaultValue: true, defaultValue: null),
                    new IntrinsicParameter("env", hasDefaultValue: true, defaultValue: null),
                    new IntrinsicParameter("input", hasDefaultValue: true, defaultValue: null),
                    new IntrinsicParameter("timeoutMs", hasDefaultValue: true, defaultValue: 0),
                    new IntrinsicParameter("allowFailure", hasDefaultValue: true, defaultValue: false),
                    new IntrinsicParameter("stream", hasDefaultValue: true, defaultValue: false)
                }),
            new IntrinsicSignature(
                "std.process.fail",
                IntrinsicId.ProcessFail,
                new[] { new IntrinsicParameter("result") }),
            new IntrinsicSignature(
                "std.process.requireSuccess",
                IntrinsicId.ProcessRequireSuccess,
                new[] { new IntrinsicParameter("result") }),
            new IntrinsicSignature(
                "std.os.cwd",
                IntrinsicId.OsCwd,
                Array.Empty<IntrinsicParameter>()),
            new IntrinsicSignature(
                "std.os.chdir",
                IntrinsicId.OsChdir,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.console.error",
                IntrinsicId.ConsoleError,
                new[] { new IntrinsicParameter("value", hasDefaultValue: true, defaultValue: "") }),
            new IntrinsicSignature(
                "std.console.readLine",
                IntrinsicId.ConsoleReadLine,
                Array.Empty<IntrinsicParameter>()),
            new IntrinsicSignature(
                "std.fs.glob",
                IntrinsicId.FsGlob,
                new[]
                {
                    new IntrinsicParameter("pattern"),
                    new IntrinsicParameter("cwd", hasDefaultValue: true, defaultValue: null)
                }),
            new IntrinsicSignature(
                "std.fs.size",
                IntrinsicId.FsSize,
                new[] { new IntrinsicParameter("path", typeName: "string") }),
            new IntrinsicSignature(
                "std.fs.isFile",
                IntrinsicId.FsIsFile,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.fs.isDirectory",
                IntrinsicId.FsIsDirectory,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.fs.createDirectory",
                IntrinsicId.FsCreateDirectory,
                new[] { new IntrinsicParameter("path") }),
            new IntrinsicSignature(
                "std.fs.remove",
                IntrinsicId.FsRemove,
                new[]
                {
                    new IntrinsicParameter("path"),
                    new IntrinsicParameter("recursive", hasDefaultValue: true, defaultValue: false)
                }),
            new IntrinsicSignature(
                "std.fs.copy",
                IntrinsicId.FsCopy,
                new[]
                {
                    new IntrinsicParameter("source"),
                    new IntrinsicParameter("destination"),
                    new IntrinsicParameter("recursive", hasDefaultValue: true, defaultValue: false)
                }),
            new IntrinsicSignature(
                "std.fs.move",
                IntrinsicId.FsMove,
                new[] { new IntrinsicParameter("source"), new IntrinsicParameter("destination") }),
            new IntrinsicSignature(
                "std.archive.zip",
                IntrinsicId.ArchiveZip,
                new[] { new IntrinsicParameter("source"), new IntrinsicParameter("destination") }),
            new IntrinsicSignature(
                "std.archive.unzip",
                IntrinsicId.ArchiveUnzip,
                new[] { new IntrinsicParameter("source"), new IntrinsicParameter("destination") }),
            new IntrinsicSignature(
                "std.http.get",
                IntrinsicId.HttpGet,
                new[]
                {
                    new IntrinsicParameter("url"),
                    new IntrinsicParameter("headers", hasDefaultValue: true, defaultValue: null)
                }),
            new IntrinsicSignature(
                "std.http.download",
                IntrinsicId.HttpDownload,
                new[]
                {
                    new IntrinsicParameter("url"),
                    new IntrinsicParameter("destination")
                }),
            new IntrinsicSignature(
                "std.http.post",
                IntrinsicId.HttpPost,
                new[]
                {
                    new IntrinsicParameter("url"),
                    new IntrinsicParameter("body"),
                    new IntrinsicParameter("headers", hasDefaultValue: true, defaultValue: null),
                    new IntrinsicParameter("contentType", hasDefaultValue: true, defaultValue: "application/json")
                })
        };

        return new IntrinsicRegistry(signatures);
    }

    public bool TryResolve(string canonicalName, out IntrinsicSignature signature)
    {
        return _signatures.TryGetValue(canonicalName, out signature!);
    }

    /// <summary>All built-in compiler intrinsics, for shared editor/API metadata.</summary>
    public IReadOnlyCollection<IntrinsicSignature> Signatures => _signatures.Values;
}
