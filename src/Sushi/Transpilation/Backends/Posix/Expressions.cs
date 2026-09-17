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
    private string PrepareValue(IrExpression expression, bool inFunction)
    {
        switch (expression)
        {
            case IrTruthinessExpression:
            case IrUnaryExpression { Operator: "!" }:
            case IrBinaryExpression { Operator: "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" }:
            {
                return PrepareBooleanValue(expression, inFunction);
            }
            case IrLiteralExpression or IrIdentifierExpression:
                return EmitValueExpression(expression);

            case IrConversionExpression conversion:
                return PrepareConversionExpression(conversion, inFunction);

            case IrArrayLiteralExpression array:
            {
                var elements = array.Elements.Select(element => PrepareValue(element, inFunction)).ToList();
                var result = DeclareUninitializedTemp(inFunction);
                WriteLine($"{(inFunction ? "local " : "declare ")}-a {result}=({string.Join(" ", elements)})");
                _nativeArrayVariables[result] = result;
                return $"\"${{{result}[@]}}\"";
            }

            case IrObjectLiteralExpression obj:
            {
                var result = DeclareUninitializedTemp(inFunction);
                var entries = obj.Properties.Select(property =>
                    $"[{Escape.PosixSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}").ToList();
                EmitAssociativeObject(result, entries, inFunction ? "local " : "declare ", false);
                _nativeObjectVariables.Add(result);
                return Escape.PosixSingleQuoted(result);
            }

            case IrMemberAccessExpression member:
            {
                if (member.Target is IrConstructionExpression construction)
                {
                    var objectName = PrepareConstructionReference(construction, inFunction);
                    return $"\"${{{objectName}[{EmitObjectSubscript(member.MemberName)}]-}}\"";
                }
                if (member.Target is IrIntrinsicCallExpression recordIntrinsic)
                {
                    var recordName = $"__sushi_record_{++_valueTempId}";
                    if (EmitNativeIntrinsicDeclaration(recordName, recordIntrinsic, inFunction))
                    {
                        return $"\"${{{recordName}_{SanitizeVariableName(member.MemberName)}-}}\"";
                    }
                }
                if (member.Target is IrIdentifierExpression directIdentifier)
                {
                    var sourceName = SanitizeVariableName(directIdentifier.Name);
                    var directName = ResolveNativeObjectName(sourceName);
                    if (_dialect.IsZsh && _zshReadOnlyObjectParameters.Contains(sourceName) &&
                        _zshObjectParameterNames.TryGetValue(sourceName, out var readOnlyReference))
                    {
                        return "\"${${(@P)" + readOnlyReference + "}[" + EmitObjectSubscript(member.MemberName) + "]-}\"";
                    }
                    if (_nativeObjectVariables.Contains(directName))
                    {
                        return $"\"${{{directName}[{EmitObjectSubscript(member.MemberName)}]-}}\"";
                    }
                    if (_recordVariables.Contains(directName))
                    {
                        return $"\"${{{directName}_{SanitizeVariableName(member.MemberName)}-}}\"";
                    }
                }

                _context.Error(AmbiguousShapeCode, $"Member '{member.MemberName}' requires a statically known object shape");
                return "''";
            }

            case IrCollectionLengthExpression length when length.Target is IrIdentifierExpression identifier &&
                                                           _nativeArrayVariables.TryGetValue(SanitizeVariableName(identifier.Name), out var lengthArrayName):
                return $"\"${{#{lengthArrayName}[@]}}\"";

            case IrSliceExpression slice:
            {
                var targetName = slice.Target is IrIdentifierExpression targetIdentifier
                    ? SanitizeVariableName(targetIdentifier.Name)
                    : DeclareTemp(PrepareValue(slice.Target, inFunction), inFunction);
                var isArray = _nativeArrayVariables.ContainsKey(targetName);
                if (!isArray)
                    return PrepareNativeStringSlice(targetName, slice, inFunction);
                return PrepareNativeArraySlice(targetName, slice, inFunction);
            }

            case IrIndexExpression index when IsStringExpression(index.Target):
                return PrepareNativeStringIndex(index, inFunction);

            case IrIndexExpression index when index.Target is IrIdentifierExpression identifier &&
                                             _nativeArrayVariables.TryGetValue(SanitizeVariableName(identifier.Name), out var arrayName):
            {
                if (arrayName.Length > 0 && IsDefinitelyInteger(index.Index))
                {
                    var directIndex = index.Index switch
                    {
                        IrIdentifierExpression indexIdentifier => SanitizeVariableName(indexIdentifier.Name),
                        IrLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
                        _ => EmitArithmeticExpression(index.Index)
                    };
                    return $"\"${{{arrayName}[{directIndex}]-}}\"";
                }
                var indexValue = DeclareTemp(PrepareValue(index.Index, inFunction), inFunction);
                if (arrayName.Length > 0)
                {
                    WriteLine($"if (( {indexValue} < 0 )); then {indexValue}=$(( ${{#{arrayName}[@]}} + {indexValue} )); fi");
                    var directResult = DeclareTemp($"\"${{{arrayName}[{indexValue}]-}}\"", inFunction);
                    return $"\"${{{directResult}-}}\"";
                }

                _context.Error(AmbiguousShapeCode, "Array index requires native array storage");
                return "''";
            }

            case IrIndexExpression index:
                _context.Error(AmbiguousShapeCode, "Indexing requires a statically known native array or object shape", index.Origin?.Line ?? 1, index.Origin?.Column ?? 1);
                return "''";

            case IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%":
                return PrepareArithmetic(binary, inFunction);

            case IrUnaryExpression unary when unary.Operator is "+" or "-":
            {
                var operand = PrepareValue(unary.Operand, inFunction);
                var operandTemp = DeclareTemp(operand, inFunction);
                return $"$(( {unary.Operator}{operandTemp} ))";
            }

            case IrCallExpression call when
                !call.Callee.StartsWith("__sushi_new_", StringComparison.Ordinal) &&
                (_functions.ContainsKey(call.Callee) ||
                 !call.Callee.StartsWith("__sushi_", StringComparison.Ordinal) ||
                 call.Callee.StartsWith("__sushi_method_", StringComparison.Ordinal)):
            {
                var arguments = new List<string>();
                _functions.TryGetValue(call.Callee, out var function);
                if (function != null && FunctionReturnsValue(function))
                {
                    var result = DeclareUninitializedTemp(inFunction);
                    EmitCallInto(result, call, function, inFunction);
                    if (_integerReturningFunctions.Contains(call.Callee)) _knownIntegerVariables.Add(result);
                    return $"\"${{{result}-}}\"";
                }
                for (var index = 0; index < call.Arguments.Count; index++)
                {
                    var argument = call.Arguments[index].Value;
                    var parameter = function != null && index < function.Parameters.Count
                        ? function.Parameters[index]
                        : null;
                    if (argument is IrObjectLiteralExpression objectLiteral &&
                        parameter?.DeclaredType.Kind == IrTypeKind.Structural)
                    {
                        foreach (var field in parameter.DeclaredType.StructuralFields)
                        {
                            var property = objectLiteral.Properties.FirstOrDefault(item => item.Name == field.Name);
                            arguments.Add(property == null ? "''" : PrepareValue(property.Value, inFunction));
                        }
                    }
                    else if (argument is IrConstructionExpression construction &&
                             parameter != null && IsNativeObjectType(parameter.DeclaredType))
                    {
                        arguments.Add(Escape.PosixSingleQuoted(PrepareConstructionReference(construction, inFunction)));
                    }
                    else if (argument is IrIdentifierExpression aggregateIdentifier &&
                             parameter?.DeclaredType.Kind == IrTypeKind.Structural)
                    {
                        var aggregateName = SanitizeVariableName(aggregateIdentifier.Name);
                        foreach (var field in parameter.DeclaredType.StructuralFields)
                        {
                            arguments.Add(_nativeObjectVariables.Contains(aggregateName)
                                ? $"\"${{{aggregateName}[{EmitObjectSubscript(field.Name)}]-}}\""
                                : $"\"${{{aggregateName}_{SanitizeVariableName(field.Name)}-}}\"");
                        }
                    }
                    else if (argument is IrIdentifierExpression aggregateIdentifier2 &&
                             parameter != null && (parameter.DeclaredType.Name == "array" || IsNativeObjectType(parameter.DeclaredType)))
                    {
                        arguments.Add(Escape.PosixSingleQuoted(ResolveNativeObjectName(SanitizeVariableName(aggregateIdentifier2.Name))));
                    }
                    else
                    {
                        arguments.Add(PrepareValue(argument, inFunction));
                    }
                }
                var command = arguments.Count > 0
                    ? $"{SanitizeFunctionName(call.Callee)} {string.Join(" ", arguments)}"
                    : SanitizeFunctionName(call.Callee);
                WriteLine(command);
                return "''";
            }

            case IrResolvedMethodCallExpression method:
                return PrepareValue(method.AsFunctionCall(), inFunction);

            case IrAdapterCallExpression adapter:
                return PrepareValue(adapter.AsFunctionCall(), inFunction);

            case IrConstructionExpression:
                _context.Error(AmbiguousShapeCode, "Constructed objects must be assigned to a variable before use on Bash/Zsh targets.");
                return "''";

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id is IntrinsicId.ProcessRun or IntrinsicId.ProcessPipeline or IntrinsicId.HttpGet or IntrinsicId.HttpPost:
                return EmitAggregateIntrinsicFallback(intrinsic);

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id is IntrinsicId.IoWriteText or IntrinsicId.EnvSet or IntrinsicId.ProcessExit or IntrinsicId.OsChdir:
            {
                var command = intrinsic.Id switch
                {
                    IntrinsicId.IoWriteText => EmitIoWriteText(
                        PrepareValue(intrinsic.Arguments[0], inFunction),
                        PrepareValue(intrinsic.Arguments[1], inFunction),
                        PrepareValue(intrinsic.Arguments[2], inFunction)),
                    IntrinsicId.EnvSet => EmitEnvSet(
                        PrepareValue(intrinsic.Arguments[0], inFunction),
                        PrepareValue(intrinsic.Arguments[1], inFunction)),
                    IntrinsicId.ProcessExit => $"exit {PrepareValue(intrinsic.Arguments[0], inFunction)}",
                    _ => $"cd -- {PrepareValue(intrinsic.Arguments[0], inFunction)}"
                };
                WriteLine(command);
                return "''";
            }

            case IrIntrinsicCallExpression intrinsic when IsInlineStringIntrinsic(intrinsic.Id):
                return PrepareStringIntrinsic(intrinsic, inFunction);

            case IrConditionalExpression conditional:
            {
                var result = DeclareTemp("''", inFunction);
                var condition = PrepareCondition(conditional.Condition, inFunction);
                WriteLine($"if {condition}; then");
                _indent++;
                var whenTrue = PrepareValue(conditional.TrueExpression, inFunction);
                WriteLine($"{result}={whenTrue}");
                _indent--;
                WriteLine("else");
                _indent++;
                var whenFalse = PrepareValue(conditional.FalseExpression, inFunction);
                WriteLine($"{result}={whenFalse}");
                _indent--;
                WriteLine("fi");
                return $"\"${{{result}-}}\"";
            }

            default:
                return CaptureValue(EmitValueExpression(expression), inFunction);
        }
    }

    private static bool IsBooleanValueExpression(IrExpression expression) => expression switch
    {
        IrTruthinessExpression => true,
        IrUnaryExpression { Operator: "!" } => true,
        IrBinaryExpression { Operator: "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" } => true,
        _ => false
    };

    private void EmitBooleanAssignment(string name, IrExpression expression, bool inFunction)
    {
        WriteLine($"{(inFunction ? "local " : string.Empty)}{name}='false'");
        var condition = PrepareCondition(expression, inFunction);
        WriteLine($"if {condition}; then {name}='true'; fi");
    }

    private void EmitBooleanOutput(IrExpression expression)
    {
        var condition = PrepareCondition(expression, inFunction: true);
        if (_dialect.IsZsh)
        {
            WriteLine($"if {condition}; then : ${{(P){_currentOutputName}::='true'}}; else : ${{(P){_currentOutputName}::='false'}}; fi");
        }
        else
        {
            WriteLine($"if {condition}; then {_currentOutputName}='true'; else {_currentOutputName}='false'; fi");
        }
    }

    private string PrepareBooleanValue(IrExpression expression, bool inFunction)
    {
        var result = DeclareTemp("'false'", inFunction);
        var condition = PrepareCondition(expression, inFunction);
        WriteLine($"if {condition}; then {result}='true'; fi");
        return $"\"${{{result}-}}\"";
    }

    private void EmitNativeObject(string name, IrObjectLiteralExpression obj, bool inFunction)
    {
        var properties = MetadataProperties(obj.Properties);
        var entries = (_dialect.IsZsh
            ? properties.Select(property =>
                $"[{Escape.PosixSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
            : properties.Select(property =>
                $"[{Escape.PosixSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")).ToList();
        EmitAssociativeObject(name, entries, inFunction ? "local " : "declare ", false);
        _nativeObjectVariables.Add(name);
    }

    private IReadOnlyList<IrObjectProperty> MetadataProperties(IReadOnlyList<IrObjectProperty> properties)
        => properties.Where(property => !property.Name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal)).ToList();

    private void EmitAssociativeObject(string name, IReadOnlyList<string> entries, string declaration, bool assignmentOnly)
    {
        var multiline = entries.Count >= 8 || entries.Any(entry =>
            entry.Contains("['_", StringComparison.Ordinal) || entry.Contains("'_m_", StringComparison.Ordinal));
        var prefix = assignmentOnly ? $"{name}=(" : $"{declaration}-A {name}=(";
        if (!multiline)
        {
            WriteLine($"{prefix}{string.Join(" ", entries)})");
            return;
        }

        WriteLine(prefix);
        _indent++;
        foreach (var entry in entries) WriteLine(entry);
        _indent--;
        WriteLine(")");
    }

    private string PrepareArithmetic(IrBinaryExpression binary, bool inFunction)
    {
        if (binary.Operator == "+" && ContainsStringLiteral(binary) && TryEmitInlineString(binary, out var inlineString))
        {
            return $"\"{inlineString}\"";
        }

        if (CanEmitInlineInteger(binary))
        {
            return $"$(( {EmitInlineInteger(binary)} ))";
        }

        if (IsDefinitelyFloat(binary))
        {
            return EmitAwkFloatExpression(binary);
        }

        var leftExpression = PrepareValue(binary.Left, inFunction);
        var rightExpression = PrepareValue(binary.Right, inFunction);
        var left = DeclareTemp(leftExpression, inFunction);
        var right = DeclareTemp(rightExpression, inFunction);
        var result = DeclareTemp("''", inFunction);

        if (binary.Operator == "+" && !IsDefinitelyInteger(binary))
        {
            WriteLine($"{result}=\"${{{left}-}}${{{right}-}}\"");
            return $"\"${{{result}-}}\"";
        }
        WriteLine($"{result}=$(( {left} {binary.Operator} {right} ))");
        _knownIntegerVariables.Add(result);
        return $"\"${{{result}-}}\"";
    }

    private bool TryEmitInlineString(IrExpression expression, out string value)
    {
        switch (expression)
        {
            case IrLiteralExpression { Value: string text }:
                value = EscapePosixDoubleQuotedContent(text);
                return true;
            case IrLiteralExpression { Value: char character }:
                value = EscapePosixDoubleQuotedContent(character.ToString());
                return true;
            case IrIdentifierExpression identifier:
                value = _positionalParameterReferences.TryGetValue(SanitizeVariableName(identifier.Name), out var positional)
                    ? $"${{{positional}:-}}"
                    : $"${{{SanitizeVariableName(identifier.Name)}:-}}";
                return true;
            case IrMemberAccessExpression { Target: IrIdentifierExpression target } member:
            {
                var targetName = ResolveNativeObjectName(SanitizeVariableName(target.Name));
                var sourceName = SanitizeVariableName(target.Name);
                if (_dialect.IsZsh && _zshReadOnlyObjectParameters.Contains(sourceName) &&
                    _zshObjectParameterNames.TryGetValue(sourceName, out var readOnlyReference))
                {
                    value = $"${{${{(@P){readOnlyReference}}}[{EmitObjectSubscript(member.MemberName)}]-}}";
                    return true;
                }
                if (_nativeObjectVariables.Contains(targetName))
                {
                    value = $"${{{targetName}[{EmitObjectSubscript(member.MemberName)}]-}}";
                    return true;
                }
                if (_recordVariables.Contains(targetName))
                {
                    value = $"${{{targetName}_{SanitizeVariableName(member.MemberName)}-}}";
                    return true;
                }
                break;
            }
            case IrIndexExpression { Target: IrIdentifierExpression target, Index: IrLiteralExpression { Value: int index } }:
            {
                var targetName = SanitizeVariableName(target.Name);
                if (_nativeArrayVariables.ContainsKey(targetName))
                {
                    value = $"${{{targetName}[{index}]-}}";
                    return true;
                }
                break;
            }
            case IrBinaryExpression { Operator: "+" } binary:
                if (TryEmitInlineString(binary.Left, out var left) && TryEmitInlineString(binary.Right, out var right))
                {
                    value = left + right;
                    return true;
                }
                break;
        }

        value = "";
        return false;
    }

    private static bool ContainsStringLiteral(IrExpression expression) => expression switch
    {
        IrLiteralExpression { Value: string or char } => true,
        IrBinaryExpression { Operator: "+" } binary =>
            ContainsStringLiteral(binary.Left) || ContainsStringLiteral(binary.Right),
        _ => false
    };

    private static string EscapePosixDoubleQuotedContent(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("$", "\\$", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal);

    private string EmitObjectSubscript(string memberName) =>
        _dialect.IsZsh ? memberName : Escape.PosixSingleQuoted(memberName);

    private string PrepareCondition(IrExpression expression, bool inFunction)
    {
        if (expression is IrIntrinsicCallExpression predicate &&
            predicate.Id is IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or IntrinsicId.StringIsMatch)
        {
            var predicateValue = PrepareValue(predicate.Arguments[0], inFunction);
            var pattern = predicate.Id == IntrinsicId.StringIsMatch
                ? PrepareRegex(predicate.Arguments[1], inFunction)
                : PrepareValue(predicate.Arguments[1], inFunction);
            return predicate.Id switch
            {
                IntrinsicId.StringContains => $"[[ {predicateValue} == *{pattern}* ]]",
                IntrinsicId.StringStartsWith => $"[[ {predicateValue} == {pattern}* ]]",
                IntrinsicId.StringEndsWith => $"[[ {predicateValue} == *{pattern} ]]",
                IntrinsicId.StringIsMatch => $"[[ {predicateValue} =~ {pattern} ]]",
                _ => throw new InvalidOperationException()
            };
        }

        if (expression is IrTruthinessExpression truthiness)
            return PrepareTruthinessCondition(truthiness, inFunction);

        if (expression is IrUnaryExpression { Operator: "!" } unary)
        {
            return $"! {PrepareCondition(unary.Operand, inFunction)}";
        }

        if (expression is IrBinaryExpression logical && logical.Operator is "&&" or "||")
        {
            var result = DeclareTemp(logical.Operator == "&&" ? "'false'" : "'true'", inFunction);
            var left = PrepareCondition(logical.Left, inFunction);
            WriteLine(logical.Operator == "&&" ? $"if {left}; then" : $"if ! {left}; then");
            _indent++;
            var right = PrepareCondition(logical.Right, inFunction);
            WriteLine($"if {right}; then {result}='true'; else {result}='false'; fi");
            _indent--;
            WriteLine("fi");
            return $"[[ \"${{{result}-}}\" == 'true' ]]";
        }

        if (expression is IrBinaryExpression comparison && comparison.Operator is "==" or "!=")
        {
            var left = PrepareValue(comparison.Left, inFunction);
            var right = PrepareValue(comparison.Right, inFunction);
            return $"[[ {left} {comparison.Operator} {right} ]]";
        }

        if (expression is IrBinaryExpression relational && relational.Operator is "<" or ">" or "<=" or ">=")
        {
            if (CanEmitInlineInteger(relational.Left) && CanEmitInlineInteger(relational.Right))
            {
                return $"(( {EmitInlineInteger(relational.Left)} {relational.Operator} {EmitInlineInteger(relational.Right)} ))";
            }

            var left = DeclareTemp(PrepareValue(relational.Left, inFunction), inFunction);
            var right = DeclareTemp(PrepareValue(relational.Right, inFunction), inFunction);
            return $"(( {left} {relational.Operator} {right} ))";
        }

        if (expression is IrIdentifierExpression identifier)
        {
            var name = SanitizeVariableName(identifier.Name);
            return _knownIntegerVariables.Contains(name)
                ? "true"
                : $"[[ -n \"${{{name}:-}}\" ]]";
        }

        if (expression is IrLiteralExpression literal)
        {
            return literal.Value switch
            {
                null => "false",
                bool boolean => boolean ? "true" : "false",
                sbyte or byte or short or ushort or int or uint or long or ulong =>
                    "true",
                string text => text.Length == 0 ? "false" : "true",
                _ => "true"
            };
        }

        var value = PrepareValue(expression, inFunction);
        return $"[[ -n {value} ]]";
    }

    private string PrepareTruthinessCondition(IrTruthinessExpression expression, bool inFunction)
    {
        var type = expression.OperandType.Name;
        if (type == "null") return "false";
        if (type == "array" || type == "object" || IsNamedObjectType(expression.OperandType))
        {
            if (expression.Operand is IrArrayLiteralExpression or IrObjectLiteralExpression or IrConstructionExpression)
                return "true";
            if (expression.Operand is IrIdentifierExpression identifier)
            {
                var name = SanitizeVariableName(identifier.Name);
                if (_nativeArrayVariables.ContainsKey(name) || _nativeObjectVariables.Contains(name) || _recordVariables.Contains(name))
                    return "true";
            }
            return $"[[ -n {PrepareValue(expression.Operand, inFunction)} ]]";
        }

        if (type == "bool") return PrepareBooleanCondition(expression.Operand, inFunction);
        var value = PrepareValue(expression.Operand, inFunction);
        return type switch
        {
            "int" or "float" => "true",
            "string" => $"[[ -n {value} ]]",
            _ => "false"
        };
    }

    private string PrepareBooleanCondition(IrExpression expression, bool inFunction)
    {
        return expression switch
        {
            IrLiteralExpression { Value: true } => "true",
            IrLiteralExpression { Value: false } => "false",
            IrIdentifierExpression identifier => $"[[ \"${{{SanitizeVariableName(identifier.Name)}:-}}\" == 'true' ]]",
            IrUnaryExpression { Operator: "!" } unary => $"! {PrepareBooleanCondition(unary.Operand, inFunction)}",
            IrBinaryExpression { Operator: "&&" } binary => $"{PrepareBooleanCondition(binary.Left, inFunction)} && {PrepareBooleanCondition(binary.Right, inFunction)}",
            IrBinaryExpression { Operator: "||" } binary => $"{PrepareBooleanCondition(binary.Left, inFunction)} || {PrepareBooleanCondition(binary.Right, inFunction)}",
            IrBinaryExpression binary when binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=" => PrepareCondition(binary, inFunction),
            IrIntrinsicCallExpression predicate when predicate.Id is IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or IntrinsicId.StringIsMatch =>
                PrepareCondition(predicate, inFunction),
            IrTruthinessExpression truthiness => PrepareTruthinessCondition(truthiness, inFunction),
            _ => $"[[ {PrepareValue(expression, inFunction)} == 'true' ]]"
        };
    }

    private string PrepareStringIntrinsic(IrIntrinsicCallExpression call, bool inFunction)
    {
        var result = $"__sushi_value_{++_valueTempId}";
        EmitStringChain(result, call, inFunction, declareResult: true);
        return $"\"${{{result}-}}\"";
    }

    private void EmitStringChain(string result, IrIntrinsicCallExpression call, bool inFunction, bool declareResult)
    {
        var chain = new List<IrIntrinsicCallExpression> { call };
        var receiverExpression = call.Arguments[0];
        while (receiverExpression is IrIntrinsicCallExpression nested && IsInlineStringIntrinsic(nested.Id))
        {
            chain.Add(nested);
            receiverExpression = nested.Arguments[0];
        }
        chain.Reverse();

        var declaration = declareResult && inFunction ? "local " : "";
        WriteLine($"{declaration}{result}={PrepareValue(receiverExpression, inFunction)}");
        foreach (var operation in chain)
        {
            switch (operation.Id)
            {
                case IntrinsicId.StringTrim:
                    WriteLine($"{result}=\"${{{result}#\"${{{result}%%[![:space:]]*}}\"}}\"");
                    WriteLine($"{result}=\"${{{result}%\"${{{result}##*[![:space:]]}}\"}}\"");
                    break;
                case IntrinsicId.StringLower:
                    WriteLine(_dialect.IsZsh ? $"{result}=\"${{(L){result}}}\"" : $"{result}=\"${{{result},,}}\"");
                    break;
                case IntrinsicId.StringUpper:
                    WriteLine(_dialect.IsZsh ? $"{result}=\"${{(U){result}}}\"" : $"{result}=\"${{{result}^^}}\"");
                    break;
                case IntrinsicId.StringReplace:
                {
                    var oldValue = DeclareTemp(PrepareValue(operation.Arguments[1], inFunction), inFunction);
                    var newValue = DeclareTemp(PrepareValue(operation.Arguments[2], inFunction), inFunction);
                    WriteLine($"if [[ -n \"${{{oldValue}-}}\" ]]; then {result}=\"${{{result}//${{{oldValue}}}/${{{newValue}}}}}\"; fi");
                    break;
                }
                case IntrinsicId.StringContains:
                case IntrinsicId.StringStartsWith:
                case IntrinsicId.StringEndsWith:
                {
                    var needle = PrepareValue(operation.Arguments[1], inFunction);
                    var test = operation.Id switch
                    {
                        IntrinsicId.StringContains => $"\"${{{result}-}}\" == *{needle}*",
                        IntrinsicId.StringStartsWith => $"\"${{{result}-}}\" == {needle}*",
                        _ => $"\"${{{result}-}}\" == *{needle}"
                    };
                    WriteLine($"if [[ {test} ]]; then {result}='true'; else {result}='false'; fi");
                    break;
                }
                case IntrinsicId.StringIsMatch:
                {
                    var pattern = PrepareRegex(operation.Arguments[1], inFunction);
                    WriteLine($"if [[ \"${{{result}-}}\" =~ {pattern} ]]; then {result}='true'; else {result}='false'; fi");
                    break;
                }
            }
        }
    }

    private string CaptureValue(string expression, bool inFunction)
    {
        var result = DeclareTemp("''", inFunction);
        WriteLine($"{result}={expression}");
        return $"\"${{{result}-}}\"";
    }

    private string DeclareTemp(string value, bool inFunction)
    {
        var name = _names.Generated(TargetNameKind.Variable, "_tmp" + (++_valueTempId));
        WriteLine($"{(inFunction ? "local " : "")}{name}={value}");
        return name;
    }

    private string DeclareUninitializedTemp(bool inFunction)
    {
        var name = _names.Generated(TargetNameKind.Variable, "_tmp" + (++_valueTempId));
        if (inFunction) WriteLine($"local {name}");
        return name;
    }

    private bool IsDefinitelyInteger(IrExpression expression)
    {
        return expression switch
        {
            IrIdentifierExpression identifier when identifier.StaticType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true => true,
            IrLiteralExpression literal => literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong,
            IrIdentifierExpression identifier => !_knownFloatVariables.Contains(SanitizeVariableName(identifier.Name)) &&
                                                 _knownIntegerVariables.Contains(SanitizeVariableName(identifier.Name)),
            IrIndexExpression { Target: IrIdentifierExpression identifier } =>
                _integerArrayVariables.Contains(SanitizeVariableName(identifier.Name)),
            IrCollectionLengthExpression => true,
            IrConversionExpression conversion => conversion.TargetType.Name == "int",
            IrUnaryExpression unary when unary.Operator is "+" or "-" => IsDefinitelyInteger(unary.Operand),
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                IsDefinitelyInteger(binary.Left) && IsDefinitelyInteger(binary.Right),
            IrCallExpression call => _integerReturningFunctions.Contains(call.Callee),
            IrResolvedMethodCallExpression method => _integerReturningFunctions.Contains(method.Callee),
            IrAdapterCallExpression adapter => _integerReturningFunctions.Contains(adapter.Callee),
            IrMemberAccessExpression member => member.ValueType.Name == "int",
            _ => false
        };
    }

    private bool IsDefinitelyFloat(IrExpression expression)
    {
        return expression switch
        {
            IrIdentifierExpression identifier when identifier.StaticType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true => true,
            IrLiteralExpression literal => literal.Value is float or double or decimal,
            IrConversionExpression conversion => conversion.TargetType.Name == "float",
            IrIntrinsicCallExpression intrinsic => intrinsic.ReturnType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true,
            IrIdentifierExpression identifier => _knownFloatVariables.Contains(SanitizeVariableName(identifier.Name)),
            IrUnaryExpression unary when unary.Operator is "+" or "-" => IsDefinitelyFloat(unary.Operand),
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                IsDefinitelyFloat(binary.Left) || IsDefinitelyFloat(binary.Right),
            IrCallExpression call => _floatReturningFunctions.Contains(call.Callee) ||
                                      call.Callee.Equals("float", StringComparison.OrdinalIgnoreCase) ||
                                      call.Callee.Equals("double", StringComparison.OrdinalIgnoreCase) ||
                                      call.Callee.Equals("decimal", StringComparison.OrdinalIgnoreCase),
            IrResolvedMethodCallExpression method => _floatReturningFunctions.Contains(method.Callee),
            IrAdapterCallExpression adapter => _floatReturningFunctions.Contains(adapter.Callee),
            IrMemberAccessExpression member => member.ValueType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };
    }

    private string EmitAwkArithmetic(string left, string right, string op)
    {
        var operation = op switch
        {
            "+" or "-" or "*" or "/" or "%" => $"left {op} right",
            _ => throw new InvalidOperationException($"Unsupported floating-point operator '{op}'.")
        };
        var zeroGuard = op is "/" or "%" ? " if (right == 0) exit 2;" : "";
        return $"$(LC_ALL=C awk -v left={left} -v right={right} 'BEGIN {{{zeroGuard} result = {operation}; if (result == 0) result = 0; printf \"%.17g\", result}}')";
    }

    private string EmitAwkFloatExpression(IrExpression expression)
    {
        var bindings = new List<(string Name, string Value)>();
        string Build(IrExpression value)
        {
            switch (value)
            {
                case IrLiteralExpression literal when literal.Value is float or double or decimal:
                    return EmitLiteral(literal.Value);
                case IrLiteralExpression literal when literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0";
                case IrIdentifierExpression identifier:
                {
                    var name = $"v{bindings.Count}";
                    bindings.Add((name, $"\"${{{SanitizeVariableName(identifier.Name)}:-}}\""));
                    return name;
                }
                case IrUnaryExpression unary when unary.Operator is "+" or "-":
                    return $"({unary.Operator}{Build(unary.Operand)})";
                case IrBinaryExpression binary when binary.Operator is "+" or "-" or "*":
                    return $"({Build(binary.Left)} {binary.Operator} {Build(binary.Right)})";
                case IrBinaryExpression binary when binary.Operator is "/" or "%":
                    return $"(sushi_{(binary.Operator == "/" ? "div" : "mod")}({Build(binary.Left)}, {Build(binary.Right)}))";
                default:
                {
                    var name = $"v{bindings.Count}";
                    bindings.Add((name, EmitValueExpression(value)));
                    return name;
                }
            }
        }

        var expressionText = Build(expression);
        var arguments = string.Join(" ", bindings.Select(binding => $"-v {binding.Name}={binding.Value}"));
        return $"$(LC_ALL=C awk {arguments} 'function sushi_div(a,b) {{ if (b == 0) exit 2; return a / b }} function sushi_mod(a,b) {{ if (b == 0) exit 2; return a % b }} BEGIN {{ result = {expressionText}; if (result == 0) result = 0; printf \"%.17g\", result }}')";
    }

    private bool CanEmitInlineInteger(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong,
            IrIdentifierExpression identifier => !_knownFloatVariables.Contains(SanitizeVariableName(identifier.Name)) &&
                                                 _knownIntegerVariables.Contains(SanitizeVariableName(identifier.Name)),
            IrUnaryExpression unary when unary.Operator is "+" or "-" => CanEmitInlineInteger(unary.Operand),
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                CanEmitInlineInteger(binary.Left) && CanEmitInlineInteger(binary.Right),
            _ => false
        };
    }

    private string EmitInlineInteger(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
            IrIdentifierExpression identifier => SanitizeVariableName(identifier.Name),
            IrUnaryExpression unary => $"{unary.Operator}{EmitInlineInteger(unary.Operand)}",
            IrBinaryExpression binary => $"({EmitInlineInteger(binary.Left)} {binary.Operator} {EmitInlineInteger(binary.Right)})",
            _ => "0"
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
        if (isFloat)
            _knownFloatVariables.Add(name);
        else
            _knownFloatVariables.Remove(name);
    }

    private void SetKnownArray(string name, bool isArray)
    {
        if (isArray)
        {
            _nativeArrayVariables[name] = string.Empty;
        }
        else
        {
            _nativeArrayVariables.Remove(name);
        }
    }

    private static bool IsInlineStringIntrinsic(IntrinsicId id)
    {
        return id is IntrinsicId.StringTrim or IntrinsicId.StringLower or IntrinsicId.StringUpper or
            IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or
            IntrinsicId.StringReplace or IntrinsicId.StringIsMatch;
    }

    private string EmitCallCommand(IrCallExpression call)
    {
        var callee = SanitizeFunctionName(call.Callee);
        var arguments = call.Arguments.Select(argument => EmitValueExpression(argument.Value)).ToList();
        return arguments.Count > 0
            ? $"{callee} {string.Join(" ", arguments)}"
            : callee;
    }

    private string EmitMethodCallCommand(IrMethodCallExpression call)
    {
        _context.Error(UnsupportedEmitCode,
            $"Dynamic method dispatch for '{call.MethodName}' is not supported on Bash/Zsh; use a statically typed receiver.",
            call.Origin?.Line ?? 1, call.Origin?.Column ?? 1);
        return ":";
    }

    private string EmitConditionCommand(IrExpression expression)
    {
        if (expression is IrTruthinessExpression truthiness)
            return EmitTruthinessCommand(truthiness);

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
            if (IsDefinitelyFloat(comparison))
                return EmitAwkComparison(comparison);
            var op = comparison.Operator == "==" ? "==" : "!=";
            return $"[[ {EmitComparableValue(comparison.Left)} {op} {EmitComparableValue(comparison.Right)} ]]";
        }

        if (expression is IrBinaryExpression relational && relational.Operator is "<" or ">" or "<=" or ">=")
        {
            if (IsDefinitelyFloat(relational))
                return EmitAwkComparison(relational);
            return $"(( {EmitArithmeticExpression(relational.Left)} {relational.Operator} {EmitArithmeticExpression(relational.Right)} ))";
        }

        return $"[[ {EmitValueExpression(expression)} == 'true' ]]";
    }

    private string EmitAwkComparison(IrBinaryExpression comparison)
    {
        var left = EmitFloatOperand(comparison.Left);
        var right = EmitFloatOperand(comparison.Right);
        return $"LC_ALL=C awk -v left={left} -v right={right} 'BEGIN {{ exit !(left {comparison.Operator} right) }}'";
    }

    private string EmitComparableValue(IrExpression expression)
    {
        return expression switch
        {
            IrMemberAccessExpression { Target: IrIdentifierExpression target } member
                when _nativeObjectVariables.Contains(SanitizeVariableName(target.Name)) =>
                $"\"${{{ResolveNativeObjectName(SanitizeVariableName(target.Name))}[{EmitObjectSubscript(member.MemberName)}]-}}\"",
            IrIdentifierExpression identifier => EmitVariableValue(identifier.Name),
            IrLiteralExpression literal when literal.Value is string str => Escape.PosixSingleQuoted(str),
            IrLiteralExpression literal when literal.Value is char ch => Escape.PosixSingleQuoted(ch.ToString()),
            IrLiteralExpression literal when literal.Value is bool boolean => Escape.PosixSingleQuoted(boolean ? "true" : "false"),
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
            IrIdentifierExpression identifier => EmitVariableValue(identifier.Name),
            IrArrayLiteralExpression array => EmitArrayLiteral(array),
            IrObjectLiteralExpression obj => EmitObjectLiteral(obj),
            IrConversionExpression conversion => PrepareConversionExpression(conversion, _currentFunctionName != null),
            IrMemberAccessExpression member => EmitMemberValueExpression(member),
            IrCollectionLengthExpression length when length.Target is IrIdentifierExpression identifier &&
                                                     _nativeArrayVariables.TryGetValue(SanitizeVariableName(identifier.Name), out var lengthArrayName) =>
                $"\"${{#{lengthArrayName}[@]}}\"",
            IrSliceExpression slice => EmitNativeSliceValue(slice),
            IrIndexExpression index => EmitNativeIndexValue(index),
            IrUnaryExpression unary when unary.Operator == "!" =>
                $"\"$(if {EmitConditionCommand(unary.Operand)}; then printf '%s' 'false'; else printf '%s' 'true'; fi)\"",
            IrTruthinessExpression truthiness =>
                $"\"$(if {EmitTruthinessCommand(truthiness)}; then printf '%s' 'true'; else printf '%s' 'false'; fi)\"",
            IrUnaryExpression unary when unary.Operator is "-" or "+" && IsDefinitelyFloat(unary) =>
                EmitArithmeticExpression(unary),
            IrUnaryExpression unary when unary.Operator is "-" or "+" =>
                $"$(( {unary.Operator}{EmitArithmeticExpression(unary.Operand)} ))",
            IrBinaryExpression binary when binary.Operator == "+" && IsDefinitelyFloat(binary) =>
                EmitArithmeticExpression(binary),
            IrBinaryExpression binary when binary.Operator == "+" && IsDefinitelyInteger(binary) =>
                EmitArithmeticExpression(binary),
            IrBinaryExpression binary when binary.Operator is "+" =>
                $"\"$(printf '%s%s' {EmitValueExpression(binary.Left)} {EmitValueExpression(binary.Right)})\"",
            IrBinaryExpression binary when binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" =>
                $"\"$(if {EmitConditionCommand(binary)}; then printf '%s' 'true'; else printf '%s' 'false'; fi)\"",
            IrBinaryExpression binary when binary.Operator is "-" or "*" or "/" or "%" =>
                EmitArithmeticExpression(binary),
            IrConditionalExpression conditional =>
                $"\"$(if {EmitConditionCommand(conditional.Condition)}; then printf '%s' {EmitValueExpression(conditional.TrueExpression)}; else printf '%s' {EmitValueExpression(conditional.FalseExpression)}; fi)\"",
            IrIntrinsicCallExpression intrinsicCall => EmitIntrinsicValue(intrinsicCall),
            IrCallExpression call => $"$({EmitCallCommand(call)})",
            IrResolvedMethodCallExpression method => $"$({EmitCallCommand(method.AsFunctionCall())})",
            IrAdapterCallExpression adapter => $"$({EmitCallCommand(adapter.AsFunctionCall())})",
            IrConstructionExpression => "''",
            IrMethodCallExpression methodCall => $"\"$({EmitMethodCallCommand(methodCall)})\"",
            IrAssignmentExpression assignment => $"$({EmitAssignmentExpression(assignment)}; printf '%s' \"${{{SanitizeVariableName(assignment.Target.Name)}:-}}\")",
            _ => "''"
        };
    }

    private string EmitNativeSliceValue(IrSliceExpression slice)
    {
        var target = EmitValueExpression(slice.Target);
        var targetName = slice.Target is IrIdentifierExpression identifier
            ? SanitizeVariableName(identifier.Name)
            : null;
        var isArray = targetName != null && _nativeArrayVariables.ContainsKey(targetName);
        if (!isArray)
        {
            var stringTarget = targetName ?? DeclareTemp(EmitValueExpression(slice.Target), _currentFunctionName != null);
            return PrepareNativeStringSlice(stringTarget, slice, _currentFunctionName != null);
        }
        return PrepareNativeArraySlice(targetName!, slice, _currentFunctionName != null);
    }

    private string EmitNativeIndexValue(IrIndexExpression index)
    {
        var targetName = index.Target is IrIdentifierExpression identifier
            ? SanitizeVariableName(identifier.Name)
            : null;
        if (targetName != null && _nativeArrayVariables.TryGetValue(targetName, out var arrayName))
        {
            var subscript = index.Index switch
            {
                IrLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
                IrIdentifierExpression id => SanitizeVariableName(id.Name),
                _ => EmitArithmeticExpression(index.Index)
            };
            return $"\"${{{arrayName}[{subscript}]-}}\"";
        }

        if (IsStringExpression(index.Target))
            return PrepareNativeStringIndex(index, _currentFunctionName != null);

        _context.Error(AmbiguousShapeCode,
            "Indexing requires a statically known native array or string",
            index.Origin?.Line ?? 1, index.Origin?.Column ?? 1);
        return "''";
    }

    private string PrepareNativeStringIndex(IrIndexExpression index, bool inFunction)
    {
        var target = index.Target is IrIdentifierExpression identifier
            ? SanitizeVariableName(identifier.Name)
            : DeclareTemp(PrepareValue(index.Target, inFunction), inFunction);
        var position = DeclareTemp(EmitNativeSliceBound(index.Index), inFunction);
        var targetLength = "${#" + target + "}";
        WriteLine($"if (( {position} < 0 )); then {position}=$(( {targetLength} + {position} )); fi");
        var failure = inFunction ? "return 2" : "exit 2";
        WriteLine($"if (( {position} < 0 || {position} >= {targetLength} )); then printf 'Sushi: string index out of range\\n' >&2; {failure}; fi");
        return $"\"${{{target}:{position}:1}}\"";
    }

    private string PrepareNativeStringSlice(string target, IrSliceExpression slice, bool inFunction)
    {
        var start = DeclareTemp(slice.Start == null ? "0" : EmitNativeSliceBound(slice.Start), inFunction);
        var end = DeclareTemp(slice.End == null ? $"${{#{target}}}" : EmitNativeSliceBound(slice.End), inFunction);
        var length = $"${{#{target}}}";
        WriteLine($"if (( {start} < 0 )); then {start}=$(( {length} + {start} )); fi");
        WriteLine($"if (( {end} < 0 )); then {end}=$(( {length} + {end} )); fi");
        WriteLine($"if (( {start} < 0 )); then {start}=0; elif (( {start} > {length} )); then {start}={length}; fi");
        WriteLine($"if (( {end} < 0 )); then {end}=0; elif (( {end} > {length} )); then {end}={length}; fi");
        WriteLine($"if (( {end} < {start} )); then {end}={start}; fi");
        var sliceLength = DeclareTemp($"$(( {end} - {start} ))", inFunction);
        return $"\"${{{target}:{start}:{sliceLength}}}\"";
    }

    private string PrepareNativeArraySlice(string target, IrSliceExpression slice, bool inFunction)
    {
        var start = DeclareTemp(slice.Start == null ? "0" : EmitNativeSliceBound(slice.Start), inFunction);
        var end = DeclareTemp(slice.End == null ? $"${{#{target}[@]}}" : EmitNativeSliceBound(slice.End), inFunction);
        var length = $"${{#{target}[@]}}";
        WriteLine($"if (( {start} < 0 )); then {start}=$(( {length} + {start} )); fi");
        WriteLine($"if (( {end} < 0 )); then {end}=$(( {length} + {end} )); fi");
        WriteLine($"if (( {start} < 0 )); then {start}=0; elif (( {start} > {length} )); then {start}={length}; fi");
        WriteLine($"if (( {end} < 0 )); then {end}=0; elif (( {end} > {length} )); then {end}={length}; fi");
        WriteLine($"if (( {end} < {start} )); then {end}={start}; fi");
        var sliceLength = DeclareTemp($"$(( {end} - {start} ))", inFunction);
        return $"\"${{{target}[@]:{start}:{sliceLength}}}\"";
    }

    private static bool IsStringExpression(IrExpression expression) => expression switch
    {
        IrIdentifierExpression identifier => identifier.StaticType.Name?.Equals("string", StringComparison.OrdinalIgnoreCase) == true,
        IrLiteralExpression { Value: string } => true,
        IrMemberAccessExpression member => member.ValueType.Name?.Equals("string", StringComparison.OrdinalIgnoreCase) == true,
        IrIntrinsicCallExpression intrinsic => intrinsic.ReturnType.Name?.Equals("string", StringComparison.OrdinalIgnoreCase) == true,
        _ => false
    };

    private string EmitVariableValue(string name)
    {
        var variable = SanitizeVariableName(name);
        return _positionalParameterReferences.TryGetValue(variable, out var positional)
            ? $"\"${{{positional}:-}}\""
            : $"\"${{{variable}:-}}\"";
    }

    private string EmitMemberValueExpression(IrMemberAccessExpression member)
    {
        if (_dialect.IsZsh && member.Target is IrIdentifierExpression identifier)
        {
            var sourceName = SanitizeVariableName(identifier.Name);
            if (_zshReadOnlyObjectParameters.Contains(sourceName) &&
                _zshObjectParameterNames.TryGetValue(sourceName, out var referenceName))
            {
                return "\"${${(@P)" + referenceName + "}[" + EmitObjectSubscript(member.MemberName) + "]-}\"";
            }
        }
        _context.Error(AmbiguousShapeCode,
            $"Member '{member.MemberName}' requires a statically known native object shape",
            member.Origin?.Line ?? 1, member.Origin?.Column ?? 1);
        return "''";
    }

    private string EmitTruthinessCommand(IrTruthinessExpression expression)
    {
        var type = expression.OperandType.Name;
        if (type == "null") return "false";
        if (type == "array" || type == "object" || IsNamedObjectType(expression.OperandType)) return "true";
        if (type == "bool") return EmitBooleanCondition(expression.Operand);
        var value = EmitValueExpression(expression.Operand);
        return type switch
        {
            "int" or "float" => "true",
            "string" => $"[[ -n {value} ]]",
            _ => "false"
        };
    }

    private string EmitBooleanCondition(IrExpression expression) => expression switch
    {
        IrLiteralExpression { Value: true } => "true",
        IrLiteralExpression { Value: false } => "false",
        IrIdentifierExpression identifier => $"[[ \"${{{SanitizeVariableName(identifier.Name)}:-}}\" == 'true' ]]",
        IrUnaryExpression { Operator: "!" } unary => $"! {EmitBooleanCondition(unary.Operand)}",
        IrBinaryExpression { Operator: "&&" } binary => $"{EmitBooleanCondition(binary.Left)} && {EmitBooleanCondition(binary.Right)}",
        IrBinaryExpression { Operator: "||" } binary => $"{EmitBooleanCondition(binary.Left)} || {EmitBooleanCondition(binary.Right)}",
        IrBinaryExpression binary when binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=" => EmitConditionCommand(binary),
        IrTruthinessExpression truthiness => EmitTruthinessCommand(truthiness),
        _ => $"[[ {EmitValueExpression(expression)} == 'true' ]]"
    };

    private string EmitArrayLiteral(IrArrayLiteralExpression expression)
    {
        var name = DeclareUninitializedTemp(inFunction: _currentFunctionName != null);
        var values = string.Join(" ", expression.Elements.Select(EmitValueExpression));
        WriteLine($"{(_currentFunctionName != null ? "local " : "declare ")} -a {name}=({values})".Replace("local  -a", "local -a"));
        _nativeArrayVariables[name] = name;
        return $"\"${{{name}[@]}}\"";
    }

    private string EmitObjectLiteral(IrObjectLiteralExpression expression)
    {
        var name = DeclareUninitializedTemp(inFunction: _currentFunctionName != null);
        var entries = expression.Properties.Select(property =>
            $"[{Escape.PosixSingleQuoted(property.Name)}]={EmitValueExpression(property.Value)}").ToList();
        EmitAssociativeObject(name, entries, _currentFunctionName != null ? "local " : "declare ", false);
        _nativeObjectVariables.Add(name);
        return Escape.PosixSingleQuoted(name);
    }

    private void EmitVarargsContractCheck(
        IrFunctionParameter parameter,
        string parameterName,
        string functionName,
        string? nativeArrayName = null)
    {
        if (parameter.DeclaredType.IsAnyOrUnknown)
        {
            return;
        }

        WriteLine("local __sushi_vararg_item");
        if (nativeArrayName != null)
        {
            WriteLine($"for __sushi_vararg_item in \"${{{nativeArrayName}[@]}}\"; do");
        }
        else
        {
            _context.Error(AmbiguousShapeCode,
                $"Varargs parameter '{parameter.Name}' requires native array storage",
                1, 1);
            return;
        }
        _indent++;
        EmitContractCheckForValue(
            parameter.DeclaredType,
            "\"${__sushi_vararg_item:-}\"",
            $"varargs parameter '{parameter.Name}' of function '{functionName}'");
        _indent--;
        WriteLine("done");
    }

    private void EmitContractCheckForValue(IrTypeRef type, string valueExpression, string context)
    {
        // Type errors that can be proven statically are reported by lowering.
        // Bash and Zsh otherwise use their native value model.
    }

    private string EmitArithmeticExpression(IrExpression expression)
    {
        if (IsDefinitelyFloat(expression))
        {
            return EmitAwkFloatExpression(expression);
        }
        return expression switch
        {
            IrLiteralExpression literal when literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong
                => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
            IrLiteralExpression literal => EmitLiteral(literal.Value),
            IrIdentifierExpression identifier => SanitizeVariableName(identifier.Name),
            IrMemberAccessExpression { Target: IrIdentifierExpression target } member
                when _nativeObjectVariables.Contains(SanitizeVariableName(target.Name)) =>
                $"${{{ResolveNativeObjectName(SanitizeVariableName(target.Name))}[{EmitObjectSubscript(member.MemberName)}]:-0}}",
            IrConversionExpression conversion => PrepareConversionExpression(conversion, _currentFunctionName != null),
            IrUnaryExpression unary when unary.Operator is "+" or "-" =>
                $"{unary.Operator}{EmitArithmeticExpression(unary.Operand)}",
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                $"({EmitArithmeticExpression(binary.Left)} {binary.Operator} {EmitArithmeticExpression(binary.Right)})",
            _ => EmitValueExpression(expression)
        };
    }

    private string EmitFloatOperand(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => EmitLiteral(literal.Value),
            IrIdentifierExpression identifier => $"\"${{{SanitizeVariableName(identifier.Name)}:-}}\"",
            IrUnaryExpression unary when unary.Operator is "+" or "-" => EmitAwkUnary(unary.Operator, EmitFloatOperand(unary.Operand)),
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                EmitAwkArithmetic(EmitFloatOperand(binary.Left), EmitFloatOperand(binary.Right), binary.Operator),
            _ => EmitValueExpression(expression)
        };
    }

    private static string EmitAwkUnary(string op, string operand)
    {
        var sign = op == "-" ? "-" : "+";
        return $"$(LC_ALL=C awk -v value={operand} 'BEGIN {{ result = {sign}value; if (result == 0) result = 0; printf \"%.17g\", result }}')";
    }

    private string EmitNativeSliceBound(IrExpression expression)
    {
        if (expression is IrLiteralExpression literal &&
            literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            return Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0";
        }
        if (expression is IrIdentifierExpression identifier &&
            _knownIntegerVariables.Contains(SanitizeVariableName(identifier.Name)))
        {
            return SanitizeVariableName(identifier.Name);
        }

        return EmitArithmeticExpression(expression);
    }

    private string PrepareConversionExpression(IrConversionExpression conversion, bool inFunction)
    {
        if (conversion.TargetType.Name == "int" && CanEmitInlineInteger(conversion.Value))
            return $"$(( {EmitInlineInteger(conversion.Value)} ))";

        var value = PrepareValue(conversion.Value, inFunction);
        return conversion.TargetType.Name switch
        {
            "string" => value,
            "int" => $"$(LC_ALL=C awk '/^[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)$/ {{ printf \"%.0f\", int($0); exit }} {{ exit 2 }}' <<< {value})",
            "float" => $"$(LC_ALL=C awk -v value={value} 'BEGIN {{ if (value !~ /^[+-]?([0-9]+([.][0-9]*)?|[.][0-9]+)$/) {{ print \"Sushi: cannot convert value to float\" > \"/dev/stderr\"; exit 2 }} printf \"%.17g\", value + 0 }}')",
            _ => value
        };
    }

    private string EmitLiteral(object? value)
    {
        return value switch
        {
            null => "''",
            string str => Escape.PosixSingleQuoted(str),
            char ch => Escape.PosixSingleQuoted(ch.ToString()),
            bool boolean => Escape.PosixSingleQuoted(boolean ? "true" : "false"),
            int or long or double or float or decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            _ => Escape.PosixSingleQuoted(value.ToString() ?? "")
        };
    }

    private void WriteLine(string text)
    {
        _document.Line(text);
    }

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

    private string SanitizeVariableName(string name)
    {
        return _names.Source(TargetNameKind.Variable, name);
    }
}
