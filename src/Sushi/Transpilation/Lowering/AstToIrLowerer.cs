namespace Sushi.Transpilation.Lowering;

using Sushi.Build.SyntaxTree;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed class AstToIrLowerer
{
    private const string UnsupportedSyntaxCode = "SUSHI1001";
    private const string UnsupportedParameterCode = "SUSHI1002";
    private const string UnsupportedCallCode = "SUSHI1003";
    private const string InvalidAssignmentTargetCode = "SUSHI1004";
    private const string UnresolvedNamedCallCode = "SUSHI1017";

    private readonly List<Diagnostic> _diagnostics = new();
    private readonly IntrinsicRegistry _intrinsicRegistry = IntrinsicRegistry.CreateDefault();
    private readonly Dictionary<string, List<IrFunctionParameter>> _functionSignatures = new();
    private string _sourcePath = "";

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public IrProgram Lower(ProgramNode program, string sourcePath)
    {
        _sourcePath = sourcePath;
        _functionSignatures.Clear();
        CollectFunctionSignatures(program);

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
        var parameters = _functionSignatures.TryGetValue(node.Name, out var signature)
            ? signature.Select(parameter => new IrFunctionParameter(parameter.Name, parameter.IsVarargs, parameter.DefaultValue)).ToList()
            : new List<IrFunctionParameter>();

        for (var i = 0; i < node.Parameters.Count; i++)
        {
            var parameter = node.Parameters[i];
            if (parameter.StructuralType != null)
            {
                AddDiagnostic(
                    UnsupportedParameterCode,
                    "Structural parameters are not yet supported in transpilation",
                    parameter.Line,
                    parameter.Column);
            }

            if (parameter.IsVarargs && i != node.Parameters.Count - 1)
            {
                AddDiagnostic(
                    FunctionCallBinder.InvalidVarargsDeclarationCode,
                    $"Function '{node.Name}' has an invalid varargs declaration. Varargs must be the final parameter.",
                    parameter.Line,
                    parameter.Column);
            }
        }

        if (parameters.Count == 0)
        {
            foreach (var parameter in node.Parameters)
            {
                parameters.Add(new IrFunctionParameter(
                    parameter.Name,
                    parameter.IsVarargs,
                    parameter.DefaultValue != null ? LowerExpression(parameter.DefaultValue) : null));
            }
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

        return new IrFunctionDeclarationStatement(node.Name, parameters, body);
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
            ArrayLiteralExpressionNode array => new IrArrayLiteralExpression(array.Elements.Select(LowerExpression)),
            ObjectLiteralExpressionNode obj => LowerObjectLiteral(obj),
            MemberAccessExpressionNode member => new IrMemberAccessExpression(LowerExpression(member.Object), member.MemberName),
            IndexExpressionNode index => new IrIndexExpression(LowerExpression(index.Array), LowerExpression(index.Index)),
            CallExpressionNode call => LowerCall(call),
            _ => UnsupportedExpression(node)
        };
    }

    private IrExpression LowerObjectLiteral(ObjectLiteralExpressionNode node)
    {
        var properties = node.Properties
            .Select(property => new IrObjectProperty(property.Name, LowerExpression(property.Value)))
            .ToList();

        return new IrObjectLiteralExpression(properties);
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
        var intrinsicArguments = node.Arguments
            .Select(argument => new IntrinsicCallArgument(
                argument.Name,
                LowerExpression(argument.Value),
                argument.Line,
                argument.Column))
            .ToList();

        if (TryGetCalleePath(node.Callee, out var calleePath))
        {
            if (_intrinsicRegistry.TryResolve(calleePath, out var signature))
            {
                var binding = IntrinsicCallBinder.Bind(
                    signature,
                    intrinsicArguments,
                    _sourcePath,
                    node.Line,
                    node.Column);

                foreach (var diagnostic in binding.Diagnostics)
                {
                    _diagnostics.Add(diagnostic);
                }

                if (!binding.Success)
                {
                    return new IrLiteralExpression(null);
                }

                return new IrIntrinsicCallExpression(
                    signature.CanonicalName,
                    signature.Id,
                    binding.OrderedArguments);
            }

            if (calleePath.StartsWith("std.", StringComparison.Ordinal))
            {
                AddDiagnostic(
                    IntrinsicDiagnosticCodes.UnknownIntrinsic,
                    $"Unknown intrinsic '{calleePath}'",
                    node.Line,
                    node.Column);
                return new IrLiteralExpression(null);
            }
        }

        if (node.Callee is not IdentifierExpressionNode callee)
        {
            AddDiagnostic(
                UnsupportedCallCode,
                "Only identifier-based function calls are supported in Milestone 1 transpilation",
                node.Line,
                node.Column);
            return new IrLiteralExpression(null);
        }

        var loweredArguments = node.Arguments
            .Select(argument => new IrCallArgument(
                argument.Name,
                LowerExpression(argument.Value),
                argument.Line,
                argument.Column))
            .ToList();

        if (_functionSignatures.TryGetValue(callee.Name, out var functionParameters))
        {
            var binding = FunctionCallBinder.Bind(
                callee.Name,
                functionParameters,
                loweredArguments,
                _sourcePath,
                node.Line,
                node.Column);

            foreach (var diagnostic in binding.Diagnostics)
            {
                _diagnostics.Add(diagnostic);
            }

            if (!binding.Success)
            {
                return new IrLiteralExpression(null);
            }

            return new IrCallExpression(callee.Name, binding.OrderedArguments);
        }

        if (loweredArguments.Any(argument => argument.Name != null))
        {
            AddDiagnostic(
                UnresolvedNamedCallCode,
                $"Named arguments require a known Sushi function signature. Could not resolve '{callee.Name}'.",
                node.Line,
                node.Column);
            return new IrLiteralExpression(null);
        }

        return new IrCallExpression(callee.Name, loweredArguments);
    }

    private void CollectFunctionSignatures(ProgramNode program)
    {
        foreach (var declaration in program.Declarations)
        {
            if (declaration is not FunctionDeclarationNode function)
            {
                continue;
            }

            var parameters = new List<IrFunctionParameter>();
            foreach (var parameter in function.Parameters)
            {
                parameters.Add(new IrFunctionParameter(
                    parameter.Name,
                    parameter.IsVarargs,
                    parameter.DefaultValue != null ? LowerExpression(parameter.DefaultValue) : null));
            }

            _functionSignatures[function.Name] = parameters;
        }
    }

    private static bool TryGetCalleePath(ExpressionNode callee, out string path)
    {
        switch (callee)
        {
            case IdentifierExpressionNode identifier:
                path = identifier.Name;
                return true;

            case MemberAccessExpressionNode memberAccess:
                if (!TryGetCalleePath(memberAccess.Object, out var objectPath))
                {
                    path = "";
                    return false;
                }

                path = $"{objectPath}.{memberAccess.MemberName}";
                return true;

            default:
                path = "";
                return false;
        }
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
