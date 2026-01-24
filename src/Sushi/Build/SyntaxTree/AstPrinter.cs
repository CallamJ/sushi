namespace Sushi.Build.SyntaxTree;

using System;
using System.Text;

/// <summary>
/// Visitor that prints the AST in a readable tree format
/// </summary>
public class AstPrinter : IAstVisitor
{
    private readonly StringBuilder _output;
    private int _indentLevel;
    private const string IndentString = "  ";

    public AstPrinter()
    {
        _output = new StringBuilder();
        _indentLevel = 0;
    }

    public string GetResult() => _output.ToString();

    private void WriteLine(string text)
    {
        _output.Append(new string(' ', _indentLevel * 2));
        _output.AppendLine(text);
    }

    private void Indent() => _indentLevel++;
    private void Dedent() => _indentLevel--;

    // ═══════════════════════════════════════════════════════════════════
    // Program & Declarations
    // ═══════════════════════════════════════════════════════════════════

    public void Visit(ProgramNode node)
    {
        WriteLine("Program");
        Indent();
        foreach (var decl in node.Declarations)
        {
            decl.Accept(this);
        }
        Dedent();
    }

    public void Visit(BoxDeclarationNode node)
    {
        WriteLine($"BoxDeclaration: {node.FullName}");
    }

    public void Visit(UseDeclarationNode node)
    {
        var alias = node.Alias != null ? $" as {node.Alias}" : "";
        WriteLine($"UseDeclaration: {node.ImportPath}{alias}");
    }

    public void Visit(ClassDeclarationNode node)
    {
        WriteLine($"ClassDeclaration: {node.Name}");
        Indent();
        
        if (node.Fields.Count > 0)
        {
            WriteLine("Fields:");
            Indent();
            foreach (var field in node.Fields)
                field.Accept(this);
            Dedent();
        }
        
        if (node.Constructor != null)
        {
            WriteLine("Constructor:");
            Indent();
            node.Constructor.Accept(this);
            Dedent();
        }
        
        if (node.Methods.Count > 0)
        {
            WriteLine("Methods:");
            Indent();
            foreach (var method in node.Methods)
                method.Accept(this);
            Dedent();
        }
        
        if (node.TypeAdapters.Count > 0)
        {
            WriteLine("TypeAdapters:");
            Indent();
            foreach (var adapter in node.TypeAdapters)
                adapter.Accept(this);
            Dedent();
        }
        
        Dedent();
    }

    public void Visit(FunctionDeclarationNode node)
    {
        var returnType = node.ReturnType ?? "void";
        var arrow = node.IsArrowFunction ? " (arrow)" : "";
        WriteLine($"Function: {returnType} {node.Name}(...){arrow}");
        Indent();
        
        if (node.Parameters.Count > 0)
        {
            WriteLine("Parameters:");
            Indent();
            foreach (var param in node.Parameters)
                param.Accept(this);
            Dedent();
        }
        
        WriteLine("Body:");
        Indent();
        node.Body.Accept(this);
        Dedent();
        
        Dedent();
    }

    public void Visit(ConstructorDeclarationNode node)
    {
        WriteLine("Constructor");
        Indent();
        
        if (node.Parameters.Count > 0)
        {
            WriteLine("Parameters:");
            Indent();
            foreach (var param in node.Parameters)
                param.Accept(this);
            Dedent();
        }
        
        WriteLine("Body:");
        Indent();
        node.Body.Accept(this);
        Dedent();
        
        Dedent();
    }

    public void Visit(TypeAdapterDeclarationNode node)
    {
        var arrow = node.IsArrowFunction ? " (arrow)" : "";
        WriteLine($"TypeAdapter: {node.TargetType}(){arrow}");
        Indent();
        node.Body.Accept(this);
        Dedent();
    }

    public void Visit(FieldDeclarationNode node)
    {
        var type = node.Type ?? "inferred";
        var init = node.Initializer != null ? " = ..." : "";
        WriteLine($"Field: {type} {node.Name}{init}");
        if (node.Initializer != null)
        {
            Indent();
            node.Initializer.Accept(this);
            Dedent();
        }
    }

