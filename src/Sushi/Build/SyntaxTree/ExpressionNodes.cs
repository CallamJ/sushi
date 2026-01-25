namespace Sushi.Build.SyntaxTree;

using System.Collections.Generic;

/// <summary>
/// Base class for all expression nodes
/// </summary>
public abstract class ExpressionNode : AstNode
{
    protected ExpressionNode(int line, int column) : base(line, column) { }
}

/// <summary>
/// Binary expression: left op right
/// </summary>
public class BinaryExpressionNode : ExpressionNode
{
    public ExpressionNode Left { get; }
    public string Operator { get; }
    public ExpressionNode Right { get; }
    
    public BinaryExpressionNode(
        ExpressionNode left,
        string op,
        ExpressionNode right,
        int line,
        int column) : base(line, column)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Unary expression: op operand
/// </summary>
public class UnaryExpressionNode : ExpressionNode
{
    public string Operator { get; }
    public ExpressionNode Operand { get; }
    public bool IsPrefix { get; }
    
    public UnaryExpressionNode(
        string op,
        ExpressionNode operand,
        bool isPrefix,
        int line,
        int column) : base(line, column)
    {
        Operator = op;
        Operand = operand;
        IsPrefix = isPrefix;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Conditional (ternary) expression: condition ? trueExpr : falseExpr
/// </summary>
public class ConditionalExpressionNode : ExpressionNode
{
    public ExpressionNode Condition { get; }
    public ExpressionNode TrueExpression { get; }
    public ExpressionNode FalseExpression { get; }
    
    public ConditionalExpressionNode(
        ExpressionNode condition,
        ExpressionNode trueExpr,
        ExpressionNode falseExpr,
        int line,
        int column) : base(line, column)
    {
        Condition = condition;
        TrueExpression = trueExpr;
        FalseExpression = falseExpr;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Function/method call: func(args)
/// </summary>
public class CallExpressionNode : ExpressionNode
{
    public ExpressionNode Callee { get; }
    public List<ArgumentNode> Arguments { get; }
    
    public CallExpressionNode(
        ExpressionNode callee,
        List<ArgumentNode> arguments,
        int line,
        int column) : base(line, column)
    {
        Callee = callee;
        Arguments = arguments;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Argument in a function call (supports named arguments)
/// </summary>
public class ArgumentNode : AstNode
{
    public string? Name { get; }  // Null for positional arguments
    public ExpressionNode Value { get; }
    
    public ArgumentNode(
        string? name,
        ExpressionNode value,
        int line,
        int column) : base(line, column)
    {
        Name = name;
        Value = value;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Member access: object.member
/// </summary>
public class MemberAccessExpressionNode : ExpressionNode
{
    public ExpressionNode Object { get; }
    public string MemberName { get; }
    
    public MemberAccessExpressionNode(
        ExpressionNode obj,
        string memberName,
        int line,
        int column) : base(line, column)
    {
        Object = obj;
        MemberName = memberName;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Index expression: array[index]
/// </summary>
public class IndexExpressionNode : ExpressionNode
{
    public ExpressionNode Array { get; }
    public ExpressionNode Index { get; }
    
    public IndexExpressionNode(
        ExpressionNode array,
        ExpressionNode index,
        int line,
        int column) : base(line, column)
    {
        Array = array;
        Index = index;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Slice expression: array[start:end]
/// </summary>
public class SliceExpressionNode : ExpressionNode
{
    public ExpressionNode Array { get; }
    public ExpressionNode? Start { get; }  // Null means from beginning
    public ExpressionNode? End { get; }    // Null means to end
    
    public SliceExpressionNode(
        ExpressionNode array,
        ExpressionNode? start,
        ExpressionNode? end,
        int line,
        int column) : base(line, column)
    {
        Array = array;
        Start = start;
        End = end;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Pipe expression: expr | target or expr | target(args) or expr | target("before", @, "after")
/// </summary>
public class PipeExpressionNode : ExpressionNode
{
    public ExpressionNode Source { get; }
    public ExpressionNode Target { get; }
    public int? PlaceholderIndex { get; }  // Index where @ appears, null means first arg
    
    public PipeExpressionNode(
        ExpressionNode source,
        ExpressionNode target,
        int? placeholderIndex,
        int line,
        int column) : base(line, column)
    {
        Source = source;
        Target = target;
        PlaceholderIndex = placeholderIndex;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Identifier reference: variableName
/// </summary>
public class IdentifierExpressionNode : ExpressionNode
{
    public string Name { get; }
    
    public IdentifierExpressionNode(string name, int line, int column) : base(line, column)
    {
        Name = name;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Literal value: 42, 3.14, "hello", 'c', true, false, null
/// </summary>
public class LiteralExpressionNode : ExpressionNode
{
    public object? Value { get; }
    public LiteralKind Kind { get; }
    
    public LiteralExpressionNode(object? value, LiteralKind kind, int line, int column) 
        : base(line, column)
    {
        Value = value;
        Kind = kind;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

public enum LiteralKind
{
    Integer,
    Float,
    String,
    Char,
    Boolean,
    Null
}

/// <summary>
/// Array literal: [1, 2, 3]
/// </summary>
public class ArrayLiteralExpressionNode : ExpressionNode
{
    public List<ExpressionNode> Elements { get; }
    
    public ArrayLiteralExpressionNode(
        List<ExpressionNode> elements,
        int line,
        int column) : base(line, column)
    {
        Elements = elements;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Interpolated string: "Hello $(name)!"
/// </summary>
public class InterpolatedStringExpressionNode : ExpressionNode
{
    public List<InterpolatedStringPart> Parts { get; }
    
    public InterpolatedStringExpressionNode(
        List<InterpolatedStringPart> parts,
        int line,
        int column) : base(line, column)
    {
        Parts = parts;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// New expression: new Type(args)
/// </summary>
public class NewExpressionNode : ExpressionNode
{
    public string TypeName { get; }
    public List<ArgumentNode> Arguments { get; }
    
    public NewExpressionNode(
        string typeName,
        List<ArgumentNode> arguments,
        int line,
        int column) : base(line, column)
    {
        TypeName = typeName;
        Arguments = arguments;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// This expression
/// </summary>
public class ThisExpressionNode : ExpressionNode
{
    public ThisExpressionNode(int line, int column) : base(line, column) { }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Parenthesized expression: (expr)
/// </summary>
public class ParenthesizedExpressionNode : ExpressionNode
{
    public ExpressionNode Expression { get; }
    
    public ParenthesizedExpressionNode(ExpressionNode expression, int line, int column) 
        : base(line, column)
    {
        Expression = expression;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Dynamic object literal: { name: value, method: () -> expr }
/// </summary>
public class ObjectLiteralExpressionNode : ExpressionNode
{
    public List<ObjectPropertyNode> Properties { get; }
    
    public ObjectLiteralExpressionNode(int line, int column) : base(line, column)
    {
        Properties = new List<ObjectPropertyNode>();
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Property in an object literal: name: value or string: () -> expr
/// </summary>
public class ObjectPropertyNode : AstNode
{
    public string? Type { get; }  // Optional type annotation
    public string Name { get; }
    public ExpressionNode Value { get; }
    public bool IsTypeAdapter { get; }  // true if name is a type (like "string")
    
    public ObjectPropertyNode(
        string? type, 
        string name, 
        ExpressionNode value, 
        bool isTypeAdapter,
        int line, 
        int column) : base(line, column)
    {
        Type = type;
        Name = name;
        Value = value;
        IsTypeAdapter = isTypeAdapter;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Lambda expression: () -> expr or (params) -> expr or (params) -> { block }
/// </summary>
public class LambdaExpressionNode : ExpressionNode
{
    public List<ParameterNode> Parameters { get; }
    public AstNode Body { get; }  // Can be ExpressionNode or StatementNode (for blocks)
    public bool IsBlock { get; }  // true if body is { ... }, false if single expression
    
    public LambdaExpressionNode(
        List<ParameterNode> parameters,
        AstNode body,
        bool isBlock,
        int line,
        int column) : base(line, column)
    {
        Parameters = parameters;
        Body = body;
        IsBlock = isBlock;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}