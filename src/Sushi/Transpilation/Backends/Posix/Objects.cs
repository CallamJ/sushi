using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

namespace Sushi.Transpilation.Backends.Posix;

public sealed partial class PosixEmitter
{
    private void EmitPrintedStringPredicate(IrIntrinsicCallExpression predicate, bool newline, bool inFunction)
    {
        var value = PrepareValue(predicate.Arguments[0], inFunction);
        var test = predicate.Id switch
        {
            IntrinsicId.StringContains => $"{value} == *{PrepareValue(predicate.Arguments[1], inFunction)}*",
            IntrinsicId.StringStartsWith => $"{value} == {PrepareValue(predicate.Arguments[1], inFunction)}*",
            IntrinsicId.StringEndsWith => $"{value} == *{PrepareValue(predicate.Arguments[1], inFunction)}",
            IntrinsicId.StringIsMatch => $"{value} =~ {PrepareRegex(predicate.Arguments[1], inFunction)}",
            _ => throw new InvalidOperationException($"Unexpected string predicate '{predicate.Id}'.")
        };
        var suffix = newline ? "\\n" : string.Empty;
        WriteLine($"if [[ {test} ]]; then printf '%s{suffix}' 'true'; else printf '%s{suffix}' 'false'; fi");
    }

    private string EmitAssignmentExpression(IrAssignmentExpression assignment)
    {
        var name = SanitizeVariableName(assignment.Target.Name);
        if (assignment.Operator == "=")
        {
            return $"{name}={EmitValueExpression(assignment.Value)}";
        }

        if (IsDefinitelyFloat(assignment.Value) || _knownFloatVariables.Contains(name))
        {
            var currentValue = $"\"${{{name}:-}}\"";
            var rightValue = EmitFloatOperand(assignment.Value);
            return $"{name}={EmitAwkArithmetic(currentValue, rightValue, assignment.Operator[0].ToString())}";
        }

        var mathOp = assignment.Operator[0];
        return $"{name}=$(( ${{{name}:-0}} {mathOp} {EmitArithmeticExpression(assignment.Value)} ))";
    }

