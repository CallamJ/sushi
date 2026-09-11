namespace Sushi.Transpilation.IR;

public abstract class IrNode;

public sealed class IrProgram : IrNode
{
    public List<IrStatement> Statements { get; }

    public IrProgram(IEnumerable<IrStatement>? statements = null)
    {
        Statements = statements?.ToList() ?? new List<IrStatement>();
    }
}

public abstract class IrStatement : IrNode;

public sealed class IrBlockStatement : IrStatement
{
    public List<IrStatement> Statements { get; }

    public IrBlockStatement(IEnumerable<IrStatement>? statements = null)
    {
        Statements = statements?.ToList() ?? new List<IrStatement>();
    }
}

public sealed class IrVariableDeclarationStatement : IrStatement
{
    public string Name { get; }
    public IrExpression? Initializer { get; }

    public IrVariableDeclarationStatement(string name, IrExpression? initializer)
    {
        Name = name;
        Initializer = initializer;
    }
}

public sealed class IrExpressionStatement : IrStatement
{
    public IrExpression Expression { get; }

    public IrExpressionStatement(IrExpression expression)
    {
        Expression = expression;
    }
}

public sealed class IrIfStatement : IrStatement
{
    public IrExpression Condition { get; }
    public IrBlockStatement ThenBlock { get; }
    public IrBlockStatement? ElseBlock { get; }

    public IrIfStatement(IrExpression condition, IrBlockStatement thenBlock, IrBlockStatement? elseBlock)
    {
        Condition = condition;
        ThenBlock = thenBlock;
        ElseBlock = elseBlock;
    }
}

public sealed class IrWhileStatement : IrStatement
{
    public IrExpression Condition { get; }
    public IrBlockStatement Body { get; }

    public IrWhileStatement(IrExpression condition, IrBlockStatement body)
    {
        Condition = condition;
        Body = body;
    }
}

public sealed class IrForStatement : IrStatement
{
    public IrStatement? Initializer { get; }
    public IrExpression? Condition { get; }
    public IrExpression? Increment { get; }
    public IrBlockStatement Body { get; }

    public IrForStatement(
        IrStatement? initializer,
        IrExpression? condition,
        IrExpression? increment,
        IrBlockStatement body)
    {
        Initializer = initializer;
        Condition = condition;
        Increment = increment;
        Body = body;
    }
}

public sealed class IrDoWhileStatement : IrStatement
{
    public IrBlockStatement Body { get; }
    public IrExpression Condition { get; }

    public IrDoWhileStatement(IrBlockStatement body, IrExpression condition)
    {
        Body = body;
        Condition = condition;
    }
}

public enum IrTypeKind
{
    Any,
    Unknown,
    Primitive,
    Structural
}

public sealed class IrStructuralField
{
    public string Name { get; }
    public IrTypeRef Type { get; }
    public bool Optional { get; }

    public IrStructuralField(string name, IrTypeRef type, bool optional)
    {
        Name = name;
        Type = type;
        Optional = optional;
    }
}

public sealed class IrTypeRef
{
    public IrTypeKind Kind { get; }
    public string? Name { get; }
    public List<IrStructuralField> StructuralFields { get; }

    public bool IsAnyOrUnknown => Kind is IrTypeKind.Any or IrTypeKind.Unknown;

    private IrTypeRef(IrTypeKind kind, string? name, IEnumerable<IrStructuralField>? structuralFields)
    {
        Kind = kind;
        Name = name;
        StructuralFields = structuralFields?.ToList() ?? new List<IrStructuralField>();
    }

    public static IrTypeRef Any { get; } = new(IrTypeKind.Any, null, null);
    public static IrTypeRef Unknown { get; } = new(IrTypeKind.Unknown, null, null);

    public static IrTypeRef Primitive(string name)
    {
        return new IrTypeRef(IrTypeKind.Primitive, name, null);
    }

    public static IrTypeRef Structural(IEnumerable<IrStructuralField> fields)
    {
        return new IrTypeRef(IrTypeKind.Structural, "object", fields);
    }
}

public sealed class IrFunctionParameter
{
    public string Name { get; }
    public bool IsVarargs { get; }
    public IrExpression? DefaultValue { get; }
    public IrTypeRef DeclaredType { get; }

    public IrFunctionParameter(string name, bool isVarargs, IrExpression? defaultValue, IrTypeRef? declaredType = null)
    {
        Name = name;
        IsVarargs = isVarargs;
        DefaultValue = defaultValue;
        DeclaredType = declaredType ?? IrTypeRef.Any;
    }
}

public sealed class IrFunctionDeclarationStatement : IrStatement
{
    public string Name { get; }
    public List<IrFunctionParameter> Parameters { get; }
    public IrBlockStatement Body { get; }
    public IrTypeRef ReturnType { get; }

    public IrFunctionDeclarationStatement(
        string name,
        IEnumerable<IrFunctionParameter> parameters,
        IrBlockStatement body,
        IrTypeRef? returnType = null)
    {
        Name = name;
        Parameters = parameters.ToList();
        Body = body;
        ReturnType = returnType ?? IrTypeRef.Any;
    }

    public IrFunctionDeclarationStatement(string name, IEnumerable<string> parameters, IrBlockStatement body)
        : this(
            name,
            parameters.Select(parameter => new IrFunctionParameter(parameter, isVarargs: false, defaultValue: null, IrTypeRef.Any)),
            body,
            IrTypeRef.Any)
    {
    }
}

