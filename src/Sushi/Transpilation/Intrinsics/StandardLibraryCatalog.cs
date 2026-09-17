namespace Sushi.Transpilation.Intrinsics;

using System.Globalization;
using Sushi.Transpilation.IR;

/// <summary>
/// Editor-facing view of Sushi's standard API. Compiler intrinsics are sourced
/// directly from <see cref="IntrinsicRegistry"/> so lowering and IDE tooling
/// cannot drift. Language-level conversions live beside that same API surface.
/// </summary>
public sealed class StandardLibraryCatalog
{
    private readonly Dictionary<string, StandardLibraryFunction> _functions;
    private readonly Dictionary<string, StandardLibraryFunction> _fileQueryMembers;

    private StandardLibraryCatalog(IEnumerable<StandardLibraryFunction> functions, IEnumerable<StandardLibraryFunction>? fileQueryMembers = null)
    {
        _functions = functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
        _fileQueryMembers = (fileQueryMembers ?? Array.Empty<StandardLibraryFunction>())
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<StandardLibraryFunction> Functions => _functions.Values;
    public IReadOnlyCollection<StandardLibraryFunction> FileQueryMembers => _fileQueryMembers.Values;

    public bool TryGetFunction(string name, out StandardLibraryFunction function) =>
        _functions.TryGetValue(name, out function!);

    public bool TryGetFileQueryMember(string name, out StandardLibraryFunction function) =>
        _fileQueryMembers.TryGetValue(name, out function!);

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
            DisplayType(signature.ReturnType),
            StandardLibraryDocumentation.For(signature.CanonicalName)));

        var fileQueryMembers = new[]
        {
            new StandardLibraryFunction("FileQuery.recursive", [], "FileQuery", StandardLibraryDocumentation.For("FileQuery.recursive")),
            new StandardLibraryFunction("FileQuery.matching", [new StandardLibraryParameter("string", "pattern", false, false, null)], "FileQuery", StandardLibraryDocumentation.For("FileQuery.matching")),
            new StandardLibraryFunction("FileQuery.excluding", [new StandardLibraryParameter("string", "pattern", false, false, null)], "FileQuery", StandardLibraryDocumentation.For("FileQuery.excluding")),
            new StandardLibraryFunction("FileQuery.includingHidden", [], "FileQuery", StandardLibraryDocumentation.For("FileQuery.includingHidden")),
            new StandardLibraryFunction("FileQuery.hidden", [], "FileQuery", StandardLibraryDocumentation.For("FileQuery.hidden")),
            new StandardLibraryFunction("FileQuery.files", [], "string[]", StandardLibraryDocumentation.For("FileQuery.files")),
            new StandardLibraryFunction("FileQuery.directories", [], "string[]", StandardLibraryDocumentation.For("FileQuery.directories")),
            new StandardLibraryFunction("FileQuery.entries", [], "string[]", StandardLibraryDocumentation.For("FileQuery.entries"))
        };

        return new StandardLibraryCatalog(intrinsicFunctions
            .Append(new StandardLibraryFunction(
                "std.fs.query",
                [new StandardLibraryParameter("string", "root", false, true, ".")],
                "FileQuery",
                StandardLibraryDocumentation.For("std.fs.query")))
            .Append(new StandardLibraryFunction(
                "string",
                [new StandardLibraryParameter("object", "value", false, false, null)],
                "string",
                StandardLibraryDocumentation.For("string"))), fileQueryMembers);
    }

    private static string DisplayType(IrTypeRef type)
    {
        if (type.Name == "array") return $"{DisplayType(type.ElementType ?? IrTypeRef.Any)}[]";
        return type.Name ?? "any";
    }

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
