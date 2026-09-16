namespace Sushi.Application.LanguageServer;

using Sushi.Build.SyntaxTree;

/// <summary>
/// Shared semantic workspace used by language features. Documents are cached by content,
/// which keeps a completion/hover/definition request on one consistent snapshot while an
/// editor is editing. The model itself remains error tolerant and can be built from partial
/// source, so clients do not need a successful compilation before receiving assistance.
/// </summary>
internal sealed class SemanticWorkspace
{
    private readonly Dictionary<string, (string Text, SushiSemanticModel Model)> _documents = new(StringComparer.Ordinal);

    public SushiSemanticModel Analyze(string uri, string text)
    {
        if (_documents.TryGetValue(uri, out var cached) && String.Equals(cached.Text, text, StringComparison.Ordinal))
            return cached.Model;
        var model = SushiSemanticModel.Create(text);
        _documents[uri] = (text, model);
        return model;
    }

    public void Invalidate(string uri) => _documents.Remove(uri);
    public void Clear() => _documents.Clear();

    public SushiSymbol? SymbolAt(string uri, string text, int offset) => Analyze(uri, text).SymbolAt(offset);

    public IEnumerable<SushiSymbol> VisibleSymbols(string uri, string text) => Analyze(uri, text).Symbols;

    public string? TypeAt(string uri, string text, int offset) => Analyze(uri, text).TypeAt(offset);

    public string QualifiedNameAt(string uri, string text, ClassifiedToken token) => Analyze(uri, text).QualifiedNameAt(token);
}
