namespace Sushi.Tests.Application;

using System.IO;
using System.Text;
using System.Threading.Tasks;
using Sushi.Application.LanguageServer;
using Xunit;

public sealed class LanguageServerTests
{
    [Fact]
    public async Task Server_ShowsDocumentationForConstructors()
    {
        const string source = "class Person {\n    /// Creates a person.\n    /// @param name Initial name.\n    new(string name) {}\n}";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///tmp/constructor-docs.sushi\",\"version\":1,\"text\":{JsonString(source)}}}}}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":28,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/constructor-docs.sushi\"},\"position\":{\"line\":3,\"character\":5}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Creates a person.", wire);
        Assert.Contains("new(string name)", wire);
    }

    [Fact]
    public async Task Server_ShowsBuiltInDocumentationForPrintln()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/println-docs.sushi\",\"version\":1,\"text\":\"println(\\\"hello\\\")\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":29,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/println-docs.sushi\"},\"position\":{\"line\":0,\"character\":3}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Writes a value followed by a newline.", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Server_ShowsFieldDocumentationForObjectMemberAccess()
    {
        const string source = "class Person {\n    /// The person's display name.\n    string name\n}\nvar person = new Person()\nprintln(person.name)";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///tmp/field-docs.sushi\",\"version\":1,\"text\":{JsonString(source)}}}}}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":30,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/field-docs.sushi\"},\"position\":{\"line\":5,\"character\":15}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("The person\\u0027s display name.", wire);
    }

    [Fact]
    public async Task Server_ProvidesDocumentationInHoverCompletionSignatureAndLinks()
    {
        const string source = "/// Greets a person.\n/// {@link Person}\n/// @param name Person name.\n/// @returns Greeting text.\nstring greet(string name) { return name }\nclass Person {}\ngreet(\"Ada\")";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"initialize\",\"params\":{}}") +
            Frame($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///tmp/docs.sushi\",\"version\":1,\"text\":{JsonString(source)}}}}}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":32,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/docs.sushi\"},\"position\":{\"line\":4,\"character\":8}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":36,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/docs.sushi\"},\"position\":{\"line\":6,\"character\":0}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":33,\"method\":\"textDocument/signatureHelp\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/docs.sushi\"},\"position\":{\"line\":6,\"character\":7}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":34,\"method\":\"textDocument/documentLink\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/docs.sushi\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":35,\"method\":\"textDocument/codeAction\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/docs.sushi\"},\"range\":{\"start\":{\"line\":5,\"character\":7},\"end\":{\"line\":5,\"character\":7}},\"context\":{\"diagnostics\":[]}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("documentLinkProvider", wire);
        Assert.Contains("Greets a person.", wire);
        Assert.Contains("\"id\":36", wire);
        Assert.Contains("Person name.", wire);
        Assert.Contains("file:///tmp/docs.sushi#L6", wire);
        Assert.Contains("Generate documentation for \\u0027Person\\u0027", wire);
    }

