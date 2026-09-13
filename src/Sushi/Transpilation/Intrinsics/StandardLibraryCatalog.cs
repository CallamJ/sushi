namespace Sushi.Transpilation.Intrinsics;

using System.Globalization;

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
                parameter.HasDefaultValue,
                parameter.DefaultValue)).ToArray(),
            signature.ReturnType.Name ?? "object",
            DocumentationFor(signature)));

        return new StandardLibraryCatalog(intrinsicFunctions.Append(new StandardLibraryFunction(
            "string",
            [new StandardLibraryParameter("object", "value", false, false, null)],
            "string",
            "Converts a value to its string representation.")));
    }

    private static string DocumentationFor(IntrinsicSignature signature) => signature.CanonicalName switch
    {
        "print" => "Writes a value without adding a trailing newline.",
        "println" => "Writes a value followed by a newline.",
        "std.target.shell" => "Returns the target shell name selected for compilation.",
        "std.target.platform" => "Returns the target platform selected for compilation.",
        _ when signature.DeprecationMessage is not null => signature.DeprecationMessage,
        _ => $"Standard library function `{signature.CanonicalName}`."
    };
}

public sealed record StandardLibraryFunction(
    string Name,
    IReadOnlyList<StandardLibraryParameter> Parameters,
    string ReturnType,
    string Documentation);

public sealed record StandardLibraryParameter(string TypeName, string Name, bool IsVariadic, bool HasDefaultValue, object? DefaultValue)
{
    public string DisplayName => $"{TypeName}{(IsVariadic ? "..." : "")} {Name}{(HasDefaultValue ? $" = {FormatDefault(DefaultValue)}" : "")}";

    private static string FormatDefault(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null"
    };
}