public sealed class IrReturnStatement : IrStatement
{
    public IrExpression? Expression { get; }

    public IrReturnStatement(IrExpression? expression)
    {
        Expression = expression;
    }
}

public sealed class IrBreakStatement : IrStatement;

public sealed class IrContinueStatement : IrStatement;

public abstract class IrExpression : IrNode;

public sealed class IrLiteralExpression : IrExpression
{
    public object? Value { get; }

    public IrLiteralExpression(object? value)
    {
        Value = value;
    }
}

public sealed class IrIdentifierExpression : IrExpression
{
    public string Name { get; }

    public IrIdentifierExpression(string name)
    {
        Name = name;
    }
}

public sealed class IrUnaryExpression : IrExpression
{
    public string Operator { get; }
    public IrExpression Operand { get; }
    public bool IsPrefix { get; }

    public IrUnaryExpression(string op, IrExpression operand, bool isPrefix)
    {
        Operator = op;
        Operand = operand;
        IsPrefix = isPrefix;
    }
}

public sealed class IrBinaryExpression : IrExpression
{
    public IrExpression Left { get; }
    public string Operator { get; }
    public IrExpression Right { get; }

    public IrBinaryExpression(IrExpression left, string op, IrExpression right)
    {
        Left = left;
        Operator = op;
        Right = right;
    }
}

public sealed class IrAssignmentExpression : IrExpression
{
    public IrIdentifierExpression Target { get; }
    public string Operator { get; }
    public IrExpression Value { get; }

    public IrAssignmentExpression(IrIdentifierExpression target, string op, IrExpression value)
    {
        Target = target;
        Operator = op;
        Value = value;
    }
}

public sealed class IrCallArgument
{
    public string? Name { get; }
    public IrExpression Value { get; }
    public int Line { get; }
    public int Column { get; }

    public IrCallArgument(string? name, IrExpression value, int line, int column)
    {
        Name = name;
        Value = value;
        Line = line;
        Column = column;
    }
}

public sealed class IrCallExpression : IrExpression
{
    public string Callee { get; }
    public List<IrCallArgument> Arguments { get; }

    public IrCallExpression(string callee, IEnumerable<IrCallArgument> arguments)
    {
        Callee = callee;
        Arguments = arguments.ToList();
    }

    public IrCallExpression(string callee, IEnumerable<IrExpression> arguments)
        : this(
            callee,
            arguments.Select(argument => new IrCallArgument(name: null, argument, line: 1, column: 1)))
    {
    }
}

public sealed class IrMethodCallExpression : IrExpression
{
    public IrExpression Target { get; }
    public string MethodName { get; }
    public List<IrCallArgument> Arguments { get; }

    public IrMethodCallExpression(IrExpression target, string methodName, IEnumerable<IrCallArgument> arguments)
    {
        Target = target;
        MethodName = methodName;
        Arguments = arguments.ToList();
    }
}

public sealed class IrIntrinsicCallExpression : IrExpression
{
    public string CanonicalName { get; }
    public Sushi.Transpilation.Intrinsics.IntrinsicId Id { get; }
    public List<IrExpression> Arguments { get; }

    public IrIntrinsicCallExpression(
        string canonicalName,
        Sushi.Transpilation.Intrinsics.IntrinsicId id,
        IEnumerable<IrExpression> arguments)
    {
        CanonicalName = canonicalName;
        Id = id;
        Arguments = arguments.ToList();
    }
}

public sealed class IrConditionalExpression : IrExpression
{
    public IrExpression Condition { get; }
    public IrExpression TrueExpression { get; }
    public IrExpression FalseExpression { get; }

    public IrConditionalExpression(IrExpression condition, IrExpression trueExpression, IrExpression falseExpression)
    {
        Condition = condition;
        TrueExpression = trueExpression;
        FalseExpression = falseExpression;
    }
}

public sealed class IrArrayLiteralExpression : IrExpression
{
    public List<IrExpression> Elements { get; }

    public IrArrayLiteralExpression(IEnumerable<IrExpression> elements)
    {
        Elements = elements.ToList();
    }
}

public sealed class IrObjectProperty
{
    public string Name { get; }
    public IrExpression Value { get; }

    public IrObjectProperty(string name, IrExpression value)
    {
        Name = name;
        Value = value;
    }
}

public sealed class IrObjectLiteralExpression : IrExpression
{
    public List<IrObjectProperty> Properties { get; }

    public IrObjectLiteralExpression(IEnumerable<IrObjectProperty> properties)
    {
        Properties = properties.ToList();
    }
}

public sealed class IrMemberAccessExpression : IrExpression
{
    public IrExpression Target { get; }
    public string MemberName { get; }

    public IrMemberAccessExpression(IrExpression target, string memberName)
    {
        Target = target;
        MemberName = memberName;
    }
}

public sealed class IrMemberAssignmentExpression : IrExpression
{
    public IrExpression Target { get; }
    public string MemberName { get; }
    public string Operator { get; }
    public IrExpression Value { get; }

    public IrMemberAssignmentExpression(IrExpression target, string memberName, string @operator, IrExpression value)
    {
        Target = target;
        MemberName = memberName;
        Operator = @operator;
        Value = value;
    }
}

public sealed class IrIndexExpression : IrExpression
{
    public IrExpression Target { get; }
    public IrExpression Index { get; }

    public IrIndexExpression(IrExpression target, IrExpression index)
    {
        Target = target;
        Index = index;
    }
}