    [Fact]
    public async Task Server_AdvertisesCoreCapabilitiesAndPublishesDiagnostics()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/example.sushi\",\"version\":1,\"text\":\"var value =\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/example.sushi\"},\"position\":{\"line\":0,\"character\":3}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("semanticTokensProvider", wire);
        Assert.Contains("textDocument/publishDiagnostics", wire);
        Assert.Contains("println", wire);
    }

    [Fact]
    public async Task Server_RenamesIdentifierOccurrences()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/rename.sushi\",\"version\":1,\"text\":\"var value = 1\\nprintln(value)\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/rename\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/rename.sushi\"},\"position\":{\"line\":0,\"character\":5},\"newName\":\"answer\"}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("answer", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Server_UsesItsAdvertisedSemanticTokenLegend()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/tokens.sushi\",\"version\":1,\"text\":\"var value = 1\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"textDocument/semanticTokens/full\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/tokens.sushi\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        // keyword=9, variable=8, operator=10, number=12; the declaration modifier is bit 0.
        Assert.Contains("\"data\":[0,0,3,9,0,0,4,5,8,1,0,6,1,10,0,0,2,1,12,0]", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Server_SemanticTokensClassifyTypesAndComments()
    {
        const string source = "// a comment\nclass Person {}\nstring label\nvar person = new Person()";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///tmp/highlighting.sushi\",\"version\":1,\"text\":{JsonString(source)}}}}}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"textDocument/semanticTokens/full\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/highlighting.sushi\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        // comment=13, variable=8, keyword=9. Built-in types are keywords;
        // user-defined classes remain ordinary identifiers.
        Assert.Contains("0,0,12,13,0", wire);
        Assert.Contains("0,6,6,8,1", wire);
        Assert.Contains("1,0,6,9,0", wire);
        Assert.Contains("0,4,6,8,1", wire);
        Assert.Contains("0,4,6,8,0", wire);
    }

    [Fact]
    public async Task Server_ReturnsExplicitNullForHoverMiss()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/hover.sushi\",\"version\":1,\"text\":\"println(1)\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/hover.sushi\"},\"position\":{\"line\":0,\"character\":8}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("\"id\":5,\"result\":null", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Server_SupportsSymbolNavigationAndSuccessfulHover()
    {
        var source = "var value = 1\nprintln(value)";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///tmp/navigation.sushi\",\"version\":1,\"text\":{JsonString(source)}}}}}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/navigation.sushi\"},\"position\":{\"line\":0,\"character\":4}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"textDocument/definition\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/navigation.sushi\"},\"position\":{\"line\":1,\"character\":8}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"textDocument/references\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/navigation.sushi\"},\"position\":{\"line\":1,\"character\":8}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"textDocument/documentSymbol\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/navigation.sushi\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"id\":6,\"result\":{\"contents\":{\"kind\":\"markdown\",\"value\":\"\\u0060\\u0060\\u0060sushi", wire);
        Assert.Contains("\"id\":7,\"result\":[{\"uri\":\"file:///tmp/navigation.sushi\"", wire);
        Assert.Contains("\"id\":8,\"result\":[", wire);
        Assert.Contains("\"id\":9,\"result\":[{\"name\":\"value\"", wire);
    }

    [Fact]
    public async Task Server_HandlesPrepareRenameAndIncrementalChanges()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/change.sushi\",\"version\":1,\"text\":\"var value =\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/change.sushi\",\"version\":2},\"contentChanges\":[{\"text\":\"var value = 1\"}]}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"textDocument/prepareRename\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/change.sushi\"},\"position\":{\"line\":0,\"character\":4}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"textDocument/prepareRename\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/change.sushi\"},\"position\":{\"line\":0,\"character\":0}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/change.sushi\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"id\":10,\"result\":{\"start\":", wire);
        Assert.Contains("\"id\":11,\"result\":null", wire);
        Assert.Contains("\"diagnostics\":[]", wire);
    }

    [Fact]
    public async Task Server_ReportsUnsupportedRequestsAndSupportsShutdown()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"workspace/unknown\",\"params\":{}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"shutdown\"}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"id\":12,\"error\":{\"code\":-32601", wire);
        Assert.Contains("\"id\":13,\"result\":null", wire);
    }

    [Fact]
    public async Task Server_ProvidesReadOnlyEditorFeatures()
    {
        const string source = "string greet(string name) {\n    return name\n}\nvar count = 1\nprintln(greet(\"Ada\"))\nstd.fs.readText(\"path\")";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame("{\"jsonrpc\":\"2.0\",\"id\":15,\"method\":\"initialize\",\"params\":{}}") +
            Frame($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///tmp/features.sushi\",\"version\":1,\"text\":{JsonString(source)}}}}}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":16,\"method\":\"textDocument/documentHighlight\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/features.sushi\"},\"position\":{\"line\":0,\"character\":7}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":17,\"method\":\"textDocument/signatureHelp\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/features.sushi\"},\"position\":{\"line\":4,\"character\":14}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":24,\"method\":\"textDocument/signatureHelp\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/features.sushi\"},\"position\":{\"line\":4,\"character\":8}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":25,\"method\":\"textDocument/signatureHelp\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/features.sushi\"},\"position\":{\"line\":5,\"character\":16}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":18,\"method\":\"textDocument/foldingRange\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/features.sushi\"}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":19,\"method\":\"textDocument/inlayHint\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/features.sushi\"},\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":5,\"character\":0}}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"workspace/symbol\",\"params\":{\"query\":\"greet\"}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("signatureHelpProvider", wire);
        Assert.Contains("\"id\":16,\"result\":[", wire);
        Assert.Contains("\"id\":17,\"result\":{\"signatures\":[{\"label\":\"greet(string name)\"", wire);
        Assert.Contains("\"parameters\":[{\"label\":\"string name\"}]", wire);
        Assert.Contains("\"id\":24,\"result\":{\"signatures\":[{\"label\":\"println(object value = \\u0022\\u0022)\"", wire);
        Assert.Contains("\"id\":25,\"result\":{\"signatures\":[{\"label\":\"std.fs.readText(string path)\"", wire);
        Assert.Contains("\"id\":18,\"result\":[{\"startLine\":0", wire);
        Assert.Contains("\"id\":19,\"result\":[{\"position\":{\"line\":3,\"character\":9},\"label\":\": int\"", wire);
        Assert.Contains("\"id\":20,\"result\":[{\"name\":\"greet\"", wire);
    }

    [Fact]
    public async Task Server_FormatsAndOffersSafeVariableRefactors()
    {
        const string source = "if (true) {\nprintln(\"ok\")\n}\nvar answer = 42\nprintln(answer)";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            Frame($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///tmp/refactor.sushi\",\"version\":1,\"text\":{JsonString(source)}}}}}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"textDocument/formatting\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/refactor.sushi\"},\"options\":{\"tabSize\":4,\"insertSpaces\":true}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"textDocument/codeAction\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/refactor.sushi\"},\"range\":{\"start\":{\"line\":3,\"character\":4},\"end\":{\"line\":3,\"character\":10}},\"context\":{\"diagnostics\":[]}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"id\":23,\"method\":\"textDocument/codeAction\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/refactor.sushi\"},\"range\":{\"start\":{\"line\":3,\"character\":13},\"end\":{\"line\":3,\"character\":15}},\"context\":{\"diagnostics\":[]}}}") +
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")));
        var output = new MemoryStream();

        await new SushiLanguageServer(input, output, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken);

        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"id\":21,\"result\":[{\"range\":", wire);
        Assert.Contains("    println", wire);
        Assert.Contains("\"id\":22,\"result\":[{\"title\":\"Inline \\u0027answer\\u0027\"", wire);
        Assert.Contains("\"id\":23,\"result\":[{\"title\":\"Extract variable \\u0027extractedValue\\u0027\"", wire);
    }

    private static string Frame(string json) => $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