    private void EmitPreparedAssignment(IrAssignmentExpression assignment, bool inFunction)
    {
        var name = SanitizeVariableName(assignment.Target.Name);
        if (assignment.Operator == "=")
        {
            if (assignment.Value is IrConditionalExpression { IsSwitchExpression: true } switchExpression)
            {
                EmitSwitchExpressionInto(name, switchExpression, inFunction);
                return;
            }
            if (IsBooleanValueExpression(assignment.Value))
            {
                EmitBooleanAssignment(name, assignment.Value, inFunction);
                SetKnownInteger(name, false);
                return;
            }
            if (assignment.Value is IrIdentifierExpression objectAlias &&
                _nativeObjectVariables.Contains(SanitizeVariableName(objectAlias.Name)))
            {
                var source = ResolveNativeObjectName(SanitizeVariableName(objectAlias.Name));
                if (_dialect.IsZsh)
                {
                    WriteLine($"{name}=( \"${{(@kv){source}}}\" )");
                    _nativeObjectAliases[name] = source;
                }
                else
                {
                    WriteLine(_nativeObjectAliases.ContainsKey(name) ? $"unset -n {name}" : $"unset {name}");
                    WriteLine($"{(inFunction ? "local " : "declare ")}-n {name}={Escape.PosixSingleQuoted(source)}");
                    _nativeObjectAliases[name] = source;
                }
                _nativeObjectVariables.Add(name);
                return;
            }
            if (TryGetObjectReturningCall(assignment.Value, out var objectCall, out var objectFunction))
            {
                if (!_nativeObjectVariables.Contains(name))
                    WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
                EmitCallInto(name, objectCall, objectFunction, inFunction);
                _nativeObjectVariables.Add(name);
                return;
            }
            if (TryGetReturningCall(assignment.Value, out var valueCall, out var valueFunction))
            {
                EmitCallInto(name, valueCall, valueFunction, inFunction);
                SetKnownInteger(name, valueFunction.ReturnType.Name == "int");
                SetKnownFloat(name, valueFunction.ReturnType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true);
                return;
            }
            if (assignment.Value is IrArrayLiteralExpression array)
            {
                var values = array.Elements.Any(element => element is IrObjectLiteralExpression)
                    ? new List<string>()
                    : array.Elements.Select(element => PrepareValue(element, inFunction)).ToList();
                WriteLine($"{name}=({string.Join(" ", values)})");
                _nativeArrayVariables[name] = name;
                _arrayInitializers[name] = array;
                _nativeObjectVariables.Remove(name);
                _recordVariables.Remove(name);
                return;
            }

            if (assignment.Value is IrObjectLiteralExpression obj)
            {
                var entries = _dialect.IsZsh
                    ? obj.Properties.Select(property =>
                        $"[{Escape.PosixSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
                    : obj.Properties.Select(property =>
                        $"[{Escape.PosixSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}");
                WriteLine($"{name}=({string.Join(" ", entries)})");
                _nativeObjectVariables.Add(name);
                _nativeArrayVariables.Remove(name);
                _arrayInitializers.Remove(name);
                _recordVariables.Remove(name);
                return;
            }

            if (assignment.Value is IrIntrinsicCallExpression intrinsic &&
                EmitNativeIntrinsicDeclaration(name, intrinsic, inFunction))
            {
                return;
            }
        }

        var value = PrepareValue(assignment.Value, inFunction);
        if (assignment.Operator == "=")
        {
            WriteLine($"{name}={value}");
            SetKnownInteger(name, IsDefinitelyInteger(assignment.Value));
            SetKnownFloat(name, IsDefinitelyFloat(assignment.Value));
            SetKnownArray(name, assignment.Value is IrArrayLiteralExpression);
            return;
        }

        var right = DeclareTemp(value, inFunction);
        if (IsDefinitelyFloat(assignment.Value) || _knownFloatVariables.Contains(name))
        {
            var currentValue = $"\"${{{name}:-}}\"";
            var rightValue = $"\"${{{right}:-}}\"";
            WriteLine($"{name}={EmitAwkArithmetic(currentValue, rightValue, assignment.Operator[0].ToString())}");
        }
        else
            WriteLine($"{name}=$(( {name} {assignment.Operator[0]} {right} ))");
        SetKnownInteger(name, !IsDefinitelyFloat(assignment.Value) && !_knownFloatVariables.Contains(name));
        SetKnownFloat(name, IsDefinitelyFloat(assignment.Value) || _knownFloatVariables.Contains(name));
    }

    private void EmitSwitchExpressionInto(string destination, IrConditionalExpression root, bool inFunction)
    {
        var arms = new List<(IrExpression Match, IrExpression Value)>();
        IrExpression fallback = root;
        while (fallback is IrConditionalExpression conditional && conditional.IsSwitchExpression &&
               TryGetSwitchComparison(conditional.Condition, out var comparison))
        {
            arms.Add((comparison.Right, conditional.TrueExpression));
            fallback = conditional.FalseExpression;
        }

        if (arms.Count == 0)
        {
            WriteLine($"{destination}={PrepareValue(root, inFunction)}");
            return;
        }

        TryGetSwitchComparison(root.Condition, out var firstComparison);
        var selector = firstComparison is null
            ? PrepareValue(root.Condition, inFunction)
            : PrepareValue(firstComparison.Left, inFunction);
        WriteLine($"case {selector} in");
        _indent++;
        foreach (var arm in arms)
            WriteLine($"{EmitValueExpression(arm.Match)}) {destination}={PrepareValue(arm.Value, inFunction)} ;;");
        WriteLine($"*) {destination}={PrepareValue(fallback, inFunction)} ;;");
        _indent--;
        WriteLine("esac");
    }

    private static bool TryGetSwitchComparison(IrExpression condition, out IrBinaryExpression comparison)
    {
        comparison = condition switch
        {
            IrBinaryExpression { Operator: "==" } binary => binary,
            IrTruthinessExpression { Operand: IrBinaryExpression { Operator: "==" } binary } => binary,
            _ => null!
        };
        return comparison != null;
    }

    private void EmitMemberAssignment(IrMemberAssignmentExpression assignment, bool inFunction)
    {
        if (assignment.Target is IrIdentifierExpression identifier)
        {
            var target = ResolveNativeObjectName(SanitizeVariableName(identifier.Name));
            var member = EmitObjectSubscript(assignment.MemberName);
            if (assignment.Operator == "=")
                WriteLine($"{target}[{member}]={PrepareValue(assignment.Value, inFunction)}");
            else if (assignment.MemberType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true ||
                     IsDefinitelyFloat(assignment.Value))
            {
                var currentValue = $"\"${{{target}[{member}]:-}}\"";
                WriteLine($"{target}[{member}]={EmitAwkArithmetic(currentValue, EmitValueExpression(assignment.Value), assignment.Operator[0].ToString())}");
            }
            else
                WriteLine($"{target}[{member}]=$(( ${{{target}[{member}]:-0}} {assignment.Operator[0]} {EmitArithmeticExpression(assignment.Value)} ))");
            return;
        }

        _context.Error(AmbiguousShapeCode, "Member assignment requires a statically known native object.");
    }

    private void EmitCallInto(
        string destination,
        IrCallExpression call,
        IrFunctionDeclarationStatement function,
        bool inFunction)
    {
        var arguments = new List<string> { Escape.PosixSingleQuoted(destination) };
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            var argument = call.Arguments[index].Value;
            var parameter = index < function.Parameters.Count ? function.Parameters[index] : null;
            if (argument is IrObjectLiteralExpression objectLiteral &&
                parameter?.DeclaredType.Kind == IrTypeKind.Structural)
            {
                foreach (var field in parameter.DeclaredType.StructuralFields)
                {
                    var property = objectLiteral.Properties.FirstOrDefault(item => item.Name == field.Name);
                    arguments.Add(property == null ? "''" : PrepareValue(property.Value, inFunction));
                }
            }
            else if (argument is IrIdentifierExpression structuralIdentifier &&
                     parameter?.DeclaredType.Kind == IrTypeKind.Structural)
            {
                var aggregateName = SanitizeVariableName(structuralIdentifier.Name);
                foreach (var field in parameter.DeclaredType.StructuralFields)
                {
                    arguments.Add(_nativeObjectVariables.Contains(aggregateName)
                        ? $"\"${{{aggregateName}[{EmitObjectSubscript(field.Name)}]-}}\""
                        : $"\"${{{aggregateName}_{SanitizeVariableName(field.Name)}-}}\"");
                }
            }
            else if (argument is IrIdentifierExpression identifier && parameter != null && IsNativeObjectType(parameter.DeclaredType))
                arguments.Add(Escape.PosixSingleQuoted(ResolveNativeObjectName(SanitizeVariableName(identifier.Name))));
            else if (argument is IrConstructionExpression construction && parameter != null && IsNativeObjectType(parameter.DeclaredType))
                arguments.Add(Escape.PosixSingleQuoted(PrepareConstructionReference(construction, inFunction)));
            else
                arguments.Add(PrepareValue(argument, inFunction));
        }
        WriteLine($"{SanitizeFunctionName(call.Callee)} {string.Join(" ", arguments)}");
    }

