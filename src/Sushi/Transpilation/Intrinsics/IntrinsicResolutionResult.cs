namespace Sushi.Transpilation.Intrinsics;

using Sushi.Transpilation.IR;

public sealed class IntrinsicCallArgument
{
    public string? Name { get; }
    public IrExpression Value { get; }
    public int Line { get; }
    public int Column { get; }

    public IntrinsicCallArgument(string? name, IrExpression value, int line, int column)
    {
        Name = name;
        Value = value;
        Line = line;
        Column = column;
    }
}

public sealed class IntrinsicResolutionResult
{
    public bool Success { get; }
    public IReadOnlyList<IrExpression> OrderedArguments { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public IntrinsicResolutionResult(
        bool success,
        IReadOnlyList<IrExpression> orderedArguments,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Success = success;
        OrderedArguments = orderedArguments;
        Diagnostics = diagnostics;
    }
}
