using Sushi.Build;
using Sushi.Build.SyntaxTree;

namespace Sushi.Tests
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  Sushi Language Parser                                       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            
            string testFile = "examples/test_all_features.sushi";
            
            if (!File.Exists(testFile))
            {
                Console.WriteLine("Error: Test file not found!");
                Console.WriteLine("Please ensure test_all_features.sushi is in the same directory.");
                return;
            }
            
            try
            {
                Console.WriteLine("Reading file: " + testFile);
                string sourceCode = File.ReadAllText(testFile);
                
                int fileSize = sourceCode.Length;
                Console.WriteLine("File size: " + fileSize.ToString() + " bytes");
                Console.WriteLine();
                
                // Phase 1: Tokenization
                Console.WriteLine("Phase 1: Tokenizing...");
                var tokenizer = new Tokenizer(sourceCode);
                var tokens = tokenizer.Tokenize();
                int tokenCount = tokens.Count();
                Console.WriteLine("  ✓ Generated " + tokenCount.ToString() + " tokens");
                Console.WriteLine();
                
                // Phase 2: Lexical Analysis
                Console.WriteLine("Phase 2: Lexical Analysis...");
                var lexer = new Lexer(tokens);
                var classifiedTokens = lexer.Lex().ToList();
                int classifiedCount = classifiedTokens.Count;
                Console.WriteLine("  ✓ Classified " + classifiedCount.ToString() + " tokens");
                Console.WriteLine();
                
                // Phase 3: Parsing
                Console.WriteLine("Phase 3: Parsing...");
                var parser = new Parser(classifiedTokens);
                parser.DebugMode = false; // Set to true for detailed parsing logs
                
                var ast = parser.Parse();
                int declCount = ast.Declarations.Count;
                Console.WriteLine("  ✓ Built AST with " + declCount.ToString() + " top-level declarations");
                Console.WriteLine();
                
                // Phase 4: Print AST
                Console.WriteLine("Phase 4: Printing AST...");
                Console.WriteLine();
                Console.WriteLine("─────────────────────────────────────────────────────────────");
                
                var printer = new AstPrinter();
                ast.Accept(printer);
                Console.WriteLine(printer.GetResult());
                
                Console.WriteLine("─────────────────────────────────────────────────────────────");
                Console.WriteLine();
                
                // Summary
                Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║  PARSING SUCCESSFUL! All features parsed correctly.         ║");
                Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
                Console.WriteLine();
                
                PrintFeatureSummary(ast);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║  PARSING FAILED!                                            ║");
                Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
                Console.WriteLine();
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine();
                Console.WriteLine("Stack Trace:");
                Console.WriteLine(ex.StackTrace);
            }
            
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
        
        static void PrintFeatureSummary(ProgramNode ast)
        {
            Console.WriteLine("Feature Summary:");
            Console.WriteLine("─────────────────────────────────────────────────────────────");
            
            var counter = new FeatureCounter();
            ast.Accept(counter);
            
            Console.WriteLine("  Variables (var):              " + counter.VarDeclarations.ToString());
            Console.WriteLine("  Arrays:                       " + counter.ArrayLiterals.ToString());
            Console.WriteLine("  Array Index Operations:       " + counter.IndexExpressions.ToString());
            Console.WriteLine("  Array Slice Operations:       " + counter.SliceExpressions.ToString());
            Console.WriteLine("  Array Destructuring:          " + counter.ArrayDestructuring.ToString());
            Console.WriteLine("  Ternary Operators (?:):       " + counter.TernaryOperators.ToString());
            Console.WriteLine("  Switch Statements:            " + counter.SwitchStatements.ToString());
            Console.WriteLine("  Do-While Loops:               " + counter.DoWhileLoops.ToString());
            Console.WriteLine("  For-Range Loops:              " + counter.ForRangeLoops.ToString());
            Console.WriteLine("  ForEach Loops:                " + counter.ForEachLoops.ToString());
            Console.WriteLine("  Enum Declarations:            " + counter.EnumDeclarations.ToString());
            Console.WriteLine("  Named Arguments Used:         " + counter.NamedArguments.ToString());
            Console.WriteLine("  Varargs Functions:            " + counter.VarargsParameters.ToString());
            Console.WriteLine("  Default Parameters:           " + counter.DefaultParameters.ToString());
            Console.WriteLine("  Structural Types:             " + counter.StructuralTypes.ToString());
            Console.WriteLine("  Classes:                      " + counter.Classes.ToString());
            Console.WriteLine("  Functions:                    " + counter.Functions.ToString());
            Console.WriteLine();
        }
    }
    
    /// <summary>
    /// Visitor that counts language features
    /// </summary>
    class FeatureCounter : IAstVisitor
    {
        public int VarDeclarations = 0;
        public int ArrayLiterals = 0;
        public int IndexExpressions = 0;
        public int SliceExpressions = 0;
        public int ArrayDestructuring = 0;
        public int TernaryOperators = 0;
        public int SwitchStatements = 0;
        public int DoWhileLoops = 0;
        public int ForRangeLoops = 0;
        public int ForEachLoops = 0;
        public int EnumDeclarations = 0;
        public int NamedArguments = 0;
        public int VarargsParameters = 0;
        public int DefaultParameters = 0;
        public int StructuralTypes = 0;
        public int Classes = 0;
        public int Functions = 0;
        
        // Count features as we traverse
        public void Visit(ProgramNode node)
        {
            foreach (var decl in node.Declarations)
                decl.Accept(this);
        }
        
        public void Visit(ClassDeclarationNode node)
        {
            Classes++;
            foreach (var method in node.Methods)
                method.Accept(this);
            if (node.Constructor != null)
                node.Constructor.Accept(this);
        }
        
        public void Visit(FunctionDeclarationNode node)
        {
            Functions++;
            foreach (var param in node.Parameters)
                param.Accept(this);
            node.Body.Accept(this);
        }
        
        public void Visit(EnumDeclarationNode node)
        {
            EnumDeclarations++;
        }
        
        public void Visit(VariableDeclarationStatementNode node)
        {
            if (node.IsVar)
                VarDeclarations++;
            if (node.Initializer != null)
                node.Initializer.Accept(this);
        }
        
        public void Visit(ArrayLiteralExpressionNode node)
        {
            ArrayLiterals++;
            foreach (var elem in node.Elements)
                elem.Accept(this);
        }
        
        public void Visit(IndexExpressionNode node)
        {
            IndexExpressions++;
            node.Array.Accept(this);
            node.Index.Accept(this);
        }
        
        public void Visit(SliceExpressionNode node)
        {
            SliceExpressions++;
            node.Array.Accept(this);
            if (node.Start != null) node.Start.Accept(this);
            if (node.End != null) node.End.Accept(this);
        }
        
        public void Visit(ArrayDestructuringStatementNode node)
        {
            ArrayDestructuring++;
            node.Value.Accept(this);
        }
        
        public void Visit(ConditionalExpressionNode node)
        {
            TernaryOperators++;
            node.Condition.Accept(this);
            node.TrueExpression.Accept(this);
            node.FalseExpression.Accept(this);
        }
        
        public void Visit(SwitchStatementNode node)
        {
            SwitchStatements++;
            node.Value.Accept(this);
            foreach (var c in node.Cases)
                c.Accept(this);
        }
        
        public void Visit(DoWhileStatementNode node)
        {
            DoWhileLoops++;
            node.Body.Accept(this);
            node.Condition.Accept(this);
        }
        
        public void Visit(ForRangeStatementNode node)
        {
            ForRangeLoops++;
            node.Start.Accept(this);
            node.End.Accept(this);
            if (node.Step != null) node.Step.Accept(this);
            node.Body.Accept(this);
        }
        
        public void Visit(ForEachStatementNode node)
        {
            ForEachLoops++;
            node.Collection.Accept(this);
            node.Body.Accept(this);
        }
        
        public void Visit(ArgumentNode node)
        {
            if (node.Name != null)
                NamedArguments++;
            node.Value.Accept(this);
        }
        
        public void Visit(ParameterNode node)
        {
            if (node.IsVarargs)
                VarargsParameters++;
            if (node.DefaultValue != null)
                DefaultParameters++;
            if (node.StructuralType != null)
                node.StructuralType.Accept(this);
        }
        
        public void Visit(StructuralTypeNode node)
        {
            StructuralTypes++;
        }
        
        // Implement remaining required methods (empty for counting purposes)
        public void Visit(BoxDeclarationNode node) { }
        public void Visit(UseDeclarationNode node) { }
        public void Visit(EnumValueNode node) { }
        public void Visit(ConstructorDeclarationNode node) { node.Body.Accept(this); }
        public void Visit(TypeAdapterDeclarationNode node) { }
        public void Visit(FieldDeclarationNode node) { }
        public void Visit(BlockStatementNode node) { foreach (var s in node.Statements) s.Accept(this); }
        public void Visit(ReturnStatementNode node) { if (node.Expression != null) node.Expression.Accept(this); }
        public void Visit(ExpressionStatementNode node) { node.Expression.Accept(this); }
        public void Visit(DestructuringPatternNode node) { }
        public void Visit(IfStatementNode node) { node.Condition.Accept(this); node.ThenBranch.Accept(this); if (node.ElseBranch != null) node.ElseBranch.Accept(this); }
        public void Visit(SwitchCaseNode node) { foreach (var v in node.MatchValues) v.Accept(this); node.Body.Accept(this); }
        public void Visit(WhileStatementNode node) { node.Condition.Accept(this); node.Body.Accept(this); }
        public void Visit(ForStatementNode node) { if (node.Initializer != null) node.Initializer.Accept(this); if (node.Condition != null) node.Condition.Accept(this); if (node.Increment != null) node.Increment.Accept(this); node.Body.Accept(this); }
        public void Visit(BreakStatementNode node) { }
        public void Visit(ContinueStatementNode node) { }
        public void Visit(BinaryExpressionNode node) { node.Left.Accept(this); node.Right.Accept(this); }
        public void Visit(UnaryExpressionNode node) { node.Operand.Accept(this); }
        public void Visit(CallExpressionNode node) { node.Callee.Accept(this); foreach (var arg in node.Arguments) arg.Accept(this); }
        public void Visit(MemberAccessExpressionNode node) { node.Object.Accept(this); }
        public void Visit(PipeExpressionNode node) { node.Source.Accept(this); node.Target.Accept(this); }
        public void Visit(IdentifierExpressionNode node) { }
        public void Visit(LiteralExpressionNode node) { }
        public void Visit(InterpolatedStringExpressionNode node) { }
        public void Visit(NewExpressionNode node) { foreach (var arg in node.Arguments) arg.Accept(this); }
        public void Visit(ThisExpressionNode node) { }
        public void Visit(ParenthesizedExpressionNode node) { node.Expression.Accept(this); }
        public void Visit(ObjectLiteralExpressionNode node) { foreach (var prop in node.Properties) prop.Accept(this); }
        public void Visit(ObjectPropertyNode node) { node.Value.Accept(this); }
        public void Visit(LambdaExpressionNode node) { foreach (var p in node.Parameters) p.Accept(this); node.Body.Accept(this); }
    }
}