    private bool TryGetObjectReturningCall(
        IrExpression expression,
        out IrCallExpression call,
        out IrFunctionDeclarationStatement function)
    {
        call = expression switch
        {
            IrCallExpression direct => direct,
            IrResolvedMethodCallExpression method => method.AsFunctionCall(),
            IrAdapterCallExpression adapter => adapter.AsFunctionCall(),
            _ => null!
        };
        if (call != null && _functions.TryGetValue(call.Callee, out function!) && IsNamedObjectType(function.ReturnType))
            return true;
        function = null!;
        return false;
    }

    private bool TryGetReturningCall(
        IrExpression expression,
        out IrCallExpression call,
        out IrFunctionDeclarationStatement function)
    {
        call = expression switch
        {
            IrCallExpression direct => direct,
            IrResolvedMethodCallExpression method => method.AsFunctionCall(),
            IrAdapterCallExpression adapter => adapter.AsFunctionCall(),
            _ => null!
        };
        if (call != null && _functions.TryGetValue(call.Callee, out function!) && FunctionReturnsValue(function))
            return true;
        function = null!;
        return false;
    }

    private string PrepareConstructionReference(IrConstructionExpression construction, bool inFunction)
    {
        var name = _names.Generated(TargetNameKind.Variable, "_object" + (++_valueTempId));
        WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
        var arguments = construction.Arguments.Select(argument => PrepareValue(argument.Value, inFunction));
        WriteLine($"{SanitizeFunctionName(construction.ConstructorName)} {Escape.PosixSingleQuoted(name)} {string.Join(" ", arguments)}");
        _nativeObjectVariables.Add(name);
        return name;
    }

