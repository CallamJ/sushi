namespace Sushi.Build;

using System;
using System.Collections.Generic;
using System.Linq;
using Sushi.Build.SyntaxTree;

/// <summary>
/// Recursive descent parser for the Sushi language
/// Converts classified tokens into an Abstract Syntax Tree (AST)
/// </summary>
public class Parser
{
    private readonly List<ClassifiedToken> _tokens;
    private int _position;
    
    // Current namespace context for resolving symbols
    private string _currentNamespace = "";
    
    // Debug mode - set to true to see parser progress
    public bool DebugMode { get; set; } = false;
    
    private void DebugLog(string message)
    {
        if (DebugMode)
        {
            var token = IsAtEnd() ? "EOF" : Current().ToString();
            Console.WriteLine($"[Parser @ {_position}] {message} | Current: {token}");
        }
    }

    public Parser(IEnumerable<ClassifiedToken> tokens)
    {
        // Filter out whitespace (comments already removed by Lexer)
        _tokens = tokens
            .Where(t => t.Kind != ClassifiedTokenKind.Whitespace)
            .ToList();
        _position = 0;
    }

    /* ═══════════════════════════════════════════════════════════════════
       ENTRY POINT
       ═══════════════════════════════════════════════════════════════════ */

    public ProgramNode Parse()
    {
        var program = new ProgramNode(1, 1);

        while (!IsAtEnd())
        {
            var declaration = ParseTopLevelDeclaration();
            if (declaration != null)
            {
                program.Declarations.Add(declaration);
            }
        }

        return program;
    }

    /* ═══════════════════════════════════════════════════════════════════
       TOP-LEVEL DECLARATIONS
       ═══════════════════════════════════════════════════════════════════ */

    private AstNode? ParseTopLevelDeclaration()
    {
        if (IsAtEnd())
            return null;
            
        var token = Current();
        DebugLog($"ParseTopLevelDeclaration");

        // Skip empty statements (bare semicolons) - common after implicit semicolon insertion
        if (Match(ClassifiedTokenKind.Semicolon))
        {
            DebugLog("Skipped empty statement (semicolon)");
            return null; // Let Parse() continue to next iteration
        }

        if (token.IsKeyword("box"))
        {
            DebugLog("Found 'box' keyword");
            return ParseBoxDeclaration();
        }
        
        if (token.IsKeyword("use"))
        {
            DebugLog("Found 'use' keyword");
            return ParseUseDeclaration();
        }
        
        if (token.IsKeyword("class"))
        {
            DebugLog("Found 'class' keyword");
            return ParseClassDeclaration();
        }
        
        if (token.IsKeyword("enum"))
        {
            DebugLog("Found 'enum' keyword");
            return ParseEnumDeclaration();
        }
        
        // Top-level control flow statements (for scripting support)
        if (token.IsKeyword("if"))
        {
            DebugLog("Found 'if' at top level");
            return ParseIfStatement();
        }
        
        if (token.IsKeyword("while"))
        {
            DebugLog("Found 'while' at top level");
            return ParseWhileStatement();
        }
        
        if (token.IsKeyword("for"))
        {
            DebugLog("Found 'for' at top level");
            return ParseForStatement();
        }
        
        if (token.IsKeyword("switch"))
        {
            DebugLog("Found 'switch' at top level");
            return ParseSwitchStatement();
        }
        
        if (token.IsKeyword("do"))
        {
            DebugLog("Found 'do' at top level");
            return ParseDoWhileStatement();
        }
        
        if (token.IsKeyword("return"))
        {
            DebugLog("Found 'return' at top level");
            return ParseReturnStatement();
        }
        
        if (token.IsKeyword("break"))
        {
            DebugLog("Found 'break' at top level");
            return ParseBreakStatement();
        }
        
        if (token.IsKeyword("continue"))
        {
            DebugLog("Found 'continue' at top level");
            return ParseContinueStatement();
        }
        
        // Variable declaration with var keyword
        if (token.IsKeyword("var"))
        {
            DebugLog("Found 'var' keyword");
            return ParseVariableDeclarationStatement();
        }
        
        // Could be a function/variable declaration OR a statement
        if (Check(ClassifiedTokenKind.Identifier))
        {
            // Use lookahead to determine if it's a declaration or statement
            // Declaration patterns:
            // - name(...) { } or name(...) ->  (function declaration)
            // - Type name = ... (variable declaration)
            // - Type name(...) { } or Type name(...) -> (function declaration)
            
            var decl = ParseFunctionOrVariableDeclaration();
            return decl;
        }
        
        // Try to parse as expression statement (for top-level expressions like print("hello"))
        try
        {
            DebugLog("Attempting top-level expression statement");
            var expr = ParseExpression();
            ExpectSemicolon();
            return new ExpressionStatementNode(expr, expr.Line, expr.Column);
        }
        catch
        {
            throw new Exception($"Unexpected token in top-level: {token} at {token.Line}:{token.Column}");
        }
    }

