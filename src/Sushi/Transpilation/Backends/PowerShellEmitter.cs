namespace Sushi.Transpilation.Backends;

using System.Text;
using Sushi.Transpilation.IR;

public sealed class PowerShellEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1200";

    private readonly StringBuilder _builder = new();
    private EmitContext _context = null!;
    private int _indent;

    public string Emit(IrProgram program, EmitContext context)
    {
        _builder.Clear();
        _context = context;
        _indent = 0;

        WriteLine("Set-StrictMode -Version Latest");
        WriteLine("");

        foreach (var statement in program.Statements)
        {
            EmitStatement(statement);
        }

        return _builder.ToString();
    }

    private void EmitStatement(IrStatement statement)
    {
        switch (statement)
        {
            case IrBlockStatement block:
                foreach (var child in block.Statements)
                {
                    EmitStatement(child);
                }
                break;

            case IrVariableDeclarationStatement variable:
                WriteLine($"${SanitizeName(variable.Name)} = {EmitValueExpression(variable.Initializer ?? new IrLiteralExpression(null))}");
                break;

            case IrExpressionStatement expressionStatement:
                EmitExpressionStatement(expressionStatement.Expression);
                break;

            case IrIfStatement ifStatement:
                EmitIfStatement(ifStatement);
                break;

            case IrWhileStatement whileStatement:
                EmitWhileStatement(whileStatement);
                break;

            case IrForStatement forStatement:
                EmitForStatement(forStatement);
                break;

            case IrFunctionDeclarationStatement function:
                EmitFunction(function);
                break;

            case IrReturnStatement returnStatement:
                if (returnStatement.Expression != null)
                {
                    WriteLine($"return {EmitValueExpression(returnStatement.Expression)}");
                }
                else
                {
                    WriteLine("return");
                }
                break;

            case IrBreakStatement:
                WriteLine("break");
                break;

            case IrContinueStatement:
                WriteLine("continue");
                break;

            default:
                _context.Error(UnsupportedEmitCode, $"Unsupported IR statement for PowerShell emitter: {statement.GetType().Name}");
                break;
        }
    }

    private void EmitIfStatement(IrIfStatement statement)
    {
        WriteLine($"if ({EmitConditionExpression(statement.Condition)}) {{");
        _indent++;
        EmitStatement(statement.ThenBlock);
        _indent--;
        WriteLine("}");

        if (statement.ElseBlock != null)
        {
            WriteLine("else {");
            _indent++;
            EmitStatement(statement.ElseBlock);
            _indent--;
            WriteLine("}");
        }
    }

    private void EmitWhileStatement(IrWhileStatement statement)
    {
        WriteLine($"while ({EmitConditionExpression(statement.Condition)}) {{");
        _indent++;
        EmitStatement(statement.Body);
        _indent--;
        WriteLine("}");
    }

    private void EmitForStatement(IrForStatement statement)
    {
        if (statement.Initializer != null)
        {
            EmitStatement(statement.Initializer);
        }

        var condition = statement.Condition != null ? EmitConditionExpression(statement.Condition) : "$true";
        WriteLine($"while ({condition}) {{");
        _indent++;
        EmitStatement(statement.Body);

        if (statement.Increment != null)
        {
            EmitExpressionStatement(statement.Increment);
        }

        _indent--;
        WriteLine("}");
    }

    private void EmitFunction(IrFunctionDeclarationStatement statement)
    {
        WriteLine($"function {SanitizeName(statement.Name)} {{");
        _indent++;

        if (statement.Parameters.Count > 0)
        {
            var parameterList = string.Join(", ", statement.Parameters.Select(p => $"${SanitizeName(p)}"));
            WriteLine($"param({parameterList})");
        }

        EmitStatement(statement.Body);
        _indent--;
        WriteLine("}");
    }

    private void EmitExpressionStatement(IrExpression expression)
    {
        switch (expression)
        {
            case IrCallExpression call:
                WriteLine(EmitCallCommand(call));
                return;

            case IrAssignmentExpression assignment:
                WriteLine(EmitAssignmentExpression(assignment));
                return;

            case IrUnaryExpression unary when unary.Operator is "++" or "--":
                if (unary.Operand is IrIdentifierExpression identifier)
                {
                    var name = SanitizeName(identifier.Name);
                    var op = unary.Operator == "++" ? "+" : "-";
                    WriteLine($"${name} = ${name} {op} 1");
                    return;
                }
                break;
        }

        _context.Error(UnsupportedEmitCode, $"Unsupported expression statement in PowerShell emitter: {expression.GetType().Name}");
    }

    private string EmitAssignmentExpression(IrAssignmentExpression assignment)
    {
        var name = SanitizeName(assignment.Target.Name);
        return assignment.Operator switch
        {
            "=" => $"${name} = {EmitValueExpression(assignment.Value)}",
            "+=" => $"${name} += {EmitValueExpression(assignment.Value)}",
            "-=" => $"${name} -= {EmitValueExpression(assignment.Value)}",
            "*=" => $"${name} *= {EmitValueExpression(assignment.Value)}",
            "/=" => $"${name} /= {EmitValueExpression(assignment.Value)}",
            _ => $"${name} = {EmitValueExpression(assignment.Value)}"
        };
    }

    private string EmitCallCommand(IrCallExpression call)
    {
        if (call.Callee == "print")
        {
            var args = call.Arguments.Select(EmitValueExpression).ToList();
            return args.Count > 0
                ? $"Write-Host -NoNewline {string.Join(" ", args)}"
                : "Write-Host -NoNewline ''";
        }

        if (call.Callee == "println")
        {
            var args = call.Arguments.Select(EmitValueExpression).ToList();
            return args.Count > 0
                ? $"Write-Host {string.Join(" ", args)}"
                : "Write-Host";
        }

        var callee = SanitizeName(call.Callee);
        var arguments = call.Arguments.Select(EmitValueExpression).ToList();
        return arguments.Count > 0
            ? $"{callee} {string.Join(" ", arguments)}"
            : callee;
    }

    private string EmitConditionExpression(IrExpression expression)
    {
        if (expression is IrLiteralExpression literal && literal.Value is bool booleanValue)
        {
            return booleanValue ? "$true" : "$false";
        }

        if (expression is IrBinaryExpression binary)
        {
            var op = MapBinaryOperator(binary.Operator);
            return $"({EmitValueExpression(binary.Left)} {op} {EmitValueExpression(binary.Right)})";
        }

        if (expression is IrIdentifierExpression identifier)
        {
            return $"[bool]${SanitizeName(identifier.Name)}";
        }

        return $"[bool]({EmitValueExpression(expression)})";
    }

    private string EmitValueExpression(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => EmitLiteral(literal.Value),
            IrIdentifierExpression identifier => $"${SanitizeName(identifier.Name)}",
            IrUnaryExpression unary when unary.Operator is "!" =>
                $"(-not {EmitValueExpression(unary.Operand)})",
            IrUnaryExpression unary when unary.Operator is "-" or "+" =>
                $"({unary.Operator}{EmitValueExpression(unary.Operand)})",
            IrBinaryExpression binary =>
                $"({EmitValueExpression(binary.Left)} {MapBinaryOperator(binary.Operator)} {EmitValueExpression(binary.Right)})",
            IrCallExpression call =>
                $"({EmitCallCommand(call)})",
            IrAssignmentExpression assignment =>
                $"({EmitAssignmentExpression(assignment)}; ${SanitizeName(assignment.Target.Name)})",
            _ => "$null"
        };
    }

    private static string MapBinaryOperator(string op)
    {
        return op switch
        {
            "==" => "-eq",
            "!=" => "-ne",
            "<" => "-lt",
            "<=" => "-le",
            ">" => "-gt",
            ">=" => "-ge",
            "&&" => "-and",
            "||" => "-or",
            _ => op
        };
    }

    private static string EmitLiteral(object? value)
    {
        return value switch
        {
            null => "$null",
            string str => Escape.PowerShellSingleQuoted(str),
            char ch => Escape.PowerShellSingleQuoted(ch.ToString()),
            bool boolean => boolean ? "$true" : "$false",
            int or long or double or float or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            _ => Escape.PowerShellSingleQuoted(value.ToString() ?? "")
        };
    }

    private void WriteLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _builder.AppendLine();
            return;
        }

        _builder.Append(' ', _indent * 4);
        _builder.AppendLine(text);
    }

    private static string SanitizeName(string name)
    {
        return name.Replace(".", "_").Replace("-", "_");
    }
}
