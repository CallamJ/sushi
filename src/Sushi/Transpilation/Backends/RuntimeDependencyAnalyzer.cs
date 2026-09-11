namespace Sushi.Transpilation.Backends;

using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

internal static class RuntimeDependencyAnalyzer
{
    public static bool RequiresPowerShellRuntime(IrProgram program)
    {
        return RequiresPowerShellRuntime(new IrBlockStatement(program.Statements));
    }

    private static bool RequiresPowerShellRuntime(IrStatement statement) => statement switch
    {
        IrBlockStatement block => block.Statements.Any(RequiresPowerShellRuntime),
        IrVariableDeclarationStatement variable => variable.Initializer != null && RequiresPowerShellRuntime(variable.Initializer),
        IrExpressionStatement expression => RequiresPowerShellRuntime(expression.Expression),
        IrIfStatement conditional => RequiresPowerShellRuntime(conditional.Condition) || RequiresPowerShellRuntime(conditional.ThenBlock) || (conditional.ElseBlock != null && RequiresPowerShellRuntime(conditional.ElseBlock)),
        IrWhileStatement loop => RequiresPowerShellRuntime(loop.Condition) || RequiresPowerShellRuntime(loop.Body),
        IrForStatement loop => (loop.Initializer != null && RequiresPowerShellRuntime(loop.Initializer)) || (loop.Condition != null && RequiresPowerShellRuntime(loop.Condition)) || (loop.Increment != null && RequiresPowerShellRuntime(loop.Increment)) || RequiresPowerShellRuntime(loop.Body),
        IrDoWhileStatement loop => RequiresPowerShellRuntime(loop.Body) || RequiresPowerShellRuntime(loop.Condition),
        IrFunctionDeclarationStatement function =>
            !function.ReturnType.IsAnyOrUnknown ||
            function.Parameters.Any(p => !p.DeclaredType.IsAnyOrUnknown) ||
            RequiresPowerShellRuntime(function.Body),
        IrReturnStatement returned => returned.Expression != null && RequiresPowerShellRuntime(returned.Expression),
        _ => false
    };

    private static bool RequiresPowerShellRuntime(IrExpression expression) => expression switch
    {
        IrIntrinsicCallExpression intrinsic => intrinsic.Id is not (IntrinsicId.Print or IntrinsicId.Println) || intrinsic.Arguments.Any(RequiresPowerShellRuntime),
        IrMethodCallExpression method => true,
        IrMemberAccessExpression or IrIndexExpression or IrObjectLiteralExpression => true,
        IrAssignmentExpression assignment => RequiresPowerShellRuntime(assignment.Value),
        IrArrayLiteralExpression array => array.Elements.Any(RequiresPowerShellRuntime),
        IrCallExpression call => call.Arguments.Any(a => RequiresPowerShellRuntime(a.Value)),
        IrUnaryExpression unary => RequiresPowerShellRuntime(unary.Operand),
        IrBinaryExpression binary => RequiresPowerShellRuntime(binary.Left) || RequiresPowerShellRuntime(binary.Right),
        IrConditionalExpression conditional => RequiresPowerShellRuntime(conditional.Condition) || RequiresPowerShellRuntime(conditional.TrueExpression) || RequiresPowerShellRuntime(conditional.FalseExpression),
        _ => false
    };

