using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.Backends;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

namespace Sushi.Transpilation.Backends.PowerShell;

public sealed partial class PowerShellEmitter
{
    private void EmitExpressionStatement(IrExpression expression)
    {
        switch (expression)
        {
            case IrIntrinsicCallExpression intrinsicCall:
                WriteLine(EmitIntrinsicCommand(intrinsicCall));
                return;

            case IrCallExpression call:
                WriteLine(EmitCallCommand(call));
                return;

            case IrResolvedMethodCallExpression method:
                WriteLine(EmitNativeMethodCall(method));
                return;

            case IrAdapterCallExpression adapter:
                WriteLine(EmitNativeAdapterCall(adapter));
                return;

            case IrConversionExpression conversion:
                WriteLine($"$null = {EmitConversionExpression(conversion)}");
                return;

            case IrAssignmentExpression assignment:
                if (assignment.Operator == "=" && assignment.Value is IrConditionalExpression { IsSwitchExpression: true } switchExpression)
                {
                    EmitSwitchExpressionInto($"${SanitizeName(assignment.Target.Name)}", switchExpression);
                    return;
                }
                if (assignment.Operator == "=" && assignment.Value is IrIntrinsicCallExpression intrinsic &&
                    EmitNativeIntrinsicDeclaration(SanitizeName(assignment.Target.Name), intrinsic))
                {
                    SetKnownInteger(SanitizeName(assignment.Target.Name), false);
                    return;
                }
                WriteLine(EmitAssignmentExpression(assignment));
                return;

            case IrMemberAssignmentExpression assignment:
                WriteLine($"({EmitValueExpression(assignment.Target)}).{SanitizeName(assignment.MemberName)} {assignment.Operator} {EmitValueExpression(assignment.Value)}");
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

            case IrMethodCallExpression methodCall:
                if (methodCall.MethodName == "push" && methodCall.Target is IrIdentifierExpression targetIdentifier)
                {
                    var targetName = SanitizeName(targetIdentifier.Name);
                    WriteLine($"${targetName} = {EmitMethodCallExpression(methodCall)}");
                    return;
                }

                WriteLine($"$null = {EmitMethodCallExpression(methodCall)}");
                return;
        }

        _context.Error(UnsupportedEmitCode, $"Unsupported expression statement in PowerShell emitter: {expression.GetType().Name}");
    }

    private string EmitAssignmentExpression(IrAssignmentExpression assignment)
    {
        var name = SanitizeName(assignment.Target.Name);
        SetKnownInteger(name, assignment.Operator == "=" && IsDefinitelyInteger(assignment.Value));
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
        var callee = SanitizeFunctionName(call.Callee);
        var arguments = call.Arguments.Select(argument => EmitCommandArgument(argument.Value)).ToList();
        return arguments.Count > 0
            ? $"{callee} {string.Join(" ", arguments)}"
            : callee;
    }

    private string EmitCommandArgument(IrExpression expression)
    {
        var value = EmitValueExpression(expression);
        return expression is IrIdentifierExpression identifier &&
               (_nativeEnumValues.ContainsKey(identifier.Name) || _richEnumValues.ContainsKey(identifier.Name))
            ? $"({value})"
            : value;
    }

    private string EmitMethodCallExpression(IrMethodCallExpression call)
    {
        var target = EmitValueExpression(call.Target);
        var arguments = call.Arguments.Select(argument => EmitValueExpression(argument.Value)).ToList();
        return call.MethodName switch
        {
            "length" => $"@({target}).Count",
            "push" => $"@(@({target}) + @({string.Join(", ", arguments)}))",
            "map" when arguments.Count == 1 => $"@(@({target}) | ForEach-Object {{ & {arguments[0]} $_ }})",
            "filter" when arguments.Count == 1 => $"@(@({target}) | Where-Object {{ [bool](& {arguments[0]} $_) }})",
            "reduce" when arguments.Count >= 1 => EmitPowerShellReduce(target, arguments),
            "ordinal" => $"({target}).ordinal",
            "value" => $"({target}).value",
            _ => _context.ErrorAndReturn(AmbiguousShapeCode, $"Method '{call.MethodName}' requires a statically known native lowering", "$null")
        };
    }

