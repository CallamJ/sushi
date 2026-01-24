namespace Sushi.Build.SyntaxTree;

using System.Collections.Generic;

/// <summary>
/// Root node representing the entire program
/// </summary>
public class ProgramNode : AstNode
{
    public List<AstNode> Declarations { get; }

    public ProgramNode(int line, int column) : base(line, column)
    {
        Declarations = new List<AstNode>();
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Box (namespace) declaration: box People.Helpers;
/// </summary>
public class BoxDeclarationNode : AstNode
{
    public string FullName { get; }  // e.g., "People.Helpers"
    
    public BoxDeclarationNode(string fullName, int line, int column) : base(line, column)
    {
        FullName = fullName;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Use (import) declaration: use People.Person; or use People.Helpers.greet() -> greetPerson()
/// </summary>
public class UseDeclarationNode : AstNode
{
    public string ImportPath { get; }      // e.g., "People.Person"
    public string? Alias { get; }          // e.g., "greetPerson" (optional)
    
    public UseDeclarationNode(string importPath, string? alias, int line, int column) 
        : base(line, column)
    {
        ImportPath = importPath;
        Alias = alias;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Class declaration
/// </summary>
public class ClassDeclarationNode : AstNode
{
    public string Name { get; }
    public List<FieldDeclarationNode> Fields { get; }
    public ConstructorDeclarationNode? Constructor { get; set; }
    public List<FunctionDeclarationNode> Methods { get; }
    public List<TypeAdapterDeclarationNode> TypeAdapters { get; }
    
    public ClassDeclarationNode(string name, int line, int column) : base(line, column)
    {
        Name = name;
        Fields = new List<FieldDeclarationNode>();
        Methods = new List<FunctionDeclarationNode>();
        TypeAdapters = new List<TypeAdapterDeclarationNode>();
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Field declaration: string name;
/// </summary>
public class FieldDeclarationNode : AstNode
{
    public string? Type { get; }  // null if type is inferred
    public string Name { get; }
    public ExpressionNode? Initializer { get; set; }
    
    public FieldDeclarationNode(string? type, string name, int line, int column) 
        : base(line, column)
    {
        Type = type;
        Name = name;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Function/method declaration
/// </summary>
public class FunctionDeclarationNode : AstNode
{
    public string? ReturnType { get; }  // null if void or inferred
    public string Name { get; }
    public List<ParameterNode> Parameters { get; }
    public StatementNode Body { get; set; }  // BlockStatement or expression for arrow functions
    public bool IsArrowFunction { get; set; }
    
    public FunctionDeclarationNode(
        string? returnType, 
        string name, 
        List<ParameterNode> parameters,
        int line, 
        int column) : base(line, column)
    {
        ReturnType = returnType;
        Name = name;
        Parameters = parameters;
        Body = null!;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Constructor declaration: new(string name) { ... }
/// </summary>
public class ConstructorDeclarationNode : AstNode
{
    public List<ParameterNode> Parameters { get; }
    public BlockStatementNode Body { get; set; }
    
    public ConstructorDeclarationNode(List<ParameterNode> parameters, int line, int column) 
        : base(line, column)
    {
        Parameters = parameters;
        Body = null!;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Type adapter declaration: string() -> name;
/// </summary>
public class TypeAdapterDeclarationNode : AstNode
{
    public string TargetType { get; }
    public StatementNode Body { get; set; }  // Block or expression
    public bool IsArrowFunction { get; set; }
    
    public TypeAdapterDeclarationNode(string targetType, int line, int column) 
        : base(line, column)
    {
        TargetType = targetType;
        Body = null!;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Parameter declaration
/// </summary>
public class ParameterNode : AstNode
{
    public string? Type { get; }  // null if inferred
    public string Name { get; }
    
    public ParameterNode(string? type, string name, int line, int column) 
        : base(line, column)
    {
        Type = type;
        Name = name;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}