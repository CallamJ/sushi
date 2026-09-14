namespace Sushi.Transpilation.Lowering;

using Sushi.Transpilation.IR;

internal static class FunctionCallBinder
{
    public const string DuplicateArgumentCode = "SUSHI1010";
    public const string UnknownNamedArgumentCode = "SUSHI1011";
    public const string MissingRequiredArgumentCode = "SUSHI1012";
    public const string TooManyArgumentsCode = "SUSHI1013";
    public const string InvalidVarargsNamedBindingCode = "SUSHI1014";
    public const string PositionalAfterNamedCode = "SUSHI1015";
    public const string InvalidVarargsDeclarationCode = "SUSHI1016";

    public static FunctionCallBindingResult Bind(
        string calleeName,
        IReadOnlyList<IrFunctionParameter> parameters,
        IReadOnlyList<IrCallArgument> callArguments,
        string sourcePath,
        int callLine,
        int callColumn)
    {
        var diagnostics = new List<Diagnostic>();

        var varargsParameters = parameters
            .Select((parameter, index) => new { parameter, index })
            .Where(x => x.parameter.IsVarargs)
            .ToList();

        if (varargsParameters.Count > 1 || (varargsParameters.Count == 1 && varargsParameters[0].index != parameters.Count - 1))
        {
            diagnostics.Add(Diagnostic.Error(
                InvalidVarargsDeclarationCode,
                $"Function '{calleeName}' has an invalid varargs parameter declaration. Varargs must be the final parameter.",
                new SourceSpan(sourcePath, callLine, callColumn)));
            return FunctionCallBindingResult.CreateFailure(diagnostics);
        }

        var varargsIndex = varargsParameters.Count == 1 ? varargsParameters[0].index : -1;
        var namedArgumentStarted = false;
        var boundValues = new IrExpression?[parameters.Count];
        var varargsValues = new List<IrExpression>();
        var position = 0;

        foreach (var argument in callArguments)
        {
            if (argument.Name == null)
            {
                if (namedArgumentStarted)
                {
                    diagnostics.Add(Diagnostic.Error(
                        PositionalAfterNamedCode,
                        $"Positional argument cannot appear after named arguments in call to '{calleeName}'.",
                        new SourceSpan(sourcePath, argument.Line, argument.Column)));
                    continue;
                }

                if (varargsIndex >= 0 && position >= varargsIndex)
                {
                    varargsValues.Add(argument.Value);
                    continue;
                }

                if (position >= parameters.Count)
                {
                    diagnostics.Add(Diagnostic.Error(
                        TooManyArgumentsCode,
                        $"Too many positional arguments for function '{calleeName}'.",
                        new SourceSpan(sourcePath, argument.Line, argument.Column)));
                    continue;
                }

                boundValues[position] = argument.Value;
                position++;
                continue;
            }

            namedArgumentStarted = true;
            var parameterIndex = -1;
            for (var i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].Name == argument.Name)
                {
                    parameterIndex = i;
                    break;
                }
            }

            if (parameterIndex < 0)
            {
                diagnostics.Add(Diagnostic.Error(
                    UnknownNamedArgumentCode,
                    $"Unknown named argument '{argument.Name}' for function '{calleeName}'.",
                    new SourceSpan(sourcePath, argument.Line, argument.Column)));
                continue;
            }

            if (parameters[parameterIndex].IsVarargs)
            {
                diagnostics.Add(Diagnostic.Error(
                    InvalidVarargsNamedBindingCode,
                    $"Named arguments cannot target varargs parameter '{argument.Name}' in function '{calleeName}'.",
                    new SourceSpan(sourcePath, argument.Line, argument.Column)));
                continue;
            }

            if (boundValues[parameterIndex] != null)
            {
                diagnostics.Add(Diagnostic.Error(
                    DuplicateArgumentCode,
                    $"Parameter '{argument.Name}' is assigned more than once in call to '{calleeName}'.",
                    new SourceSpan(sourcePath, argument.Line, argument.Column)));
                continue;
            }

            boundValues[parameterIndex] = argument.Value;
        }

        if (diagnostics.Count > 0)
        {
            return FunctionCallBindingResult.CreateFailure(diagnostics);
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.IsVarargs)
            {
                continue;
            }

            if (boundValues[i] != null)
            {
                continue;
            }

            if (parameter.DefaultValue != null)
            {
                boundValues[i] = parameter.DefaultValue;
                continue;
            }

            diagnostics.Add(Diagnostic.Error(
                MissingRequiredArgumentCode,
                $"Missing required argument '{parameter.Name}' for function '{calleeName}'.",
                new SourceSpan(sourcePath, callLine, callArguments.LastOrDefault()?.Column ?? callColumn + 1)));
        }

        if (diagnostics.Count > 0)
        {
            return FunctionCallBindingResult.CreateFailure(diagnostics);
        }

        var ordered = new List<IrCallArgument>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.IsVarargs)
            {
                foreach (var varargValue in varargsValues)
                {
                    ordered.Add(new IrCallArgument(name: null, varargValue, callLine, callColumn));
                }

                continue;
            }

            ordered.Add(new IrCallArgument(name: null, boundValues[i]!, callLine, callColumn));
        }

        return FunctionCallBindingResult.CreateSuccess(ordered);
    }
}

internal sealed class FunctionCallBindingResult
{
    public bool Success { get; }
    public IReadOnlyList<IrCallArgument> OrderedArguments { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    private FunctionCallBindingResult(
        bool success,
        IReadOnlyList<IrCallArgument> orderedArguments,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Success = success;
        OrderedArguments = orderedArguments;
        Diagnostics = diagnostics;
    }

    public static FunctionCallBindingResult CreateSuccess(IReadOnlyList<IrCallArgument> orderedArguments)
    {
        return new FunctionCallBindingResult(
            success: true,
            orderedArguments,
            Array.Empty<Diagnostic>());
    }

    public static FunctionCallBindingResult CreateFailure(IReadOnlyList<Diagnostic> diagnostics)
    {
        return new FunctionCallBindingResult(
            success: false,
            Array.Empty<IrCallArgument>(),
            diagnostics);
    }
}
