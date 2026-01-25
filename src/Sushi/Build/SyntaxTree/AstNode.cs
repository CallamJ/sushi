namespace Sushi.Build.SyntaxTree;

/// <summary>
/// Base class for all AST nodes
/// </summary>
public abstract class AstNode
{
    public int Line { get; set; }
    public int Column { get; set; }
    
    protected AstNode(int line, int column)
    {
        Line = line;
        Column = column;
    }

    public abstract void Accept(IAstVisitor visitor);
    public abstract T Accept<T>(IAstVisitor<T> visitor);
}

/// <summary>
/// Visitor pattern interface for traversing the AST
/// </summary>
public interface IAstVisitor
{
    // Program & Declarations
    void Visit(ProgramNode node);
    void Visit(BoxDeclarationNode node);
    void Visit(UseDeclarationNode node);
    void Visit(ClassDeclarationNode node);
    void Visit(EnumDeclarationNode node);
    void Visit(EnumValueNode node);
    void Visit(FunctionDeclarationNode node);
    void Visit(ConstructorDeclarationNode node);
    void Visit(TypeAdapterDeclarationNode node);
    void Visit(FieldDeclarationNode node);
    void Visit(ParameterNode node);
    void Visit(StructuralTypeNode node);
    
    // Statements
    void Visit(BlockStatementNode node);
    void Visit(ReturnStatementNode node);
    void Visit(ExpressionStatementNode node);
    void Visit(VariableDeclarationStatementNode node);
    void Visit(ArrayDestructuringStatementNode node);
    void Visit(DestructuringPatternNode node);
    void Visit(IfStatementNode node);
    void Visit(SwitchStatementNode node);
    void Visit(SwitchCaseNode node);
    void Visit(WhileStatementNode node);
    void Visit(DoWhileStatementNode node);
    void Visit(ForStatementNode node);
    void Visit(ForRangeStatementNode node);
    void Visit(ForEachStatementNode node);
    void Visit(BreakStatementNode node);
    void Visit(ContinueStatementNode node);
    
    // Expressions
    void Visit(BinaryExpressionNode node);
    void Visit(UnaryExpressionNode node);
    void Visit(ConditionalExpressionNode node);
    void Visit(CallExpressionNode node);
    void Visit(ArgumentNode node);
    void Visit(MemberAccessExpressionNode node);
    void Visit(IndexExpressionNode node);
    void Visit(SliceExpressionNode node);
    void Visit(PipeExpressionNode node);
    void Visit(IdentifierExpressionNode node);
    void Visit(LiteralExpressionNode node);
    void Visit(ArrayLiteralExpressionNode node);
    void Visit(InterpolatedStringExpressionNode node);
    void Visit(NewExpressionNode node);
    void Visit(ThisExpressionNode node);
    void Visit(ParenthesizedExpressionNode node);
    void Visit(ObjectLiteralExpressionNode node);
    void Visit(ObjectPropertyNode node);
    void Visit(LambdaExpressionNode node);
}

/// <summary>
/// Generic visitor pattern interface for transforming the AST
/// </summary>
public interface IAstVisitor<T>
{
    // Program & Declarations
    T Visit(ProgramNode node);
    T Visit(BoxDeclarationNode node);
    T Visit(UseDeclarationNode node);
    T Visit(ClassDeclarationNode node);
    T Visit(EnumDeclarationNode node);
    T Visit(EnumValueNode node);
    T Visit(FunctionDeclarationNode node);
    T Visit(ConstructorDeclarationNode node);
    T Visit(TypeAdapterDeclarationNode node);
    T Visit(FieldDeclarationNode node);
    T Visit(ParameterNode node);
    T Visit(StructuralTypeNode node);
    
    // Statements
    T Visit(BlockStatementNode node);
    T Visit(ReturnStatementNode node);
    T Visit(ExpressionStatementNode node);
    T Visit(VariableDeclarationStatementNode node);
    T Visit(ArrayDestructuringStatementNode node);
    T Visit(DestructuringPatternNode node);
    T Visit(IfStatementNode node);
    T Visit(SwitchStatementNode node);
    T Visit(SwitchCaseNode node);
    T Visit(WhileStatementNode node);
    T Visit(DoWhileStatementNode node);
    T Visit(ForStatementNode node);
    T Visit(ForRangeStatementNode node);
    T Visit(ForEachStatementNode node);
    T Visit(BreakStatementNode node);
    T Visit(ContinueStatementNode node);
    
    // Expressions
    T Visit(BinaryExpressionNode node);
    T Visit(UnaryExpressionNode node);
    T Visit(ConditionalExpressionNode node);
    T Visit(CallExpressionNode node);
    T Visit(ArgumentNode node);
    T Visit(MemberAccessExpressionNode node);
    T Visit(IndexExpressionNode node);
    T Visit(SliceExpressionNode node);
    T Visit(PipeExpressionNode node);
    T Visit(IdentifierExpressionNode node);
    T Visit(LiteralExpressionNode node);
    T Visit(ArrayLiteralExpressionNode node);
    T Visit(InterpolatedStringExpressionNode node);
    T Visit(NewExpressionNode node);
    T Visit(ThisExpressionNode node);
    T Visit(ParenthesizedExpressionNode node);
    T Visit(ObjectLiteralExpressionNode node);
    T Visit(ObjectPropertyNode node);
    T Visit(LambdaExpressionNode node);
}