    private void EmitZshObjectParameterWritebacks()
    {
        if (!_dialect.IsZsh) return;
        foreach (var item in _zshObjectParameterNames)
        {
            if (_zshReadOnlyObjectParameters.Contains(item.Key)) continue;
            WriteLine($"typeset -gA ${{{item.Value}}}");
            WriteLine($"set -A ${{{item.Value}}} \"${{(@kv){item.Key}}}\"");
        }
    }

    private static bool FunctionMutatesReceiver(IrStatement statement) => statement switch
    {
        IrExpressionStatement { Expression: IrMemberAssignmentExpression { Target: IrIdentifierExpression { Name: "this" } } } => true,
        IrBlockStatement block => block.Statements.Any(FunctionMutatesReceiver),
        IrIfStatement conditional => FunctionMutatesReceiver(conditional.ThenBlock) ||
                                     (conditional.ElseBlock != null && FunctionMutatesReceiver(conditional.ElseBlock)),
        IrWhileStatement loop => FunctionMutatesReceiver(loop.Body),
        IrForStatement loop => FunctionMutatesReceiver(loop.Body),
        IrDoWhileStatement loop => FunctionMutatesReceiver(loop.Body),
        _ => false
    };

    private void EmitFunctionOutputAssignment(string value)
    {
        if (_dialect.IsZsh)
        {
            WriteLine($": ${{(P){_currentOutputName}::={value}}}");
            return;
        }

        WriteLine($"{_currentOutputName}={value}");
    }

    private static bool IsNamedObjectType(IrTypeRef type) =>
        type.Kind == IrTypeKind.Primitive && type.Name != null &&
        type.Name.ToLowerInvariant() is not ("string" or "int" or "float" or "bool" or "array" or "object" or "any");

    private static bool IsNativeObjectType(IrTypeRef type) =>
        type.Kind == IrTypeKind.Structural || type.Name == "object" || IsNamedObjectType(type);

    private string ResolveNativeObjectName(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (_nativeObjectAliases.TryGetValue(name, out var target) && seen.Add(name)) name = target;
        return name;
    }

