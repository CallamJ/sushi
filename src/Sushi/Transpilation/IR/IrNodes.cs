namespace Sushi.Transpilation.IR;

public sealed record IrSourceOrigin(int Line, int Column);

public abstract class IrNode
{
    public IrSourceOrigin? Origin { get; set; }
}

public sealed class IrProgram : IrNode
{
    public List<IrStatement> Statements { get; }

    public IrProgram(IEnumerable<IrStatement>? statements = null)
    {
        Statements = statements?.ToList() ?? new List<IrStatement>();
    }
}

public abstract class IrStatement : IrNode;

/// <summary>Source-visible standard-library import retained for target emission.</summary>
public sealed class IrStandardLibraryImportStatement : IrStatement
{
    public string Module { get; }
    public string? Alias { get; }
    public IReadOnlyList<string> Members { get; }

    public IrStandardLibraryImportStatement(string module, string? alias, IReadOnlyList<string> members)
    {
        Module = module;
        Alias = alias;
        Members = members;
    }
}

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
    public static IrTypeRef Void { get; } = new(IrTypeKind.Primitive, "void", null);

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

/// <summary>
/// A source-level class retained alongside the portable object lowering.  Backends
/// that have native classes can use this declaration; shell backends continue to
/// emit the accompanying portable functions and object literals.
/// </summary>
public sealed class IrClassDeclarationStatement : IrStatement
{
    public string Name { get; }
    public List<IrClassField> Fields { get; }
    public List<IrFunctionParameter> ConstructorParameters { get; }
    public IrBlockStatement ConstructorBody { get; }
    public List<IrClassMethod> Methods { get; }
    public List<IrClassMethod> Adapters { get; }
    public HashSet<string> LegacyFunctionNames { get; }

    public IrClassDeclarationStatement(string name, IEnumerable<IrClassField> fields,
        IEnumerable<IrFunctionParameter> constructorParameters, IrBlockStatement constructorBody,
        IEnumerable<IrClassMethod> methods, IEnumerable<IrClassMethod> adapters,
        IEnumerable<string> legacyFunctionNames)
    {
        Name = name;
        Fields = fields.ToList();
        ConstructorParameters = constructorParameters.ToList();
        ConstructorBody = constructorBody;
        Methods = methods.ToList();
        Adapters = adapters.ToList();
        LegacyFunctionNames = legacyFunctionNames.ToHashSet(StringComparer.Ordinal);
    }
}

public sealed class IrClassField
{
    public string Name { get; }
    public IrTypeRef Type { get; }
    public IrExpression? Initializer { get; }

    public IrClassField(string name, IrTypeRef type, IrExpression? initializer)
    {
        Name = name;
        Type = type;
        Initializer = initializer;
    }
}

public sealed class IrClassMethod
{
    public string Name { get; }
    public List<IrFunctionParameter> Parameters { get; }
    public IrBlockStatement Body { get; }
    public IrTypeRef ReturnType { get; }
    public string LegacyName { get; }

    public IrClassMethod(string name, IEnumerable<IrFunctionParameter> parameters,
        IrBlockStatement body, IrTypeRef returnType, string legacyName)
    {
        Name = name;
        Parameters = parameters.ToList();
        Body = body;
        ReturnType = returnType;
        LegacyName = legacyName;
    }
}

/// <summary>Native-enum candidate retained with the portable enum lowering.</summary>
public sealed class IrEnumDeclarationStatement : IrStatement
{
    public string Name { get; }
    public List<IrEnumValue> Values { get; }
    public HashSet<string> LegacyVariableNames { get; }

    public IrEnumDeclarationStatement(string name, IEnumerable<IrEnumValue> values, IEnumerable<string> legacyVariableNames)
    {
        Name = name;
        Values = values.ToList();
        LegacyVariableNames = legacyVariableNames.ToHashSet(StringComparer.Ordinal);
    }
}

public sealed class IrEnumValue
{
    public string Name { get; }
    public int Value { get; }
    public int Ordinal { get; }
    public IrEnumValue(string name, int value, int ordinal) { Name = name; Value = value; Ordinal = ordinal; }
}

