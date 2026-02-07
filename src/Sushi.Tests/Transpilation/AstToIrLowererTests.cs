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
    public void Lower_ClassDeclaration_IsSupported()
    {
        const string source = """
            class Person {
                string name
            }
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(program, "test.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Code == "SUSHI1001");
        Assert.NotEmpty(ir.Statements);
    }

    [Fact]
    public void Lower_UnresolvedNamedArgumentCall_ReportsDiagnostic()
    {
        const string source = """
            greet(name: "Alice")
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(program, "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1017");
    }

    [Fact]
    public void Lower_KnownFunctionNamedArgumentCall_BindsToCallIr()
    {
        const string source = """
            greet(name, punctuation = "!") {
                return name
            }
            var value = greet(name: "Alice")
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(program, "test.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var declaration = Assert.IsType<IrVariableDeclarationStatement>(ir.Statements.Last());
        var call = Assert.IsType<IrCallExpression>(declaration.Initializer);
        Assert.Equal(2, call.Arguments.Count);
        Assert.All(call.Arguments, argument => Assert.Null(argument.Name));
    }

    [Fact]
    public void Lower_StdIntrinsicCall_ProducesIntrinsicIr()
    {
        const string source = """
            var exists = std.io.exists("a.txt")
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(program, "test.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var declaration = Assert.IsType<IrVariableDeclarationStatement>(ir.Statements[0]);
        Assert.IsType<IrIntrinsicCallExpression>(declaration.Initializer);
    }

    [Fact]
    public void Lower_UnknownStdIntrinsic_ReportsDiagnostic()
    {
        const string source = """
            var x = std.io.unknown("a.txt")
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(program, "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1301");
    }

    [Fact]
    public void Lower_ObjectAndMemberExpressions_ProduceExpectedIrShapes()
    {
        const string source = """
            var payload = { hello: "world", count: 2 }
            var name = payload.hello
            var first = ["a", "b"][0]
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(program, "test.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var payloadDecl = Assert.IsType<IrVariableDeclarationStatement>(ir.Statements[0]);
        Assert.IsType<IrObjectLiteralExpression>(payloadDecl.Initializer);

        var nameDecl = Assert.IsType<IrVariableDeclarationStatement>(ir.Statements[1]);
        Assert.IsType<IrMemberAccessExpression>(nameDecl.Initializer);

        var firstDecl = Assert.IsType<IrVariableDeclarationStatement>(ir.Statements[2]);
        Assert.IsType<IrIndexExpression>(firstDecl.Initializer);
    }

    [Fact]
    public void Lower_StructuralParameter_ProducesStructuralTypeContract()
    {
        const string source = """
            process(object { string name, int age } user) {
                return user.name
            }
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(program, "test.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Code == "SUSHI1002");
        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var function = Assert.IsType<IrFunctionDeclarationStatement>(ir.Statements[0]);
        Assert.Single(function.Parameters);
        Assert.Equal(IrTypeKind.Structural, function.Parameters[0].DeclaredType.Kind);
        Assert.Equal(2, function.Parameters[0].DeclaredType.StructuralFields.Count);
    }

    [Fact]
    public void Lower_StaticCallTypeMismatch_ReportsParameterDiagnostic()
    {
        const string source = """
            square(int x) {
                return x * x
            }
            var bad = square("nope")
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(program, "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1021");
    }

    [Fact]
    public void Lower_StaticReturnTypeMismatch_ReportsDiagnostic()
    {
        const string source = """
            int id() {
                return "abc"
            }
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(program, "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1024");
    }

    [Fact]
    public void Lower_StaticStructuralFieldMismatch_ReportsDiagnostic()
    {
        const string source = """
            send(object { string name, int age } user) {
                return user.name
            }
            var u = send({ name: "n", age: "bad" })
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(program, "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1023");
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
