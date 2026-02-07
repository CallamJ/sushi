namespace Sushi.Transpilation.Lowering;

using Sushi.Build.SyntaxTree;
using Sushi.Transpilation.IR;

public sealed class AstToIrLowerer
{
    private const string UnsupportedSyntaxCode = "SUSHI1001";
    private const string UnsupportedParameterCode = "SUSHI1002";
    private const string UnsupportedCallCode = "SUSHI1003";
    private const string InvalidAssignmentTargetCode = "SUSHI1004";

    private readonly List<Diagnostic> _diagnostics = new();
    private string _sourcePath = "";

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public IrProgram Lower(ProgramNode program, string sourcePath)
    {
        _sourcePath = sourcePath;
        var output = new IrProgram();

        foreach (var declaration in program.Declarations)
        {
            var lowered = LowerTopLevel(declaration);
            if (lowered != null)
            {
                output.Statements.Add(lowered);
            }
        }

        return output;
    }

    private IrStatement? LowerTopLevel(AstNode node)
    {
        return node switch
        {
            BoxDeclarationNode => null,
            UseDeclarationNode => null,
            FunctionDeclarationNode function => LowerFunction(function),
            StatementNode statement => LowerStatement(statement),
            _ => UnsupportedStatement(node, "Top-level declaration is not yet supported in transpilation")
        };
    }

    private IrFunctionDeclarationStatement? LowerFunction(FunctionDeclarationNode node)
    {
        var parameterNames = new List<string>();

        foreach (var parameter in node.Parameters)
        {
            if (parameter.StructuralType != null || parameter.IsVarargs || parameter.DefaultValue != null)
            {
                AddDiagnostic(
                    UnsupportedParameterCode,
                    "Structural parameters, varargs, and default values are not supported in Milestone 1 transpilation",
                    parameter.Line,
                    parameter.Column);
            }

            parameterNames.Add(parameter.Name);
        }

        var body = node.Body switch
        {
            BlockStatementNode block => LowerBlock(block),
            StatementNode statement => LowerSingleStatementBlock(statement),
            _ => null
        };

        if (body == null)
        {
            AddDiagnostic(UnsupportedSyntaxCode, "Function body could not be lowered", node.Line, node.Column);
            return null;
        }

        return new IrFunctionDeclarationStatement(node.Name, parameterNames, body);
    }

    private IrBlockStatement LowerBlock(BlockStatementNode block)
    {
        var statements = new List<IrStatement>();

        foreach (var statement in block.Statements)
        {
            var lowered = LowerStatement(statement);
            if (lowered != null)
            {
                statements.Add(lowered);
            }
        }

        return new IrBlockStatement(statements);
    }

    private IrStatement? LowerStatement(StatementNode node)
    {
        return node switch
        {
            BlockStatementNode block => LowerBlock(block),
            VariableDeclarationStatementNode declaration => new IrVariableDeclarationStatement(
                declaration.Name,
                declaration.Initializer != null ? LowerExpression(declaration.Initializer) : null),
            ExpressionStatementNode expressionStatement => new IrExpressionStatement(LowerExpression(expressionStatement.Expression)),
            IfStatementNode ifStatement => new IrIfStatement(
                LowerExpression(ifStatement.Condition),
                StatementToBlock(ifStatement.ThenBranch),
                ifStatement.ElseBranch != null ? StatementToBlock(ifStatement.ElseBranch) : null),
            WhileStatementNode whileStatement => new IrWhileStatement(
                LowerExpression(whileStatement.Condition),
                StatementToBlock(whileStatement.Body)),
            ForStatementNode forStatement => new IrForStatement(
                forStatement.Initializer != null ? LowerStatement(forStatement.Initializer) : null,
                forStatement.Condition != null ? LowerExpression(forStatement.Condition) : null,
                forStatement.Increment != null ? LowerExpression(forStatement.Increment) : null,
                StatementToBlock(forStatement.Body)),
            ReturnStatementNode returnStatement => new IrReturnStatement(
                returnStatement.Expression != null ? LowerExpression(returnStatement.Expression) : null),
            BreakStatementNode => new IrBreakStatement(),
            ContinueStatementNode => new IrContinueStatement(),
            _ => UnsupportedStatement(node, "Statement is not yet supported in Milestone 1 transpilation")
        };
    }

    private IrBlockStatement StatementToBlock(StatementNode statement)
    {
        if (statement is BlockStatementNode block)
        {
            return LowerBlock(block);
        }

        return LowerSingleStatementBlock(statement);
    }

    private IrBlockStatement LowerSingleStatementBlock(StatementNode statement)
    {
        var lowered = LowerStatement(statement);
        if (lowered == null)
        {
            return new IrBlockStatement();
        }

        return new IrBlockStatement(new[] { lowered });
    }

    private IrExpression LowerExpression(ExpressionNode node)
    {
        return node switch
        {
            LiteralExpressionNode literal => new IrLiteralExpression(literal.Value),
            IdentifierExpressionNode identifier => new IrIdentifierExpression(identifier.Name),
            ParenthesizedExpressionNode parenthesized => LowerExpression(parenthesized.Expression),
            UnaryExpressionNode unary => new IrUnaryExpression(unary.Operator, LowerExpression(unary.Operand), unary.IsPrefix),
            BinaryExpressionNode binary => LowerBinary(binary),
            CallExpressionNode call => LowerCall(call),
            _ => UnsupportedExpression(node)
        };
    }

    private IrExpression LowerBinary(BinaryExpressionNode node)
    {
        if (node.Operator is "=" or "+=" or "-=" or "*=" or "/=")
        {
            if (node.Left is not IdentifierExpressionNode identifier)
            {
                AddDiagnostic(
                    InvalidAssignmentTargetCode,
                    $"Assignment target for operator '{node.Operator}' must be an identifier",
                    node.Line,
                    node.Column);
                return new IrLiteralExpression(null);
            }

            return new IrAssignmentExpression(
                new IrIdentifierExpression(identifier.Name),
                node.Operator,
                LowerExpression(node.Right));
        }

        return new IrBinaryExpression(
            LowerExpression(node.Left),
            node.Operator,
            LowerExpression(node.Right));
    }

    private IrExpression LowerCall(CallExpressionNode node)
    {
        if (node.Callee is not IdentifierExpressionNode callee)
        {
            AddDiagnostic(
                UnsupportedCallCode,
                "Only identifier-based function calls are supported in Milestone 1 transpilation",
                node.Line,
                node.Column);
            return new IrLiteralExpression(null);
        }

        var arguments = new List<IrExpression>();
        foreach (var argument in node.Arguments)
        {
            if (argument.Name != null)
            {
                AddDiagnostic(
                    UnsupportedCallCode,
                    "Named arguments are not supported in Milestone 1 transpilation",
                    argument.Line,
                    argument.Column);
            }

            arguments.Add(LowerExpression(argument.Value));
        }

        return new IrCallExpression(callee.Name, arguments);
    }

    private IrExpression UnsupportedExpression(AstNode node)
    {
        AddDiagnostic(
            UnsupportedSyntaxCode,
            $"Expression '{node.GetType().Name}' is not yet supported in Milestone 1 transpilation",
            node.Line,
            node.Column);

        return new IrLiteralExpression(null);
    }

    private IrStatement? UnsupportedStatement(AstNode node, string message)
    {
        AddDiagnostic(UnsupportedSyntaxCode, message, node.Line, node.Column);
        return null;
    }

    private void AddDiagnostic(string code, string message, int line, int column)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, new SourceSpan(_sourcePath, line, column)));
    }
}
