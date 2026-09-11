namespace Sushi.Transpilation.Backends;

using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

internal static class EmissionCapabilityAnalyzer
{
    public static bool UsesArrays(IrProgram program) => program.Statements.Any(UsesArrays);

    private static bool UsesArrays(IrStatement statement) => statement switch
    {
        IrBlockStatement block => block.Statements.Any(UsesArrays),
        IrVariableDeclarationStatement variable => variable.Initializer != null && UsesArrays(variable.Initializer),
        IrExpressionStatement expression => UsesArrays(expression.Expression),
        IrIfStatement conditional => UsesArrays(conditional.Condition) || UsesArrays(conditional.ThenBlock) ||
                                     (conditional.ElseBlock != null && UsesArrays(conditional.ElseBlock)),
        IrWhileStatement loop => UsesArrays(loop.Condition) || UsesArrays(loop.Body),
        IrForStatement loop => (loop.Initializer != null && UsesArrays(loop.Initializer)) ||
                               (loop.Condition != null && UsesArrays(loop.Condition)) ||
                               (loop.Increment != null && UsesArrays(loop.Increment)) || UsesArrays(loop.Body),
        IrDoWhileStatement loop => UsesArrays(loop.Body) || UsesArrays(loop.Condition),
        IrFunctionDeclarationStatement function =>
            function.Parameters.Any(parameter => parameter.IsVarargs || parameter.DeclaredType.Name == "array") ||
            UsesArrays(function.Body),
        IrReturnStatement returned => returned.Expression != null && UsesArrays(returned.Expression),
        _ => false
    };

    private static bool UsesArrays(IrExpression expression) => expression switch
    {
        IrArrayLiteralExpression => true,
        IrIndexExpression index => UsesArrays(index.Target) || UsesArrays(index.Index),
        IrMethodCallExpression method => method.MethodName is "push" or "map" or "filter" or "reduce" or "length" ||
                                         UsesArrays(method.Target) || method.Arguments.Any(argument => UsesArrays(argument.Value)),
        IrIntrinsicCallExpression intrinsic => intrinsic.Id is IntrinsicId.StringSplit or IntrinsicId.ProcessArgs or IntrinsicId.FsGlob ||
                                               intrinsic.Arguments.Any(UsesArrays),
        IrAssignmentExpression assignment => UsesArrays(assignment.Value),
        IrObjectLiteralExpression obj => obj.Properties.Any(property => UsesArrays(property.Value)),
        IrMemberAccessExpression member => UsesArrays(member.Target),
        IrCallExpression call => call.Arguments.Any(argument => UsesArrays(argument.Value)),
        IrConstructionExpression construction => construction.Arguments.Any(argument => UsesArrays(argument.Value)),
        IrResolvedMethodCallExpression method => UsesArrays(method.Target) || method.Arguments.Any(argument => UsesArrays(argument.Value)),
        IrAdapterCallExpression adapter => UsesArrays(adapter.Value),
        IrUnaryExpression unary => UsesArrays(unary.Operand),
        IrBinaryExpression binary => UsesArrays(binary.Left) || UsesArrays(binary.Right),
        IrConditionalExpression conditional => UsesArrays(conditional.Condition) ||
                                               UsesArrays(conditional.TrueExpression) ||
                                               UsesArrays(conditional.FalseExpression),
        _ => false
    };
}
