using Sushi.Application.LanguageServer;
using Xunit;

namespace Sushi.Tests.Application;

public sealed class SemanticWorkspaceTests
{
    [Fact]
    public void ResolvesSharedTypesForLiteralsAndDeclarations()
    {
        const string source = "string name = \"sushi\";\nvar count = 1;\n";
        var workspace = new SemanticWorkspace();
        var model = workspace.Analyze("file:///semantic.sushi", source);

        var name = model.Symbols.Single(symbol => symbol.Name == "name");
        var count = model.Symbols.Single(symbol => symbol.Name == "count");

        Assert.Equal(SushiTypeKind.String, name.Type.Kind);
        Assert.Equal(SushiTypeKind.Unknown, count.Type.Kind);
        Assert.Equal("int", model.TypeAt(source.IndexOf("count", StringComparison.Ordinal) + 2));
        Assert.Equal("string", model.TypeAt(source.IndexOf("name", StringComparison.Ordinal) + 2));
    }

    [Fact]
    public void ResolvesQualifiedNamesFromTheSameDocumentSnapshot()
    {
        const string source = "use std.fs as fs;\nfs.glob(\"*\");\n";
        var workspace = new SemanticWorkspace();
        var model = workspace.Analyze("file:///semantic.sushi", source);
        var token = model.Tokens.Single(token => token.Text == "glob");

        Assert.Equal("fs.glob", model.QualifiedNameAt(token));
    }
}
