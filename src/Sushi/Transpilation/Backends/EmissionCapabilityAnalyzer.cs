namespace Sushi.Transpilation.Backends;

using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

internal static class EmissionCapabilityAnalyzer
{
    public static bool UsesArrays(IrProgram program) => program.Statements.Any(UsesArrays);
    public static bool UsesFsGlob(IrProgram program) => program.Statements.Any(UsesFsGlob);
    public static bool UsesIntrinsic(IrProgram program, IntrinsicId id) => program.Statements.Any(statement => UsesIntrinsic(statement, id));

    private static bool UsesIntrinsic(IrStatement statement, IntrinsicId id) => statement switch
    {
        IrBlockStatement block => block.Statements.Any(child => UsesIntrinsic(child, id)),
        IrVariableDeclarationStatement variable => variable.Initializer != null && UsesIntrinsic(variable.Initializer, id),
        IrExpressionStatement expression => UsesIntrinsic(expression.Expression, id),
        IrIfStatement conditional => UsesIntrinsic(conditional.Condition, id) || UsesIntrinsic(conditional.ThenBlock, id) || (conditional.ElseBlock != null && UsesIntrinsic(conditional.ElseBlock, id)),
        IrSwitchStatement selection => UsesIntrinsic(selection.Value, id) || selection.Cases.Any(@case => @case.Matches.Any(match => UsesIntrinsic(match, id)) || UsesIntrinsic(@case.Body, id)) || (selection.DefaultBody != null && UsesIntrinsic(selection.DefaultBody, id)),
        IrWhileStatement loop => UsesIntrinsic(loop.Condition, id) || UsesIntrinsic(loop.Body, id),
        IrForStatement loop => (loop.Initializer != null && UsesIntrinsic(loop.Initializer, id)) || (loop.Condition != null && UsesIntrinsic(loop.Condition, id)) || (loop.Increment != null && UsesIntrinsic(loop.Increment, id)) || UsesIntrinsic(loop.Body, id),
        IrForEachStatement loop => UsesIntrinsic(loop.Collection, id) || UsesIntrinsic(loop.Body, id),
        IrDoWhileStatement loop => UsesIntrinsic(loop.Body, id) || UsesIntrinsic(loop.Condition, id),
        IrFunctionDeclarationStatement function => UsesIntrinsic(function.Body, id),
        IrReturnStatement returned => returned.Expression != null && UsesIntrinsic(returned.Expression, id),
        _ => false
    };

    private static bool UsesIntrinsic(IrExpression expression, IntrinsicId id) => expression switch
    {
        IrIntrinsicCallExpression intrinsic => intrinsic.Id == id || intrinsic.Arguments.Any(argument => UsesIntrinsic(argument, id)),
        IrArrayLiteralExpression array => array.Elements.Any(element => UsesIntrinsic(element, id)),
        IrIndexExpression index => UsesIntrinsic(index.Target, id) || UsesIntrinsic(index.Index, id),
        IrCollectionLengthExpression length => UsesIntrinsic(length.Target, id),
        IrSliceExpression slice => UsesIntrinsic(slice.Target, id) || (slice.Start != null && UsesIntrinsic(slice.Start, id)) || (slice.End != null && UsesIntrinsic(slice.End, id)),
        IrMethodCallExpression method => UsesIntrinsic(method.Target, id) || method.Arguments.Any(argument => UsesIntrinsic(argument.Value, id)),
        IrAssignmentExpression assignment => UsesIntrinsic(assignment.Value, id),
        IrObjectLiteralExpression obj => obj.Properties.Any(property => UsesIntrinsic(property.Value, id)),
        IrMemberAccessExpression member => UsesIntrinsic(member.Target, id),
        IrConversionExpression conversion => UsesIntrinsic(conversion.Value, id),
        IrCallExpression call => call.Arguments.Any(argument => UsesIntrinsic(argument.Value, id)),
        IrConstructionExpression construction => construction.Arguments.Any(argument => UsesIntrinsic(argument.Value, id)),
        IrResolvedMethodCallExpression method => UsesIntrinsic(method.Target, id) || method.Arguments.Any(argument => UsesIntrinsic(argument.Value, id)),
        IrAdapterCallExpression adapter => UsesIntrinsic(adapter.Value, id),
        IrTruthinessExpression truthiness => UsesIntrinsic(truthiness.Operand, id),
        IrUnaryExpression unary => UsesIntrinsic(unary.Operand, id),
        IrBinaryExpression binary => UsesIntrinsic(binary.Left, id) || UsesIntrinsic(binary.Right, id),
        IrConditionalExpression conditional => UsesIntrinsic(conditional.Condition, id) || UsesIntrinsic(conditional.TrueExpression, id) || UsesIntrinsic(conditional.FalseExpression, id),
        _ => false
    };