/// <summary>Class-shaped enum values that cannot be represented by a CLR enum.</summary>
public sealed class IrRichEnumDeclarationStatement : IrStatement
{
    public string Name { get; }
    public List<IrRichEnumValue> Values { get; }
    public HashSet<string> LegacyVariableNames { get; }
    public List<IrFunctionParameter> ConstructorParameters { get; }
    public IrBlockStatement? ConstructorBody { get; }
    public List<IrClassMethod> Methods { get; }
    public List<IrClassMethod> Adapters { get; }
    public HashSet<string> LegacyFunctionNames { get; }
    public IrRichEnumDeclarationStatement(string name, IEnumerable<IrRichEnumValue> values, IEnumerable<string> legacyVariableNames,
        IEnumerable<IrFunctionParameter>? constructorParameters = null, IrBlockStatement? constructorBody = null,
        IEnumerable<IrClassMethod>? methods = null, IEnumerable<IrClassMethod>? adapters = null,
        IEnumerable<string>? legacyFunctionNames = null)
    {
        Name = name;
        Values = values.ToList();
        LegacyVariableNames = legacyVariableNames.ToHashSet(StringComparer.Ordinal);
        ConstructorParameters = constructorParameters?.ToList() ?? new List<IrFunctionParameter>();
        ConstructorBody = constructorBody;
        Methods = methods?.ToList() ?? new List<IrClassMethod>();
        Adapters = adapters?.ToList() ?? new List<IrClassMethod>();
        LegacyFunctionNames = legacyFunctionNames?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
    }
}

public sealed class IrRichEnumValue
{
    public string Name { get; }
    public List<IrObjectProperty> Properties { get; }
    public List<IrExpression> ConstructorArguments { get; }
    public IrRichEnumValue(string name, IEnumerable<IrObjectProperty> properties, IEnumerable<IrExpression>? constructorArguments = null)
    {
        Name = name;
        Properties = properties.ToList();
        ConstructorArguments = constructorArguments?.ToList() ?? new List<IrExpression>();
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

/// <summary>Explicit conversion of a statically typed value to a boolean.</summary>
public sealed class IrTruthinessExpression : IrExpression
{
    public IrExpression Operand { get; }
    public IrTypeRef OperandType { get; }

    public IrTruthinessExpression(IrExpression operand, IrTypeRef operandType)
    {
        Operand = operand;
        OperandType = operandType;
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

public sealed class IrConstructionExpression : IrExpression
{
    public string TypeName { get; }
    public string ConstructorName { get; }
    public List<IrCallArgument> Arguments { get; }

    public IrConstructionExpression(string typeName, string constructorName, IEnumerable<IrCallArgument> arguments)
    {
        TypeName = typeName;
        ConstructorName = constructorName;
        Arguments = arguments.ToList();
    }
}

public sealed class IrResolvedMethodCallExpression : IrExpression
{
    public string TypeName { get; }
    public string MethodName { get; }
    public string Callee { get; }
    public IrExpression Target { get; }
    public List<IrCallArgument> Arguments { get; }

    public IrResolvedMethodCallExpression(
        string typeName,
        string methodName,
        string callee,
        IrExpression target,
        IEnumerable<IrCallArgument> arguments)
    {
        TypeName = typeName;
        MethodName = methodName;
        Callee = callee;
        Target = target;
        Arguments = arguments.ToList();
    }

    public IrCallExpression AsFunctionCall() => new(
        Callee,
        new[] { new IrCallArgument(null, Target, 1, 1) }.Concat(Arguments));
}

public sealed class IrAdapterCallExpression : IrExpression
{
    public string SourceTypeName { get; }
    public string TargetTypeName { get; }
    public string Callee { get; }
    public IrExpression Value { get; }

    public IrAdapterCallExpression(string sourceTypeName, string targetTypeName, string callee, IrExpression value)
    {
        SourceTypeName = sourceTypeName;
        TargetTypeName = targetTypeName;
        Callee = callee;
        Value = value;
    }

    public IrCallExpression AsFunctionCall() => new(Callee, new[] { Value });
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
    public IrTypeRef ReturnType { get; }

    public IrIntrinsicCallExpression(
        string canonicalName,
        Sushi.Transpilation.Intrinsics.IntrinsicId id,
        IEnumerable<IrExpression> arguments,
        IrTypeRef? returnType = null)
    {
        CanonicalName = canonicalName;
        Id = id;
        Arguments = arguments.ToList();
        ReturnType = returnType ?? IrTypeRef.Any;
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
    public IrTypeRef ValueType { get; }

    public IrMemberAccessExpression(IrExpression target, string memberName, IrTypeRef? valueType = null)
    {
        Target = target;
        MemberName = memberName;
        ValueType = valueType ?? IrTypeRef.Any;
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