    private bool EmitNativeIntrinsicDeclaration(
        string name,
        IrIntrinsicCallExpression intrinsic,
        bool inFunction)
    {
        var declaration = inFunction ? "local " : "";
        var arrayDeclaration = inFunction ? "local " : "declare ";
        switch (intrinsic.Id)
        {
            case IntrinsicId.ProcessArgs:
                WriteLine($"{arrayDeclaration}-a {name}=(\"$@\")");
                _nativeArrayVariables[name] = name;
                _arrayInitializers.Remove(name);
                return true;

            case IntrinsicId.StringMatch:
            {
                var value = PrepareValue(intrinsic.Arguments[0], inFunction);
                var pattern = PrepareRegex(intrinsic.Arguments[1], inFunction);
                WriteLine($"{declaration}{name}_ok='false'");
                WriteLine($"{declaration}{name}_value=''");
                WriteLine($"{declaration}{name}_index=-1");
                WriteLine($"{arrayDeclaration}-a {name}_groups=()");
                WriteLine($"if [[ {value} =~ {pattern} ]]; then");
                _indent++;
                WriteLine($"{name}_ok='true'");
                WriteLine($"{name}_value=\"${{BASH_REMATCH[0]-}}\"");
                WriteLine($"{name}_groups=(\"${{BASH_REMATCH[@]}}\")");
                _indent--;
                WriteLine("fi");
                _recordVariables.Add(name);
                return true;
            }

            case IntrinsicId.StringSplit:
            {
                var value = PrepareValue(intrinsic.Arguments[0], inFunction);
                var separator = PrepareValue(intrinsic.Arguments[1], inFunction);
                WriteLine($"{arrayDeclaration}-a {name}=()");
                if (_dialect.IsZsh)
                {
                    WriteLine($"{name}=(${{(s:{separator}:)${{:-{value}}}}})");
                }
                else
                {
                    WriteLine($"IFS={separator} read -r -a {name} <<< {value}");
                }
                _nativeArrayVariables[name] = name;
                return true;
            }

            case IntrinsicId.ProcessRun:
                EmitNativeProcessRun(name, intrinsic.Arguments, inFunction);
                _recordVariables.Add(name);
                return true;

            case IntrinsicId.ProcessPipeline:
                EmitNativeProcessPipeline(name, intrinsic.Arguments, inFunction);
                _recordVariables.Add(name);
                return true;

            case IntrinsicId.HttpGet:
            case IntrinsicId.HttpPost:
                EmitNativeHttp(name, intrinsic, inFunction);
                _recordVariables.Add(name);
                return true;

            default:
                return false;
        }
    }

    private bool EmitNativeMethodDeclaration(string name, IrMethodCallExpression method, bool inFunction)
    {
        if (method.Target is not IrIdentifierExpression identifier ||
            !_nativeArrayVariables.TryGetValue(SanitizeVariableName(identifier.Name), out var source))
        {
            return false;
        }
        var declaration = inFunction ? "local " : "declare ";
        switch (method.MethodName)
        {
            case "push":
                WriteLine($"{declaration}-a {name}=(\"${{{source}[@]}}\" {string.Join(" ", method.Arguments.Select(a => PrepareValue(a.Value, inFunction)))})");
                _nativeArrayVariables[name] = name;
                return true;
            case "map" when method.Arguments.Count == 1:
            case "filter" when method.Arguments.Count == 1:
            {
                var callback = PrepareValue(method.Arguments[0].Value, inFunction);
                WriteLine($"{declaration}-a {name}=()");
                var callbackResult = _names.Generated(TargetNameKind.Variable, "_callback");
                WriteLine($"{declaration}{callbackResult}=''");
                WriteLine($"for _item in \"${{{source}[@]}}\"; do");
                _indent++;
                WriteLine($"{callback} {callbackResult} \"$_item\"");
                if (method.MethodName == "map")
                {
                    WriteLine($"{name}+=(\"${{{callbackResult}-}}\")");
                }
                else
                {
                    WriteLine($"[[ -n \"${{{callbackResult}-}}\" && \"${{{callbackResult}-}}\" != false ]] && {name}+=(\"$_item\")");
                }
                _indent--;
                WriteLine("done");
                _nativeArrayVariables[name] = name;
                return true;
            }
            case "reduce" when method.Arguments.Count >= 1:
            {
                var callback = PrepareValue(method.Arguments[0].Value, inFunction);
                var seed = method.Arguments.Count > 1 ? PrepareValue(method.Arguments[1].Value, inFunction) : $"\"${{{source}[0]-}}\"";
                var start = method.Arguments.Count > 1 ? 0 : 1;
                WriteLine($"{declaration}{name}={seed}");
                var callbackResult = _names.Generated(TargetNameKind.Variable, "_callback");
                WriteLine($"{declaration}{callbackResult}=''");
                WriteLine($"for ((_i={start}; _i<${{#{source}[@]}}; _i++)); do");
                _indent++;
                WriteLine($"{callback} {callbackResult} \"${{{name}-}}\" \"${{{source}[_i]}}\"");
                WriteLine($"{name}=\"${{{callbackResult}-}}\"");
                _indent--;
                WriteLine("done");
                return true;
            }
            default:
                return false;
        }
    }

