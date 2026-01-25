namespace Sushi.Build.SyntaxTree;

using System.Collections.Generic;

/// <summary>
/// Base class for all statement nodes
/// </summary>
public abstract class StatementNode : AstNode
{
    protected StatementNode(int line, int column) : base(line, column) { }
}

/// <summary>
/// Block statement: { ... }
/// </summary>
public class BlockStatementNode : StatementNode
{
    public List<StatementNode> Statements { get; }
    
    public BlockStatementNode(int line, int column) : base(line, column)
    {
        Statements = new List<StatementNode>();
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Return statement: return expr;
/// </summary>
public class ReturnStatementNode : StatementNode
{
    public ExpressionNode? Expression { get; }  // null for empty return
    
    public ReturnStatementNode(ExpressionNode? expression, int line, int column) 
        : base(line, column)
    {
        Expression = expression;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Expression statement: expr;
/// </summary>
public class ExpressionStatementNode : StatementNode
{
    public ExpressionNode Expression { get; }
    
    public ExpressionStatementNode(ExpressionNode expression, int line, int column) 
        : base(line, column)
    {
        Expression = expression;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Variable declaration: Type name = value; or var name = value;
/// </summary>
public class VariableDeclarationStatementNode : StatementNode
{
    public string? Type { get; }  // null if using var
    public string Name { get; }
    public ExpressionNode? Initializer { get; }
    public bool IsVar { get; }  // true if declared with 'var' keyword
    
    public VariableDeclarationStatementNode(
        string? type, 
        string name, 
        ExpressionNode? initializer,
        bool isVar,
        int line, 
        int column) : base(line, column)
    {
        Type = type;
        Name = name;
        Initializer = initializer;
        IsVar = isVar;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Array destructuring: var [a, b, c] = arr
/// </summary>
public class ArrayDestructuringStatementNode : StatementNode
{
    public List<DestructuringPatternNode> Patterns { get; }
    public ExpressionNode Value { get; }
    
    public ArrayDestructuringStatementNode(
        List<DestructuringPatternNode> patterns,
        ExpressionNode value,
        int line,
        int column) : base(line, column)
    {
        Patterns = patterns;
        Value = value;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Destructuring pattern for array destructuring
/// </summary>
public class DestructuringPatternNode : AstNode
{
    public string? Type { get; }           // Optional type annotation
    public string? Name { get; }           // Variable name (null to skip)
    public bool IsRest { get; }            // true for ...rest
    public ExpressionNode? DefaultValue { get; } // Default if undefined
    
    public DestructuringPatternNode(
        string? type,
        string? name,
        bool isRest,
        ExpressionNode? defaultValue,
        int line,
        int column) : base(line, column)
    {
        Type = type;
        Name = name;
        IsRest = isRest;
        DefaultValue = defaultValue;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// If statement: if (condition) thenBranch else elseBranch
/// </summary>
public class IfStatementNode : StatementNode
{
    public ExpressionNode Condition { get; }
    public StatementNode ThenBranch { get; }
    public StatementNode? ElseBranch { get; }
    
    public IfStatementNode(
        ExpressionNode condition,
        StatementNode thenBranch,
        StatementNode? elseBranch,
        int line,
        int column) : base(line, column)
    {
        Condition = condition;
        ThenBranch = thenBranch;
        ElseBranch = elseBranch;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Switch statement: switch (value) { case1 -> { } case2 -> { } }
/// </summary>
public class SwitchStatementNode : StatementNode
{
    public ExpressionNode Value { get; }
    public List<SwitchCaseNode> Cases { get; }
    public BlockStatementNode? DefaultCase { get; }
    
    public SwitchStatementNode(
        ExpressionNode value,
        List<SwitchCaseNode> cases,
        BlockStatementNode? defaultCase,
        int line,
        int column) : base(line, column)
    {
        Value = value;
        Cases = cases;
        DefaultCase = defaultCase;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Switch case: value1, value2 -> { body }
/// </summary>
public class SwitchCaseNode : AstNode
{
    public List<ExpressionNode> MatchValues { get; }
    public BlockStatementNode Body { get; }
    public List<ExpressionNode> AlsoCases { get; }  // Values to also execute
    
    public SwitchCaseNode(
        List<ExpressionNode> matchValues,
        BlockStatementNode body,
        List<ExpressionNode> alsoCases,
        int line,
        int column) : base(line, column)
    {
        MatchValues = matchValues;
        Body = body;
        AlsoCases = alsoCases;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// While loop: while (condition) body
/// </summary>
public class WhileStatementNode : StatementNode
{
    public ExpressionNode Condition { get; }
    public StatementNode Body { get; }
    
    public WhileStatementNode(
        ExpressionNode condition,
        StatementNode body,
        int line,
        int column) : base(line, column)
    {
        Condition = condition;
        Body = body;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Do-while loop: do { body } while (condition)
/// </summary>
public class DoWhileStatementNode : StatementNode
{
    public StatementNode Body { get; }
    public ExpressionNode Condition { get; }
    
    public DoWhileStatementNode(
        StatementNode body,
        ExpressionNode condition,
        int line,
        int column) : base(line, column)
    {
        Body = body;
        Condition = condition;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// For loop: for (init; condition; increment) body
/// </summary>
public class ForStatementNode : StatementNode
{
    public StatementNode? Initializer { get; }
    public ExpressionNode? Condition { get; }
    public ExpressionNode? Increment { get; }
    public StatementNode Body { get; }
    
    public ForStatementNode(
        StatementNode? initializer,
        ExpressionNode? condition,
        ExpressionNode? increment,
        StatementNode body,
        int line,
        int column) : base(line, column)
    {
        Initializer = initializer;
        Condition = condition;
        Increment = increment;
        Body = body;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// For-range loop: for (var i : 0..10) body
/// </summary>
public class ForRangeStatementNode : StatementNode
{
    public string Variable { get; }
    public ExpressionNode Start { get; }
    public ExpressionNode End { get; }
    public bool IsInclusive { get; }  // .. vs ...
    public ExpressionNode? Step { get; }
    public StatementNode Body { get; }
    
    public ForRangeStatementNode(
        string variable,
        ExpressionNode start,
        ExpressionNode end,
        bool isInclusive,
        ExpressionNode? step,
        StatementNode body,
        int line,
        int column) : base(line, column)
    {
        Variable = variable;
        Start = start;
        End = end;
        IsInclusive = isInclusive;
        Step = step;
        Body = body;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// For-each loop: for (var item : collection) body
/// </summary>
public class ForEachStatementNode : StatementNode
{
    public string? IndexVariable { get; }  // Optional index variable
    public string ItemVariable { get; }
    public ExpressionNode Collection { get; }
    public StatementNode Body { get; }
    
    public ForEachStatementNode(
        string? indexVariable,
        string itemVariable,
        ExpressionNode collection,
        StatementNode body,
        int line,
        int column) : base(line, column)
    {
        IndexVariable = indexVariable;
        ItemVariable = itemVariable;
        Collection = collection;
        Body = body;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Break statement
/// </summary>
public class BreakStatementNode : StatementNode
{
    public BreakStatementNode(int line, int column) : base(line, column) { }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Continue statement
/// </summary>
public class ContinueStatementNode : StatementNode
{
    public ContinueStatementNode(int line, int column) : base(line, column) { }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}