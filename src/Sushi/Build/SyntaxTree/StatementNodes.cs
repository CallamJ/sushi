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
/// Variable declaration: Type name = value;
/// </summary>
public class VariableDeclarationStatementNode : StatementNode
{
    public string? Type { get; }  // null if inferred
    public string Name { get; }
    public ExpressionNode? Initializer { get; }
    
    public VariableDeclarationStatementNode(
        string? type, 
        string name, 
        ExpressionNode? initializer,
        int line, 
        int column) : base(line, column)
    {
        Type = type;
        Name = name;
        Initializer = initializer;
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