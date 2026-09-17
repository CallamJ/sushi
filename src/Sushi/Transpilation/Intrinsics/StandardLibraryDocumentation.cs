namespace Sushi.Transpilation.Intrinsics;

using System.Reflection;

/// <summary>Loads the canonical Markdown shown by editors and generated API references.</summary>
internal static class StandardLibraryDocumentation
{
    private const string ResourcePrefix = "Sushi.StandardLibraryDocs.";
    private static readonly Assembly Assembly = typeof(StandardLibraryDocumentation).Assembly;

    public static string For(string canonicalName)
    {
        using var stream = Assembly.GetManifestResourceStream(ResourcePrefix + canonicalName + ".md")
            ?? throw new InvalidOperationException($"Missing standard-library documentation resource '{canonicalName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
