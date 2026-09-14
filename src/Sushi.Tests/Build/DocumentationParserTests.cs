namespace Sushi.Tests.Build;

using System.Linq;
using Sushi.Build;
using Xunit;

public sealed class DocumentationParserTests
{
    [Fact]
    public void ParsesTripleSlashCommentsTagsAndLinks()
    {
        const string source = "/// Greets a person.\n///\n/// {@link Person person type}\n/// @param name Their name.\n/// @returns A greeting.\nstring greet(string name) { return name }";

        var comment = Assert.Single(DocumentationParser.Parse(source));

        Assert.Equal("Greets a person.", comment.Summary);
        Assert.Equal("Their name.", comment.ParameterDocumentation("name"));
        Assert.Equal("A greeting.", comment.ReturnsDocumentation);
        Assert.Equal("Person", Assert.Single(DocumentationParser.Links(comment)).Target);
        Assert.Empty(DocumentationParser.ValidateSource(source));
    }

    [Fact]
    public void ReportsUnknownAndMismatchedTagsAsWarnings()
    {
        const string source = "/// Does work.\n/// @parm value typo\n/// @param missing nope\nstring work(string value) { return value }";

        var codes = DocumentationParser.ValidateSource(source).Select(issue => issue.Code).ToArray();

        Assert.Contains("SUSHI1101", codes);
        Assert.Contains("SUSHI1103", codes);
        Assert.Contains("SUSHI1107", codes);
    }

    [Fact]
    public void AcceptsDocumentationForUntypedParameters()
    {
        const string source = "///\n/// @param param\n/// @returns\nname(param) {}";

        Assert.Empty(DocumentationParser.ValidateSource(source));
    }
}
