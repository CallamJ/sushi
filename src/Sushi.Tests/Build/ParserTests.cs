using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Sushi.Build;
using Sushi.Build.SyntaxTree;

namespace Sushi.Tests
{
    public class ParserTests
    {
        private ProgramNode Parse(string source)
        {
            var tokenizer = new Tokenizer(source);
            List<UnclassifiedToken> tokens = tokenizer.Tokenize().ToList();
            
            var lexer = new Lexer(tokens);
            List<ClassifiedToken> classifiedTokens = lexer.Lex().ToList();
            
            var parser = new Parser(classifiedTokens);
            return parser.Parse();
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // BASIC FEATURES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestBoxDeclaration()
        {
            var ast = Parse("box Test");
            Assert.Single(ast.Declarations);
            Assert.IsType<BoxDeclarationNode>(ast.Declarations[0]);
            var box = (BoxDeclarationNode)ast.Declarations[0];
            Assert.Equal("Test", box.FullName);
        }
        
        [Fact]
        public void TestVariableWithVar()
        {
            var ast = Parse("var x = 42");
            Assert.Single(ast.Declarations);
            Assert.IsType<VariableDeclarationStatementNode>(ast.Declarations[0]);
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.True(varDecl.IsVar);
            Assert.Equal("x", varDecl.Name);
            Assert.Null(varDecl.Type);
        }
        
        [Fact]
        public void TestVariableWithType()
        {
            var ast = Parse("int x = 42");
            Assert.Single(ast.Declarations);
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.False(varDecl.IsVar);
            Assert.Equal("int", varDecl.Type);
            Assert.Equal("x", varDecl.Name);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // ARRAYS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestArrayLiteral()
        {
            var ast = Parse("var arr = [1, 2, 3]");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<ArrayLiteralExpressionNode>(varDecl.Initializer);
            var array = (ArrayLiteralExpressionNode)varDecl.Initializer;
            Assert.Equal(3, array.Elements.Count);
        }
        
        [Fact]
        public void TestArrayIndexing()
        {
            var ast = Parse("var x = arr[0]");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<IndexExpressionNode>(varDecl.Initializer);
        }
        
        [Fact]
        public void TestArraySlicing()
        {
            var ast = Parse("var x = arr[1:4]");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<SliceExpressionNode>(varDecl.Initializer);
            var slice = (SliceExpressionNode)varDecl.Initializer;
            Assert.NotNull(slice.Start);
            Assert.NotNull(slice.End);
        }
        
        [Fact]
        public void TestArrayDestructuring()
        {
            var ast = Parse("var [a, b, c] = [1, 2, 3]");
            Assert.IsType<ArrayDestructuringStatementNode>(ast.Declarations[0]);
            var destruct = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(3, destruct.Patterns.Count);
        }
        
        [Fact]
        public void TestArrayDestructuringWithRest()
        {
            var ast = Parse("var [head, ...tail] = [1, 2, 3, 4]");
            var destruct = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(2, destruct.Patterns.Count);
            Assert.True(destruct.Patterns[1].IsRest);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // TERNARY OPERATOR
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestTernaryOperator()
        {
            var ast = Parse("var x = age >= 18 ? \"Adult\" : \"Minor\"");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<ConditionalExpressionNode>(varDecl.Initializer);
            var ternary = (ConditionalExpressionNode)varDecl.Initializer;
            Assert.NotNull(ternary.Condition);
            Assert.NotNull(ternary.TrueExpression);
            Assert.NotNull(ternary.FalseExpression);
        }
        
        [Fact]
        public void TestNestedTernary()
        {
            var ast = Parse(@"var grade = score >= 90 ? ""A"" : 
                                          score >= 80 ? ""B"" : ""C""");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var ternary = (ConditionalExpressionNode)varDecl.Initializer;
            Assert.IsType<ConditionalExpressionNode>(ternary.FalseExpression);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // FUNCTIONS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestFunctionWithParameters()
        {
            var ast = Parse(@"
                add(int a, int b) {
                    return a + b
                }
            ");
            Assert.Single(ast.Declarations);
            Assert.IsType<FunctionDeclarationNode>(ast.Declarations[0]);
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Equal("add", func.Name);
            Assert.Equal(2, func.Parameters.Count);
        }
        
        [Fact]
        public void TestFunctionWithNoParameters()
        {
            var ast = Parse(@"
                test() {
                    var x = 42
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Equal("test", func.Name);
            Assert.Empty(func.Parameters);
        }
        
        [Fact]
        public void TestFunctionWithDefaultParameters()
        {
            var ast = Parse(@"
                greet(string name = ""World"") {
                    print(name)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Single(func.Parameters);
            Assert.NotNull(func.Parameters[0].DefaultValue);
        }
        
        [Fact]
        public void TestFunctionWithVarargs()
        {
            var ast = Parse(@"
                sum(int... numbers) {
                    return 0
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Single(func.Parameters);
            Assert.True(func.Parameters[0].IsVarargs);
        }
        
        [Fact]
        public void TestArrowFunction()
        {
            var ast = Parse("double(int x) -> x * 2");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.True(func.IsArrowFunction);
            Assert.IsType<ReturnStatementNode>(func.Body);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // SWITCH STATEMENTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestSwitchWithIntegers()
        {
            var ast = Parse(@"
                test(int x) {
                    switch (x) {
                        1 -> { print(""one"") }
                        2, 3 -> { print(""two or three"") }
                        default -> { print(""other"") }
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<SwitchStatementNode>(block.Statements[0]);
            var switchStmt = (SwitchStatementNode)block.Statements[0];
            Assert.Equal(2, switchStmt.Cases.Count);
            Assert.NotNull(switchStmt.DefaultCase);
        }
        
        [Fact]
        public void TestSwitchWithStrings()
        {
            var ast = Parse(@"
                test(string s) {
                    switch (s) {
                        ""pending"", ""waiting"" -> { print(""wait"") }
                        default -> { print(""done"") }
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var switchStmt = (SwitchStatementNode)block.Statements[0];
            Assert.Single(switchStmt.Cases);
            Assert.Equal(2, switchStmt.Cases[0].MatchValues.Count);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // ENUMS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestSimpleEnum()
        {
            var ast = Parse(@"
                enum Color {
                    Red,
                    Green,
                    Blue
                }
            ");
            Assert.IsType<EnumDeclarationNode>(ast.Declarations[0]);
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Equal("Color", enumDecl.Name);
            Assert.Equal(3, enumDecl.Values.Count);
        }
        
        [Fact]
        public void TestEnumWithDirectValues()
        {
            var ast = Parse(@"
                enum Status {
                    Pending = 0,
                    Active = 1,
                    Done = 2
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.All(enumDecl.Values, v => Assert.NotNull(v.DirectValue));
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // NAMED ARGUMENTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestNamedArguments()
        {
            var ast = Parse(@"
                test() {
                    greet(name: ""Alice"", age: 25)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            var call = (CallExpressionNode)exprStmt.Expression;
            Assert.Equal(2, call.Arguments.Count);
            Assert.NotNull(call.Arguments[0].Name);
            Assert.Equal("name", call.Arguments[0].Name);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // CLASSES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestClassDeclaration()
        {
            var ast = Parse(@"
                class Person {
                    string name
                    int age
                    
                    new(string name, int age) {
                        this.name = name
                        this.age = age
                    }
                    
                    greet() {
                        print(this.name)
                    }
                }
            ");
            Assert.IsType<ClassDeclarationNode>(ast.Declarations[0]);
            var classDecl = (ClassDeclarationNode)ast.Declarations[0];
            Assert.Equal("Person", classDecl.Name);
            Assert.Equal(2, classDecl.Fields.Count);
            Assert.NotNull(classDecl.Constructor);
            Assert.Single(classDecl.Methods);
        }

        [Fact]
        public void TestClassFieldsSupportCommaSeparatedAndChainedInitializers()
        {
            var ast = Parse("class Settings { string dee = \"hello\", dum = \"world\", doo = too = foo = \"many things\" }");

            var declaration = Assert.IsType<ClassDeclarationNode>(ast.Declarations[0]);
            Assert.Equal(new[] { "dee", "dum", "doo", "too", "foo" }, declaration.Fields.Select(field => field.Name));
            Assert.All(declaration.Fields, field => Assert.Equal("string", field.Type));
            Assert.Equal("many things", Assert.IsType<LiteralExpressionNode>(declaration.Fields[2].Initializer).Value);
            Assert.Equal("many things", Assert.IsType<LiteralExpressionNode>(declaration.Fields[3].Initializer).Value);
            Assert.Equal("many things", Assert.IsType<LiteralExpressionNode>(declaration.Fields[4].Initializer).Value);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // FLOAT LITERALS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestFloatLiteral()
        {
            var ast = Parse("var price = 99.99");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var literal = (LiteralExpressionNode)varDecl.Initializer;
            Assert.Equal(99.99, literal.Value);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // VAR IN FUNCTION BODIES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestVarInFunctionBody()
        {
            var ast = Parse(@"
                test() {
                    var x = 42
                    var y = ""hello""
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(2, block.Statements.Count);
            Assert.All(block.Statements, s => Assert.IsType<VariableDeclarationStatementNode>(s));
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // ERROR CASES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestVarWithoutInitializerThrows()
        {
            Assert.Throws<Exception>(() => Parse("var x"));
        }
        
        [Fact]
        public void TestInvalidSyntaxThrows()
        {
            Assert.Throws<Exception>(() => Parse("@#$%"));
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // DO-WHILE LOOPS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestDoWhileLoop()
        {
            var ast = Parse(@"
                test() {
                    var count = 0
                    do {
                        print(count)
                        count = count + 1
                    } while (count < 5)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<DoWhileStatementNode>(block.Statements[1]);
            var doWhile = (DoWhileStatementNode)block.Statements[1];
            Assert.NotNull(doWhile.Condition);
            Assert.NotNull(doWhile.Body);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // TRADITIONAL FOR LOOPS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestTraditionalForLoop()
        {
            var ast = Parse(@"
                test() {
                    for (var i = 0; i < 10; i = i + 1) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<ForStatementNode>(block.Statements[0]);
            var forLoop = (ForStatementNode)block.Statements[0];
            Assert.NotNull(forLoop.Initializer);
            Assert.NotNull(forLoop.Condition);
            Assert.NotNull(forLoop.Increment);
        }
        
        [Fact]
        public void TestForLoopWithoutInitializer()
        {
            var ast = Parse(@"
                test() {
                    var i = 0
                    for (; i < 10; i = i + 1) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forLoop = (ForStatementNode)block.Statements[1];
            Assert.Null(forLoop.Initializer);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // CONTROL FLOW
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestBreakStatement()
        {
            var ast = Parse(@"
                test() {
                    while (true) {
                        break
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var whileLoop = (WhileStatementNode)block.Statements[0];
            var whileBody = (BlockStatementNode)whileLoop.Body;
            Assert.IsType<BreakStatementNode>(whileBody.Statements[0]);
        }
        
        [Fact]
        public void TestContinueStatement()
        {
            var ast = Parse(@"
                test() {
                    while (true) {
                        continue
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var whileLoop = (WhileStatementNode)block.Statements[0];
            var whileBody = (BlockStatementNode)whileLoop.Body;
            Assert.IsType<ContinueStatementNode>(whileBody.Statements[0]);
        }
        
        [Fact]
        public void TestIfStatement()
        {
            var ast = Parse(@"
                test() {
                    if (x > 0) {
                        print(""positive"")
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<IfStatementNode>(block.Statements[0]);
            var ifStmt = (IfStatementNode)block.Statements[0];
            Assert.NotNull(ifStmt.Condition);
            Assert.NotNull(ifStmt.ThenBranch);
            Assert.Null(ifStmt.ElseBranch);
        }
        
        [Fact]
        public void TestIfElseStatement()
        {
            var ast = Parse(@"
                test() {
                    if (x > 0) {
                        print(""positive"")
                    } else {
                        print(""negative"")
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var ifStmt = (IfStatementNode)block.Statements[0];
            Assert.NotNull(ifStmt.ElseBranch);
        }
        
        [Fact]
        public void TestWhileLoop()
        {
            var ast = Parse(@"
                test() {
                    while (x < 10) {
                        x = x + 1
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<WhileStatementNode>(block.Statements[0]);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // EXPRESSIONS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestBinaryExpressions()
        {
            var ast = Parse("var result = 2 + 3 * 4");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<BinaryExpressionNode>(varDecl.Initializer);
            var binary = (BinaryExpressionNode)varDecl.Initializer;
            Assert.Equal("+", binary.Operator);
            // Right side should be 3 * 4 (multiplication has higher precedence)
            Assert.IsType<BinaryExpressionNode>(binary.Right);
        }
        
        [Fact]
        public void TestUnaryExpression()
        {
            var ast = Parse("var x = -42");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<UnaryExpressionNode>(varDecl.Initializer);
            var unary = (UnaryExpressionNode)varDecl.Initializer;
            Assert.Equal("-", unary.Operator);
        }

        [Fact]
        public void TestTruthinessUnaryExpression()
        {
            var ast = Parse("var ready = ?value ? true : false");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var conditional = Assert.IsType<ConditionalExpressionNode>(varDecl.Initializer);
            var truthiness = Assert.IsType<UnaryExpressionNode>(conditional.Condition);
            Assert.Equal("?", truthiness.Operator);
            Assert.IsType<IdentifierExpressionNode>(truthiness.Operand);
        }
        
        [Fact]
        public void TestLogicalOperators()
        {
            var ast = Parse("var result = x > 0 && y < 10");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<BinaryExpressionNode>(varDecl.Initializer);
            var binary = (BinaryExpressionNode)varDecl.Initializer;
            Assert.Equal("&&", binary.Operator);
        }
        
        [Fact]
        public void TestParenthesizedExpression()
        {
            var ast = Parse("var result = (2 + 3) * 4");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var binary = (BinaryExpressionNode)varDecl.Initializer;
            Assert.Equal("*", binary.Operator);
            Assert.IsType<ParenthesizedExpressionNode>(binary.Left);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // FUNCTION CALLS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestFunctionCall()
        {
            var ast = Parse(@"
                test() {
                    print(""hello"")
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            Assert.IsType<CallExpressionNode>(exprStmt.Expression);
            var call = (CallExpressionNode)exprStmt.Expression;
            Assert.Single(call.Arguments);
        }
        
        [Fact]
        public void TestChainedMethodCalls()
        {
            var ast = Parse(@"
                test() {
                    arr.filter(x).map(y).reduce(z)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            Assert.IsType<CallExpressionNode>(exprStmt.Expression);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // OBJECT LITERALS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestObjectLiteral()
        {
            var ast = Parse(@"
                var obj = {
                    string name: ""Alice"",
                    int age: 25
                }
            ");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<ObjectLiteralExpressionNode>(varDecl.Initializer);
            var obj = (ObjectLiteralExpressionNode)varDecl.Initializer;
            Assert.Equal(2, obj.Properties.Count);
        }
        
        [Fact]
        public void TestDynamicObjectLiteral()
        {
            var ast = Parse(@"
                var obj = {
                    name: ""Alice"",
                    age: 25
                }
            ");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var obj = (ObjectLiteralExpressionNode)varDecl.Initializer;
            Assert.Equal(2, obj.Properties.Count);
            Assert.Null(obj.Properties[0].Type); // Dynamic property
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // LAMBDAS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestLambdaExpression()
        {
            var ast = Parse(@"
                test() {
                    var double = (x) -> x * 2
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var varDecl = (VariableDeclarationStatementNode)block.Statements[0];
            Assert.IsType<LambdaExpressionNode>(varDecl.Initializer);
            var lambda = (LambdaExpressionNode)varDecl.Initializer;
            Assert.Single(lambda.Parameters);
        }
        
        [Fact]
        public void TestLambdaWithMultipleParameters()
        {
            var ast = Parse(@"
                test() {
                    var add = (x, y) -> x + y
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var varDecl = (VariableDeclarationStatementNode)block.Statements[0];
            var lambda = (LambdaExpressionNode)varDecl.Initializer;
            Assert.Equal(2, lambda.Parameters.Count);
        }
        
        [Fact]
        public void TestLambdaWithBlock()
        {
            var ast = Parse(@"
                test() {
                    var func = (x) -> {
                        var result = x * 2
                        return result
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var varDecl = (VariableDeclarationStatementNode)block.Statements[0];
            var lambda = (LambdaExpressionNode)varDecl.Initializer;
            Assert.IsType<BlockStatementNode>(lambda.Body);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // MEMBER ACCESS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestMemberAccess()
        {
            var ast = Parse("var x = person.name");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<MemberAccessExpressionNode>(varDecl.Initializer);
            var member = (MemberAccessExpressionNode)varDecl.Initializer;
            Assert.Equal("name", member.MemberName);
        }
        
        [Fact]
        public void TestChainedMemberAccess()
        {
            var ast = Parse("var x = person.address.city");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<MemberAccessExpressionNode>(varDecl.Initializer);
            var member = (MemberAccessExpressionNode)varDecl.Initializer;
            Assert.Equal("city", member.MemberName);
            Assert.IsType<MemberAccessExpressionNode>(member.Object);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // NEW EXPRESSIONS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestNewExpression()
        {
            var ast = Parse(@"
                test() {
                    var p = new Person(""Alice"", 25)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var varDecl = (VariableDeclarationStatementNode)block.Statements[0];
            Assert.IsType<NewExpressionNode>(varDecl.Initializer);
            var newExpr = (NewExpressionNode)varDecl.Initializer;
            Assert.Equal("Person", newExpr.TypeName);
            Assert.Equal(2, newExpr.Arguments.Count);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // THIS KEYWORD
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestThisExpression()
        {
            var ast = Parse(@"
                class Person {
                    string name
                    
                    getName() {
                        return this.name
                    }
                }
            ");
            var classDecl = (ClassDeclarationNode)ast.Declarations[0];
            var method = classDecl.Methods[0];
            var block = (BlockStatementNode)method.Body;
            var returnStmt = (ReturnStatementNode)block.Statements[0];
            Assert.IsType<MemberAccessExpressionNode>(returnStmt.Expression);
            var member = (MemberAccessExpressionNode)returnStmt.Expression;
            Assert.IsType<ThisExpressionNode>(member.Object);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // RETURN STATEMENTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestReturnWithValue()
        {
            var ast = Parse(@"
                getValue() {
                    return 42
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<ReturnStatementNode>(block.Statements[0]);
            var returnStmt = (ReturnStatementNode)block.Statements[0];
            Assert.NotNull(returnStmt.Expression);
        }
        
        [Fact]
        public void TestReturnWithoutValue()
        {
            var ast = Parse(@"
                test() {
                    return
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var returnStmt = (ReturnStatementNode)block.Statements[0];
            Assert.Null(returnStmt.Expression);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // STRING INTERPOLATION
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestStringInterpolation()
        {
            var ast = Parse(@"
                test() {
                    var name = ""Alice""
                    var greeting = ""Hello, $(name)!""
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var varDecl = (VariableDeclarationStatementNode)block.Statements[1];
            Assert.IsType<InterpolatedStringExpressionNode>(varDecl.Initializer);
            var interpolated = (InterpolatedStringExpressionNode)varDecl.Initializer;
            Assert.NotEmpty(interpolated.Parts);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // PIPE OPERATOR
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestPipeOperator()
        {
            var ast = Parse(@"
                test() {
                    var result = getValue() | double() | print()
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var varDecl = (VariableDeclarationStatementNode)block.Statements[0];
            Assert.IsType<PipeExpressionNode>(varDecl.Initializer);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // LITERALS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestIntegerLiteral()
        {
            var ast = Parse("var x = 42");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var literal = (LiteralExpressionNode)varDecl.Initializer;
            Assert.Equal(42, literal.Value);
        }
        
        [Fact]
        public void TestStringLiteral()
        {
            var ast = Parse("var s = \"hello\"");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var literal = (LiteralExpressionNode)varDecl.Initializer;
            Assert.Equal("hello", literal.Value);
        }
        
        [Fact]
        public void TestBooleanLiterals()
        {
            var ast = Parse(@"
                var t = true
                var f = false
            ");
            var trueDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var trueLit = (LiteralExpressionNode)trueDecl.Initializer;
            Assert.Equal(true, trueLit.Value);
            
            var falseDecl = (VariableDeclarationStatementNode)ast.Declarations[1];
            var falseLit = (LiteralExpressionNode)falseDecl.Initializer;
            Assert.Equal(false, falseLit.Value);
        }
        
        [Fact]
        public void TestNullLiteral()
        {
            var ast = Parse("var x = null");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var literal = (LiteralExpressionNode)varDecl.Initializer;
            Assert.Null(literal.Value);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // USE DECLARATIONS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestUseDeclaration()
        {
            var ast = Parse("use \"./collections.sushi\" as collections");
            Assert.Single(ast.Declarations);
            Assert.IsType<UseDeclarationNode>(ast.Declarations[0]);
            var useDecl = (UseDeclarationNode)ast.Declarations[0];
            Assert.Equal("./collections.sushi", useDecl.ImportPath);
            Assert.Equal("collections", useDecl.Alias);
        }

        [Fact]
        public void TestExportDeclaration()
        {
            var ast = Parse("export greet(name) { return name }");
            var exported = Assert.IsType<ExportDeclarationNode>(Assert.Single(ast.Declarations));
            Assert.IsType<FunctionDeclarationNode>(exported.Declaration);
        }

        [Fact]
        public void TestQualifiedNewExpression()
        {
            var ast = Parse("var person = new helpers.Person(\"Ada\")");
            var variable = Assert.IsType<VariableDeclarationStatementNode>(Assert.Single(ast.Declarations));
            var expression = Assert.IsType<NewExpressionNode>(variable.Initializer);
            Assert.Equal("helpers.Person", expression.TypeName);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // STRUCTURAL TYPES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestStructuralTypeParameter()
        {
            var ast = Parse(@"
                processUser(object { string name, int age } user) {
                    print(name)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Single(func.Parameters);
            Assert.NotNull(func.Parameters[0].StructuralType);
            var structType = func.Parameters[0].StructuralType;
            Assert.Equal(2, structType.Fields.Count);
            Assert.Equal("user", func.Parameters[0].Name);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // MIXED NAMED AND POSITIONAL ARGUMENTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestMixedArguments()
        {
            var ast = Parse(@"
                test() {
                    greet(""Alice"", age: 25, city: ""NYC"")
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            var call = (CallExpressionNode)exprStmt.Expression;
            Assert.Equal(3, call.Arguments.Count);
            Assert.Null(call.Arguments[0].Name); // Positional
            Assert.NotNull(call.Arguments[1].Name); // Named
            Assert.NotNull(call.Arguments[2].Name); // Named
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // ARRAY DESTRUCTURING EDGE CASES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestArrayDestructuringWithSkip()
        {
            var ast = Parse("var [a, , c] = [1, 2, 3]");
            var destruct = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(3, destruct.Patterns.Count);
            Assert.Null(destruct.Patterns[1].Name); // Skip pattern
        }
        
        [Fact]
        public void TestArrayDestructuringWithDefaults()
        {
            var ast = Parse("var [x = 0, y = 0] = [100]");
            var destruct = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(2, destruct.Patterns.Count);
            Assert.NotNull(destruct.Patterns[0].DefaultValue);
            Assert.NotNull(destruct.Patterns[1].DefaultValue);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // ENUM EDGE CASES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestEnumWithMethods()
        {
            var ast = Parse(@"
                enum Direction {
                    North,
                    South
                    
                    opposite() {
                        return Direction.North
                    }
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Single(enumDecl.Methods);
        }
        
        [Fact]
        public void TestEnumWithConstructor()
        {
            var ast = Parse(@"
                enum Result {
                    Ok,
                    Error
                    
                    new() {
                        print(""constructor"")
                    }
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Equal(2, enumDecl.Values.Count);
            Assert.NotNull(enumDecl.ExplicitConstructor);
        }
        
        [Fact]
        public void TestEnumWithConstructorArgs()
        {
            var ast = Parse(@"
                enum Status {
                    Pending(""pending""),
                    Active(""active""),
                    Done(""done"")
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Equal(3, enumDecl.Values.Count);
            Assert.All(enumDecl.Values, v => Assert.NotNull(v.ConstructorArgs));
        }
        
        [Fact]
        public void TestEnumWithInlineProperties()
        {
            var ast = Parse(@"
                enum Status {
                    Active { priority: 1 },
                    Inactive { priority: 0 }
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Equal(2, enumDecl.Values.Count);
            Assert.NotNull(enumDecl.Values[0].Properties);
            Assert.NotNull(enumDecl.Values[1].Properties);
        }
        
        [Fact]
        public void TestEnumMixedValueTypes()
        {
            var ast = Parse(@"
                enum HttpMethod {
                    GET = ""GET"",
                    POST = ""POST"",
                    PUT = ""PUT"",
                    DELETE = ""DELETE""
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Equal(4, enumDecl.Values.Count);
            Assert.All(enumDecl.Values, v => Assert.NotNull(v.DirectValue));
        }
        
        [Fact]
        public void TestEnumWithMultipleMethods()
        {
            var ast = Parse(@"
                enum Color {
                    Red,
                    Green,
                    Blue
                    
                    toHex() {
                        return ""#000000""
                    }
                    
                    darker() {
                        return Color.Red
                    }
                    
                    lighter() {
                        return Color.Blue
                    }
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Equal(3, enumDecl.Values.Count);
            Assert.Equal(3, enumDecl.Methods.Count);
        }
        
        [Fact]
        public void TestEnumValuesWithoutTrailingComma()
        {
            var ast = Parse(@"
                enum Priority {
                    Low,
                    Medium,
                    High
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Equal(3, enumDecl.Values.Count);
        }
        
        [Fact]
        public void TestEnumSingleValue()
        {
            var ast = Parse(@"
                enum Singleton {
                    Instance
                }
            ");
            var enumDecl = (EnumDeclarationNode)ast.Declarations[0];
            Assert.Single(enumDecl.Values);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMPREHENSIVE ARRAY TESTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestEmptyArray()
        {
            var ast = Parse("var empty = []");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var array = (ArrayLiteralExpressionNode)varDecl.Initializer;
            Assert.Empty(array.Elements);
        }
        
        [Fact]
        public void TestArrayNegativeIndexing()
        {
            var ast = Parse("var last = arr[-1]");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var index = (IndexExpressionNode)varDecl.Initializer;
            Assert.IsType<UnaryExpressionNode>(index.Index);
        }
        
        [Fact]
        public void TestArraySliceFromStart()
        {
            var ast = Parse("var slice = arr[:5]");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var slice = (SliceExpressionNode)varDecl.Initializer;
            Assert.Null(slice.Start);
            Assert.NotNull(slice.End);
        }
        
        [Fact]
        public void TestArraySliceToEnd()
        {
            var ast = Parse("var slice = arr[5:]");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var slice = (SliceExpressionNode)varDecl.Initializer;
            Assert.NotNull(slice.Start);
            Assert.Null(slice.End);
        }
        
        [Fact]
        public void TestArraySliceFull()
        {
            var ast = Parse("var copy = arr[:]");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var slice = (SliceExpressionNode)varDecl.Initializer;
            Assert.Null(slice.Start);
            Assert.Null(slice.End);
        }
        
        [Fact]
        public void TestArrayDestructuringAllSkip()
        {
            var ast = Parse("var [, , third] = [1, 2, 3]");
            var destruct = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(3, destruct.Patterns.Count);
            Assert.Null(destruct.Patterns[0].Name);
            Assert.Null(destruct.Patterns[1].Name);
            Assert.NotNull(destruct.Patterns[2].Name);
        }
        
        [Fact]
        public void TestArrayDestructuringRestOnly()
        {
            var ast = Parse("var [...all] = [1, 2, 3]");
            var destruct = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Single(destruct.Patterns);
            Assert.True(destruct.Patterns[0].IsRest);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMPREHENSIVE FUNCTION TESTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestFunctionWithReturnType()
        {
            var ast = Parse(@"
                int add(int a, int b) {
                    return a + b
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Equal("int", func.ReturnType);
            Assert.Equal("add", func.Name);
        }
        
        [Fact]
        public void TestFunctionVoidReturnType()
        {
            var ast = Parse(@"
                void log(string msg) {
                    print(msg)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Equal("void", func.ReturnType);
        }
        
        [Fact]
        public void TestFunctionMultipleDefaultParams()
        {
            var ast = Parse(@"
                greet(string name = ""World"", string greeting = ""Hello"") {
                    print(greeting)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Equal(2, func.Parameters.Count);
            Assert.All(func.Parameters, p => Assert.NotNull(p.DefaultValue));
        }
        
        [Fact]
        public void TestFunctionMixedDefaultParams()
        {
            var ast = Parse(@"
                test(int x, int y = 10, int z = 20) {
                    return x + y + z
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Equal(3, func.Parameters.Count);
            Assert.Null(func.Parameters[0].DefaultValue);
            Assert.NotNull(func.Parameters[1].DefaultValue);
            Assert.NotNull(func.Parameters[2].DefaultValue);
        }
        
        [Fact]
        public void TestFunctionVarargsWithType()
        {
            var ast = Parse(@"
                sum(int... numbers) {
                    return 0
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Single(func.Parameters);
            Assert.Equal("int", func.Parameters[0].Type);
            Assert.True(func.Parameters[0].IsVarargs);
        }
        
        [Fact]
        public void TestFunctionVarargsAtEnd()
        {
            var ast = Parse(@"
                format(string template, string... args) {
                    return template
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Equal(2, func.Parameters.Count);
            Assert.False(func.Parameters[0].IsVarargs);
            Assert.True(func.Parameters[1].IsVarargs);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMPREHENSIVE CLASS TESTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestClassWithMultipleFields()
        {
            var ast = Parse(@"
                class Person {
                    string name
                    int age
                    string email
                    bool isActive
                }
            ");
            var classDecl = (ClassDeclarationNode)ast.Declarations[0];
            Assert.Equal(4, classDecl.Fields.Count);
        }
        
        [Fact]
        public void TestClassWithConstructorAndMethods()
        {
            var ast = Parse(@"
                class Counter {
                    int count
                    
                    new() {
                        this.count = 0
                    }
                    
                    increment() {
                        this.count = this.count + 1
                    }
                    
                    decrement() {
                        this.count = this.count - 1
                    }
                    
                    reset() {
                        this.count = 0
                    }
                }
            ");
            var classDecl = (ClassDeclarationNode)ast.Declarations[0];
            Assert.Single(classDecl.Fields);
            Assert.NotNull(classDecl.Constructor);
            Assert.Equal(3, classDecl.Methods.Count);
        }
        
        [Fact]
        public void TestClassNoConstructor()
        {
            var ast = Parse(@"
                class Point {
                    int x
                    int y
                }
            ");
            var classDecl = (ClassDeclarationNode)ast.Declarations[0];
            Assert.Null(classDecl.Constructor);
            Assert.Equal(2, classDecl.Fields.Count);
        }
        
        [Fact]
        public void TestClassNoFields()
        {
            var ast = Parse(@"
                class Utility {
                    new() {
                        print(""init"")
                    }
                    
                    doSomething() {
                        print(""doing"")
                    }
                }
            ");
            var classDecl = (ClassDeclarationNode)ast.Declarations[0];
            Assert.Empty(classDecl.Fields);
            Assert.NotNull(classDecl.Constructor);
            Assert.Single(classDecl.Methods);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMPREHENSIVE LAMBDA TESTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestLambdaNoParams()
        {
            var ast = Parse(@"
                test() {
                    var getValue = () -> 42
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var varDecl = (VariableDeclarationStatementNode)block.Statements[0];
            var lambda = (LambdaExpressionNode)varDecl.Initializer;
            Assert.Empty(lambda.Parameters);
        }
        
        [Fact]
        public void TestLambdaTypedParameters()
        {
            var ast = Parse(@"
                test() {
                    var add = (int x, int y) -> x + y
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var varDecl = (VariableDeclarationStatementNode)block.Statements[0];
            var lambda = (LambdaExpressionNode)varDecl.Initializer;
            Assert.Equal(2, lambda.Parameters.Count);
            Assert.All(lambda.Parameters, p => Assert.NotNull(p.Type));
        }
        
        [Fact]
        public void TestLambdaNestedInCall()
        {
            var ast = Parse(@"
                test() {
                    arr.map((x) -> x * 2)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            var call = (CallExpressionNode)exprStmt.Expression;
            Assert.Single(call.Arguments);
            Assert.IsType<LambdaExpressionNode>(call.Arguments[0].Value);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMPREHENSIVE SWITCH TESTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestSwitchNoDefault()
        {
            var ast = Parse(@"
                test(int x) {
                    switch (x) {
                        1 -> { print(""one"") }
                        2 -> { print(""two"") }
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var switchStmt = (SwitchStatementNode)block.Statements[0];
            Assert.Equal(2, switchStmt.Cases.Count);
            Assert.Null(switchStmt.DefaultCase);
        }
        
        [Fact]
        public void TestSwitchOnlyDefault()
        {
            var ast = Parse(@"
                test(int x) {
                    switch (x) {
                        default -> { print(""anything"") }
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var switchStmt = (SwitchStatementNode)block.Statements[0];
            Assert.Empty(switchStmt.Cases);
            Assert.NotNull(switchStmt.DefaultCase);
        }
        
        [Fact]
        public void TestSwitchManyValues()
        {
            var ast = Parse(@"
                test(int x) {
                    switch (x) {
                        1, 2, 3, 4, 5 -> { print(""small"") }
                        10, 20, 30 -> { print(""medium"") }
                        100 -> { print(""large"") }
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var switchStmt = (SwitchStatementNode)block.Statements[0];
            Assert.Equal(3, switchStmt.Cases.Count);
            Assert.Equal(5, switchStmt.Cases[0].MatchValues.Count);
            Assert.Equal(3, switchStmt.Cases[1].MatchValues.Count);
            Assert.Single(switchStmt.Cases[2].MatchValues);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMPREHENSIVE EXPRESSION TESTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestNestedTernaryChain()
        {
            var ast = Parse(@"
                var result = a > b ? ""a"" : 
                            b > c ? ""b"" : 
                            c > d ? ""c"" : ""d""
            ");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var ternary = (ConditionalExpressionNode)varDecl.Initializer;
            Assert.IsType<ConditionalExpressionNode>(ternary.FalseExpression);
            var nested = (ConditionalExpressionNode)ternary.FalseExpression;
            Assert.IsType<ConditionalExpressionNode>(nested.FalseExpression);
        }
        
        [Fact]
        public void TestComplexBinaryExpression()
        {
            var ast = Parse("var x = a + b * c - d / e");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<BinaryExpressionNode>(varDecl.Initializer);
        }
        
        [Fact]
        public void TestChainedComparisons()
        {
            var ast = Parse("var result = a < b && b < c && c < d");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var expr = (BinaryExpressionNode)varDecl.Initializer;
            Assert.Equal("&&", expr.Operator);
            Assert.IsType<BinaryExpressionNode>(expr.Left);
            Assert.IsType<BinaryExpressionNode>(expr.Right);
        }
        
        [Fact]
        public void TestNestedParenthesizedExpressions()
        {
            var ast = Parse("var x = ((a + b) * (c + d))");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<ParenthesizedExpressionNode>(varDecl.Initializer);
        }
        
        [Fact]
        public void TestPrefixDecrement()
        {
            var ast = Parse("var x = --y");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var unary = (UnaryExpressionNode)varDecl.Initializer;
            Assert.Equal("--", unary.Operator);
            Assert.True(unary.IsPrefix);
            Assert.IsType<IdentifierExpressionNode>(unary.Operand);
        }
        
        [Fact]
        public void TestPostfixDecrement()
        {
            var ast = Parse("var x = y--");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var unary = (UnaryExpressionNode)varDecl.Initializer;
            Assert.Equal("--", unary.Operator);
            Assert.False(unary.IsPrefix);
            Assert.IsType<IdentifierExpressionNode>(unary.Operand);
        }
        
        [Fact]
        public void TestPrefixIncrement()
        {
            var ast = Parse("var x = ++y");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var unary = (UnaryExpressionNode)varDecl.Initializer;
            Assert.Equal("++", unary.Operator);
            Assert.True(unary.IsPrefix);
        }
        
        [Fact]
        public void TestPostfixIncrement()
        {
            var ast = Parse("var x = y++");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var unary = (UnaryExpressionNode)varDecl.Initializer;
            Assert.Equal("++", unary.Operator);
            Assert.False(unary.IsPrefix);
        }
        
        [Fact]
        public void TestCompoundAssignmentPlus()
        {
            var ast = Parse(@"
                test() {
                    x += 5
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            var binary = (BinaryExpressionNode)exprStmt.Expression;
            Assert.Equal("+=", binary.Operator);
        }
        
        [Fact]
        public void TestCompoundAssignmentMinus()
        {
            var ast = Parse(@"
                test() {
                    x -= 5
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            var binary = (BinaryExpressionNode)exprStmt.Expression;
            Assert.Equal("-=", binary.Operator);
        }
        
        [Fact]
        public void TestCompoundAssignmentMultiply()
        {
            var ast = Parse(@"
                test() {
                    x *= 5
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            var binary = (BinaryExpressionNode)exprStmt.Expression;
            Assert.Equal("*=", binary.Operator);
        }
        
        [Fact]
        public void TestCompoundAssignmentDivide()
        {
            var ast = Parse(@"
                test() {
                    x /= 5
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var exprStmt = (ExpressionStatementNode)block.Statements[0];
            var binary = (BinaryExpressionNode)exprStmt.Expression;
            Assert.Equal("/=", binary.Operator);
        }
        
        [Fact]
        public void TestDoubleNegativeIsNotDecrement()
        {
            var ast = Parse("var x = - -y");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var unary = (UnaryExpressionNode)varDecl.Initializer;
            Assert.Equal("-", unary.Operator);
            Assert.IsType<UnaryExpressionNode>(unary.Operand);
            var innerUnary = (UnaryExpressionNode)unary.Operand;
            Assert.Equal("-", innerUnary.Operator);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMPREHENSIVE LOOP TESTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestWhileLoopEmpty()
        {
            var ast = Parse(@"
                test() {
                    while (true) {
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var whileLoop = (WhileStatementNode)block.Statements[0];
            var body = (BlockStatementNode)whileLoop.Body;
            Assert.Empty(body.Statements);
        }
        
        [Fact]
        public void TestDoWhileMinimal()
        {
            var ast = Parse(@"
                test() {
                    do {
                        x = x + 1
                    } while (x < 10)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<DoWhileStatementNode>(block.Statements[0]);
        }
        
        [Fact]
        public void TestForLoopAllParts()
        {
            var ast = Parse(@"
                test() {
                    for (var i = 0; i < 10; i = i + 1) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forLoop = (ForStatementNode)block.Statements[0];
            Assert.NotNull(forLoop.Initializer);
            Assert.NotNull(forLoop.Condition);
            Assert.NotNull(forLoop.Increment);
        }
        
        [Fact]
        public void TestForLoopMinimal()
        {
            var ast = Parse(@"
                test() {
                    for (;;) {
                        break
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forLoop = (ForStatementNode)block.Statements[0];
            Assert.Null(forLoop.Initializer);
            Assert.Null(forLoop.Condition);
            Assert.Null(forLoop.Increment);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // FOR-RANGE LOOPS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestForRangeExclusive()
        {
            var ast = Parse(@"
                test() {
                    for (var i : 0..5) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<ForRangeStatementNode>(block.Statements[0]);
            var forRange = (ForRangeStatementNode)block.Statements[0];
            Assert.Equal("i", forRange.Variable);
            Assert.False(forRange.IsInclusive);
        }
        
        [Fact]
        public void TestForRangeInclusive()
        {
            var ast = Parse(@"
                test() {
                    for (var i : 0...5) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forRange = (ForRangeStatementNode)block.Statements[0];
            Assert.Equal("i", forRange.Variable);
            Assert.True(forRange.IsInclusive);
        }
        
        [Fact]
        public void TestForRangeWithExpressions()
        {
            var ast = Parse(@"
                test() {
                    for (var i : start..end) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forRange = (ForRangeStatementNode)block.Statements[0];
            Assert.IsType<IdentifierExpressionNode>(forRange.Start);
            Assert.IsType<IdentifierExpressionNode>(forRange.End);
        }
        
        [Fact]
        public void TestForRangeReverse()
        {
            var ast = Parse(@"
                test() {
                    for (var i : 10..0) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forRange = (ForRangeStatementNode)block.Statements[0];
            Assert.Equal("i", forRange.Variable);
        }
        
        [Fact]
        public void TestForRangeWithStep()
        {
            var ast = Parse(@"
                test() {
                    for (var i : 0..100 step 10) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forRange = (ForRangeStatementNode)block.Statements[0];
            Assert.Equal("i", forRange.Variable);
            Assert.NotNull(forRange.Step);
            Assert.IsType<LiteralExpressionNode>(forRange.Step);
        }
        
        [Fact]
        public void TestForRangeWithStepExpression()
        {
            var ast = Parse(@"
                test() {
                    for (var i : 0..100 step stepSize) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forRange = (ForRangeStatementNode)block.Statements[0];
            Assert.NotNull(forRange.Step);
            Assert.IsType<IdentifierExpressionNode>(forRange.Step);
        }
        
        [Fact]
        public void TestForRangeInclusiveWithStep()
        {
            var ast = Parse(@"
                test() {
                    for (var i : 0...100 step 5) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forRange = (ForRangeStatementNode)block.Statements[0];
            Assert.True(forRange.IsInclusive);
            Assert.NotNull(forRange.Step);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // FOREACH LOOPS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestForEachSimple()
        {
            var ast = Parse(@"
                test() {
                    for (var item : collection) {
                        print(item)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.IsType<ForEachStatementNode>(block.Statements[0]);
            var forEach = (ForEachStatementNode)block.Statements[0];
            Assert.Equal("item", forEach.ItemVariable);
            Assert.Null(forEach.IndexVariable);
        }
        
        [Fact]
        public void TestForEachWithIndex()
        {
            var ast = Parse(@"
                test() {
                    for (var i, var item : collection) {
                        print(i, item)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forEach = (ForEachStatementNode)block.Statements[0];
            Assert.Equal("i", forEach.IndexVariable);
            Assert.Equal("item", forEach.ItemVariable);
        }
        
        [Fact]
        public void TestForEachArray()
        {
            var ast = Parse(@"
                test() {
                    for (var x : [1, 2, 3]) {
                        print(x)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forEach = (ForEachStatementNode)block.Statements[0];
            Assert.IsType<ArrayLiteralExpressionNode>(forEach.Collection);
        }
        
        [Fact]
        public void TestForEachMethodCall()
        {
            var ast = Parse(@"
                test() {
                    for (var item : getItems()) {
                        print(item)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forEach = (ForEachStatementNode)block.Statements[0];
            Assert.IsType<CallExpressionNode>(forEach.Collection);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // RANGE EXPRESSIONS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestRangeExpressionExclusive()
        {
            var ast = Parse("var range = 1..10");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<BinaryExpressionNode>(varDecl.Initializer);
            var binary = (BinaryExpressionNode)varDecl.Initializer;
            Assert.Equal("..", binary.Operator);
        }
        
        [Fact]
        public void TestRangeExpressionInclusive()
        {
            var ast = Parse("var range = 1...10");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var binary = (BinaryExpressionNode)varDecl.Initializer;
            Assert.Equal("...", binary.Operator);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // EDGE CASES AND ERROR CONDITIONS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestEmptyBlock()
        {
            var ast = Parse(@"
                test() {
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Empty(block.Statements);
        }
        
        [Fact]
        public void TestMultipleStatementsNoSemicolons()
        {
            var ast = Parse(@"
                test() {
                    var x = 1
                    var y = 2
                    var z = 3
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(3, block.Statements.Count);
        }
        
        [Fact]
        public void TestNestedBlocks()
        {
            var ast = Parse(@"
                test() {
                    {
                        var x = 1
                        {
                            var y = 2
                        }
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var outerBlock = (BlockStatementNode)func.Body;
            Assert.Single(outerBlock.Statements); // Contains one nested block
            var nestedBlock = (BlockStatementNode)outerBlock.Statements[0];
            Assert.Equal(2, nestedBlock.Statements.Count); // var x and another nested block
        }
        
        [Fact]
        public void TestEmptyObjectLiteral()
        {
            var ast = Parse("var obj = {}");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var obj = (ObjectLiteralExpressionNode)varDecl.Initializer;
            Assert.Empty(obj.Properties);
        }
        
        [Fact]
        public void TestSinglePropertyObject()
        {
            var ast = Parse(@"
                var obj = {
                    name: ""Alice""
                }
            ");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var obj = (ObjectLiteralExpressionNode)varDecl.Initializer;
            Assert.Single(obj.Properties);
        }
        
        [Fact]
        public void TestTypedVariableWithoutInitializer()
        {
            var ast = Parse("int x");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.Equal("int", varDecl.Type);
            Assert.Null(varDecl.Initializer);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMPLEX NESTED STRUCTURES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestNestedArrays()
        {
            var ast = Parse("var matrix = [[1, 2], [3, 4]]");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var array = (ArrayLiteralExpressionNode)varDecl.Initializer;
            Assert.Equal(2, array.Elements.Count);
            Assert.IsType<ArrayLiteralExpressionNode>(array.Elements[0]);
        }
        
        [Fact]
        public void TestComplexExpression()
        {
            var ast = Parse("var result = (x + y) * (a - b) / (c > d ? 1 : 2)");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            Assert.IsType<BinaryExpressionNode>(varDecl.Initializer);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // ASSIGNMENT OPERATORS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestCompoundAssignmentMultiple()
        {
            var ast = Parse(@"
                test() {
                    x += 5
                    y -= 3
                    z *= 2
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(3, block.Statements.Count);
            Assert.All(block.Statements, s => Assert.IsType<ExpressionStatementNode>(s));
        }
        
        [Fact]
        public void TestIncrementInForLoop()
        {
            var ast = Parse(@"
                test() {
                    for (var i = 0; i < 10; i++) {
                        print(i)
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var forLoop = (ForStatementNode)block.Statements[0];
            Assert.NotNull(forLoop.Increment);
            Assert.IsType<UnaryExpressionNode>(forLoop.Increment);
            var unary = (UnaryExpressionNode)forLoop.Increment;
            Assert.Equal("++", unary.Operator);
            Assert.False(unary.IsPrefix); // Postfix i++
        }
        
        [Fact]
        public void TestDecrementInExpression()
        {
            var ast = Parse("var x = --y + z++");
            var varDecl = (VariableDeclarationStatementNode)ast.Declarations[0];
            var binary = (BinaryExpressionNode)varDecl.Initializer;
            Assert.Equal("+", binary.Operator);
            
            // Left side: --y (prefix)
            var leftUnary = (UnaryExpressionNode)binary.Left;
            Assert.Equal("--", leftUnary.Operator);
            Assert.True(leftUnary.IsPrefix);
            
            // Right side: z++ (postfix)
            var rightUnary = (UnaryExpressionNode)binary.Right;
            Assert.Equal("++", rightUnary.Operator);
            Assert.False(rightUnary.IsPrefix);
        }
        
        [Fact]
        public void TestIncrementDecrementStatement()
        {
            var ast = Parse(@"
                test() {
                    x++
                    ++x
                    y--
                    --y
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(4, block.Statements.Count);
            Assert.All(block.Statements, s => Assert.IsType<ExpressionStatementNode>(s));
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // COMMENTS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestCommentsDontCauseSemicolonInsertion()
        {
            var ast = Parse(@"
                test() {
                    // This is a comment
                    var x = 1
                    // Another comment
                    var y = 2
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(2, block.Statements.Count);
        }
        
        [Fact]
        public void TestCommentsInFunctionBody()
        {
            var ast = Parse(@"
                test() {
                    // Comment at start
                    print(""hello"")
                    // Comment in middle
                    print(""world"")
                    // Comment at end
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(2, block.Statements.Count);
        }
        
        [Fact]
        public void TestMultipleCommentsInRow()
        {
            var ast = Parse(@"
                test() {
                    // First comment
                    // Second comment
                    // Third comment
                    var x = 1
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Single(block.Statements);
        }
        
        [Fact]
        public void TestInlineCommentAfterStatement()
        {
            var ast = Parse(@"
                test() {
                    fun2() // This should get semicolon BEFORE comment
                    fun3()
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(2, block.Statements.Count);
        }
        
        [Fact]
        public void TestCommentBetweenDeclarations()
        {
            var ast = Parse(@"
                // Function 1
                test1() {
                    print(""test1"")
                }
                
                // Function 2
                test2() {
                    print(""test2"")
                }
            ");
            Assert.Equal(2, ast.Declarations.Count);
            Assert.All(ast.Declarations, d => Assert.IsType<FunctionDeclarationNode>(d));
        }
        
        [Fact]
        public void TestCommentInExpression()
        {
            var ast = Parse(@"
                test() {
                    var x = 1 + // add
                            2
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Single(block.Statements);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // OPTIONAL SEMICOLONS
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestExplicitSemicolons()
        {
            var ast = Parse(@"
                test() {
                    var x = 1;
                    var y = 2;
                    var z = 3;
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(3, block.Statements.Count);
        }
        
        [Fact]
        public void TestDoubleSemicolons()
        {
            var ast = Parse(@"
                test() {
                    var x = 1;;
                    var y = 2;;;
                    var z = 3
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(3, block.Statements.Count);
        }
        
        [Fact]
        public void TestLeadingSemicolons()
        {
            var ast = Parse(@"
                test() {
                    ;
                    ;;
                    var x = 1
                    ;
                    var y = 2
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(2, block.Statements.Count);
        }
        
        [Fact]
        public void TestTrailingSemicolons()
        {
            var ast = Parse(@"
                test() {
                    var x = 1
                    var y = 2;
                    ;
                    ;;
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(2, block.Statements.Count);
        }
        
        [Fact]
        public void TestMixedSemicolonStyles()
        {
            var ast = Parse(@"
                test() {
                    var x = 1;
                    var y = 2
                    var z = 3;;
                    print(x)
                    print(y);
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(5, block.Statements.Count);
        }
        
        [Fact]
        public void TestSemicolonsInSwitchStatement()
        {
            var ast = Parse(@"
                test(int x) {
                    switch (x) {
                        ;
                        1 -> { print(""one"") };
                        2 -> { print(""two"") }
                        ;
                        default -> { print(""other"") };
                    }
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            var switchStmt = (SwitchStatementNode)block.Statements[0];
            Assert.Equal(2, switchStmt.Cases.Count);
            Assert.NotNull(switchStmt.DefaultCase);
        }
        
        [Fact]
        public void TestEmptyStatements()
        {
            var ast = Parse(@"
                test() {
                    ;;;;;
                    var x = 1;;;;;
                    ;;;;;
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Single(block.Statements);
        }
        
        [Fact]
        public void TestSemicolonAfterFunctionDeclaration()
        {
            var ast = Parse(@"
                myFunc() {
                    print(""hello"")
                };
                
                otherFunc() {
                    print(""world"")
                }
            ");
            Assert.Equal(2, ast.Declarations.Count);
        }
        
        [Fact]
        public void TestSemicolonAfterClassDeclaration()
        {
            var ast = Parse(@"
                class MyClass {
                    string name
                };
                
                class OtherClass {
                    int value
                }
            ");
            Assert.Equal(2, ast.Declarations.Count);
        }
        
        [Fact]
        public void TestSemicolonAfterEnumDeclaration()
        {
            var ast = Parse(@"
                enum Color {
                    Red,
                    Green,
                    Blue
                };
                
                enum Status {
                    Active,
                    Inactive
                }
            ");
            Assert.Equal(2, ast.Declarations.Count);
        }
        
        [Fact]
        public void TestSemicolonAfterIfStatement()
        {
            var ast = Parse(@"
                test() {
                    if (x > 0) {
                        print(""positive"")
                    };
                    
                    if (x < 0) {
                        print(""negative"")
                    } else {
                        print(""zero"")
                    };
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(2, block.Statements.Count);
        }
        
        [Fact]
        public void TestSemicolonAfterWhileLoop()
        {
            var ast = Parse(@"
                test() {
                    while (true) {
                        break
                    };
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Single(block.Statements);
        }
        
        [Fact]
        public void TestSemicolonAfterForLoop()
        {
            var ast = Parse(@"
                test() {
                    for (var i = 0; i < 10; i++) {
                        print(i)
                    };
                    
                    for (var x : 0..5) {
                        print(x)
                    };
                    
                    for (var item : items) {
                        print(item)
                    };
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Equal(3, block.Statements.Count);
        }
        
        [Fact]
        public void TestSemicolonAfterSwitchStatement()
        {
            var ast = Parse(@"
                test(int x) {
                    switch (x) {
                        1 -> { print(""one"") }
                        default -> { print(""other"") }
                    };
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            var block = (BlockStatementNode)func.Body;
            Assert.Single(block.Statements);
        }
        
        [Fact]
        public void TestMultipleSemicolonsAfterStatements()
        {
            var ast = Parse(@"
                myFunc() {
                    print(""test"")
                };;;
                
                for (var i = 0; i < 5; i++) {
                    continue
                };;
                
                while (false) {
                };;;
            ");
            // Should have: function, for loop, while loop all at top level
            Assert.Equal(3, ast.Declarations.Count);
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // TOP-LEVEL SCRIPTING SUPPORT
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestTopLevelExpressionStatements()
        {
            var ast = Parse(@"
                print(""Hello, World!"")
                var x = 42
                print(x)
            ");
            Assert.Equal(3, ast.Declarations.Count);
            Assert.IsType<ExpressionStatementNode>(ast.Declarations[0]);
            Assert.IsType<VariableDeclarationStatementNode>(ast.Declarations[1]);
            Assert.IsType<ExpressionStatementNode>(ast.Declarations[2]);
        }
        
        [Fact]
        public void TestTopLevelIfStatement()
        {
            var ast = Parse(@"
                var x = 10
                if (x > 5) {
                    print(""big"")
                }
            ");
            Assert.Equal(2, ast.Declarations.Count);
            Assert.IsType<VariableDeclarationStatementNode>(ast.Declarations[0]);
            Assert.IsType<IfStatementNode>(ast.Declarations[1]);
        }
        
        [Fact]
        public void TestTopLevelForLoop()
        {
            var ast = Parse(@"
                for (var i = 0; i < 10; i++) {
                    print(i)
                }
            ");
            Assert.Single(ast.Declarations);
            Assert.IsType<ForStatementNode>(ast.Declarations[0]);
        }
        
        [Fact]
        public void TestTopLevelWhileLoop()
        {
            var ast = Parse(@"
                var count = 0
                while (count < 5) {
                    print(count)
                    count = count + 1
                }
            ");
            Assert.Equal(2, ast.Declarations.Count);
            Assert.IsType<VariableDeclarationStatementNode>(ast.Declarations[0]);
            Assert.IsType<WhileStatementNode>(ast.Declarations[1]);
        }
        
        [Fact]
        public void TestTopLevelForRangeLoop()
        {
            var ast = Parse(@"
                for (var i : 0..5) {
                    print(i)
                }
            ");
            Assert.Single(ast.Declarations);
            Assert.IsType<ForRangeStatementNode>(ast.Declarations[0]);
        }
        
        [Fact]
        public void TestTopLevelSwitchStatement()
        {
            var ast = Parse(@"
                var x = 2
                switch (x) {
                    1 -> { print(""one"") }
                    2 -> { print(""two"") }
                    default -> { print(""other"") }
                }
            ");
            Assert.Equal(2, ast.Declarations.Count);
            Assert.IsType<VariableDeclarationStatementNode>(ast.Declarations[0]);
            Assert.IsType<SwitchStatementNode>(ast.Declarations[1]);
        }
        
        [Fact]
        public void TestMixedTopLevelDeclarationsAndStatements()
        {
            var ast = Parse(@"
                print(""Starting script"")
                
                class MyClass {
                    int value
                }
                
                var obj = new MyClass()
                
                myFunc() {
                    return 42
                }
                
                for (var i : 0..3) {
                    print(i)
                }
                
                print(""Done"")
            ");
            Assert.Equal(6, ast.Declarations.Count);
            Assert.IsType<ExpressionStatementNode>(ast.Declarations[0]); // print
            Assert.IsType<ClassDeclarationNode>(ast.Declarations[1]); // class
            Assert.IsType<VariableDeclarationStatementNode>(ast.Declarations[2]); // var
            Assert.IsType<FunctionDeclarationNode>(ast.Declarations[3]); // func
            Assert.IsType<ForRangeStatementNode>(ast.Declarations[4]); // for
            Assert.IsType<ExpressionStatementNode>(ast.Declarations[5]); // print
        }
        
        [Fact]
        public void TestTopLevelComplexExpressions()
        {
            var ast = Parse(@"
                1 + 2 * 3
                [1, 2, 3].map((x) -> x * 2)
                { name: ""Alice"", age: 30 }
            ");
            Assert.Equal(3, ast.Declarations.Count);
            Assert.All(ast.Declarations, d => Assert.IsType<ExpressionStatementNode>(d));
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // STRUCTURAL TYPES
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestStructuralTypeParameterWithName()
        {
            var ast = Parse(@"
                processUser(object { string name, int age } user) {
                    print(user.name)
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Single(func.Parameters);
            Assert.NotNull(func.Parameters[0].StructuralType);
            Assert.Equal("user", func.Parameters[0].Name);
        }
        
        [Fact]
        public void TestStructuralTypeParameterWithoutName()
        {
            var ast = Parse(@"
                processUser(object { string name, int age }) {
                    print(""Processing user"")
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Single(func.Parameters);
            Assert.NotNull(func.Parameters[0].StructuralType);
            Assert.Equal("_", func.Parameters[0].Name); // Anonymous placeholder
        }
        
        [Fact]
        public void TestMultipleStructuralTypeParameters()
        {
            var ast = Parse(@"
                compare(object { int x, int y } point1, object { int x, int y } point2) {
                    return point1.x == point2.x
                }
            ");
            var func = (FunctionDeclarationNode)ast.Declarations[0];
            Assert.Equal(2, func.Parameters.Count);
            Assert.All(func.Parameters, p => Assert.NotNull(p.StructuralType));
        }
        // ═══════════════════════════════════════════════════════════════════
        // NESTED ARRAY DESTRUCTURING
        // ═══════════════════════════════════════════════════════════════════
        
        [Fact]
        public void TestNestedArrayDestructuring()
        {
            var ast = Parse(@"
                var [[a, b], [c, d]] = [[1, 2], [3, 4]]
            ");
            var destructure = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(2, destructure.Patterns.Count);
            
            // First pattern should be nested
            Assert.NotNull(destructure.Patterns[0].NestedPatterns);
            Assert.Equal(2, destructure.Patterns[0].NestedPatterns!.Count);
            Assert.Equal("a", destructure.Patterns[0].NestedPatterns[0].Name);
            Assert.Equal("b", destructure.Patterns[0].NestedPatterns[1].Name);
            
            // Second pattern should be nested
            Assert.NotNull(destructure.Patterns[1].NestedPatterns);
            Assert.Equal(2, destructure.Patterns[1].NestedPatterns!.Count);
            Assert.Equal("c", destructure.Patterns[1].NestedPatterns[0].Name);
            Assert.Equal("d", destructure.Patterns[1].NestedPatterns[1].Name);
        }
        
        [Fact]
        public void TestDeeplyNestedArrayDestructuring()
        {
            var ast = Parse(@"
                var [[[a]], b] = [[[1]], 2]
            ");
            var destructure = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(2, destructure.Patterns.Count);
            
            // First is nested
            Assert.NotNull(destructure.Patterns[0].NestedPatterns);
            var level1 = destructure.Patterns[0].NestedPatterns![0];
            Assert.NotNull(level1.NestedPatterns);
            var level2 = level1.NestedPatterns![0];
            Assert.Equal("a", level2.Name);
            
            // Second is simple
            Assert.Equal("b", destructure.Patterns[1].Name);
        }
        
        [Fact]
        public void TestMixedNestedAndSimpleDestructuring()
        {
            var ast = Parse(@"
                var [x, [y, z], w] = [1, [2, 3], 4]
            ");
            var destructure = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(3, destructure.Patterns.Count);
            
            Assert.Equal("x", destructure.Patterns[0].Name);
            Assert.NotNull(destructure.Patterns[1].NestedPatterns);
            Assert.Equal("w", destructure.Patterns[2].Name);
        }
        
        [Fact]
        public void TestNestedDestructuringWithSkip()
        {
            var ast = Parse(@"
                var [[a, , b], [c]] = [[1, 2, 3], [4]]
            ");
            var destructure = (ArrayDestructuringStatementNode)ast.Declarations[0];
            Assert.Equal(2, destructure.Patterns.Count);
            
            var firstNested = destructure.Patterns[0].NestedPatterns!;
            Assert.Equal(3, firstNested.Count);
            Assert.Equal("a", firstNested[0].Name);
            Assert.Null(firstNested[1].Name); // Skip
            Assert.Equal("b", firstNested[2].Name);
        }
        
        [Fact]
        public void TestNestedDestructuringWithDefaults()
        {
            var ast = Parse(@"
                var [[a = 1, b], c] = [[[], 2], 3]
            ");
            var destructure = (ArrayDestructuringStatementNode)ast.Declarations[0];
            var firstNested = destructure.Patterns[0].NestedPatterns!;
            Assert.NotNull(firstNested[0].DefaultValue);
        }

        [Fact]
        public void TestTopLevelMemberCallExpressionStatement()
        {
            var ast = Parse(@"
                std.io.exists(""file.txt"")
            ");
            Assert.Single(ast.Declarations);
            Assert.IsType<ExpressionStatementNode>(ast.Declarations[0]);
            var exprStmt = (ExpressionStatementNode)ast.Declarations[0];
            Assert.IsType<CallExpressionNode>(exprStmt.Expression);
            var call = (CallExpressionNode)exprStmt.Expression;
            Assert.IsType<MemberAccessExpressionNode>(call.Callee);
        }

        [Fact]
        public void TestTopLevelMemberCallFollowedByVariable()
        {
            var ast = Parse(@"
                std.io.exists(""file.txt"")
                var x = 42
            ");
            Assert.Equal(2, ast.Declarations.Count);
            Assert.IsType<ExpressionStatementNode>(ast.Declarations[0]);
            Assert.IsType<VariableDeclarationStatementNode>(ast.Declarations[1]);
        }
    }
}
