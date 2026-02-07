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

public sealed class IrFunctionDeclarationStatement : IrStatement
{
    public string Name { get; }
    public List<string> Parameters { get; }
    public IrBlockStatement Body { get; }

    public IrFunctionDeclarationStatement(string name, IEnumerable<string> parameters, IrBlockStatement body)
    {
        Name = name;
        Parameters = parameters.ToList();
        Body = body;
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

public sealed class IrCallExpression : IrExpression
{
    public string Callee { get; }
    public List<IrExpression> Arguments { get; }

    public IrCallExpression(string callee, IEnumerable<IrExpression> arguments)
    {
        Callee = callee;
        Arguments = arguments.ToList();
    }
}