    public void Visit(ParameterNode node)
    {
        var type = node.Type ?? "inferred";
        WriteLine($"Parameter: {type} {node.Name}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Statements
    // ═══════════════════════════════════════════════════════════════════

    public void Visit(BlockStatementNode node)
    {
        WriteLine("Block {");
        Indent();
        foreach (var stmt in node.Statements)
            stmt.Accept(this);
        Dedent();
        WriteLine("}");
    }

    public void Visit(ReturnStatementNode node)
    {
        WriteLine("Return");
        if (node.Expression != null)
        {
            Indent();
            node.Expression.Accept(this);
            Dedent();
        }
    }

    public void Visit(ExpressionStatementNode node)
    {
        WriteLine("ExpressionStatement");
        Indent();
        node.Expression.Accept(this);
        Dedent();
    }

    public void Visit(VariableDeclarationStatementNode node)
    {
        var type = node.Type ?? "inferred";
        WriteLine($"VariableDeclaration: {type} {node.Name}");
        if (node.Initializer != null)
        {
            Indent();
            node.Initializer.Accept(this);
            Dedent();
        }
    }

    public void Visit(IfStatementNode node)
    {
        WriteLine("If");
        Indent();
        
        WriteLine("Condition:");
        Indent();
        node.Condition.Accept(this);
        Dedent();
        
        WriteLine("Then:");
        Indent();
        node.ThenBranch.Accept(this);
        Dedent();
        
        if (node.ElseBranch != null)
        {
            WriteLine("Else:");
            Indent();
            node.ElseBranch.Accept(this);
            Dedent();
        }
        
        Dedent();
    }

    public void Visit(WhileStatementNode node)
    {
        WriteLine("While");
        Indent();
        
        WriteLine("Condition:");
        Indent();
        node.Condition.Accept(this);
        Dedent();
        
        WriteLine("Body:");
        Indent();
        node.Body.Accept(this);
        Dedent();
        
        Dedent();
    }

    public void Visit(ForStatementNode node)
    {
        WriteLine("For");
        Indent();
        
        if (node.Initializer != null)
        {
            WriteLine("Init:");
            Indent();
            node.Initializer.Accept(this);
            Dedent();
        }
        
        if (node.Condition != null)
        {
            WriteLine("Condition:");
            Indent();
            node.Condition.Accept(this);
            Dedent();
        }
        
        if (node.Increment != null)
        {
            WriteLine("Increment:");
            Indent();
            node.Increment.Accept(this);
            Dedent();
        }
        
        WriteLine("Body:");
        Indent();
        node.Body.Accept(this);
        Dedent();
        
        Dedent();
    }

    public void Visit(BreakStatementNode node)
    {
        WriteLine("Break");
    }

    public void Visit(ContinueStatementNode node)
    {
        WriteLine("Continue");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Expressions
    // ═══════════════════════════════════════════════════════════════════

    public void Visit(BinaryExpressionNode node)
    {
        WriteLine($"BinaryExpression: {node.Operator}");
        Indent();
        WriteLine("Left:");
        Indent();
        node.Left.Accept(this);
        Dedent();
        WriteLine("Right:");
        Indent();
        node.Right.Accept(this);
        Dedent();
        Dedent();
    }

    public void Visit(UnaryExpressionNode node)
    {
        var position = node.IsPrefix ? "prefix" : "postfix";
        WriteLine($"UnaryExpression: {node.Operator} ({position})");
        Indent();
        node.Operand.Accept(this);
        Dedent();
    }

    public void Visit(CallExpressionNode node)
    {
        WriteLine("Call");
        Indent();
        WriteLine("Callee:");
        Indent();
        node.Callee.Accept(this);
        Dedent();
        
        if (node.Arguments.Count > 0)
        {
            WriteLine($"Arguments ({node.Arguments.Count}):");
            Indent();
            foreach (var arg in node.Arguments)
                arg.Accept(this);
            Dedent();
        }
        Dedent();
    }

    public void Visit(MemberAccessExpressionNode node)
    {
        WriteLine($"MemberAccess: .{node.MemberName}");
        Indent();
        node.Object.Accept(this);
        Dedent();
    }

    public void Visit(PipeExpressionNode node)
    {
        var placeholder = node.PlaceholderIndex.HasValue 
            ? $" (@ at index {node.PlaceholderIndex})" 
            : " (first arg)";
        WriteLine($"Pipe{placeholder}");
        Indent();
        WriteLine("Source:");
        Indent();
        node.Source.Accept(this);
        Dedent();
        WriteLine("Target:");
        Indent();
        node.Target.Accept(this);
        Dedent();
        Dedent();
    }

    public void Visit(IdentifierExpressionNode node)
    {
        WriteLine($"Identifier: {node.Name}");
    }

    public void Visit(LiteralExpressionNode node)
    {
        var value = node.Value?.ToString() ?? "null";
        WriteLine($"Literal ({node.Kind}): {value}");
    }

    public void Visit(InterpolatedStringExpressionNode node)
    {
        WriteLine($"InterpolatedString ({node.Parts.Count} parts)");
        Indent();
        foreach (var part in node.Parts)
        {
            if (part.IsLiteral)
            {
                WriteLine($"Literal: \"{part.Content}\"");
            }
            else
            {
                WriteLine($"Expression: {part.Content}");
            }
        }
        Dedent();
    }

    public void Visit(NewExpressionNode node)
    {
        WriteLine($"New: {node.TypeName}");
        Indent();
        if (node.Arguments.Count > 0)
        {
            WriteLine($"Arguments ({node.Arguments.Count}):");
            Indent();
            foreach (var arg in node.Arguments)
                arg.Accept(this);
            Dedent();
        }
        Dedent();
    }

    public void Visit(ThisExpressionNode node)
    {
        WriteLine("This");
    }

    public void Visit(ParenthesizedExpressionNode node)
    {
        WriteLine("Parenthesized");
        Indent();
        node.Expression.Accept(this);
        Dedent();
    }

    public void Visit(ObjectLiteralExpressionNode node)
    {
        WriteLine($"ObjectLiteral ({node.Properties.Count} properties)");
        Indent();
        foreach (var prop in node.Properties)
            prop.Accept(this);
        Dedent();
    }

    public void Visit(ObjectPropertyNode node)
    {
        var typeStr = node.Type != null ? $"{node.Type} " : "";
        var adapterStr = node.IsTypeAdapter ? " (type adapter)" : "";
        WriteLine($"Property: {typeStr}{node.Name}{adapterStr}");
        Indent();
        node.Value.Accept(this);
        Dedent();
    }

    public void Visit(LambdaExpressionNode node)
    {
        var paramCount = node.Parameters.Count;
        var blockStr = node.IsBlock ? " {block}" : " {expr}";
        WriteLine($"Lambda ({paramCount} params){blockStr}");
        Indent();
        
        if (node.Parameters.Count > 0)
        {
            WriteLine("Parameters:");
            Indent();
            foreach (var param in node.Parameters)
                param.Accept(this);
            Dedent();
        }
        
        WriteLine("Body:");
        Indent();
        node.Body.Accept(this);
        Dedent();
        
        Dedent();
    }
}