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
    void Visit(ProgramNode node);
    void Visit(BoxDeclarationNode node);
    void Visit(UseDeclarationNode node);
    void Visit(ClassDeclarationNode node);
    void Visit(FunctionDeclarationNode node);
    void Visit(ConstructorDeclarationNode node);
    void Visit(TypeAdapterDeclarationNode node);
    void Visit(FieldDeclarationNode node);
    void Visit(ParameterNode node);
    
    // Statements
    void Visit(BlockStatementNode node);
    void Visit(ReturnStatementNode node);
    void Visit(ExpressionStatementNode node);
    void Visit(VariableDeclarationStatementNode node);
    void Visit(IfStatementNode node);
    void Visit(WhileStatementNode node);
    void Visit(ForStatementNode node);
    void Visit(BreakStatementNode node);
    void Visit(ContinueStatementNode node);
    
    // Expressions
    void Visit(BinaryExpressionNode node);
    void Visit(UnaryExpressionNode node);
    void Visit(CallExpressionNode node);
    void Visit(MemberAccessExpressionNode node);
    void Visit(PipeExpressionNode node);
    void Visit(IdentifierExpressionNode node);
    void Visit(LiteralExpressionNode node);
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
    T Visit(ProgramNode node);
    T Visit(BoxDeclarationNode node);
    T Visit(UseDeclarationNode node);
    T Visit(ClassDeclarationNode node);
    T Visit(FunctionDeclarationNode node);
    T Visit(ConstructorDeclarationNode node);
    T Visit(TypeAdapterDeclarationNode node);
    T Visit(FieldDeclarationNode node);
    T Visit(ParameterNode node);
    
    T Visit(BlockStatementNode node);
    T Visit(ReturnStatementNode node);
    T Visit(ExpressionStatementNode node);
    T Visit(VariableDeclarationStatementNode node);
    T Visit(IfStatementNode node);
    T Visit(WhileStatementNode node);
    T Visit(ForStatementNode node);
    T Visit(BreakStatementNode node);
    T Visit(ContinueStatementNode node);
    
    T Visit(BinaryExpressionNode node);
    T Visit(UnaryExpressionNode node);
    T Visit(CallExpressionNode node);
    T Visit(MemberAccessExpressionNode node);
    T Visit(PipeExpressionNode node);
    T Visit(IdentifierExpressionNode node);
    T Visit(LiteralExpressionNode node);
    T Visit(InterpolatedStringExpressionNode node);
    T Visit(NewExpressionNode node);
    T Visit(ThisExpressionNode node);
    T Visit(ParenthesizedExpressionNode node);
    T Visit(ObjectLiteralExpressionNode node);
    T Visit(ObjectPropertyNode node);
    T Visit(LambdaExpressionNode node);
}