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
/// Enum declaration
/// </summary>
public class EnumDeclarationNode : AstNode
{
    public string Name { get; }
    public List<ParameterNode>? RecordParameters { get; }  // For record-style enums
    public List<EnumValueNode> Values { get; }
    public ConstructorDeclarationNode? ExplicitConstructor { get; }
    public List<FunctionDeclarationNode> Methods { get; }
    public List<TypeAdapterDeclarationNode> TypeAdapters { get; }
    
    // Computed during semantic analysis
    public EnumKind Kind { get; set; }
    public string? ValueType { get; set; }  // For DirectValue enums
    
    public EnumDeclarationNode(
        string name,
        List<ParameterNode>? recordParameters,
        List<EnumValueNode> values,
        ConstructorDeclarationNode? explicitConstructor,
        List<FunctionDeclarationNode> methods,
        List<TypeAdapterDeclarationNode> typeAdapters,
        int line,
        int column) : base(line, column)
    {
        Name = name;
        RecordParameters = recordParameters;
        Values = values;
        ExplicitConstructor = explicitConstructor;
        Methods = methods;
        TypeAdapters = typeAdapters;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

public enum EnumKind
{
    Simple,           // Just names: Red, Green, Blue
    DirectValue,      // Name = value: Red = 1
    Record,           // Name(args): Red(255, 0, 0)
    InlineProperties  // Name { props }: Red { r = 255 }
}

/// <summary>
/// Enum value
/// </summary>
public class EnumValueNode : AstNode
{
    public string Name { get; }
    
    // Exactly one of these will be non-null:
    public ExpressionNode? DirectValue { get; set; }              // For: Red = 1
    public List<ExpressionNode>? ConstructorArgs { get; }    // For: Ok(200, "OK")
    public Dictionary<string, ExpressionNode>? Properties { get; } // For: North { x = 0 }
    
    public EnumValueNode(
        string name,
        ExpressionNode? directValue,
        List<ExpressionNode>? constructorArgs,
        Dictionary<string, ExpressionNode>? properties,
        int line,
        int column) : base(line, column)
    {
        Name = name;
        DirectValue = directValue;
        ConstructorArgs = constructorArgs;
        Properties = properties;
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
/// Parameter declaration (supports varargs, structural types, defaults, named args)
/// </summary>
public class ParameterNode : AstNode
{
    public string? Type { get; }  // null if inferred or using structural type
    public StructuralTypeNode? StructuralType { get; }  // For object { ... } parameters
    public string Name { get; }
    public bool IsVarargs { get; }  // true for type... name
    public ExpressionNode? DefaultValue { get; }  // Default parameter value
    
    public ParameterNode(
        string? type, 
        StructuralTypeNode? structuralType,
        string name,
        bool isVarargs,
        ExpressionNode? defaultValue,
        int line, 
        int column) : base(line, column)
    {
        Type = type;
        StructuralType = structuralType;
        Name = name;
        IsVarargs = isVarargs;
        DefaultValue = defaultValue;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// Structural type notation: object { string name, int age }
/// </summary>
public class StructuralTypeNode : AstNode
{
    public Dictionary<string, string> Fields { get; }  // field name -> type
    
    public StructuralTypeNode(
        Dictionary<string, string> fields,
        int line,
        int column) : base(line, column)
    {
        Fields = fields;
    }

    public override void Accept(IAstVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
}