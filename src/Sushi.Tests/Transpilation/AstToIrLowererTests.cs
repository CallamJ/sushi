namespace Sushi.Tests.Transpilation;

using System.Linq;
using Sushi.Build;
using Sushi.Build.SyntaxTree;
using Sushi.Transpilation;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Lowering;
using Xunit;

public class AstToIrLowererTests
{
    [Fact]
    public void Lower_SimpleProgram_ProducesIrWithoutErrors()
    {
        const string source = """
            var x = 1
            println(x)
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(program, "test.sushi");

        Assert.NotNull(ir);
        Assert.Equal(2, ir.Statements.Count);
        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Lower_ClassDeclaration_ReportsUnsupportedSyntax()
    {
        const string source = """
            class Person {
                string name
            }
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(program, "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1001");
    }

    [Fact]
    public void Lower_NamedArgumentCall_ReportsUnsupportedCallShape()
    {
        const string source = """
            greet(name: "Alice")
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(program, "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1003");
    }

    private static ProgramNode Parse(string source)
    {
        var tokenizer = new Tokenizer(source);
        var tokens = tokenizer.Tokenize().ToList();
        var lexer = new Lexer(tokens);
        var classifiedTokens = lexer.Lex().ToList();
        var parser = new Parser(classifiedTokens);
        return parser.Parse();
    }
}