    private string PrepareRegex(IrExpression expression, bool inFunction)
    {
        var pattern = expression is IrLiteralExpression { Value: string literal }
            ? Escape.PosixSingleQuoted(literal
                .Replace("(?:", "(", StringComparison.Ordinal)
                .Replace("\\d", "[0-9]", StringComparison.Ordinal)
                .Replace("\\D", "[^0-9]", StringComparison.Ordinal)
                .Replace("\\s", "[[:space:]]", StringComparison.Ordinal)
                .Replace("\\S", "[^[:space:]]", StringComparison.Ordinal)
                .Replace("\\w", "[[:alnum:]_]", StringComparison.Ordinal)
                .Replace("\\W", "[^[:alnum:]_]", StringComparison.Ordinal))
            : PrepareValue(expression, inFunction);
        var name = $"__sushi_regex_{++_valueTempId}";
        WriteLine($"{(inFunction ? "local " : "")}{name}={pattern}");
        // Bash/Zsh interpret a quoted RHS of =~ literally; expand a temporary unquoted instead.
        return $"${{{name}-}}";
    }

    private void EmitNativeProcessRun(string name, IReadOnlyList<IrExpression> arguments, bool inFunction)
    {
        var declaration = inFunction ? "local " : "";
        var command = PrepareValue(arguments[0], inFunction);
        var commandParts = new List<string> { command };
        if (arguments.Count > 1 && arguments[1] is IrArrayLiteralExpression args)
        {
            commandParts.AddRange(args.Elements.Select(arg => PrepareValue(arg, inFunction)));
        }
        else if (arguments.Count > 1 && arguments[1] is IrIdentifierExpression argsIdentifier &&
                 _nativeArrayVariables.TryGetValue(SanitizeVariableName(argsIdentifier.Name), out var argsName))
        {
            commandParts.Add($"\"${{{argsName}[@]}}\"");
        }

        var invocation = string.Join(" ", commandParts);
        if (arguments.Count > 5 && arguments[5] is IrLiteralExpression { Value: int timeoutMs } && timeoutMs > 0)
        {
            invocation = $"timeout {Escape.PosixSingleQuoted((timeoutMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + "s")} {invocation}";
        }
        if (arguments.Count > 2 && arguments[2] is not IrLiteralExpression { Value: null })
        {
            invocation = $"(cd -- {PrepareValue(arguments[2], inFunction)} && {invocation})";
        }
        if (arguments.Count > 4 && arguments[4] is not IrLiteralExpression { Value: null })
        {
            invocation += $" <<< {PrepareValue(arguments[4], inFunction)}";
        }

        WriteLine($"{declaration}{name}_stderr_file=$(mktemp)");
        WriteLine($"if {name}_stdout=$({invocation} 2>\"${{{name}_stderr_file}}\"); then {name}_code=0; else {name}_code=$?; fi");
        WriteLine($"{declaration}{name}_stderr=$(<\"${{{name}_stderr_file}}\")");
        WriteLine($"rm -f -- \"${{{name}_stderr_file}}\"");
        WriteLine($"{declaration}{name}_ok=$([[ ${{{name}_code}} -eq 0 ]] && printf true || printf false)");
        WriteLine($"{declaration}{name}_command={command}");
        WriteLine($"{declaration}{name}_timedOut=$([[ ${{{name}_code}} -eq 124 ]] && printf true || printf false)");
        if (arguments.Count <= 6 || arguments[6] is not IrLiteralExpression { Value: true })
        {
            WriteLine($"(( {name}_code == 0 )) || exit \"${{{name}_code}}\"");
        }
    }

