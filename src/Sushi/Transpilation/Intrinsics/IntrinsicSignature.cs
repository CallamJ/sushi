namespace Sushi.Transpilation.Intrinsics;

using Sushi.Transpilation.IR;

public sealed class IntrinsicParameter
{
    public string Name { get; }
    public bool IsVariadic { get; }
    public bool HasDefaultValue { get; }
    public object? DefaultValue { get; }

    public IntrinsicParameter(
        string name,
        bool isVariadic = false,
        bool hasDefaultValue = false,
        object? defaultValue = null)
    {
        Name = name;
        IsVariadic = isVariadic;
        HasDefaultValue = hasDefaultValue;
        DefaultValue = defaultValue;
    }
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
        IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or
        IntrinsicId.StringIsMatch or IntrinsicId.IoExists or IntrinsicId.EnvHas => IrTypeRef.Primitive("bool"),
        IntrinsicId.StringSplit or IntrinsicId.ProcessArgs or IntrinsicId.FsGlob => IrTypeRef.Primitive("array"),
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
