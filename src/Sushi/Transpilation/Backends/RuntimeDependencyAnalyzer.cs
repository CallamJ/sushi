namespace Sushi.Transpilation.Backends;

using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

internal static class RuntimeDependencyAnalyzer
{
    public static bool RequiresRuntime(IrProgram program)
    {
        return program.Statements.Any(RequiresRuntime);
    }

    private static bool RequiresRuntime(IrStatement statement)
    {
        return statement switch
        {
            IrBlockStatement block => block.Statements.Any(RequiresRuntime),
            IrVariableDeclarationStatement variable =>
                variable.Initializer != null && RequiresRuntime(variable.Initializer),
            IrExpressionStatement expression => RequiresRuntime(expression.Expression),
            IrIfStatement conditional =>
                RequiresRuntime(conditional.Condition) ||
                RequiresRuntime(conditional.ThenBlock) ||
                (conditional.ElseBlock != null && RequiresRuntime(conditional.ElseBlock)),
            IrWhileStatement => true,
            IrForStatement => true,
            IrDoWhileStatement => true,
            IrFunctionDeclarationStatement function =>
                !function.ReturnType.IsAnyOrUnknown ||
                function.Parameters.Any(parameter => parameter.IsVarargs || !parameter.DeclaredType.IsAnyOrUnknown) ||
                RequiresRuntime(function.Body),
            IrReturnStatement returned => returned.Expression != null && RequiresRuntime(returned.Expression),
            IrBreakStatement => false,
            IrContinueStatement => false,
            _ => true
        };
    }

    private static bool RequiresRuntime(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression => false,
            IrIdentifierExpression => false,
            IrIntrinsicCallExpression intrinsic =>
                intrinsic.Id is not (IntrinsicId.Print or IntrinsicId.Println) ||
                intrinsic.Arguments.Any(RequiresRuntime),
            IrCallExpression call => call.Arguments.Any(argument => RequiresRuntime(argument.Value)),
            IrAssignmentExpression assignment =>
                assignment.Operator != "=" || RequiresRuntime(assignment.Value),
            IrArrayLiteralExpression => true,
            IrObjectLiteralExpression => true,
            IrMemberAccessExpression => true,
            IrIndexExpression => true,
            IrMethodCallExpression => true,
            IrUnaryExpression => true,
            IrBinaryExpression => true,
            IrConditionalExpression => true,
            _ => true
        };
    }
}
