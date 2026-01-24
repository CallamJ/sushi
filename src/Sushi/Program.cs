using System;
using System.Linq;
using Sushi.Build;
using Sushi.Build.SyntaxTree;

class ProperStructureTest
{
    static void Main()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("     SUSHI PROPER STRUCTURE TEST");
        Console.WriteLine("═══════════════════════════════════════════════════════════\n");

        // Option 1: Wrap in a function
        string sushiCode1 = @"
box Test;

class Person {
    string name;
    
    new(string name){
        this.name = name
    }
    
    getCombinedData(){
        return {
            string name: name,
            string greeting: getGreeting(),
            string: () -> {
                return ""Person: $(name) '$(getGreeting())'""
            }
        }
    }
    
    string getGreeting(){
        return ""Hello, "" + this.name
    }
    
    string() -> name
}

main(){
    Person greg = new Person(""Greg"")
    print(greg.getCombinedData())
}
";

        Console.WriteLine("TEST 1: Code wrapped in main() function");
        Console.WriteLine("───────────────────────────────────────────────────────────");
        Test(sushiCode1);

        Console.WriteLine("\n" + new string('=', 60) + "\n");

        // Option 2: Just the class (valid on its own)
        string sushiCode2 = @"
box Test;

class Person {
    string name;
    
    new(string name){
        this.name = name
    }
    
    getCombinedData(){
        return {
            string name: name,
            string greeting: getGreeting(),
            string: () -> {
                return ""Person: $(name) '$(getGreeting())'""
            }
        }
    }
    
    string getGreeting(){
        return ""Hello, "" + this.name
    }
    
    string() -> name
}
";

        Console.WriteLine("TEST 2: Just the class definition");
        Console.WriteLine("───────────────────────────────────────────────────────────");
        Test(sushiCode2);
    }

    static void Test(string code)
    {
        try
        {
            var tokenizer = new Tokenizer(code);
            var tokens = tokenizer.Tokenize().ToList();
            
            var lexer = new Lexer(tokens);
            var classifiedTokens = lexer.Lex().ToList();
            
            var parser = new Parser(classifiedTokens);
            var ast = parser.Parse();
            
            Console.WriteLine("✓ PARSING SUCCESSFUL!\n");
            
            var printer = new AstPrinter();
            ast.Accept(printer);
            var output = printer.GetResult();
            
            // Show summary
            var lines = output.Split('\n');
            Console.WriteLine("AST Summary:");
            foreach (var line in lines.Take(20))
            {
                Console.WriteLine(line);
            }
            
            if (lines.Length > 20)
            {
                Console.WriteLine($"... ({lines.Length - 20} more lines)");
            }
            
            // Count features
            int boxes = output.Split("BoxDeclaration").Length - 1;
            int classes = output.Split("ClassDeclaration").Length - 1;
            int methods = output.Split("Function:").Length - 1;
            int objectLiterals = output.Split("ObjectLiteral").Length - 1;
            int lambdas = output.Split("Lambda").Length - 1;
            
            Console.WriteLine($"\nFeature Count:");
            Console.WriteLine($"  Boxes: {boxes}");
            Console.WriteLine($"  Classes: {classes}");
            Console.WriteLine($"  Methods: {methods}");
            Console.WriteLine($"  Object Literals: {objectLiterals}");
            Console.WriteLine($"  Lambdas: {lambdas}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ ERROR: {ex.Message}");
        }
    }
}