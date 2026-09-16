namespace Sushi.Transpilation.Backends;

using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

internal static class EmissionCapabilityAnalyzer
{
    public static bool UsesArrays(IrProgram program) => program.Statements.Any(UsesArrays);
    public static bool UsesFsGlob(IrProgram program) => program.Statements.Any(UsesFsGlob);

    private static bool UsesFsGlob(IrStatement statement) => statement switch
    {
        IrBlockStatement block => block.Statements.Any(UsesFsGlob),
        IrVariableDeclarationStatement variable => variable.Initializer != null && UsesFsGlob(variable.Initializer),
        IrExpressionStatement expression => UsesFsGlob(expression.Expression),
        IrIfStatement conditional => UsesFsGlob(conditional.Condition) || UsesFsGlob(conditional.ThenBlock) || (conditional.ElseBlock != null && UsesFsGlob(conditional.ElseBlock)),
        IrSwitchStatement selection => UsesFsGlob(selection.Value) || selection.Cases.Any(@case => @case.Matches.Any(UsesFsGlob) || UsesFsGlob(@case.Body)) || (selection.DefaultBody != null && UsesFsGlob(selection.DefaultBody)),
        IrWhileStatement loop => UsesFsGlob(loop.Condition) || UsesFsGlob(loop.Body),
        IrForStatement loop => (loop.Initializer != null && UsesFsGlob(loop.Initializer)) || (loop.Condition != null && UsesFsGlob(loop.Condition)) || (loop.Increment != null && UsesFsGlob(loop.Increment)) || UsesFsGlob(loop.Body),
        IrDoWhileStatement loop => UsesFsGlob(loop.Body) || UsesFsGlob(loop.Condition),
        IrFunctionDeclarationStatement function => UsesFsGlob(function.Body),
        IrReturnStatement returned => returned.Expression != null && UsesFsGlob(returned.Expression),
        _ => false
    };

    private static bool UsesFsGlob(IrExpression expression) => expression switch
    {
        IrIntrinsicCallExpression { Id: IntrinsicId.FsGlob } => true,
        IrArrayLiteralExpression array => array.Elements.Any(UsesFsGlob),
        IrIndexExpression index => UsesFsGlob(index.Target) || UsesFsGlob(index.Index),
        IrMethodCallExpression method => UsesFsGlob(method.Target) || method.Arguments.Any(argument => UsesFsGlob(argument.Value)),
        IrIntrinsicCallExpression intrinsic => intrinsic.Arguments.Any(UsesFsGlob),
        IrAssignmentExpression assignment => UsesFsGlob(assignment.Value),
        IrObjectLiteralExpression obj => obj.Properties.Any(property => UsesFsGlob(property.Value)),
        IrMemberAccessExpression member => UsesFsGlob(member.Target),
        IrCallExpression call => call.Arguments.Any(argument => UsesFsGlob(argument.Value)),
        IrConstructionExpression construction => construction.Arguments.Any(argument => UsesFsGlob(argument.Value)),
        IrResolvedMethodCallExpression method => UsesFsGlob(method.Target) || method.Arguments.Any(argument => UsesFsGlob(argument.Value)),
        IrAdapterCallExpression adapter => UsesFsGlob(adapter.Value),
        IrTruthinessExpression truthiness => UsesFsGlob(truthiness.Operand),
        IrUnaryExpression unary => UsesFsGlob(unary.Operand),
        IrBinaryExpression binary => UsesFsGlob(binary.Left) || UsesFsGlob(binary.Right),
        IrConditionalExpression conditional => UsesFsGlob(conditional.Condition) || UsesFsGlob(conditional.TrueExpression) || UsesFsGlob(conditional.FalseExpression),
        _ => false
    };

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
        IrTruthinessExpression truthiness => UsesArrays(truthiness.Operand),
        IrUnaryExpression unary => UsesArrays(unary.Operand),
        IrBinaryExpression binary => UsesArrays(binary.Left) || UsesArrays(binary.Right),
        IrConditionalExpression conditional => UsesArrays(conditional.Condition) ||
                                               UsesArrays(conditional.TrueExpression) ||
                                               UsesArrays(conditional.FalseExpression),
        _ => false
    };
}
