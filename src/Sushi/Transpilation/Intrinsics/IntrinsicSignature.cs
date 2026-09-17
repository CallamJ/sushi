namespace Sushi.Transpilation.Intrinsics;

using Sushi.Transpilation.IR;

public sealed class IntrinsicParameter
{
    public string Name { get; }
    /// <summary>Source-language type used by tooling and generated API help.</summary>
    public string TypeName { get; }
    public bool IsVariadic { get; }
    public bool HasDefaultValue { get; }
    public object? DefaultValue { get; }

    public IntrinsicParameter(
        string name,
        bool isVariadic = false,
        bool hasDefaultValue = false,
        object? defaultValue = null,
        string? typeName = null)
    {
        Name = name;
        TypeName = typeName ?? DefaultTypeFor(name);
        IsVariadic = isVariadic;
        HasDefaultValue = hasDefaultValue;
        DefaultValue = defaultValue;
    }

    private static string DefaultTypeFor(string name) => name switch
    {
        "append" or "allowFailure" or "recursive" or "stream" => "bool",
        "code" or "limit" or "milliseconds" or "timeoutMs" => "int",
        "args" or "stages" => "array",
        "input" or "env" or "headers" or "result" => "object",
        "contentType" or "path" or "source" or "destination" or "command" or "url" or "text" or
        "sep" or "needle" or "prefix" or "old" or "new" or "pattern" or "name" or "fallback" or "cwd" => "string",
        _ => "object"
    };
}

public sealed class IntrinsicSignature
{
    public string CanonicalName { get; }
    public IntrinsicId Id { get; }
    public IReadOnlyList<IntrinsicParameter> Parameters { get; }
    public string? DeprecationMessage { get; }
    public IrTypeRef ReturnType { get; }

    public IntrinsicSignature(string canonicalName, IntrinsicId id, IEnumerable<IntrinsicParameter> parameters, string? deprecationMessage = null, IrTypeRef? returnType = null)
    {
        CanonicalName = canonicalName;
        Id = id;
        Parameters = parameters.ToList();
        DeprecationMessage = deprecationMessage;
        ReturnType = returnType ?? DefaultReturnType(id);
    }

    private static IrTypeRef DefaultReturnType(IntrinsicId id) => id switch
    {
        // These operations are intentionally statement-oriented. They either
        // perform a side effect or terminate the process and must not be
        // presented as producing an arbitrary object in source tooling.
        IntrinsicId.Print or IntrinsicId.Println or IntrinsicId.IoWriteText or
        IntrinsicId.EnvSet or IntrinsicId.EnvUnset or IntrinsicId.ProcessExit or
        IntrinsicId.ProcessFail or IntrinsicId.OsChdir or IntrinsicId.ProcessSleep or
        IntrinsicId.ConsoleError or IntrinsicId.FsCreateDirectory or IntrinsicId.FsRemove or
        IntrinsicId.FsCopy or IntrinsicId.FsMove or IntrinsicId.ArchiveZip or
        IntrinsicId.ArchiveUnzip or IntrinsicId.HttpDownload => IrTypeRef.Primitive("void"),
        IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or
        IntrinsicId.StringIsMatch or IntrinsicId.IoExists or IntrinsicId.EnvHas => IrTypeRef.Primitive("bool"),
        IntrinsicId.StringLength or IntrinsicId.FsSize => IrTypeRef.Primitive("int"),
        IntrinsicId.StringSplit or IntrinsicId.ProcessArgs or IntrinsicId.FsGlob =>
            IrTypeRef.Primitive("array", elementType: IrTypeRef.Primitive("string")),
        IntrinsicId.TargetShell or IntrinsicId.TargetPlatform or IntrinsicId.StringTrim or IntrinsicId.StringLower or
        IntrinsicId.StringUpper or IntrinsicId.StringReplace or IntrinsicId.IoReadText or IntrinsicId.PathJoin or
        IntrinsicId.PathDirname or IntrinsicId.PathBasename or IntrinsicId.PathExtension or IntrinsicId.PathStem or
        IntrinsicId.EnvGet or IntrinsicId.ProcessWhich => IrTypeRef.Primitive("string"),
        IntrinsicId.ProcessRun or IntrinsicId.ProcessPipeline => IrTypeRef.Structural(new[]
        {
            new IrStructuralField("code", IrTypeRef.Primitive("int"), false),
            new IrStructuralField("stdout", IrTypeRef.Primitive("string"), false),
            new IrStructuralField("stderr", IrTypeRef.Primitive("string"), false),
            new IrStructuralField("ok", IrTypeRef.Primitive("bool"), false)
        }),
        IntrinsicId.HttpGet or IntrinsicId.HttpPost => IrTypeRef.Structural(new[]
        {
            new IrStructuralField("status", IrTypeRef.Primitive("int"), false),
            new IrStructuralField("body", IrTypeRef.Primitive("string"), false),
            new IrStructuralField("ok", IrTypeRef.Primitive("bool"), false),
            new IrStructuralField("error", IrTypeRef.Primitive("string"), false)
        }),
        _ => IrTypeRef.Any
    };
}