    private BoxDeclarationNode ParseBoxDeclaration()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "box");
        
        // Parse namespace path: People.Helpers
        var path = ParseQualifiedName();
        
        ExpectSemicolon();
        
        _currentNamespace = path;
        return new BoxDeclarationNode(path, token.Line, token.Column);
    }

    private UseDeclarationNode ParseUseDeclaration()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "use");
        
        // Parse import path: People.Person or People.Helpers.greet()
        var path = ParseQualifiedName();
        
        // Check for function call syntax
        if (Match(ClassifiedTokenKind.LeftParen))
        {
            Expect(ClassifiedTokenKind.RightParen);
            path += "()";
        }
        
        // Check for alias: -> greetPerson()
        string? alias = null;
        if (MatchOperator("->"))
        {
            alias = Expect(ClassifiedTokenKind.Identifier).Text;
            if (Match(ClassifiedTokenKind.LeftParen))
            {
                Expect(ClassifiedTokenKind.RightParen);
                alias += "()";
            }
        }
        
        ExpectSemicolon();
        
        return new UseDeclarationNode(path, alias, token.Line, token.Column);
    }

    private ClassDeclarationNode ParseClassDeclaration()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "class");
        var name = Expect(ClassifiedTokenKind.Identifier).Text;
        
        Expect(ClassifiedTokenKind.LeftBrace);
        
        var classNode = new ClassDeclarationNode(name, token.Line, token.Column);
        
        while (!Check(ClassifiedTokenKind.RightBrace) && !IsAtEnd())
        {
            var member = ParseClassMember();
            
            if (member is FieldDeclarationNode field)
                classNode.Fields.Add(field);
            else if (member is ConstructorDeclarationNode constructor)
                classNode.Constructor = constructor;
            else if (member is FunctionDeclarationNode method)
                classNode.Methods.Add(method);
            else if (member is TypeAdapterDeclarationNode adapter)
                classNode.TypeAdapters.Add(adapter);
        }
        
        Expect(ClassifiedTokenKind.RightBrace);
        
        // Consume optional semicolon after class body
        OptionalSemicolon();
        
        return classNode;
    }

    private AstNode ParseClassMember()
    {
        // Skip empty statements (bare semicolons from implicit insertion)
        if (Match(ClassifiedTokenKind.Semicolon))
        {
            return ParseClassMember(); // Recursively skip to next member
        }
        
        // Check for constructor: new(...)
        if (Check(ClassifiedTokenKind.Keyword, "new"))
        {
            return ParseConstructor();
        }
        
        // Check for type adapter: string() -> ... or string() { ... }
        // Type adapters have a TYPE NAME as the identifier (not a regular method name)
        if (Check(ClassifiedTokenKind.Identifier) && Peek(1)?.Is(ClassifiedTokenKind.LeftParen) == true)
        {
            var lookahead = Peek(2);
            if (lookahead?.Is(ClassifiedTokenKind.RightParen) == true)
            {
                // Could be type adapter string() or method with no params
                var next = Peek(3);
                if (next?.IsOperator("->") == true || next?.Is(ClassifiedTokenKind.LeftBrace) == true)
                {
                    // Check if the identifier is a type name
                    var identifierName = Current().Text;
                    if (IsTypeName(identifierName))
                    {
                        return ParseTypeAdapter();
                    }
                    // Otherwise fall through to ParseFieldOrMethod
                }
            }
        }
        
        // Otherwise it's a field or method
        return ParseFieldOrMethod();
    }

    private ConstructorDeclarationNode ParseConstructor()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "new");
        
        var parameters = ParseParameterList();
        var body = ParseBlock();
        
        var ctor = new ConstructorDeclarationNode(parameters, token.Line, token.Column)
        {
            Body = body
        };
        
        return ctor;
    }

    private TypeAdapterDeclarationNode ParseTypeAdapter()
    {
        var typeToken = Expect(ClassifiedTokenKind.Identifier);
        var typeName = typeToken.Text;
        
        Expect(ClassifiedTokenKind.LeftParen);
        Expect(ClassifiedTokenKind.RightParen);
        
        var adapter = new TypeAdapterDeclarationNode(typeName, typeToken.Line, typeToken.Column);
        
        // Check for arrow function: string() -> expr
        if (MatchOperator("->"))
        {
            adapter.IsArrowFunction = true;
            var expr = ParseExpression();
            adapter.Body = new ReturnStatementNode(expr, expr.Line, expr.Column);
        }
        else
        {
            adapter.IsArrowFunction = false;
            adapter.Body = ParseBlock();
        }
        
        return adapter;
    }

    private AstNode ParseFieldOrMethod()
    {
        var start = Current();
        
        // Look ahead to determine what we're parsing
        // Possibilities:
        // 1. name(...) → method without return type
        // 2. Type name(...) → method with return type
        // 3. Type name; → field
        // 4. Type name = ... → field with initializer
        
        string? type = null;
        string name;
        
        if (Check(ClassifiedTokenKind.Identifier))
        {
            var firstToken = Advance();
            
            if (Check(ClassifiedTokenKind.LeftParen))
            {
                // Pattern: name(...) → method without return type
                name = firstToken.Text;
                type = null;
                // DON'T consume the ( - ParseMethodDeclaration expects it
                return ParseMethodDeclaration(type, name, start.Line, start.Column);
            }
            else if (Check(ClassifiedTokenKind.Identifier))
            {
                // Pattern: Type name → could be method or field
                type = firstToken.Text;
                name = Advance().Text;
            }
            else
            {
                // Pattern: name; or name = ... → field without type (shouldn't happen in class)
                name = firstToken.Text;
                type = null;
            }
        }
        else
        {
            throw new Exception($"Expected identifier at {start.Line}:{start.Column}");
        }
        
        // Check if it's a method or field
        if (Check(ClassifiedTokenKind.LeftParen))
        {
            // It's a method - don't consume the (, let ParseMethodDeclaration do it
            return ParseMethodDeclaration(type, name, start.Line, start.Column);
        }
        
        // Otherwise it's a field
        ExpressionNode? initializer = null;
        if (MatchOperator("="))
        {
            initializer = ParseExpression();
        }
        
        ExpectSemicolon();
        
        return new FieldDeclarationNode(type, name, start.Line, start.Column)
        {
            Initializer = initializer
        };
    }

    private FunctionDeclarationNode ParseMethodDeclaration(
        string? returnType, 
        string name, 
        int line, 
        int column)
    {
        Expect(ClassifiedTokenKind.LeftParen); // Consume the (
        var parameters = ParseParameterListFromParen();
        
        var function = new FunctionDeclarationNode(returnType, name, parameters, line, column);
        
        // Check for arrow function
        if (MatchOperator("->"))
        {
            function.IsArrowFunction = true;
            var expr = ParseExpression();
            function.Body = new ReturnStatementNode(expr, expr.Line, expr.Column);
            ExpectSemicolon();
        }
        else
        {
            function.IsArrowFunction = false;
            function.Body = ParseBlock();
        }
        
        return function;
    }

    private AstNode ParseFunctionOrVariableDeclaration()
    {
        var start = Current();
        
        // Look ahead to determine what we're parsing
        // Possibilities:
        // 1. name(...) { } or name(...) -> → function declaration
        // 2. name(...) with no block/arrow → function call (expression statement)
        // 3. Type name(...) → function with return type
        // 4. Type name = ... → variable declaration
        // 5. name = ... → variable declaration (inferred type)
        
        string? type = null;
        string name;
        
        if (Check(ClassifiedTokenKind.Identifier))
        {
            var firstToken = Advance();
            
            if (Check(ClassifiedTokenKind.LeftParen))
            {
                // Pattern: name(...) → could be function declaration or function call
                // Need to look ahead to see if there's a block or arrow after the closing paren
                
                // Save position to potentially backtrack
                var checkpoint = _position - 1; // Before the identifier
                
                // Try to scan ahead to find the closing paren
                int parenDepth = 1;
                Advance(); // consume (
                int scanPos = _position;
                
                while (scanPos < _tokens.Count && parenDepth > 0)
                {
                    if (_tokens[scanPos].Is(ClassifiedTokenKind.LeftParen))
                        parenDepth++;
                    else if (_tokens[scanPos].Is(ClassifiedTokenKind.RightParen))
                        parenDepth--;
                    scanPos++;
                }
                
                // Check what follows the closing paren
                bool isFunctionDeclaration = false;
                if (scanPos < _tokens.Count)
                {
                    var afterParen = _tokens[scanPos];
                    if (afterParen.Is(ClassifiedTokenKind.LeftBrace) || afterParen.IsOperator("->"))
                    {
                        isFunctionDeclaration = true;
                    }
                }
                
                // Restore position
                _position = checkpoint;
                Advance(); // consume identifier again
                
                if (!isFunctionDeclaration)
                {
                    // It's a function call - restore to before identifier and parse as expression
                    _position = checkpoint;
                    var expr = ParseExpression();
                    ExpectSemicolon();
                    return new ExpressionStatementNode(expr, expr.Line, expr.Column);
                }
                
                // It's a function declaration
                name = firstToken.Text;
                type = null;
            }
            else if (Check(ClassifiedTokenKind.Identifier))
            {
                // Pattern: Type name → could be function or variable
                type = firstToken.Text;
                name = Advance().Text;
            }
            else
            {
                // Pattern: name = ... → variable without type
                name = firstToken.Text;
                type = null;
            }
        }
        else
        {
            throw new Exception($"Expected identifier at {start.Line}:{start.Column}");
        }
        
        // Now check if it's a function or variable
        if (Match(ClassifiedTokenKind.LeftParen))
        {
            // It's a function declaration
            var parameters = ParseParameterListFromParen();
            var function = new FunctionDeclarationNode(type, name, parameters, start.Line, start.Column);
            
            if (MatchOperator("->"))
            {
                function.IsArrowFunction = true;
                var expr = ParseExpression();
                function.Body = new ReturnStatementNode(expr, expr.Line, expr.Column);
                ExpectSemicolon();
            }
            else
            {
                function.IsArrowFunction = false;
                function.Body = ParseBlock();
                // Consume optional semicolon after function body
                OptionalSemicolon();
            }
            
            return function;
        }
        
        // Otherwise it's a variable declaration
        ExpressionNode? initializer = null;
        if (MatchOperator("="))
        {
            initializer = ParseExpression();
        }
        
        ExpectSemicolon();
        
        return new VariableDeclarationStatementNode(type, name, initializer, false, start.Line, start.Column);
    }

    /* ═══════════════════════════════════════════════════════════════════
       STATEMENTS
       ═══════════════════════════════════════════════════════════════════ */

    private StatementNode ParseStatement()
    {
        var token = Current();

        if (token.IsKeyword("return"))
            return ParseReturnStatement();
        
        if (token.IsKeyword("if"))
            return ParseIfStatement();
        
        if (token.IsKeyword("while"))
            return ParseWhileStatement();
        
        if (token.IsKeyword("for"))
            return ParseForStatement();
        
        if (token.IsKeyword("switch"))
            return ParseSwitchStatement();
        
        if (token.IsKeyword("do"))
            return ParseDoWhileStatement();
        
        if (token.IsKeyword("break"))
            return ParseBreakStatement();
        
        if (token.IsKeyword("continue"))
            return ParseContinueStatement();
        
        if (token.Is(ClassifiedTokenKind.LeftBrace))
            return ParseBlock();
        
        // Check for variable declaration or expression statement
        return ParseVariableDeclarationOrExpressionStatement();
    }

    private BlockStatementNode ParseBlock()
    {
        var token = Expect(ClassifiedTokenKind.LeftBrace);
        var block = new BlockStatementNode(token.Line, token.Column);
        
        while (!Check(ClassifiedTokenKind.RightBrace) && !IsAtEnd())
        {
            // Skip any empty statements (extra semicolons)
            if (Match(ClassifiedTokenKind.Semicolon))
                continue;
                
            block.Statements.Add(ParseStatement());
        }
        
        // Consume any trailing semicolons before closing brace
        OptionalSemicolon();
        
        Expect(ClassifiedTokenKind.RightBrace);
        return block;
    }

    private ReturnStatementNode ParseReturnStatement()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "return");
        
        ExpressionNode? expr = null;
        if (!Check(ClassifiedTokenKind.Semicolon) && !Check(ClassifiedTokenKind.RightBrace) && !IsAtEnd())
        {
            expr = ParseExpression();
        }
        
        ExpectSemicolon();
        
        return new ReturnStatementNode(expr, token.Line, token.Column);
    }

    private IfStatementNode ParseIfStatement()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "if");
        
        Expect(ClassifiedTokenKind.LeftParen);
        var condition = ParseExpression();
        Expect(ClassifiedTokenKind.RightParen);
        
        var thenBranch = ParseStatement();
        
        StatementNode? elseBranch = null;
        if (MatchKeyword("else"))
        {
            elseBranch = ParseStatement();
        }
        
        // Consume optional semicolon after if statement
        OptionalSemicolon();
        
        return new IfStatementNode(condition, thenBranch, elseBranch, token.Line, token.Column);
    }

    private WhileStatementNode ParseWhileStatement()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "while");
        
        Expect(ClassifiedTokenKind.LeftParen);
        var condition = ParseExpression();
        Expect(ClassifiedTokenKind.RightParen);
        
        var body = ParseStatement();
        
        // Consume optional semicolon after while statement
        OptionalSemicolon();
        
        return new WhileStatementNode(condition, body, token.Line, token.Column);
    }

    private StatementNode ParseForStatement()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "for");
        
        Expect(ClassifiedTokenKind.LeftParen);
        
        // Try to detect for-range or foreach: look for "var x :" or "var i, var x :"
        var checkpoint = _position;
        bool isForEachOrRange = false;
        string? loopVariable = null;
        string? indexVariable = null;
        
        // Check for var/type followed by identifier and colon
        if (MatchKeyword("var") || Check(ClassifiedTokenKind.Identifier))
        {
            bool hasVar = Previous().IsKeyword("var");
            if (!hasVar && Check(ClassifiedTokenKind.Identifier))
            {
                Advance(); // consume type name
            }
            
            if (Check(ClassifiedTokenKind.Identifier))
            {
                loopVariable = Advance().Text;
                
                // Check for comma (index,item pattern)
                if (Match(ClassifiedTokenKind.Comma))
                {
                    indexVariable = loopVariable;
                    if (MatchKeyword("var") || Check(ClassifiedTokenKind.Identifier))
                    {
                        if (!Previous().IsKeyword("var") && Check(ClassifiedTokenKind.Identifier))
                            Advance(); // consume type
                        loopVariable = Expect(ClassifiedTokenKind.Identifier).Text;
                    }
                }
                
                // Check for colon
                if (Check(ClassifiedTokenKind.Colon))
                {
                    isForEachOrRange = true;
                }
            }
        }
        
        if (!isForEachOrRange)
        {
            _position = checkpoint;
        }
        
        if (isForEachOrRange)
        {
            Expect(ClassifiedTokenKind.Colon);
            var rangeOrCollection = ParseExpression();
            
            // Check for optional step keyword (for-range only)
            ExpressionNode? step = null;
            if (MatchKeyword("step"))
            {
                step = ParseExpression();
            }
            
            Expect(ClassifiedTokenKind.RightParen);
            var body = ParseStatement();
            
            // Check if it's a range expression
            if (rangeOrCollection is BinaryExpressionNode binary && 
                (binary.Operator == ".." || binary.Operator == "..."))
            {
                bool isInclusive = binary.Operator == "...";
                var forRange = new ForRangeStatementNode(loopVariable!, binary.Left, binary.Right, 
                    isInclusive, step, body, token.Line, token.Column);
                OptionalSemicolon();
                return forRange;
            }
            else
            {
                // If step was specified but it's not a range, that's an error
                if (step != null)
                {
                    throw new Exception($"'step' keyword can only be used with range expressions (for-range loops) at {token.Line}:{token.Column}");
                }
                
                var forEach = new ForEachStatementNode(indexVariable, loopVariable!, 
                    rangeOrCollection, body, token.Line, token.Column);
                OptionalSemicolon();
                return forEach;
            }
        }
        else
        {
            // Traditional for loop
            StatementNode? init = null;
            if (!Check(ClassifiedTokenKind.Semicolon))
            {
                init = ParseVariableDeclarationOrExpressionStatement();
            }
            else
            {
                Advance();
            }
            
            ExpressionNode? condition = null;
            if (!Check(ClassifiedTokenKind.Semicolon))
            {
                condition = ParseExpression();
            }
            Expect(ClassifiedTokenKind.Semicolon);
            
            ExpressionNode? increment = null;
            if (!Check(ClassifiedTokenKind.RightParen))
            {
                increment = ParseExpression();
            }
            
            Expect(ClassifiedTokenKind.RightParen);
            
            var body = ParseStatement();
            
            var forStmt = new ForStatementNode(init, condition, increment, body, token.Line, token.Column);
            OptionalSemicolon();
            return forStmt;
        }
    }

    private BreakStatementNode ParseBreakStatement()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "break");
        ExpectSemicolon();
        return new BreakStatementNode(token.Line, token.Column);
    }

    private ContinueStatementNode ParseContinueStatement()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "continue");
        ExpectSemicolon();
        return new ContinueStatementNode(token.Line, token.Column);
    }

    private StatementNode ParseVariableDeclarationOrExpressionStatement()
    {
        // Check for var keyword
        if (Current().IsKeyword("var"))
        {
            return ParseVariableDeclarationStatement();
        }
        
        // Look ahead to determine if this is a variable declaration
        // Variable declaration: Type name = expr OR name = expr
        var checkpoint = _position;
        
        if (Check(ClassifiedTokenKind.Identifier))
        {
            var first = Advance();
            if (Check(ClassifiedTokenKind.Identifier))
            {
                // Type Name pattern - definitely variable declaration
                _position = checkpoint;
                return ParseVariableDeclarationStatement();
            }
            _position = checkpoint;
        }
        
        // Otherwise, it's an expression statement
        var expr = ParseExpression();
        ExpectSemicolon();
        return new ExpressionStatementNode(expr, expr.Line, expr.Column);
    }

    private StatementNode ParseVariableDeclarationStatement()
    {
        var start = Current();
        
        bool isVar = false;
        string? type = null;
        
        if (MatchKeyword("var"))
        {
            isVar = true;
        }
        else if (Check(ClassifiedTokenKind.Identifier))
        {
            var first = Advance();
            if (Check(ClassifiedTokenKind.Identifier))
            {
                type = first.Text;
            }
            else
            {
                // No type, restore position
                _position--;
            }
        }
        
        // Check for array destructuring pattern: var [a, b, c] = ...
        if (Check(ClassifiedTokenKind.LeftBracket))
        {
            return ParseArrayDestructuringStatement(type, isVar, start);
        }
        
        var name = Expect(ClassifiedTokenKind.Identifier).Text;
        
        ExpressionNode? initializer = null;
        if (MatchOperator("="))
        {
            initializer = ParseExpression();
        }
        else if (isVar)
        {
            throw new Exception($"Variable declared with 'var' must have initializer at {start.Line}:{start.Column}");
        }
        
        ExpectSemicolon();
        
        return new VariableDeclarationStatementNode(type, name, initializer, isVar, start.Line, start.Column);
    }

    private ArrayDestructuringStatementNode ParseArrayDestructuringStatement(string? type, bool isVar, ClassifiedToken start)
    {
        Expect(ClassifiedTokenKind.LeftBracket);
        
        var patterns = ParseDestructuringPatterns(type);
        
        Expect(ClassifiedTokenKind.RightBracket);
        
        ExpectOperator("=");
        var value = ParseExpression();
        
        ExpectSemicolon();
        
        return new ArrayDestructuringStatementNode(patterns, value, start.Line, start.Column);
    }
    
    private List<DestructuringPatternNode> ParseDestructuringPatterns(string? type)
    {
        var patterns = new List<DestructuringPatternNode>();
        
        while (!Check(ClassifiedTokenKind.RightBracket) && !IsAtEnd())
        {
            var patternStart = Current();
            
            // Check for skip pattern (empty comma: ,)
            if (Check(ClassifiedTokenKind.Comma))
            {
                patterns.Add(new DestructuringPatternNode(null, null, false, null, patternStart.Line, patternStart.Column));
                Advance();
                continue;
            }
            
            // Check for rest pattern: ...name
            bool isRest = false;
            if (Check(ClassifiedTokenKind.RangeInclusive))
            {
                Advance();
                isRest = true;
            }
            
            // Check for nested destructuring: [a, b]
            if (Check(ClassifiedTokenKind.LeftBracket))
            {
                Advance(); // consume [
                var nestedPatterns = ParseDestructuringPatterns(type);
                Expect(ClassifiedTokenKind.RightBracket);
                
                patterns.Add(new DestructuringPatternNode(nestedPatterns, patternStart.Line, patternStart.Column));
                
                if (!Check(ClassifiedTokenKind.RightBracket))
                    Expect(ClassifiedTokenKind.Comma);
                continue;
            }
            
            // Get the variable name
            string name = Expect(ClassifiedTokenKind.Identifier).Text;
            
            // Check for default value: name = expr
            ExpressionNode? defaultValue = null;
            if (MatchOperator("="))
            {
                defaultValue = ParseExpression();
            }
            
            patterns.Add(new DestructuringPatternNode(
                type, name, isRest, defaultValue, patternStart.Line, patternStart.Column));
            
            // If we have a rest pattern, it must be the last one
            if (isRest)
                break;
            
            if (!Check(ClassifiedTokenKind.RightBracket))
                Expect(ClassifiedTokenKind.Comma);
        }
        
        return patterns;
    }

    /* ═══════════════════════════════════════════════════════════════════
       EXPRESSIONS (with precedence climbing)
       ═══════════════════════════════════════════════════════════════════ */

    private ExpressionNode ParseExpression()
    {
        return ParsePipeExpression();
    }

    private ExpressionNode ParsePipeExpression()
    {
        var expr = ParseAssignmentExpression();
        
        while (Match(ClassifiedTokenKind.Pipe))
        {
            var target = ParsePipeTarget();
            
            // Check if target is a call with @ placeholder
            int? placeholderIndex = FindPlaceholderIndex(target);
            
            expr = new PipeExpressionNode(expr, target, placeholderIndex, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParsePipeTarget()
    {
        // After |, parse the target which could be:
        // - identifier (becomes call with piped value as first arg)
        // - function call with @ placeholders
        return ParseAssignmentExpression();
    }

    private int? FindPlaceholderIndex(ExpressionNode expr)
    {
        // Search for @ symbol in call arguments
        if (expr is CallExpressionNode call)
        {
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (IsAtPlaceholder(call.Arguments[i].Value))
                    return i;
            }
        }
        return null;  // Default to first argument
    }

    private bool IsAtPlaceholder(ExpressionNode expr)
    {
        // Check if expression is the @ placeholder
        // In the tokenizer, @ is a separate token
        // We need to represent it somehow - for now, check for special identifier
        return expr is IdentifierExpressionNode id && id.Name == "@";
    }

    private ExpressionNode ParseConditionalExpression()
    {
        var expr = ParseLogicalOrExpression();
        
        if (Match(ClassifiedTokenKind.Question))
        {
            var trueExpr = ParseExpression();
            Expect(ClassifiedTokenKind.Colon);
            var falseExpr = ParseConditionalExpression();
            
            return new ConditionalExpressionNode(
                expr, trueExpr, falseExpr, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParseAssignmentExpression()
    {
        var expr = ParseConditionalExpression();  // Changed from ParseLogicalOrExpression
        
        if (MatchOperator("=") || MatchOperator("+=") || MatchOperator("-=") || 
            MatchOperator("*=") || MatchOperator("/="))
        {
            var op = Previous().Text;
            var right = ParseAssignmentExpression();
            expr = new BinaryExpressionNode(expr, op, right, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParseLogicalOrExpression()
    {
        var expr = ParseLogicalAndExpression();
        
        while (MatchOperator("||"))
        {
            var op = Previous().Text;
            var right = ParseLogicalAndExpression();
            expr = new BinaryExpressionNode(expr, op, right, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParseLogicalAndExpression()
    {
        var expr = ParseEqualityExpression();
        
        while (MatchOperator("&&"))
        {
            var op = Previous().Text;
            var right = ParseEqualityExpression();
            expr = new BinaryExpressionNode(expr, op, right, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParseEqualityExpression()
    {
        var expr = ParseRelationalExpression();
        
        while (MatchOperator("==") || MatchOperator("!="))
        {
            var op = Previous().Text;
            var right = ParseRelationalExpression();
            expr = new BinaryExpressionNode(expr, op, right, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParseRelationalExpression()
    {
        var expr = ParseRangeExpression();
        
        while (MatchOperator("<") || MatchOperator(">") || 
               MatchOperator("<=") || MatchOperator(">="))
        {
            var op = Previous().Text;
            var right = ParseRangeExpression();
            expr = new BinaryExpressionNode(expr, op, right, expr.Line, expr.Column);
        }
        
        return expr;
    }
    
    private ExpressionNode ParseRangeExpression()
    {
        var expr = ParseAdditiveExpression();
        
        // Range operators: .. (exclusive) and ... (inclusive)
        // These are ClassifiedTokenKind.Range and ClassifiedTokenKind.RangeInclusive
        if (Match(ClassifiedTokenKind.RangeInclusive) || Match(ClassifiedTokenKind.Range))
        {
            var op = Previous().Text;
            var right = ParseAdditiveExpression();
            expr = new BinaryExpressionNode(expr, op, right, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParseAdditiveExpression()
    {
        var expr = ParseMultiplicativeExpression();
        
        while (MatchOperator("+") || MatchOperator("-"))
        {
            var op = Previous().Text;
            var right = ParseMultiplicativeExpression();
            expr = new BinaryExpressionNode(expr, op, right, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParseMultiplicativeExpression()
    {
        var expr = ParseUnaryExpression();
        
        while (MatchOperator("*") || MatchOperator("/") || MatchOperator("%"))
        {
            var op = Previous().Text;
            var right = ParseUnaryExpression();
            expr = new BinaryExpressionNode(expr, op, right, expr.Line, expr.Column);
        }
        
        return expr;
    }

    private ExpressionNode ParseUnaryExpression()
    {
        if (MatchOperator("!") || MatchOperator("-") || MatchOperator("+") ||
            MatchOperator("++") || MatchOperator("--"))
        {
            var op = Previous().Text;
            var operand = ParseUnaryExpression();
            return new UnaryExpressionNode(op, operand, true, operand.Line, operand.Column);
        }
        
        return ParsePostfixExpression();
    }

    private ExpressionNode ParsePostfixExpression()
    {
        var expr = ParsePrimaryExpression();
        
        while (true)
        {
            if (Match(ClassifiedTokenKind.LeftParen))
            {
                // Function call
                var args = ParseArgumentList();
                Expect(ClassifiedTokenKind.RightParen);
                expr = new CallExpressionNode(expr, args, expr.Line, expr.Column);
            }
            else if (Match(ClassifiedTokenKind.Dot))
            {
                // Member access
                var member = Expect(ClassifiedTokenKind.Identifier).Text;
                expr = new MemberAccessExpressionNode(expr, member, expr.Line, expr.Column);
            }
            else if (Match(ClassifiedTokenKind.LeftBracket))
            {
                // Array indexing or slicing
                ExpressionNode? first = null;
                if (!Check(ClassifiedTokenKind.Colon) && !Check(ClassifiedTokenKind.RightBracket))
                {
                    first = ParseExpression();
                }
                
                if (Match(ClassifiedTokenKind.Colon))
                {
                    // Slice: arr[start:end]
                    ExpressionNode? end = null;
                    if (!Check(ClassifiedTokenKind.RightBracket))
                    {
                        end = ParseExpression();
                    }
                    
                    Expect(ClassifiedTokenKind.RightBracket);
                    expr = new SliceExpressionNode(expr, first, end, expr.Line, expr.Column);
                }
                else
                {
                    // Index: arr[index]
                    if (first == null)
                    {
                        throw new Exception("Expected index expression");
                    }
                    
                    Expect(ClassifiedTokenKind.RightBracket);
                    expr = new IndexExpressionNode(expr, first, expr.Line, expr.Column);
                }
            }
            else if (MatchOperator("++") || MatchOperator("--"))
            {
                // Postfix increment/decrement
                var op = Previous().Text;
                expr = new UnaryExpressionNode(op, expr, false, expr.Line, expr.Column);
            }
            else
            {
                break;
            }
        }
        
        return expr;
    }

    private ExpressionNode ParsePrimaryExpression()
    {
        var token = Current();

        // Literals
        if (token.Is(ClassifiedTokenKind.IntegerLiteral))
        {
            Advance();
            return new LiteralExpressionNode(token.Value, LiteralKind.Integer, token.Line, token.Column);
        }
        
        if (token.Is(ClassifiedTokenKind.FloatLiteral))
        {
            Advance();
            return new LiteralExpressionNode(token.Value, LiteralKind.Float, token.Line, token.Column);
        }
        
        if (token.Is(ClassifiedTokenKind.StringLiteral))
        {
            Advance();
            return new LiteralExpressionNode(token.Value, LiteralKind.String, token.Line, token.Column);
        }
        
        if (token.Is(ClassifiedTokenKind.InterpolatedString))
        {
            Advance();
            var parts = token.InterpolationParts?.ToList() ?? new List<InterpolatedStringPart>();
            return new InterpolatedStringExpressionNode(parts, token.Line, token.Column);
        }
        
        if (token.Is(ClassifiedTokenKind.CharLiteral))
        {
            Advance();
            return new LiteralExpressionNode(token.Value, LiteralKind.Char, token.Line, token.Column);
        }
        
        // Keywords as literals
        if (token.IsKeyword("true"))
        {
            Advance();
            return new LiteralExpressionNode(true, LiteralKind.Boolean, token.Line, token.Column);
        }
        
        if (token.IsKeyword("false"))
        {
            Advance();
            return new LiteralExpressionNode(false, LiteralKind.Boolean, token.Line, token.Column);
        }
        
        if (token.IsKeyword("null"))
        {
            Advance();
            return new LiteralExpressionNode(null, LiteralKind.Null, token.Line, token.Column);
        }
        
        if (token.IsKeyword("this"))
        {
            Advance();
            return new ThisExpressionNode(token.Line, token.Column);
        }
        
        if (token.IsKeyword("new"))
        {
            return ParseNewExpression();
        }
        
        // @ placeholder for pipes
        if (token.Is(ClassifiedTokenKind.At))
        {
            Advance();
            return new IdentifierExpressionNode("@", token.Line, token.Column);
        }
        
        // Object literal: { ... }
        if (token.Is(ClassifiedTokenKind.LeftBrace))
        {
            return ParseObjectLiteral();
        }
        
        // Array literal: [ ... ]
        if (Match(ClassifiedTokenKind.LeftBracket))
        {
            return ParseArrayLiteral(token);
        }
        
        // Parenthesized expression or lambda
        if (Match(ClassifiedTokenKind.LeftParen))
        {
            // Look ahead to determine if it's a lambda or parenthesized expression
            // Lambda: () -> expr or (params) -> expr
            // Parenthesized: (expr)
            
            var checkpoint = _position;
            
            // Try to parse as lambda parameters
            if (Check(ClassifiedTokenKind.RightParen))
            {
                // () -> might be lambda with no params
                Advance(); // consume )
                if (MatchOperator("->"))
                {
                    // It's a lambda with no parameters
                    return ParseLambdaBody(new List<ParameterNode>(), token.Line, token.Column);
                }
                // Not a lambda, restore and parse as error (empty parens)
                _position = checkpoint;
            }
            else if (TryParseLambdaParameters(out var parameters))
            {
                // Successfully parsed lambda parameters
                Expect(ClassifiedTokenKind.RightParen);
                if (MatchOperator("->"))
                {
                    return ParseLambdaBody(parameters, token.Line, token.Column);
                }
                // Not a lambda, restore position
                _position = checkpoint;
            }
            
            // Not a lambda, parse as parenthesized expression
            var expr = ParseExpression();
            Expect(ClassifiedTokenKind.RightParen);
            return new ParenthesizedExpressionNode(expr, token.Line, token.Column);
        }
        
        // Identifier
        if (token.Is(ClassifiedTokenKind.Identifier))
        {
            Advance();
            return new IdentifierExpressionNode(token.Text, token.Line, token.Column);
        }
        
        throw new Exception($"Unexpected token: {token} at {token.Line}:{token.Column}");
    }

    private NewExpressionNode ParseNewExpression()
    {
        var token = Expect(ClassifiedTokenKind.Keyword, "new");
        var typeName = Expect(ClassifiedTokenKind.Identifier).Text;
        
        Expect(ClassifiedTokenKind.LeftParen);
        var args = ParseArgumentList();
        Expect(ClassifiedTokenKind.RightParen);
        
        return new NewExpressionNode(typeName, args, token.Line, token.Column);
    }

    /* ═══════════════════════════════════════════════════════════════════
       OBJECT LITERALS AND LAMBDAS
       ═══════════════════════════════════════════════════════════════════ */

    private ObjectLiteralExpressionNode ParseObjectLiteral()
    {
        var token = Expect(ClassifiedTokenKind.LeftBrace);
        var obj = new ObjectLiteralExpressionNode(token.Line, token.Column);
        
        while (!Check(ClassifiedTokenKind.RightBrace) && !IsAtEnd())
        {
            obj.Properties.Add(ParseObjectProperty());
            
            // Properties can be separated by commas or newlines (implicit semicolons)
            if (Check(ClassifiedTokenKind.Comma))
            {
                Advance();
            }
            else if (Check(ClassifiedTokenKind.Semicolon))
            {
                Advance(); // Optional separator
            }
        }
        
        Expect(ClassifiedTokenKind.RightBrace);
        return obj;
    }

    private ObjectPropertyNode ParseObjectProperty()
    {
        var start = Current();
        
        // Parse: [Type] name: value
        // or:    string: () -> expr  (type adapter)
        
        string? type = null;
        string name;
        bool isTypeAdapter = false;
        
        if (Check(ClassifiedTokenKind.Identifier))
        {
            var firstToken = Advance();
            
            if (Check(ClassifiedTokenKind.Colon))
            {
                // Pattern: name: value (no type)
                name = firstToken.Text;
                type = null;
                
                // Check if this is a type adapter (name looks like a type)
                isTypeAdapter = IsTypeName(name);
            }
            else if (Check(ClassifiedTokenKind.Identifier))
            {
                // Pattern: Type name: value
                type = firstToken.Text;
                name = Advance().Text;
            }
            else
            {
                throw new Exception($"Expected ':' or identifier after '{firstToken.Text}' at {firstToken.Line}:{firstToken.Column}");
            }
        }
        else
        {
            throw new Exception($"Expected property name at {start.Line}:{start.Column}");
        }
        
        Expect(ClassifiedTokenKind.Colon);
        
        var value = ParseExpression();
        
        return new ObjectPropertyNode(type, name, value, isTypeAdapter, start.Line, start.Column);
    }

    private bool IsTypeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
            
        // Check if identifier starts with uppercase (type convention)
        // or is a known type keyword
        return char.IsUpper(name[0]) || 
               name == "int" || name == "string" || name == "bool" || 
               name == "float" || name == "double" || name == "object" ||
               name == "void" || name == "var";
    }

    private bool IsTypeAdapter()
    {
        // Check if current position looks like a type adapter: TypeName() -> ... or TypeName() { ... }
        if (!Check(ClassifiedTokenKind.Identifier))
            return false;
            
        var identifierName = Current().Text;
        if (!IsTypeName(identifierName))
            return false;
            
        var lookahead1 = Peek(1);
        if (lookahead1?.Is(ClassifiedTokenKind.LeftParen) != true)
            return false;
            
        var lookahead2 = Peek(2);
        if (lookahead2?.Is(ClassifiedTokenKind.RightParen) != true)
            return false;
            
        var lookahead3 = Peek(3);
        return lookahead3?.IsOperator("->") == true || lookahead3?.Is(ClassifiedTokenKind.LeftBrace) == true;
    }

    private bool TryParseLambdaParameters(out List<ParameterNode> parameters)
    {
        parameters = new List<ParameterNode>();
        var checkpoint = _position;
        
        try
        {
            // Try to parse parameter list
            // Could be: name, name, ... or Type name, Type name, ...
            do
            {
                if (Check(ClassifiedTokenKind.RightParen))
                    break;
                    
                var start = Current();
                
                if (!Check(ClassifiedTokenKind.Identifier))
                {
                    _position = checkpoint;
                    return false;
                }
                
                var firstToken = Advance();
                
                string? type = null;
                string name;
                
                if (Check(ClassifiedTokenKind.Identifier))
                {
                    // Has type
                    type = firstToken.Text;
                    name = Advance().Text;
                }
                else
                {
                    // No type
                    name = firstToken.Text;
                }
                
                parameters.Add(new ParameterNode(type, null, name, false, null, start.Line, start.Column));
                
            } while (Match(ClassifiedTokenKind.Comma));
            
            // Must be followed by ) for valid lambda parameters
            if (!Check(ClassifiedTokenKind.RightParen))
            {
                _position = checkpoint;
                return false;
            }
            
            return true;
        }
        catch
        {
            _position = checkpoint;
            return false;
        }
    }

    private LambdaExpressionNode ParseLambdaBody(List<ParameterNode> parameters, int line, int column)
    {
        // After ->, parse either a block or an expression
        if (Check(ClassifiedTokenKind.LeftBrace))
        {
            // Block lambda: () -> { statements }
            var block = ParseBlock();
            return new LambdaExpressionNode(parameters, block, true, line, column);
        }
        else
        {
            // Expression lambda: () -> expr
            var expr = ParseExpression();
            return new LambdaExpressionNode(parameters, expr, false, line, column);
        }
    }
    

    /* ═══════════════════════════════════════════════════════════════════
       HELPER METHODS
       ═══════════════════════════════════════════════════════════════════ */

    private string ParseQualifiedName()
    {
        var parts = new List<string>();
        parts.Add(Expect(ClassifiedTokenKind.Identifier).Text);
        
        while (Match(ClassifiedTokenKind.Dot))
        {
            parts.Add(Expect(ClassifiedTokenKind.Identifier).Text);
        }
        
        return string.Join(".", parts);
    }

    private List<ParameterNode> ParseParameterList()
    {
        Expect(ClassifiedTokenKind.LeftParen);
        return ParseParameterListFromParen();
    }

    private List<ParameterNode> ParseParameterListFromParen()
    {
        var parameters = new List<ParameterNode>();
        
        if (!Check(ClassifiedTokenKind.RightParen))
        {
            do
            {
                var start = Current();
                
                string? type = null;
                StructuralTypeNode? structuralType = null;
                string name;
                
                // Check for object with structural type
                if (Check(ClassifiedTokenKind.Identifier) && Current().Text == "object")
                {
                    Advance(); // consume "object"
                    if (Check(ClassifiedTokenKind.LeftBrace))
                    {
                        structuralType = ParseStructuralType();
                        // Parameter name is optional for structural types
                        // If followed by comma or ), use a placeholder name
                        if (Check(ClassifiedTokenKind.Comma) || Check(ClassifiedTokenKind.RightParen))
                        {
                            name = "_"; // Anonymous structural parameter
                            ExpressionNode? defaultValue = null;
                            if (MatchOperator("="))
                            {
                                defaultValue = ParseExpression();
                            }
                            parameters.Add(new ParameterNode(null, structuralType, name, false, defaultValue, start.Line, start.Column));
                            continue;
                        }
                    }
                    else
                    {
                        type = "object";
                    }
                }
                else
                {
                    // Try to parse type (optional)
                    var first = Expect(ClassifiedTokenKind.Identifier).Text;
                    
                    if (Check(ClassifiedTokenKind.Identifier) || Check(ClassifiedTokenKind.RangeInclusive))
                    {
                        // Has type
                        type = first;
                    }
                    else
                    {
                        // No type, first is the name
                        name = first;
                        
                        ExpressionNode? defaultValue = null;
                        if (MatchOperator("="))
                        {
                            defaultValue = ParseExpression();
                        }
                        
                        parameters.Add(new ParameterNode(null, null, name, false, defaultValue, start.Line, start.Column));
                        continue;
                    }
                }
                
                // Check for varargs: type...
                bool isVarargs = false;
                if (Check(ClassifiedTokenKind.RangeInclusive))
                {
                    Advance();
                    isVarargs = true;
                }
                
                name = Expect(ClassifiedTokenKind.Identifier).Text;
                
                ExpressionNode? paramDefaultValue = null;
                if (MatchOperator("="))
                {
                    paramDefaultValue = ParseExpression();
                }
                
                parameters.Add(new ParameterNode(type, structuralType, name, isVarargs, paramDefaultValue, start.Line, start.Column));
                
            } while (Match(ClassifiedTokenKind.Comma));
        }
        
        Expect(ClassifiedTokenKind.RightParen);
        return parameters;
    }

    private List<ArgumentNode> ParseArgumentList()
    {
        var arguments = new List<ArgumentNode>();
        bool seenNamed = false;
        
        if (!Check(ClassifiedTokenKind.RightParen))
        {
            do
            {
                var argStart = Current();
                
                // Check for named argument: name: value
                if (Check(ClassifiedTokenKind.Identifier) && 
                    Peek(1)?.Is(ClassifiedTokenKind.Colon) == true)
                {
                    var name = Advance().Text;
                    Expect(ClassifiedTokenKind.Colon);
                    var value = ParseExpression();
                    
                    arguments.Add(new ArgumentNode(name, value, argStart.Line, argStart.Column));
                    seenNamed = true;
                }
                else
                {
                    // Positional argument
                    if (seenNamed)
                    {
                        throw new Exception(
                            $"Positional argument cannot appear after named argument at {argStart.Line}:{argStart.Column}");
                    }
                    
                    var value = ParseExpression();
                    arguments.Add(new ArgumentNode(null, value, argStart.Line, argStart.Column));
                }
            } while (Match(ClassifiedTokenKind.Comma));
        }
        
        return arguments;
    }

    private void ExpectSemicolon()
    {
        if (!Match(ClassifiedTokenKind.Semicolon))
        {
            // Semicolons are optional in Sushi, but we should consume them if present
            // If not present, that's OK due to implicit semicolon insertion
        }
    }
    
    private void OptionalSemicolon()
    {
        // Consume any semicolons that are present (even multiple ones)
        // This handles cases like:
        // var x = 1;
        // var y = 2;;  // double semicolon is harmless
        // var z = 3
        while (Match(ClassifiedTokenKind.Semicolon))
        {
            // Keep consuming semicolons
        }
    }

    /* ═══════════════════════════════════════════════════════════════════
       TOKEN NAVIGATION
       ═══════════════════════════════════════════════════════════════════ */

    private ClassifiedToken Current()
    {
        return _tokens[_position];
    }

    private ClassifiedToken? Peek(int offset)
    {
        var pos = _position + offset;
        if (pos >= _tokens.Count)
            return null;
        return _tokens[pos];
    }

    private ClassifiedToken Previous()
    {
        return _tokens[_position - 1];
    }

    private ClassifiedToken Advance()
    {
        if (!IsAtEnd())
            _position++;
        return Previous();
    }

    private bool IsAtEnd()
    {
        return _position >= _tokens.Count || Current().Is(ClassifiedTokenKind.EndOfFile);
    }

    private bool Check(ClassifiedTokenKind kind)
    {
        if (IsAtEnd()) return false;
        return Current().Is(kind);
    }

    private bool Check(ClassifiedTokenKind kind, string text)
    {
        if (IsAtEnd()) return false;
        var token = Current();
        return token.Is(kind) && token.Text == text;
    }

    private bool Match(ClassifiedTokenKind kind)
    {
        if (Check(kind))
        {
            Advance();
            return true;
        }
        return false;
    }

    private bool MatchKeyword(string keyword)
    {
        if (Check(ClassifiedTokenKind.Keyword, keyword))
        {
            Advance();
            return true;
        }
        return false;
    }

    private bool MatchOperator(string op)
    {
        if (IsAtEnd()) return false;
        var token = Current();
        if (token.Is(ClassifiedTokenKind.Operator) && token.Text == op)
        {
            Advance();
            return true;
        }
        return false;
    }

    private ClassifiedToken Expect(ClassifiedTokenKind kind)
    {
        if (Check(kind))
            return Advance();
        
        var current = IsAtEnd() ? "EOF" : Current().ToString();
        throw new Exception($"Expected {kind} but got {current}");
    }

    private ClassifiedToken Expect(ClassifiedTokenKind kind, string text)
    {
        if (Check(kind, text))
            return Advance();
        
        var current = IsAtEnd() ? "EOF" : Current().ToString();
        throw new Exception($"Expected {kind} '{text}' but got {current}");
    }

    private void ExpectKeyword(string keyword)
    {
        if (!Current().IsKeyword(keyword))
        {
            throw new Exception($"Expected keyword '{keyword}' but got {Current()}");
        }
        Advance();
    }

    private bool CheckOperator(string op)
    {
        return !IsAtEnd() && Current().IsOperator(op);
    }

    private void ExpectOperator(string op)
    {
        if (!Current().IsOperator(op))
        {
            throw new Exception($"Expected operator '{op}' but got {Current()}");
        }
        Advance();
    }

    /* ═══════════════════════════════════════════════════════════════════
       NEW PARSING METHODS - ARRAYS, ENUMS, SWITCH, ETC.
       ═══════════════════════════════════════════════════════════════════ */

    private ArrayLiteralExpressionNode ParseArrayLiteral(ClassifiedToken start)
    {
        var elements = new List<ExpressionNode>();
        
        while (!Check(ClassifiedTokenKind.RightBracket) && !IsAtEnd())
        {
            elements.Add(ParseExpression());
            
            if (!Check(ClassifiedTokenKind.RightBracket))
                Expect(ClassifiedTokenKind.Comma);
        }
        
        Expect(ClassifiedTokenKind.RightBracket);
        
        return new ArrayLiteralExpressionNode(elements, start.Line, start.Column);
    }

    private StructuralTypeNode ParseStructuralType()
    {
        var start = Expect(ClassifiedTokenKind.LeftBrace);
        var fields = new Dictionary<string, string>();
        
        while (!Check(ClassifiedTokenKind.RightBrace) && !IsAtEnd())
        {
            var fieldType = Expect(ClassifiedTokenKind.Identifier).Text;
            
            // Optional: handle optional fields with ?
            if (Match(ClassifiedTokenKind.Question))
            {
                fieldType += "?";
            }
            
            var fieldName = Expect(ClassifiedTokenKind.Identifier).Text;
            fields[fieldName] = fieldType;
            
            if (!Check(ClassifiedTokenKind.RightBrace))
                Match(ClassifiedTokenKind.Comma);
        }
        
        Expect(ClassifiedTokenKind.RightBrace);
        
        return new StructuralTypeNode(fields, start.Line, start.Column);
    }

    private SwitchStatementNode ParseSwitchStatement()
    {
        var start = Expect(ClassifiedTokenKind.Keyword, "switch");
        
        Expect(ClassifiedTokenKind.LeftParen);
        var value = ParseExpression();
        Expect(ClassifiedTokenKind.RightParen);
        
        Expect(ClassifiedTokenKind.LeftBrace);
        
        var cases = new List<SwitchCaseNode>();
        BlockStatementNode? defaultCase = null;
        
        while (!Check(ClassifiedTokenKind.RightBrace) && !IsAtEnd())
        {
            if (Match(ClassifiedTokenKind.Semicolon))
                continue;
            
            if (MatchKeyword("default"))
            {
                ExpectOperator("->");
                defaultCase = ParseBlock();
            }
            else
            {
                cases.Add(ParseSwitchCase());
            }
        }
        
        Expect(ClassifiedTokenKind.RightBrace);
        
        // Consume optional semicolon after switch statement
        OptionalSemicolon();
        
        return new SwitchStatementNode(value, cases, defaultCase, start.Line, start.Column);
    }

    private SwitchCaseNode ParseSwitchCase()
    {
        var start = Current();
        var matchValues = new List<ExpressionNode>();
        
        // Parse match values: 1, 2, 3 ->
        matchValues.Add(ParseExpression());
        
        while (Check(ClassifiedTokenKind.Comma))
        {
            // Peek ahead to see if we have -> after the comma
            var checkpoint = _position;
            Advance(); // consume comma
            
            if (CheckOperator("->"))
            {
                // This comma was a mistake, restore and break
                _position = checkpoint;
                break;
            }
            
            // Parse next match value
            matchValues.Add(ParseExpression());
        }
        
        ExpectOperator("->");
        var body = ParseBlock();
        
        var alsoCases = new List<ExpressionNode>();
        
        return new SwitchCaseNode(matchValues, body, alsoCases, start.Line, start.Column);
    }
    
    private DoWhileStatementNode ParseDoWhileStatement()
    {
        var start = Expect(ClassifiedTokenKind.Keyword, "do");
        var body = ParseStatement();
        
        ExpectKeyword("while");
        Expect(ClassifiedTokenKind.LeftParen);
        var condition = ParseExpression();
        Expect(ClassifiedTokenKind.RightParen);
        
        ExpectSemicolon();
        
        return new DoWhileStatementNode(body, condition, start.Line, start.Column);
    }

    private EnumDeclarationNode ParseEnumDeclaration()
    {
        var start = Expect(ClassifiedTokenKind.Keyword, "enum");
        var name = Expect(ClassifiedTokenKind.Identifier).Text;
        
        // Optional record-style parameters: enum Status(int code, string message)
        List<ParameterNode>? recordParams = null;
        if (Check(ClassifiedTokenKind.LeftParen))
        {
            Advance();
            recordParams = ParseParameterListFromParen();
        }
        
        Expect(ClassifiedTokenKind.LeftBrace);
        
        var values = new List<EnumValueNode>();
        ConstructorDeclarationNode? explicitConstructor = null;
        var methods = new List<FunctionDeclarationNode>();
        var typeAdapters = new List<TypeAdapterDeclarationNode>();
        
        bool parsingValues = true;
        while (!Check(ClassifiedTokenKind.RightBrace) && !IsAtEnd())
        {
            // Skip any semicolons - they separate values from methods
            while (Match(ClassifiedTokenKind.Semicolon))
            {
                parsingValues = false;
            }
            
            // After consuming semicolons, check if we're at the end
            if (Check(ClassifiedTokenKind.RightBrace) || IsAtEnd())
                break;
            
            if (parsingValues && LooksLikeEnumValue())
            {
                values.Add(ParseEnumValue(recordParams));
                Match(ClassifiedTokenKind.Comma);
            }
            else if (Check(ClassifiedTokenKind.Keyword, "new"))
            {
                parsingValues = false;
                explicitConstructor = ParseConstructor();
            }
            else if (IsTypeAdapter())
            {
                parsingValues = false;
                typeAdapters.Add(ParseTypeAdapter());
            }
            else if (!parsingValues && Check(ClassifiedTokenKind.Identifier))
            {
                // Parse method (no return type support in enum methods for simplicity)
                var methodStart = Current();
                var methodName = Expect(ClassifiedTokenKind.Identifier).Text;
                var parameters = ParseParameterList();
                
                StatementNode body;
                bool isArrow = false;
                if (MatchOperator("->"))
                {
                    isArrow = true;
                    var expr = ParseExpression();
                    body = new ReturnStatementNode(expr, methodStart.Line, methodStart.Column);
                    ExpectSemicolon();
                }
                else
                {
                    body = ParseBlock();
                }
                
                var method = new FunctionDeclarationNode(null, methodName, parameters, methodStart.Line, methodStart.Column);
                method.Body = body;
                method.IsArrowFunction = isArrow;
                methods.Add(method);
            }
            else
            {
                throw new Exception($"Unexpected token in enum: {Current()}");
            }
        }
        
        Expect(ClassifiedTokenKind.RightBrace);
        
        // Consume optional semicolon after enum body
        OptionalSemicolon();
        
        return new EnumDeclarationNode(
            name, recordParams, values, explicitConstructor,
            methods, typeAdapters, start.Line, start.Column);
    }

    private bool LooksLikeEnumValue()
    {
        if (!Check(ClassifiedTokenKind.Identifier))
            return false;
        
        var next = Peek(1);
        
        // If followed by (, need to look further to distinguish enum value from method
        if (next != null && next.Is(ClassifiedTokenKind.LeftParen))
        {
            // Scan ahead to find the matching )
            int parenDepth = 0;
            int offset = 1;
            while (true)
            {
                var tok = Peek(offset);
                if (tok == null) break;
                
                if (tok.Is(ClassifiedTokenKind.LeftParen))
                    parenDepth++;
                else if (tok.Is(ClassifiedTokenKind.RightParen))
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        // Found matching ), check what's after
                        var afterParen = Peek(offset + 1);
                        // Method if followed by { or ->
                        // Enum value if followed by , or } or nothing
                        if (afterParen == null)
                            return true; // enum value
                        if (afterParen.Is(ClassifiedTokenKind.LeftBrace) || afterParen.IsOperator("->"))
                            return false; // method
                        return true; // enum value
                    }
                }
                offset++;
                if (offset > 50) break; // Safety limit
            }
        }
        
        return next == null || 
               next.Is(ClassifiedTokenKind.Comma) ||
               next.Is(ClassifiedTokenKind.RightBrace) ||
               next.Is(ClassifiedTokenKind.LeftBrace) ||
               next.Is(ClassifiedTokenKind.Semicolon) ||
               next.IsOperator("=");
    }

    private EnumValueNode ParseEnumValue(List<ParameterNode>? recordParams)
    {
        var start = Current();
        var name = Expect(ClassifiedTokenKind.Identifier).Text;
        
        ExpressionNode? directValue = null;
        List<ExpressionNode>? constructorArgs = null;
        Dictionary<string, ExpressionNode>? properties = null;
        
        if (MatchOperator("="))
        {
            // Direct value: Red = 1
            directValue = ParseExpression();
        }
        else if (Match(ClassifiedTokenKind.LeftParen))
        {
            // Constructor args: Ok(200, "OK")
            constructorArgs = new List<ExpressionNode>();
            
            if (!Check(ClassifiedTokenKind.RightParen))
            {
                constructorArgs.Add(ParseExpression());
                while (Match(ClassifiedTokenKind.Comma))
                {
                    constructorArgs.Add(ParseExpression());
                }
            }
            
            Expect(ClassifiedTokenKind.RightParen);
        }
        else if (Match(ClassifiedTokenKind.LeftBrace))
        {
            // Inline properties: North { x : 0, y : 1 }
            properties = new Dictionary<string, ExpressionNode>();
            
            while (!Check(ClassifiedTokenKind.RightBrace))
            {
                var propName = Expect(ClassifiedTokenKind.Identifier).Text;
                Expect(ClassifiedTokenKind.Colon, ":");
                var propValue = ParseExpression();
                properties[propName] = propValue;
                
                if (!Check(ClassifiedTokenKind.RightBrace))
                    Match(ClassifiedTokenKind.Comma);
            }
            
            Expect(ClassifiedTokenKind.RightBrace);
        }
        
        return new EnumValueNode(name, directValue, constructorArgs, properties, start.Line, start.Column);
    }
}