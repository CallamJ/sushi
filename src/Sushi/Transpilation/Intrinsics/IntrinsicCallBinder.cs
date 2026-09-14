namespace Sushi.Transpilation.Intrinsics;

using Sushi.Transpilation.IR;

public static class IntrinsicCallBinder
{
    public static IntrinsicResolutionResult Bind(
        IntrinsicSignature signature,
        IReadOnlyList<IntrinsicCallArgument> arguments,
        string sourcePath,
        int callLine,
        int callColumn)
    {
        var diagnostics = new List<Diagnostic>();
        var ordered = new List<IrExpression>();

        var parameters = signature.Parameters;
        var variadicIndex = parameters
            .Select((parameter, index) => (parameter, index))
            .FirstOrDefault(t => t.parameter.IsVariadic).index;
        var hasVariadic = parameters.Any(parameter => parameter.IsVariadic);

        var named = new Dictionary<string, IntrinsicCallArgument>(StringComparer.Ordinal);
        var positional = new List<IntrinsicCallArgument>();

        foreach (var argument in arguments)
        {
            if (argument.Name == null)
            {
                positional.Add(argument);
                continue;
            }

            if (named.ContainsKey(argument.Name))
            {
                diagnostics.Add(Diagnostic.Error(
                    IntrinsicDiagnosticCodes.DuplicateNamedArgument,
                    $"Duplicate named argument '{argument.Name}' for intrinsic '{signature.CanonicalName}'",
                    new SourceSpan(sourcePath, argument.Line, argument.Column)));
                continue;
            }

            named[argument.Name] = argument;
        }

        foreach (var namedArgument in named.Values)
        {
            var exists = parameters.Any(parameter => parameter.Name == namedArgument.Name);
            if (!exists)
            {
                diagnostics.Add(Diagnostic.Error(
                    IntrinsicDiagnosticCodes.UnknownNamedArgument,
                    $"Unknown named argument '{namedArgument.Name}' for intrinsic '{signature.CanonicalName}'",
                    new SourceSpan(sourcePath, namedArgument.Line, namedArgument.Column)));
            }
        }

        var positionalIndex = 0;
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.IsVariadic)
            {
                while (positionalIndex < positional.Count)
                {
                    ordered.Add(positional[positionalIndex++].Value);
                }

                continue;
            }

            if (named.TryGetValue(parameter.Name, out var namedArg))
            {
                ordered.Add(namedArg.Value);
                continue;
            }

            if (positionalIndex < positional.Count)
            {
                ordered.Add(positional[positionalIndex++].Value);
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                ordered.Add(new IrLiteralExpression(parameter.DefaultValue));
                continue;
            }

            diagnostics.Add(Diagnostic.Error(
                IntrinsicDiagnosticCodes.InvalidArgumentCount,
                $"Missing required argument '{parameter.Name}' for intrinsic '{signature.CanonicalName}'",
                new SourceSpan(sourcePath, callLine, arguments.LastOrDefault()?.Column ?? callColumn + 1)));
        }

        if (!hasVariadic && positionalIndex < positional.Count)
        {
            diagnostics.Add(Diagnostic.Error(
                IntrinsicDiagnosticCodes.InvalidArgumentCount,
                $"Too many arguments for intrinsic '{signature.CanonicalName}'",
                new SourceSpan(sourcePath, callLine, callColumn)));
        }

        if (hasVariadic && variadicIndex >= 0 && parameters[variadicIndex].HasDefaultValue && ordered.Count == variadicIndex)
        {
            ordered.Add(new IrLiteralExpression(parameters[variadicIndex].DefaultValue));
        }

        return new IntrinsicResolutionResult(
            success: diagnostics.Count == 0,
            orderedArguments: ordered,
            diagnostics: diagnostics);
    }
}