    private static bool UsesFsGlob(IrStatement statement) => statement switch
    {
        IrBlockStatement block => block.Statements.Any(UsesFsGlob),
        IrVariableDeclarationStatement variable => variable.Initializer != null && UsesFsGlob(variable.Initializer),
        IrExpressionStatement expression => UsesFsGlob(expression.Expression),
        IrIfStatement conditional => UsesFsGlob(conditional.Condition) || UsesFsGlob(conditional.ThenBlock) || (conditional.ElseBlock != null && UsesFsGlob(conditional.ElseBlock)),
        IrSwitchStatement selection => UsesFsGlob(selection.Value) || selection.Cases.Any(@case => @case.Matches.Any(UsesFsGlob) || UsesFsGlob(@case.Body)) || (selection.DefaultBody != null && UsesFsGlob(selection.DefaultBody)),
        IrWhileStatement loop => UsesFsGlob(loop.Condition) || UsesFsGlob(loop.Body),
        IrForStatement loop => (loop.Initializer != null && UsesFsGlob(loop.Initializer)) || (loop.Condition != null && UsesFsGlob(loop.Condition)) || (loop.Increment != null && UsesFsGlob(loop.Increment)) || UsesFsGlob(loop.Body),
        IrForEachStatement loop => UsesFsGlob(loop.Collection) || UsesFsGlob(loop.Body),
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
        IrCollectionLengthExpression length => UsesFsGlob(length.Target),
        IrSliceExpression slice => UsesFsGlob(slice.Target) ||
                                   (slice.Start != null && UsesFsGlob(slice.Start)) ||
                                   (slice.End != null && UsesFsGlob(slice.End)),
        IrMethodCallExpression method => UsesFsGlob(method.Target) || method.Arguments.Any(argument => UsesFsGlob(argument.Value)),
        IrIntrinsicCallExpression intrinsic => intrinsic.Arguments.Any(UsesFsGlob),
        IrAssignmentExpression assignment => UsesFsGlob(assignment.Value),
        IrObjectLiteralExpression obj => obj.Properties.Any(property => UsesFsGlob(property.Value)),
        IrMemberAccessExpression member => UsesFsGlob(member.Target),
        IrConversionExpression conversion => UsesFsGlob(conversion.Value),
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
        IrForEachStatement loop => UsesArrays(loop.Collection) || UsesArrays(loop.Body),
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
        IrCollectionLengthExpression length => UsesArrays(length.Target),
        IrSliceExpression slice => UsesArrays(slice.Target) ||
                                   (slice.Start != null && UsesArrays(slice.Start)) ||
                                   (slice.End != null && UsesArrays(slice.End)),
        IrMethodCallExpression method => method.MethodName is "push" or "map" or "filter" or "reduce" or "length" ||
                                         UsesArrays(method.Target) || method.Arguments.Any(argument => UsesArrays(argument.Value)),
        IrIntrinsicCallExpression intrinsic => intrinsic.Id is IntrinsicId.StringSplit or IntrinsicId.ProcessArgs or IntrinsicId.FsGlob ||
                                               intrinsic.Arguments.Any(UsesArrays),
        IrAssignmentExpression assignment => UsesArrays(assignment.Value),
        IrObjectLiteralExpression obj => obj.Properties.Any(property => UsesArrays(property.Value)),
        IrMemberAccessExpression member => UsesArrays(member.Target),
        IrConversionExpression conversion => UsesArrays(conversion.Value),
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