    public static bool RequiresRuntime(IrProgram program)
    {
        return RequiresRuntime(new IrBlockStatement(program.Statements), new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool RequiresRuntime(IrStatement statement, HashSet<string> nativeArrays)
    {
        return statement switch
        {
            IrBlockStatement block => RequiresRuntime(block.Statements, nativeArrays),
            IrVariableDeclarationStatement variable => RequiresRuntime(variable, nativeArrays),
            IrExpressionStatement expression => RequiresRuntime(expression.Expression, nativeArrays),
            IrIfStatement conditional =>
                RequiresRuntime(conditional.Condition, nativeArrays) ||
                RequiresRuntime(conditional.ThenBlock, nativeArrays) ||
                (conditional.ElseBlock != null && RequiresRuntime(conditional.ElseBlock, nativeArrays)),
            IrWhileStatement loop =>
                RequiresRuntime(loop.Condition, nativeArrays) || RequiresRuntime(loop.Body, nativeArrays),
            IrForStatement loop =>
                (loop.Initializer != null && RequiresRuntime(loop.Initializer, nativeArrays)) ||
                (loop.Condition != null && RequiresRuntime(loop.Condition, nativeArrays)) ||
                (loop.Increment != null && RequiresRuntime(loop.Increment, nativeArrays)) ||
                RequiresRuntime(loop.Body, nativeArrays),
            IrDoWhileStatement loop =>
                RequiresRuntime(loop.Body, nativeArrays) || RequiresRuntime(loop.Condition, nativeArrays),
            IrFunctionDeclarationStatement function => RequiresRuntime(function),
            IrReturnStatement returned => returned.Expression != null && RequiresRuntime(returned.Expression, nativeArrays),
            IrBreakStatement => false,
            IrContinueStatement => false,
            _ => true
        };
    }

    private static bool RequiresRuntime(IEnumerable<IrStatement> statements, HashSet<string> nativeArrays)
    {
        foreach (var statement in statements)
        {
            if (RequiresRuntime(statement, nativeArrays))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RequiresRuntime(IrVariableDeclarationStatement variable, HashSet<string> nativeArrays)
    {
        var name = variable.Name;
        if (variable.Initializer is IrArrayLiteralExpression)
        {
            nativeArrays.Add(name);
        }
        else
        {
            nativeArrays.Remove(name);
        }

        return variable.Initializer != null && RequiresRuntime(variable.Initializer, nativeArrays);
    }

    private static bool RequiresRuntime(IrFunctionDeclarationStatement function)
    {
        if (RequiresFullTypeRuntime(function.ReturnType) ||
            function.Parameters.Any(parameter => RequiresFullTypeRuntime(parameter.DeclaredType)))
        {
            return true;
        }

        var nativeArrays = function.Parameters
            .Where(parameter => parameter.IsVarargs)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        return RequiresRuntime(function.Body, nativeArrays);
    }

    private static bool RequiresFullTypeRuntime(IrTypeRef type)
    {
        return type.Kind == IrTypeKind.Structural ||
               (type.Kind == IrTypeKind.Primitive &&
                type.Name is not ("int" or "bool"));
    }

    private static bool RequiresRuntime(IrExpression expression, HashSet<string> nativeArrays)
    {
        return expression switch
        {
            IrLiteralExpression => false,
            IrIdentifierExpression => false,
            IrIntrinsicCallExpression intrinsic =>
                RequiresFullIntrinsicRuntime(intrinsic.Id) ||
                intrinsic.Arguments.Any(argument => RequiresRuntime(argument, nativeArrays)),
            IrCallExpression call =>
                call.Callee.StartsWith("__sushi_", StringComparison.Ordinal) ||
                call.Arguments.Any(argument => RequiresRuntime(argument.Value, nativeArrays)),
            IrAssignmentExpression assignment => RequiresRuntime(assignment, nativeArrays),
            IrArrayLiteralExpression array => array.Elements.Any(element => RequiresRuntime(element, nativeArrays)),
            IrObjectLiteralExpression => true,
            IrMemberAccessExpression => true,
            IrIndexExpression index =>
                index.Target is not IrIdentifierExpression identifier ||
                !nativeArrays.Contains(identifier.Name) ||
                RequiresRuntime(index.Index, nativeArrays),
            IrMethodCallExpression => true,
            IrUnaryExpression unary => RequiresRuntime(unary.Operand, nativeArrays),
            IrBinaryExpression binary =>
                RequiresRuntime(binary.Left, nativeArrays) || RequiresRuntime(binary.Right, nativeArrays),
            IrConditionalExpression conditional =>
                RequiresRuntime(conditional.Condition, nativeArrays) ||
                RequiresRuntime(conditional.TrueExpression, nativeArrays) ||
                RequiresRuntime(conditional.FalseExpression, nativeArrays),
            _ => true
        };
    }

    private static bool RequiresRuntime(IrAssignmentExpression assignment, HashSet<string> nativeArrays)
    {
        if (assignment.Operator == "=" && assignment.Value is IrArrayLiteralExpression)
        {
            nativeArrays.Add(assignment.Target.Name);
        }
        else
        {
            nativeArrays.Remove(assignment.Target.Name);
        }

        return RequiresRuntime(assignment.Value, nativeArrays);
    }

    private static bool RequiresFullIntrinsicRuntime(IntrinsicId id)
    {
        return id is not (
            IntrinsicId.Print or IntrinsicId.Println or
            IntrinsicId.StringTrim or IntrinsicId.StringLower or IntrinsicId.StringUpper or
            IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or
            IntrinsicId.StringReplace or IntrinsicId.StringIsMatch);
    }
}
