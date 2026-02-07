namespace Sushi.Transpilation.Intrinsics;

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

    public IntrinsicSignature(string canonicalName, IntrinsicId id, IEnumerable<IntrinsicParameter> parameters)
    {
        CanonicalName = canonicalName;
        Id = id;
        Parameters = parameters.ToList();
    }
}
