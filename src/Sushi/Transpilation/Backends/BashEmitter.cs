namespace Sushi.Transpilation.Backends;

using System.Text;
using Sushi.Transpilation.IR;

public sealed class BashEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1100";

    private readonly StringBuilder _builder = new();
    private EmitContext _context = null!;
    private int _indent;

    public string Emit(IrProgram program, EmitContext context)
    {
        _builder.Clear();
        _context = context;
        _indent = 0;

        WriteLine("#!/usr/bin/env bash");
        WriteLine("set -euo pipefail");
        WriteLine("");

        foreach (var statement in program.Statements)
        {
            EmitStatement(statement, inFunction: false);
        }

        return _builder.ToString();
    }

    private void EmitStatement(IrStatement statement, bool inFunction)
    {
        switch (statement)
        {
            case IrBlockStatement block:
                foreach (var child in block.Statements)
                {
                    EmitStatement(child, inFunction);
                }
                break;

            case IrVariableDeclarationStatement variable:
                WriteLine($"{SanitizeName(variable.Name)}={EmitValueExpression(variable.Initializer ?? new IrLiteralExpression(null))}");
                break;

            case IrExpressionStatement expressionStatement:
                EmitExpressionStatement(expressionStatement.Expression);
                break;

            case IrIfStatement ifStatement:
                EmitIfStatement(ifStatement, inFunction);
                break;

            case IrWhileStatement whileStatement:
                EmitWhileStatement(whileStatement, inFunction);
                break;

            case IrForStatement forStatement:
                EmitForStatement(forStatement, inFunction);
                break;

            case IrFunctionDeclarationStatement function:
                EmitFunctionDeclaration(function);
                break;

            case IrReturnStatement returnStatement:
                EmitReturn(returnStatement, inFunction);
                break;

            case IrBreakStatement:
                WriteLine("break");
                break;

            case IrContinueStatement:
                WriteLine("continue");
                break;

            default:
                _context.Error(UnsupportedEmitCode, $"Unsupported IR statement for Bash emitter: {statement.GetType().Name}");
                break;
        }
    }

    private void EmitIfStatement(IrIfStatement statement, bool inFunction)
    {
        WriteLine($"if {EmitConditionCommand(statement.Condition)}; then");
        _indent++;
        EmitStatement(statement.ThenBlock, inFunction);
        _indent--;

        if (statement.ElseBlock != null)
        {
            WriteLine("else");
            _indent++;
            EmitStatement(statement.ElseBlock, inFunction);
            _indent--;
        }

        WriteLine("fi");
    }

    private void EmitWhileStatement(IrWhileStatement statement, bool inFunction)
    {
        WriteLine($"while {EmitConditionCommand(statement.Condition)}; do");
        _indent++;
        EmitStatement(statement.Body, inFunction);
        _indent--;
        WriteLine("done");
    }

    private void EmitForStatement(IrForStatement statement, bool inFunction)
    {
        if (statement.Initializer != null)
        {
            EmitStatement(statement.Initializer, inFunction);
        }

        var condition = statement.Condition != null ? EmitConditionCommand(statement.Condition) : "true";

        WriteLine($"while {condition}; do");
        _indent++;
        EmitStatement(statement.Body, inFunction);

        if (statement.Increment != null)
        {
            EmitExpressionStatement(statement.Increment);
        }

        _indent--;
        WriteLine("done");
    }

    private void EmitFunctionDeclaration(IrFunctionDeclarationStatement statement)
    {
        WriteLine($"{SanitizeName(statement.Name)}() {{");
        _indent++;

        for (var i = 0; i < statement.Parameters.Count; i++)
        {
            var param = SanitizeName(statement.Parameters[i]);
            WriteLine($"local {param}=\"${i + 1}\"");
        }

        EmitStatement(statement.Body, inFunction: true);
        _indent--;
        WriteLine("}");
    }

    private void EmitReturn(IrReturnStatement statement, bool inFunction)
    {
        if (statement.Expression != null)
        {
            WriteLine($"printf '%s\\n' {EmitValueExpression(statement.Expression)}");
        }

        WriteLine(inFunction ? "return 0" : "exit 0");
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
                    var op = unary.Operator == "++" ? "+" : "-";
                    var name = SanitizeName(identifier.Name);
                    WriteLine($"{name}=$(( ${{{name}:-0}} {op} 1 ))");
                    return;
                }
                break;
        }

        _context.Error(UnsupportedEmitCode, $"Unsupported expression statement in Bash emitter: {expression.GetType().Name}");
    }

    private string EmitAssignmentExpression(IrAssignmentExpression assignment)
    {
        var name = SanitizeName(assignment.Target.Name);
        if (assignment.Operator == "=")
        {
            return $"{name}={EmitValueExpression(assignment.Value)}";
        }

        var mathOp = assignment.Operator[0];
        return $"{name}=$(( ${{{name}:-0}} {mathOp} {EmitArithmeticExpression(assignment.Value)} ))";
    }

    private string EmitCallCommand(IrCallExpression call)
    {
        if (call.Callee == "print")
        {
            var args = call.Arguments.Select(EmitValueExpression).ToList();
            return args.Count > 0 ? $"echo -n {string.Join(" ", args)}" : "echo -n";
        }

        if (call.Callee == "println")
        {
            var args = call.Arguments.Select(EmitValueExpression).ToList();
            return args.Count > 0 ? $"echo {string.Join(" ", args)}" : "echo";
        }

        var callee = SanitizeName(call.Callee);
        var arguments = call.Arguments.Select(EmitValueExpression).ToList();
        return arguments.Count > 0
            ? $"{callee} {string.Join(" ", arguments)}"
            : callee;
    }

    private string EmitConditionCommand(IrExpression expression)
    {
        if (expression is IrLiteralExpression literal && literal.Value is bool booleanValue)
        {
            return booleanValue ? "true" : "false";
        }

        if (expression is IrBinaryExpression binary && binary.Operator is "&&" or "||")
        {
            return $"{EmitConditionCommand(binary.Left)} {binary.Operator} {EmitConditionCommand(binary.Right)}";
        }

        if (expression is IrBinaryExpression comparison && comparison.Operator is "==" or "!=")
        {
            var op = comparison.Operator == "==" ? "==" : "!=";
            return $"[[ {EmitComparableValue(comparison.Left)} {op} {EmitComparableValue(comparison.Right)} ]]";
        }

        if (expression is IrBinaryExpression relational && relational.Operator is "<" or ">" or "<=" or ">=")
        {
            return $"(( {EmitArithmeticExpression(relational.Left)} {relational.Operator} {EmitArithmeticExpression(relational.Right)} ))";
        }

        if (expression is IrIdentifierExpression identifier)
        {
            return $"[[ -n \"${{{SanitizeName(identifier.Name)}:-}}\" ]]";
        }

        return $"[[ {EmitValueExpression(expression)} != '' ]]";
    }

    private string EmitComparableValue(IrExpression expression)
    {
        return expression switch
        {
            IrIdentifierExpression identifier => $"\"${{{SanitizeName(identifier.Name)}:-}}\"",
            IrLiteralExpression literal when literal.Value is string str => Escape.BashSingleQuoted(str),
            IrLiteralExpression literal when literal.Value is char ch => Escape.BashSingleQuoted(ch.ToString()),
            IrLiteralExpression literal when literal.Value is bool boolean => Escape.BashSingleQuoted(boolean ? "true" : "false"),
            IrLiteralExpression literal when literal.Value == null => "''",
            IrLiteralExpression literal => literal.Value?.ToString() ?? "''",
            _ => EmitValueExpression(expression)
        };
    }

    private string EmitValueExpression(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => EmitLiteral(literal.Value),
            IrIdentifierExpression identifier => $"\"${{{SanitizeName(identifier.Name)}:-}}\"",
            IrUnaryExpression unary when unary.Operator is "-" or "+" =>
                $"$(( {unary.Operator}{EmitArithmeticExpression(unary.Operand)} ))",
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                $"$(( {EmitArithmeticExpression(binary)} ))",
            IrCallExpression call => $"$({EmitCallCommand(call)})",
            IrAssignmentExpression assignment => $"$({EmitAssignmentExpression(assignment)}; printf '%s' \"${{{SanitizeName(assignment.Target.Name)}:-}}\")",
            _ => "''"
        };
    }

    private string EmitArithmeticExpression(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal when literal.Value is int or long or double or float or decimal
                => Convert.ToString(literal.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            IrLiteralExpression literal when literal.Value is bool boolean => boolean ? "1" : "0",
            IrLiteralExpression => "0",
            IrIdentifierExpression identifier => $"${{{SanitizeName(identifier.Name)}:-0}}",
            IrUnaryExpression unary when unary.Operator is "+" or "-" =>
                $"{unary.Operator}{EmitArithmeticExpression(unary.Operand)}",
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                $"({EmitArithmeticExpression(binary.Left)} {binary.Operator} {EmitArithmeticExpression(binary.Right)})",
            _ => "0"
        };
    }

    private string EmitLiteral(object? value)
    {
        return value switch
        {
            null => "''",
            string str => Escape.BashSingleQuoted(str),
            char ch => Escape.BashSingleQuoted(ch.ToString()),
            bool boolean => Escape.BashSingleQuoted(boolean ? "true" : "false"),
            int or long or double or float or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            _ => Escape.BashSingleQuoted(value.ToString() ?? "")
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