    private static string EmitPowerShellReduce(string target, IReadOnlyList<string> arguments)
    {
        var initial = arguments.Count > 1 ? arguments[1] : "$items[0]";
        var start = arguments.Count > 1 ? "0" : "1";
        return $"$($items=@({target}); $acc={initial}; for($i={start}; $i -lt $items.Count; $i++) {{ $acc=& {arguments[0]} $acc $items[$i] }}; $acc)";
    }

    private string EmitConditionExpression(IrExpression expression)
    {
        if (expression is IrTruthinessExpression truthiness)
            return EmitTruthinessExpression(truthiness);

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
            IrIdentifierExpression identifier => EmitIdentifier(identifier),
            IrArrayLiteralExpression array => EmitArrayLiteral(array),
            IrObjectLiteralExpression obj => EmitObjectLiteral(obj),
            IrConversionExpression conversion => EmitConversionExpression(conversion),
            IrMemberAccessExpression member => EmitMemberAccess(member),
            IrIndexExpression index when IsStringExpression(index.Target) => EmitNativeStringIndex(index),
            IrIndexExpression index => $"({EmitValueExpression(index.Target)})[{EmitValueExpression(index.Index)}]",
            IrCollectionLengthExpression length => $"@({EmitValueExpression(length.Target)}).Count",
            IrSliceExpression slice => EmitNativeSlice(slice),
            IrUnaryExpression unary when unary.Operator is "!" =>
                $"(-not {EmitValueExpression(unary.Operand)})",
            IrTruthinessExpression truthiness => EmitTruthinessExpression(truthiness),
            IrUnaryExpression unary when unary.Operator is "-" or "+" =>
                $"({unary.Operator}{EmitValueExpression(unary.Operand)})",
            IrConditionalExpression conditional when conditional.IsSwitchExpression => EmitSwitchExpression(conditional),
            IrConditionalExpression conditional =>
                $"$(if ({EmitConditionExpression(conditional.Condition)}) {{ {EmitValueExpression(conditional.TrueExpression)} }} else {{ {EmitValueExpression(conditional.FalseExpression)} }})",
            IrBinaryExpression binary when binary.Operator == "+" =>
                $"({EmitValueExpression(binary.Left)} + {EmitValueExpression(binary.Right)})",
            IrBinaryExpression binary =>
                $"({EmitValueExpression(binary.Left)} {MapBinaryOperator(binary.Operator)} {EmitValueExpression(binary.Right)})",
            IrIntrinsicCallExpression intrinsicCall =>
                EmitIntrinsicValue(intrinsicCall),
            IrCallExpression call =>
                $"({EmitCallCommand(call)})",
            IrConstructionExpression construction => EmitNativeConstruction(construction),
            IrResolvedMethodCallExpression method => $"({EmitNativeMethodCall(method)})",
            IrAdapterCallExpression adapter => $"({EmitNativeAdapterCall(adapter)})",
            IrMethodCallExpression methodCall =>
                EmitMethodCallExpression(methodCall),
            IrAssignmentExpression assignment =>
                $"({EmitAssignmentExpression(assignment)}; ${SanitizeName(assignment.Target.Name)})",
            IrMemberAssignmentExpression assignment =>
                $"$(({EmitValueExpression(assignment.Target)}).{SanitizeMemberName(assignment.MemberName)} {assignment.Operator} {EmitValueExpression(assignment.Value)})",
            _ => "$null"
        };
    }

    private string EmitConversionExpression(IrConversionExpression conversion)
    {
        var value = EmitValueExpression(conversion.Value);
        return conversion.TargetType.Name switch
        {
            "string" => $"[string]({value})",
            "int" => $"[long]({value})",
            "float" => $"[double]({value})",
            _ => value
        };
    }

    private string EmitSwitchExpression(IrConditionalExpression root)
    {
        var arms = new List<(IrExpression Value, IrExpression Result)>();
        IrExpression current = root;
        while (current is IrConditionalExpression conditional && conditional.IsSwitchExpression)
        {
            if (!TryGetSwitchComparison(conditional.Condition, out var comparison) || comparison is null)
                break;
            arms.Add((comparison.Right, conditional.TrueExpression));
            current = conditional.FalseExpression;
        }
        if (arms.Count == 0)
            return $"$(if ({EmitConditionExpression(root.Condition)}) {{ {EmitValueExpression(root.TrueExpression)} }} else {{ {EmitValueExpression(root.FalseExpression)} }})";
        TryGetSwitchComparison(root.Condition, out var firstComparison);
        var value = firstComparison is not null ? EmitValueExpression(firstComparison.Left) : "$null";
        var builder = new System.Text.StringBuilder($"$(switch ({value}) {{ ");
        foreach (var arm in arms)
            builder.Append($"{EmitValueExpression(arm.Value)} {{ {EmitValueExpression(arm.Result)}; break }} ");
        var fallback = current is IrConditionalExpression fallbackConditional
            ? $"$(if ({EmitConditionExpression(fallbackConditional.Condition)}) {{ {EmitValueExpression(fallbackConditional.TrueExpression)} }} else {{ {EmitValueExpression(fallbackConditional.FalseExpression)} }})"
            : EmitValueExpression(current);
        builder.Append($"default {{ {fallback} }} }})");
        return builder.ToString();
    }

    private void EmitSwitchExpressionInto(string destination, IrConditionalExpression root)
    {
        var arms = new List<(IrExpression Value, IrExpression Result)>();
        IrExpression fallback = root;
        while (fallback is IrConditionalExpression conditional && conditional.IsSwitchExpression &&
               TryGetSwitchComparison(conditional.Condition, out var comparison) && comparison is not null)
        {
            arms.Add((comparison.Right, conditional.TrueExpression));
            fallback = conditional.FalseExpression;
        }

        if (arms.Count == 0)
        {
            WriteLine($"{destination} = {EmitValueExpression(root)}");
            return;
        }

        TryGetSwitchComparison(root.Condition, out var firstComparison);
        var selector = firstComparison is null ? "$null" : EmitValueExpression(firstComparison.Left);
        WriteLine($"{destination} = switch ({selector}) {{");
        _indent++;
        foreach (var arm in arms)
        {
            WriteLine($"{EmitValueExpression(arm.Value)} {{");
            _indent++;
            WriteLine(EmitValueExpression(arm.Result));
            WriteLine("break");
            _indent--;
            WriteLine("}");
        }
        WriteLine("default {");
        _indent++;
        WriteLine(EmitValueExpression(fallback));
        _indent--;
        WriteLine("}");
        _indent--;
        WriteLine("}");
    }

    private static bool TryGetSwitchComparison(IrExpression condition, out IrBinaryExpression? comparison)
    {
        comparison = condition switch
        {
            IrBinaryExpression { Operator: "==" } binary => binary,
            IrTruthinessExpression { Operand: IrBinaryExpression { Operator: "==" } binary } => binary,
            _ => null
        };
        return comparison is not null;
    }

    private string EmitNativeSlice(IrSliceExpression slice) =>
        EmitNativeSlice(slice.Target, slice.Start, slice.End);

    private string EmitNativeSlice(IrExpression targetExpression, IrExpression? startExpression, IrExpression? endExpression)
    {
        var target = EmitValueExpression(targetExpression);
        var start = EmitSliceIndex(startExpression);
        var isArray = targetExpression is IrIdentifierExpression identifier &&
                      _arrayInitializers.ContainsKey(SanitizeName(identifier.Name));
        if (isArray)
            return EmitNativeArraySlice(target, startExpression, endExpression);
        return EmitNativeStringSlice(targetExpression, startExpression, endExpression);
    }

    private string EmitNativeArraySlice(string target, IrExpression? startExpression, IrExpression? endExpression)
    {
        var start = startExpression == null ? "$null" : EmitValueExpression(startExpression);
        var end = endExpression == null ? "$null" : EmitValueExpression(endExpression);
        return $"@(& {{ param([object[]]$items, $start, $end) if ($null -eq $start) {{ $start = 0 }} else {{ $start = [int]$start }}; if ($null -eq $end) {{ $end = $items.Count }} else {{ $end = [int]$end }}; if ($start -lt 0) {{ $start += $items.Count }}; if ($end -lt 0) {{ $end += $items.Count }}; $start = [Math]::Min($items.Count, [Math]::Max(0, $start)); $end = [Math]::Min($items.Count, [Math]::Max(0, $end)); if ($end -le $start) {{ return @() }}; $items[$start..($end - 1)] }} @({target}) {start} {end})";
    }

    private string EmitNativeStringIndex(IrIndexExpression index)
    {
        var target = EmitValueExpression(index.Target);
        var position = EmitValueExpression(index.Index);
        return $"(& {{ param([string]$text, [int]$index) if ($index -lt 0) {{ $index += $text.Length }}; if ($index -lt 0 -or $index -ge $text.Length) {{ throw 'Sushi: string index out of range' }}; $text[$index] }} {target} {position})";
    }

    private string EmitNativeStringSlice(IrExpression targetExpression, IrExpression? startExpression, IrExpression? endExpression)
    {
        var target = EmitValueExpression(targetExpression);
        var start = startExpression == null ? "$null" : EmitValueExpression(startExpression);
        var end = endExpression == null ? "$null" : EmitValueExpression(endExpression);
        return $"(& {{ param([string]$text, $start, $end) if ($null -eq $start) {{ $start = 0 }} else {{ $start = [int]$start }}; if ($null -eq $end) {{ $end = $text.Length }} else {{ $end = [int]$end }}; if ($start -lt 0) {{ $start += $text.Length }}; if ($end -lt 0) {{ $end += $text.Length }}; $start = [Math]::Min($text.Length, [Math]::Max(0, $start)); $end = [Math]::Min($text.Length, [Math]::Max(0, $end)); if ($end -lt $start) {{ $end = $start }}; $text.Substring($start, $end - $start) }} {target} {start} {end})";
    }

    private string EmitSliceIndex(IrExpression? expression)
    {
        if (expression is null or IrLiteralExpression { Value: 0 }) return "0";
        return $"[int]({EmitValueExpression(expression)})";
    }

    private static bool IsStringExpression(IrExpression expression) => expression switch
    {
        IrIdentifierExpression identifier => identifier.StaticType.Name?.Equals("string", StringComparison.OrdinalIgnoreCase) == true,
        IrLiteralExpression { Value: string } => true,
        IrMemberAccessExpression member => member.ValueType.Name?.Equals("string", StringComparison.OrdinalIgnoreCase) == true,
        IrIntrinsicCallExpression intrinsic => intrinsic.ReturnType.Name?.Equals("string", StringComparison.OrdinalIgnoreCase) == true,
        _ => false
    };

    private string EmitIdentifier(IrIdentifierExpression identifier)
    {
        if (_nativeEnumValues.TryGetValue(identifier.Name, out var value))
            return $"[{value.Type}]::{value.Value}";
        if (_richEnumValues.TryGetValue(identifier.Name, out var richValue))
            return $"[{richValue.Type}]::{richValue.Value}";
        return $"${SanitizeName(identifier.Name)}";
    }

    private string EmitMemberAccess(IrMemberAccessExpression member)
    {
        if (member.Target is IrIdentifierExpression identifier &&
            _nativeEnumValues.TryGetValue(identifier.Name, out var value))
        {
            var enumValue = $"[{value.Type}]::{value.Value}";
            return member.MemberName switch
            {
                "_name" => $"(({enumValue}).ToString())",
                "_value" => $"([int]({enumValue}))",
                "_ord" => $"({value.Ordinal.ToString(CultureInfo.InvariantCulture)})",
                _ => $"({enumValue}).{SanitizeMemberName(member.MemberName)}"
            };
        }
        if (member.Target is IrIdentifierExpression variable &&
            _nativeEnumVariableTypes.TryGetValue(SanitizeName(variable.Name), out var enumType))
        {
            var enumValue = EmitValueExpression(variable);
            return member.MemberName switch
            {
                "_name" => $"(({enumValue}).ToString())",
                "_value" => $"([int]({enumValue}))",
                "_ord" => $"([Array]::IndexOf([{enumType}]::GetEnumValues(), {enumValue}))",
                _ => $"({enumValue}).{SanitizeMemberName(member.MemberName)}"
            };
        }
        if (IsRichEnumTarget(member.Target))
        {
            var enumValue = EmitMemberTarget(member.Target);
            return member.MemberName == NativeObjectMetadata.EnumValue
                ? $"{enumValue}.{SanitizeMemberName(NativeObjectMetadata.EnumOrdinal)}"
                : $"{enumValue}.{SanitizeMemberName(member.MemberName)}";
        }
        return $"{EmitMemberTarget(member.Target)}.{SanitizeMemberName(member.MemberName)}";
    }

    private string EmitMemberTarget(IrExpression target) => target switch
    {
        IrIdentifierExpression identifier when _nativeEnumValues.ContainsKey(identifier.Name) || _richEnumValues.ContainsKey(identifier.Name) =>
            $"({EmitValueExpression(target)})",
        IrIdentifierExpression => EmitValueExpression(target),
        _ => $"({EmitValueExpression(target)})"
    };

    private bool TryGetNativeEnumType(IrExpression expression, out string type)
    {
        if (expression is IrIdentifierExpression identifier && _nativeEnumValues.TryGetValue(identifier.Name, out var value))
        {
            type = value.Type;
            return true;
        }
        type = "";
        return false;
    }

    private bool TryGetRichEnumType(IrExpression expression, out string type)
    {
        if (expression is IrIdentifierExpression identifier && _richEnumValues.TryGetValue(identifier.Name, out var value))
        {
            type = value.Type;
            return true;
        }
        type = "";
        return false;
    }

    private bool IsRichEnumTarget(IrExpression expression) => expression switch
    {
        IrIdentifierExpression identifier when _richEnumValues.ContainsKey(identifier.Name) => true,
        IrIdentifierExpression identifier when _richEnumVariableTypes.ContainsKey(SanitizeName(identifier.Name)) => true,
        IrIdentifierExpression { Name: "this" } when _currentRichEnumReceiver != null => true,
        _ => false
    };

    private string? GetRichEnumReceiver(string functionName)
    {
        foreach (var richType in _richEnumValues.Keys.Select(key => key[..key.LastIndexOf('_')]).Distinct(StringComparer.Ordinal))
        {
            if (functionName.StartsWith($"{NativeObjectMetadata.MethodPrefix}{richType}_", StringComparison.Ordinal) ||
                functionName.StartsWith($"__sushi_adapter_{richType}_", StringComparison.Ordinal))
                return richType;
        }
        return null;
    }

    private string EmitTruthinessExpression(IrTruthinessExpression expression)
    {
        var type = expression.OperandType.Name;
        if (type == "null") return "$false";
        var value = EmitValueExpression(expression.Operand);
        if (type == "array" || type == "object" || IsNamedObjectType(expression.OperandType))
            return $"($null -ne [object]({value}))";
        return type switch
        {
            "bool" => value,
            "int" or "float" => "$true",
            "string" => $"(-not [string]::IsNullOrEmpty({value}))",
            _ => "$false"
        };
    }

    private static bool IsNamedObjectType(IrTypeRef type) =>
        type.Kind == IrTypeKind.Primitive && type.Name is not null &&
        type.Name is not ("string" or "int" or "float" or "bool" or "array" or "object" or "any");

    private bool IsDefinitelyFloat(IrExpression expression)
    {
        return expression switch
        {
            IrIdentifierExpression identifier when identifier.StaticType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true => true,
            IrLiteralExpression literal => literal.Value is float or double or decimal,
            IrConversionExpression conversion => conversion.TargetType.Name == "float",
            IrIdentifierExpression identifier => _knownFloatVariables.Contains(SanitizeName(identifier.Name)),
            IrUnaryExpression unary when unary.Operator is "+" or "-" => IsDefinitelyFloat(unary.Operand),
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                IsDefinitelyFloat(binary.Left) || IsDefinitelyFloat(binary.Right),
            IrCallExpression call => call.Callee.Equals("float", StringComparison.OrdinalIgnoreCase) ||
                                      call.Callee.Equals("double", StringComparison.OrdinalIgnoreCase) ||
                                      call.Callee.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
                                      _floatReturningFunctions.Contains(call.Callee),
            IrResolvedMethodCallExpression method => _floatReturningFunctions.Contains(method.Callee),
            IrAdapterCallExpression adapter => _floatReturningFunctions.Contains(adapter.Callee),
            IrMemberAccessExpression member => member.ValueType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };
    }

    private bool IsDefinitelyInteger(IrExpression expression)
    {
        return expression switch
        {
            IrIdentifierExpression identifier when identifier.StaticType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true => true,
            IrLiteralExpression literal => literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong,
            IrConversionExpression conversion => conversion.TargetType.Name == "int",
            IrIdentifierExpression identifier => _knownIntegerVariables.Contains(SanitizeName(identifier.Name)),
            IrUnaryExpression unary when unary.Operator is "+" or "-" => IsDefinitelyInteger(unary.Operand),
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                IsDefinitelyInteger(binary.Left) && IsDefinitelyInteger(binary.Right),
            IrCallExpression call => _integerReturningFunctions.Contains(call.Callee),
            IrResolvedMethodCallExpression method => _integerReturningFunctions.Contains(method.Callee),
            IrAdapterCallExpression adapter => _integerReturningFunctions.Contains(adapter.Callee),
            _ => false
        };
    }

    private void SetKnownInteger(string name, bool isInteger)
    {
        if (isInteger)
        {
            _knownIntegerVariables.Add(name);
        }
        else
        {
            _knownIntegerVariables.Remove(name);
        }
    }

    private void SetKnownFloat(string name, bool isFloat)
    {
        if (isFloat) _knownFloatVariables.Add(name);
        else _knownFloatVariables.Remove(name);
    }

    private string EmitArrayLiteral(IrArrayLiteralExpression expression)
    {
        if (expression.Elements.Count == 0)
        {
            return "@()";
        }

        var values = string.Join(", ", expression.Elements.Select(EmitValueExpression));
        return $"@({values})";
    }

    private string EmitObjectLiteral(IrObjectLiteralExpression expression)
    {
        if (expression.Properties.Count == 0)
        {
            return "([PSCustomObject]@{})";
        }

        var entries = expression.Properties.Select(property =>
            $"{Escape.PowerShellSingleQuoted(property.Name)} = {EmitValueExpression(property.Value)}").ToList();
        var complex = entries.Count >= 5 || expression.Properties.Any(property => property.Name.StartsWith("_", StringComparison.Ordinal));
        if (!complex)
            return $"([PSCustomObject]@{{ {string.Join("; ", entries)} }})";

        var innerIndent = new string(' ', (_indent + 1) * 4);
        var outerIndent = new string(' ', _indent * 4);
        return $"([PSCustomObject][ordered]@{{\n{innerIndent}{string.Join($"\n{innerIndent}", entries)}\n{outerIndent}}})";
    }

    private void EmitContractCheckForValue(IrTypeRef type, string valueExpression, string context)
    {
        // Parameter annotations provide native PowerShell typing; statically
        // provable mismatches are diagnosed before emission.
    }

    private string EmitNativeConstruction(IrConstructionExpression construction)
    {
        if (_nativeClassNames.TryGetValue(construction.TypeName, out var className))
        {
            var arguments = string.Join(", ", construction.Arguments.Select(argument => EmitValueExpression(argument.Value)));
            return $"[{className}]::new({arguments})";
        }
        return $"({SanitizeFunctionName(construction.ConstructorName)} {string.Join(" ", construction.Arguments.Select(argument => EmitValueExpression(argument.Value)))})";
    }

    private string EmitNativeMethodCall(IrResolvedMethodCallExpression method)
    {
        if (_nativeClassNames.ContainsKey(method.TypeName) && _nativeMethods.TryGetValue(method.Callee, out var declaration))
        {
            var arguments = string.Join(", ", method.Arguments.Select(argument => EmitValueExpression(argument.Value)));
            return $"{EmitMemberTarget(method.Target)}.{SanitizeMemberName(NativeMethodName(declaration.Name))}({arguments})";
        }
        return EmitCallCommand(method.AsFunctionCall());
    }

    private string EmitNativeAdapterCall(IrAdapterCallExpression adapter)
    {
        if (_nativeClassNames.ContainsKey(adapter.SourceTypeName) && _nativeMethods.TryGetValue(adapter.Callee, out var declaration))
            return $"{EmitMemberTarget(adapter.Value)}.{SanitizeMemberName(NativeMethodName(declaration.Name))}()";
        return EmitCallCommand(adapter.AsFunctionCall());
    }

    private string EmitPowerShellParameter(IrFunctionParameter parameter, string? ownerClass = null)
    {
        var annotation = EmitPowerShellType(parameter.DeclaredType, ownerClass);
        return $"{annotation}${SanitizeName(parameter.Name)}";
    }

    private string EmitPowerShellType(IrTypeRef type, string? ownerClass = null)
    {
        return type.Kind switch
        {
            IrTypeKind.Structural => "[pscustomobject]",
            IrTypeKind.Primitive when type.Name is not null && _nativeClassNames.TryGetValue(type.Name, out var className) &&
                                        !IsCyclicClassReference(ownerClass, type.Name) => $"[{className}]",
            IrTypeKind.Primitive when type.Name is not null && _nativeEnumNames.TryGetValue(type.Name, out var enumName) => $"[{enumName}]",
            IrTypeKind.Primitive => type.Name?.ToLowerInvariant() switch
            {
                "int" => "[long]",
                "float" => "[double]",
                "bool" => "[bool]",
                "string" => "[string]",
                "array" => "[object[]]",
                "object" => "[object]",
                _ => "[object]"
            },
            _ => "[object]"
        };
    }

    private bool IsCyclicClassReference(string? ownerClass, string targetClass)
    {
        if (ownerClass == null || ownerClass == targetClass) return false;
        return DependsOn(targetClass, ownerClass, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool DependsOn(string current, string target, HashSet<string> seen)
    {
        if (!seen.Add(current) || !_nativeClasses.TryGetValue(current, out var declaration)) return false;
        foreach (var reference in ReferencedClassTypes(declaration))
            if (reference == target || DependsOn(reference, target, seen)) return true;
        return false;
    }

    private static string EncodeRuntimeType(IrTypeRef type)
    {
        if (type.Kind == IrTypeKind.Structural)
        {
            return "object";
        }

        return type.Name ?? "any";
    }

    private static string EncodeStructuralSpec(IrTypeRef type)
    {
        if (type.Kind != IrTypeKind.Structural || type.StructuralFields.Count == 0)
        {
            return "";
        }

        return string.Join(
            ",",
            type.StructuralFields.Select(field =>
                $"{field.Name}:{EncodeRuntimeType(field.Type)}:{(field.Optional ? "opt" : "req")}"));
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
            int or long or double or float or decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            _ => Escape.PowerShellSingleQuoted(value.ToString() ?? "")
        };
    }

    private void WriteLine(string text)
    {
        _document.Line(text);
    }

    private string SanitizeName(string name) =>
        name == "this" && _fallbackReceiverName != null
            ? _fallbackReceiverName
            : _names.Source(TargetNameKind.Variable, name);

    // Members live in their own PowerShell namespace. Keeping them separate
    // avoids a local variable allocation changing a public property spelling.
    private static string SanitizeMemberName(string name) => TargetNameAllocator.Normalize(name, "member");

    private string SanitizeFunctionName(string name)
    {
        if (name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal))
        {
            if (_generatedFunctionNames.TryGetValue(name, out var existingMethodName)) return existingMethodName;
            var readableName = name[NativeObjectMetadata.MethodPrefix.Length..];
            var allocatedMethodName = _names.Generated(TargetNameKind.Function, readableName);
            _generatedFunctionNames[name] = allocatedMethodName;
            return allocatedMethodName;
        }
        if (!name.StartsWith("__sushi_", StringComparison.Ordinal))
            return _names.Source(TargetNameKind.Function, name);

        if (_generatedFunctionNames.TryGetValue(name, out var existing)) return existing;
        var preferred = name switch
        {
            var value when value.StartsWith("__sushi_new_", StringComparison.Ordinal) =>
                value["__sushi_new_".Length..].ToLowerInvariant() + "_new",
            var value when value.StartsWith("__sushi_method_", StringComparison.Ordinal) =>
                value["__sushi_method_".Length..].ToLowerInvariant(),
            var value when value.StartsWith("__sushi_adapter_", StringComparison.Ordinal) =>
                value["__sushi_adapter_".Length..].ToLowerInvariant(),
            var value when value.StartsWith("__sushi_lambda_", StringComparison.Ordinal) =>
                "_lambda_" + value["__sushi_lambda_".Length..],
            _ => "_s_" + name["__sushi_".Length..]
        };
        var allocated = _names.Generated(TargetNameKind.Function, preferred);
        _generatedFunctionNames[name] = allocated;
        return allocated;
    }
}