    private void EmitNativeProcessPipeline(string name, IReadOnlyList<IrExpression> arguments, bool inFunction)
    {
        var stagesExpression = arguments[0];
        IrArrayLiteralExpression? stages = stagesExpression as IrArrayLiteralExpression;
        if (stages == null && stagesExpression is IrIdentifierExpression identifier)
        {
            _arrayInitializers.TryGetValue(SanitizeVariableName(identifier.Name), out stages);
        }
        if (stages == null)
        {
            _context.Error(AmbiguousShapeCode, "Process pipeline stages must have a statically known array shape");
            return;
        }

        var commands = new List<string>();
        foreach (var stage in stages.Elements.OfType<IrObjectLiteralExpression>())
        {
            var commandProperty = stage.Properties.FirstOrDefault(property => property.Name == "command");
            var argsProperty = stage.Properties.FirstOrDefault(property => property.Name == "args");
            if (commandProperty == null) continue;
            var parts = new List<string> { PrepareValue(commandProperty.Value, inFunction) };
            if (argsProperty?.Value is IrArrayLiteralExpression stageArgs)
            {
                parts.AddRange(stageArgs.Elements.Select(arg => PrepareValue(arg, inFunction)));
            }
            commands.Add(string.Join(" ", parts));
        }
        var invocation = string.Join(" | ", commands);
        var declaration = inFunction ? "local " : "";
        WriteLine($"{declaration}{name}_stderr_file=$(mktemp)");
        WriteLine($"if {name}_stdout=$({invocation} 2>\"${{{name}_stderr_file}}\"); then {name}_code=0; else {name}_code=$?; fi");
        WriteLine($"{declaration}{name}_stderr=$(<\"${{{name}_stderr_file}}\")");
        WriteLine($"rm -f -- \"${{{name}_stderr_file}}\"");
        WriteLine($"{declaration}{name}_ok=$([[ ${{{name}_code}} -eq 0 ]] && printf true || printf false)");
        WriteLine($"{declaration}{name}_command='pipeline'");
        WriteLine($"{declaration}{name}_timedOut='false'");
        if (arguments.Count <= 5 || arguments[5] is not IrLiteralExpression { Value: true })
        {
            WriteLine($"(( {name}_code == 0 )) || exit \"${{{name}_code}}\"");
        }
    }

    private void EmitNativeHttp(string name, IrIntrinsicCallExpression intrinsic, bool inFunction)
    {
        var declaration = inFunction ? "local " : "";
        var url = PrepareValue(intrinsic.Arguments[0], inFunction);
        var method = intrinsic.Id == IntrinsicId.HttpPost ? "POST" : "GET";
        WriteLine($"{declaration}{name}_body_file=$(mktemp)");
        var curl = $"curl -sS -o \"${{{name}_body_file}}\" -w '%{{http_code}}' -X {method}";
        if (intrinsic.Id == IntrinsicId.HttpPost)
        {
            curl += $" -H 'Content-Type: '" + PrepareValue(intrinsic.Arguments[3], inFunction) + $" --data {PrepareValue(intrinsic.Arguments[1], inFunction)}";
        }
        WriteLine($"if {name}_status=$({curl} {url}); then {name}_transport_ok=true; else {name}_transport_ok=false; {name}_status=0; fi");
        WriteLine($"{declaration}{name}_body=$(<\"${{{name}_body_file}}\")");
        WriteLine($"rm -f -- \"${{{name}_body_file}}\"");
        WriteLine($"{declaration}{name}_ok=$([[ ${{{name}_transport_ok}} == true && ${{{name}_status}} -ge 200 && ${{{name}_status}} -lt 300 ]] && printf true || printf false)");
        WriteLine($"{declaration}{name}_url={url}");
        WriteLine($"{declaration}{name}_headers=''");
    }
}
