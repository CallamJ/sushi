namespace Sushi.Tests.Transpilation;

using System.Linq;
using Sushi.Build;
using Sushi.Build.SyntaxTree;
using Sushi.Transpilation;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;
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
    public void Lower_NewExpression_ProducesDedicatedConstructionIr()
    {
        const string source = "class Person { string name }\nvar person = new Person(\"Ada\")";
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(Parse(source), "test.sushi");
        var declaration = Assert.IsType<IrVariableDeclarationStatement>(ir.Statements.Last());
        var construction = Assert.IsType<IrConstructionExpression>(declaration.Initializer);
        Assert.Equal("Person", construction.TypeName);
    }

    [Fact]
    public void Lower_KnownMethodsAndAdapters_ProduceDedicatedIr()
    {
        const string source = """
            class Person {
                string name
                string label() -> this.name
                string() -> this.name
            }
            var person = new Person("Ada")
            var label = person.label()
            var text = string(person)
            """;
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(Parse(source), "test.sushi");
        var declarations = ir.Statements.OfType<IrVariableDeclarationStatement>().ToList();
        Assert.IsType<IrResolvedMethodCallExpression>(declarations.Single(item => item.Name == "label").Initializer);
        Assert.IsType<IrAdapterCallExpression>(declarations.Single(item => item.Name == "text").Initializer);
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
    public void Lower_StringMethodSugar_ProducesStringIntrinsicIr()
    {
        const string source = """
            var text = "  Hello  "
            var lowered = text.trim().lower()
            var parts = lowered.split("e", limit: 2)
            """;

        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(program, "test.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var loweredDeclaration = Assert.IsType<IrVariableDeclarationStatement>(ir.Statements[1]);
        var loweredCall = Assert.IsType<IrIntrinsicCallExpression>(loweredDeclaration.Initializer);
        Assert.Equal(IntrinsicId.StringLower, loweredCall.Id);

        var partsDeclaration = Assert.IsType<IrVariableDeclarationStatement>(ir.Statements[2]);
        var partsCall = Assert.IsType<IrIntrinsicCallExpression>(partsDeclaration.Initializer);
        Assert.Equal(IntrinsicId.StringSplit, partsCall.Id);
        Assert.Equal(3, partsCall.Arguments.Count);
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
    public void Lower_InferredFunctionReturnType_IsUsedByCallers()
    {
        const string source = """
            answer() { return 42 }
            int value = answer()
            """;

        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(Parse(source), "test.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var function = Assert.IsType<IrFunctionDeclarationStatement>(ir.Statements[0]);
        Assert.Equal("int", function.ReturnType.Name);
    }

    [Fact]
    public void Lower_InferredReturnTypes_WidenIntegersAndFloats()
    {
        const string source = """
            number() {
                if (true) return 1
                return 2.5
            }
            """;

        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(Parse(source), "test.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var function = Assert.IsType<IrFunctionDeclarationStatement>(ir.Statements[0]);
        Assert.Equal("float", function.ReturnType.Name);
    }

    [Fact]
    public void Lower_IncompatibleInferredReturnTypes_ReportDiagnostic()
    {
        const string source = """
            mixed() {
                if (true) return "text"
                return 1
            }
            """;

        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(Parse(source), "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1049");
    }

    [Fact]
    public void Lower_VoidFunctionRejectsValueReturn()
    {
        const string source = "void log() { return 1 }";
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(Parse(source), "test.sushi");

        Assert.Contains(lowerer.Diagnostics, d => d.Code == "SUSHI1051");
    }

    [Fact]
    public void Lower_VariableTypesAreLockedUnlessDeclaredAny()
    {
        const string source = """
            var inferred = 1
            inferred = "wrong"
            any flexible = 1
            flexible = "allowed"
        """;

        var lowerer = new AstToIrLowerer();
        var ir = lowerer.Lower(Parse(source), "test.sushi");

        Assert.IsType<IrExpressionStatement>(ir.Statements[1]);
        Assert.Single(lowerer.Diagnostics, d => d.Code == "SUSHI1049");
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

    [Theory]
    [InlineData("break", "SUSHI1027")]
    [InlineData("continue", "SUSHI1028")]
    [InlineData("return 1", "SUSHI1029")]
    public void Lower_InvalidControlFlowContext_ReportsDiagnostic(string source, string code)
    {
        var program = Parse(source);
        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(program, "control-flow.sushi");

        Assert.Contains(lowerer.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void Lower_LoopControlInsideFunctionLoop_IsValid()
    {
        const string source = """
            work() {
                while (true) {
                    continue
                    break
                }
                return 1
            }
            """;

        var lowerer = new AstToIrLowerer();
        _ = lowerer.Lower(Parse(source), "control-flow.sushi");

        Assert.DoesNotContain(lowerer.Diagnostics, diagnostic =>
            diagnostic.Code is "SUSHI1027" or "SUSHI1028" or "SUSHI1029");
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
