namespace Sushi.Transpilation.Intrinsics;

/// <summary>
/// Editor-facing view of Sushi's standard API. Compiler intrinsics are sourced
/// directly from <see cref="IntrinsicRegistry"/> so lowering and IDE tooling
/// cannot drift. Language-level conversions live beside that same API surface.
/// </summary>
public sealed class StandardLibraryCatalog
{
    private readonly Dictionary<string, StandardLibraryFunction> _functions;

    private StandardLibraryCatalog(IEnumerable<StandardLibraryFunction> functions)
    {
        _functions = functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<StandardLibraryFunction> Functions => _functions.Values;

    public bool TryGetFunction(string name, out StandardLibraryFunction function) =>
        _functions.TryGetValue(name, out function!);

    public static StandardLibraryCatalog CreateDefault()
    {
        var intrinsicFunctions = IntrinsicRegistry.CreateDefault().Signatures.Select(signature => new StandardLibraryFunction(
            signature.CanonicalName,
            signature.Parameters.Select(parameter => new StandardLibraryParameter(
                parameter.TypeName,
                parameter.Name,
                parameter.IsVariadic,
                parameter.HasDefaultValue)).ToArray(),
            signature.ReturnType.Name ?? "object",
            signature.DeprecationMessage ?? $"Standard library function `{signature.CanonicalName}`."));

        return new StandardLibraryCatalog(intrinsicFunctions.Append(new StandardLibraryFunction(
            "string",
            [new StandardLibraryParameter("object", "value", false, false)],
            "string",
            "Converts a value to its string representation.")));
    }
}

public sealed record StandardLibraryFunction(
    string Name,
    IReadOnlyList<StandardLibraryParameter> Parameters,
    string ReturnType,
    string Documentation);

public sealed record StandardLibraryParameter(string TypeName, string Name, bool IsVariadic, bool HasDefaultValue)
{
    public string DisplayName => $"{TypeName}{(IsVariadic ? "..." : "")} {Name}{(HasDefaultValue ? " = …" : "")}";
}
