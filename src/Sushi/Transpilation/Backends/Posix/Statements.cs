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
    private void EmitStatement(IrStatement statement, bool inFunction)
    {
        switch (statement)
        {
            case IrStandardLibraryImportStatement import:
                break;

            case IrBlockStatement block:
                foreach (var child in block.Statements)
                {
                    EmitStatement(child, inFunction);
                }
                break;

            // Native-class metadata is consumed by the PowerShell backend.  The
            // portable functions and object constructors which follow it remain
            // the Bash/Zsh representation.
            case IrClassDeclarationStatement:
                break;

            case IrEnumDeclarationStatement:
                break;

            case IrRichEnumDeclarationStatement:
                break;

            case IrVariableDeclarationStatement variable:
            {
                var enumType = "";
                var enumValue = "";
                var separator = variable.Name.LastIndexOf('_');
                var isEnumValue = !inFunction &&
                                  (IsEnumValueInitializer(variable.Initializer, out enumType, out enumValue) ||
                                   TryGetGeneratedEnumValue(variable.Initializer, out enumType, out enumValue));
                if (isEnumValue)
                {
                    if (separator > 0) enumType = variable.Name[..separator];
                    if (_commentedTypes.Add(enumType))
                    {
                        WriteLine("");
                        WriteLine($"# --- enum: {enumType} ---");
                    }
                    _indent++;
                }
                try
                {
                    if (isEnumValue)
                    {
                        WriteLine("");
                        if (string.IsNullOrEmpty(enumValue) && separator > 0)
                            enumValue = variable.Name[(separator + 1)..];
                        WriteLine($"# enum value: {enumType}.{enumValue}");
                    }
                    
                var initializer = variable.Initializer ?? new IrLiteralExpression(null);
                var name = SanitizeVariableName(variable.Name);
                var declaredInt = variable.DeclaredType.Kind == IrTypeKind.Primitive &&
                                  variable.DeclaredType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true;
                var declaredFloat = variable.DeclaredType.Kind == IrTypeKind.Primitive &&
                                    variable.DeclaredType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true;
                if (declaredInt) SetKnownInteger(name, true);
                if (declaredFloat) SetKnownFloat(name, true);
                if (initializer is IrConditionalExpression { IsSwitchExpression: true } switchExpression)
                {
                    EmitSwitchExpressionInto(name, switchExpression, inFunction);
                    SetKnownInteger(name, declaredInt);
                    SetKnownFloat(name, declaredFloat);
                    break;
                }
                if (initializer is IrConstructionExpression constructor)
                {
                    WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
                    var arguments = constructor.Arguments.Select(argument => PrepareValue(argument.Value, inFunction));
                    WriteLine($"{SanitizeFunctionName(constructor.ConstructorName)} {Escape.PosixSingleQuoted(name)} {string.Join(" ", arguments)}");
                    _nativeObjectVariables.Add(name);
                    break;
                }
                if (initializer is IrFileQueryExpression query)
                {
                    EmitFileQueryDeclaration(name, query, inFunction);
                    break;
                }
                if (initializer is IrFileQueryExecutionExpression queryExecution)
                {
                    EmitFileQueryExecutionDeclaration(name, queryExecution, inFunction);
                    break;
                }
                if (TryGetObjectReturningCall(initializer, out var objectCall, out var objectFunction))
                {
                    WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
                    EmitCallInto(name, objectCall, objectFunction, inFunction);
                    _nativeObjectVariables.Add(name);
                    break;
                }
                if (TryGetReturningCall(initializer, out var valueCall, out var valueFunction))
                {
                    var returnsArray = valueFunction.ReturnType.Name == "array";
                    if (returnsArray)
                    {
                        WriteLine($"{(inFunction ? "local " : "declare ")}-a {name}=()");
                        _nativeArrayVariables[name] = name;
                    }
                    else if (inFunction) WriteLine($"local {name}");
                    EmitCallInto(name, valueCall, valueFunction, inFunction);
                    SetKnownInteger(name, declaredInt || valueFunction.ReturnType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true);
                    SetKnownFloat(name, declaredFloat || valueFunction.ReturnType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true);
                    break;
                }
                if (initializer is IrIdentifierExpression objectAlias &&
                    _nativeObjectVariables.Contains(SanitizeVariableName(objectAlias.Name)))
                {
                    var source = ResolveNativeObjectName(SanitizeVariableName(objectAlias.Name));
                    if (_dialect.IsZsh)
                    {
                        WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=( \"${{(@kv){source}}}\" )");
                        _nativeObjectAliases[name] = source;
                    }
                    else
                        WriteLine($"{(inFunction ? "local " : "declare ")}-n {name}={Escape.PosixSingleQuoted(source)}");
                    _nativeObjectAliases[name] = source;
                    _nativeObjectVariables.Add(name);
                    break;
                }
                if (initializer is IrIdentifierExpression arrayAlias &&
                    _nativeArrayVariables.ContainsKey(SanitizeVariableName(arrayAlias.Name)))
                {
                    var source = SanitizeVariableName(arrayAlias.Name);
                    WriteLine($"{(inFunction ? "local " : "declare ")}-a {name}=(\"${{{source}[@]}}\")");
                    _nativeArrayVariables[name] = name;
                    _arrayInitializers.Remove(name);
                    _nativeObjectVariables.Remove(name);
                    _recordVariables.Remove(name);
                    break;
                }
                if (initializer is IrSliceExpression slice &&
                    slice.Target is IrIdentifierExpression sliceTarget &&
                    _nativeArrayVariables.ContainsKey(SanitizeVariableName(sliceTarget.Name)))
                {
                    var values = PrepareNativeArraySlice(SanitizeVariableName(sliceTarget.Name), slice, inFunction);
                    WriteLine($"{(inFunction ? "local " : "declare ")}-a {name}=({values})");
                    _nativeArrayVariables[name] = name;
                    _arrayInitializers.Remove(name);
                    _nativeObjectVariables.Remove(name);
                    _recordVariables.Remove(name);
                    break;
                }
                if (initializer is IrArrayLiteralExpression array)
                {
                    var values = array.Elements.Any(element => element is IrObjectLiteralExpression)
                        ? new List<string>()
                        : array.Elements.Select(element => PrepareValue(element, inFunction)).ToList();
                    WriteLine($"{(inFunction ? "local " : "declare ")}-a {name}=({string.Join(" ", values)})");
                    _nativeArrayVariables[name] = name;
                    _arrayInitializers[name] = array;
                    _nativeObjectVariables.Remove(name);
                    _recordVariables.Remove(name);
                    break;
                }

                if (initializer is IrObjectLiteralExpression obj)
                {
                    var properties = MetadataProperties(obj.Properties);
                    var entries = (_dialect.IsZsh
                        ? properties.Select(property =>
                            $"[{Escape.PosixSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
                        : properties.Select(property =>
                            $"[{Escape.PosixSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")).ToList();
                    EmitAssociativeObject(name, entries, inFunction ? "local " : "declare ",
                        name == "this" && _currentFunctionRole == IrFunctionRole.Constructor);
                    _nativeObjectVariables.Add(name);
                    _nativeArrayVariables.Remove(name);
                    _arrayInitializers.Remove(name);
                    _recordVariables.Remove(name);
                    break;
                }

                if (initializer is IrIntrinsicCallExpression intrinsic &&
                    EmitNativeIntrinsicDeclaration(name, intrinsic, inFunction))
                {
                    break;
                }

                if (IsBooleanValueExpression(initializer))
                {
                    EmitBooleanAssignment(name, initializer, inFunction);
                    SetKnownInteger(name, false);
                    break;
                }

                if (initializer is IrMethodCallExpression method &&
                    EmitNativeMethodDeclaration(name, method, inFunction))
                {
                    break;
                }

                if (initializer is IrIntrinsicCallExpression stringIntrinsic &&
                    IsInlineStringIntrinsic(stringIntrinsic.Id))
                {
                    EmitStringChain(name, stringIntrinsic, inFunction, declareResult: true);
                    break;
                }

                if (initializer is IrIntrinsicCallExpression directIntrinsic &&
                    directIntrinsic.Id is IntrinsicId.IoReadText or IntrinsicId.IoExists or IntrinsicId.PathJoin or IntrinsicId.PathDirname or IntrinsicId.PathBasename or IntrinsicId.EnvGet or IntrinsicId.OsCwd)
                {
                    WriteLine($"{(inFunction ? "local " : "")}{name}={EmitValueExpression(directIntrinsic)}");
                    break;
                }

                var value = PrepareValue(initializer, inFunction);
                WriteLine($"{(inFunction ? "local " : "")}{name}={value}");
                SetKnownInteger(name, declaredInt || (!declaredFloat && IsDefinitelyInteger(initializer)));
                SetKnownFloat(name, declaredFloat || IsDefinitelyFloat(initializer));
                SetKnownArray(name, initializer is IrArrayLiteralExpression);
                    break;
                }
                finally
                {
                    if (isEnumValue) _indent--;
                }
            }

            case IrExpressionStatement expressionStatement:
                EnsureTopLevelSection(inFunction);
                EmitExpressionStatement(expressionStatement.Expression, inFunction);
                break;

            case IrIfStatement ifStatement:
                EnsureTopLevelSection(inFunction);
                EmitIfStatement(ifStatement, inFunction);
                break;

            case IrSwitchStatement switchStatement:
                EnsureTopLevelSection(inFunction);
                EmitSwitchAsConditions(switchStatement, inFunction);
                break;

            case IrWhileStatement whileStatement:
                EmitWhileStatement(whileStatement, inFunction);
                break;

            case IrForStatement forStatement:
                EmitForStatement(forStatement, inFunction);
                break;

            case IrForEachStatement forEach:
                EmitForEachStatement(forEach, inFunction);
                break;

            case IrDoWhileStatement doWhileStatement:
                EmitDoWhileStatement(doWhileStatement, inFunction);
                break;

            case IrFunctionDeclarationStatement function:
                EmitFunctionDeclaration(function, EmitGeneratedFunctionComment(function));
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
        var condition = PrepareCondition(statement.Condition, inFunction);
        WriteLine($"if {condition}; then");
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

    private void EmitSwitchAsConditions(IrSwitchStatement statement, bool inFunction)
    {
        var temp = $"__sushi_switch_{++_valueTempId}";
        WriteLine($"{(inFunction ? "local " : "")}{temp}={EmitValueExpression(statement.Value)}");
        IrBlockStatement? next = statement.DefaultBody;
        for (var i = statement.Cases.Count - 1; i >= 0; i--)
        {
            var matches = statement.Cases[i].Matches
                .Select(match => (IrExpression)new IrBinaryExpression(new IrIdentifierExpression(temp), "==", match))
                .Aggregate((left, right) => new IrBinaryExpression(left, "||", right));
            next = new IrBlockStatement(new IrStatement[] { new IrIfStatement(matches, statement.Cases[i].Body, next) });
        }
        if (next != null) EmitStatement(next, inFunction);
    }

    private void EmitWhileStatement(IrWhileStatement statement, bool inFunction)
    {
        if (CanEmitDirectLoopCondition(statement.Condition))
        {
            var directCondition = PrepareCondition(statement.Condition, inFunction);
            WriteLine($"while {directCondition}; do");
            _indent++;
            EmitStatement(statement.Body, inFunction);
            _indent--;
            WriteLine("done");
            return;
        }

        WriteLine("while true; do");
        _indent++;
        var condition = PrepareCondition(statement.Condition, inFunction);
        WriteLine($"if ! {condition}; then");
        _indent++;
        WriteLine("break");
        _indent--;
        WriteLine("fi");
        EmitStatement(statement.Body, inFunction);
        _indent--;
        WriteLine("done");
    }

    private bool CanEmitDirectLoopCondition(IrExpression expression) => expression switch
    {
        IrLiteralExpression => true,
        IrIdentifierExpression => true,
        IrUnaryExpression { Operator: "!" } unary => CanEmitDirectLoopCondition(unary.Operand),
        IrBinaryExpression binary when binary.Operator is "<" or ">" or "<=" or ">=" =>
            CanEmitInlineInteger(binary.Left) && CanEmitInlineInteger(binary.Right),
        IrBinaryExpression binary when binary.Operator is "==" or "!=" =>
            binary.Left is IrLiteralExpression or IrIdentifierExpression &&
            binary.Right is IrLiteralExpression or IrIdentifierExpression,
        _ => false
    };

    private void EmitForStatement(IrForStatement statement, bool inFunction)
    {
        if (statement.Initializer != null)
        {
            EmitStatement(statement.Initializer, inFunction);
        }

        WriteLine("while true; do");
        _indent++;
        if (statement.Condition != null)
        {
            var condition = PrepareCondition(statement.Condition, inFunction);
            WriteLine($"if ! {condition}; then");
            _indent++;
            WriteLine("break");
            _indent--;
            WriteLine("fi");
        }
        EmitStatement(statement.Body, inFunction);

        if (statement.Increment != null)
        {
            EmitExpressionStatement(statement.Increment, inFunction);
        }

        _indent--;
        WriteLine("done");
    }

    private void EmitForEachStatement(IrForEachStatement statement, bool inFunction)
    {
        if (statement.Collection is not IrIdentifierExpression collection ||
            !_nativeArrayVariables.TryGetValue(SanitizeVariableName(collection.Name), out var arrayName))
        {
            _context.Error(AmbiguousShapeCode, "foreach requires a statically known native array.");
            return;
        }

        var item = SanitizeVariableName(statement.ItemName);
        if (statement.IndexName == null)
        {
            WriteLine($"for {item} in \"${{{arrayName}[@]}}\"; do");
            _indent++;
            EmitStatement(statement.Body, inFunction);
            _indent--;
            WriteLine("done");
            return;
        }

        var index = SanitizeVariableName(statement.IndexName);
        WriteLine($"for {index} in \"${{!{arrayName}[@]}}\"; do");
        _indent++;
        WriteLine($"{item}=\"${{{arrayName}[{index}]-}}\"");
        EmitStatement(statement.Body, inFunction);
        _indent--;
        WriteLine("done");
    }

    private void EmitDoWhileStatement(IrDoWhileStatement statement, bool inFunction)
    {
        WriteLine("while true; do");
        _indent++;
        EmitStatement(statement.Body, inFunction);
        var condition = PrepareCondition(statement.Condition, inFunction);
        WriteLine($"if ! {condition}; then");
        _indent++;
        WriteLine("break");
        _indent--;
        WriteLine("fi");
        _indent--;
        WriteLine("done");
    }

    private void EmitFunctionDeclaration(IrFunctionDeclarationStatement statement, string? headerComment = null)
    {
        var suffix = string.IsNullOrEmpty(headerComment) ? string.Empty : $" # {headerComment}";
        WriteLine($"{SanitizeFunctionName(statement.Name)}() {{{suffix}");
        _indent++;

        var previousFunctionName = _currentFunctionName;
        var previousFunctionRole = _currentFunctionRole;
        var previousReturnType = _currentFunctionReturnType;
        var previousFunctionReturnsValue = _currentFunctionReturnsValue;
        var previousOutputName = _currentOutputName;
        var previousKnownIntegers = _knownIntegerVariables;
        var previousKnownFloats = _knownFloatVariables;
        var previousNativeArrays = _nativeArrayVariables;
        var previousNativeObjects = _nativeObjectVariables;
        var previousRecords = _recordVariables;
        var previousIntegerArrays = _integerArrayVariables;
        var previousZshObjectParameters = _zshObjectParameterNames;
        var previousZshReadOnlyParameters = _zshReadOnlyObjectParameters;
        var previousNativeObjectAliases = _nativeObjectAliases;
        _currentFunctionName = statement.Name;
        _currentFunctionRole = statement.Role;
        _currentFunctionReturnType = statement.ReturnType;
        _currentFunctionReturnsValue = FunctionReturnsValue(statement);
        _currentOutputName = _currentFunctionReturnsValue
            ? AllocateFunctionOutputName(statement)
            : "";
        _knownIntegerVariables = new HashSet<string>(StringComparer.Ordinal);
        _knownFloatVariables = new HashSet<string>(StringComparer.Ordinal);
        _nativeArrayVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        _nativeObjectVariables = new HashSet<string>(StringComparer.Ordinal);
        _recordVariables = new HashSet<string>(StringComparer.Ordinal);
        _integerArrayVariables = new HashSet<string>(StringComparer.Ordinal);
        _zshObjectParameterNames = new Dictionary<string, string>(StringComparer.Ordinal);
        _zshReadOnlyObjectParameters = new HashSet<string>(StringComparer.Ordinal);
        _nativeObjectAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        _positionalParameterReferences = new Dictionary<string, int>(StringComparer.Ordinal);

        var isConstructor = statement.Role == IrFunctionRole.Constructor;
        var mutatesReceiver = FunctionMutatesReceiver(statement.Body);
        var returnsObject = !isConstructor && IsNamedObjectType(statement.ReturnType);
        var argIndex = _currentFunctionReturnsValue ? 2 : 1;
        var parameterBindings = new List<string>();
        var parameterContracts = new List<(IrTypeRef Type, string Value, string Description)>();
        if (isConstructor)
        {
            if (_dialect.IsZsh)
            {
                parameterBindings.Add("local this_name=\"$1\"");
                parameterBindings.Add("local -A this=()");
            }
            else
            {
                parameterBindings.Add("local -n this=\"$1\"");
            }
            _nativeObjectVariables.Add("this");
        }
        else if (returnsObject)
        {
            if (_dialect.IsZsh)
                parameterBindings.Add($"local {_currentOutputName}=\"$1\"");
            else
                parameterBindings.Add($"local -n {_currentOutputName}=\"$1\"");
        }
        else if (_currentFunctionReturnsValue)
        {
            parameterBindings.Add(_dialect.IsZsh
                ? $"local {_currentOutputName}=\"$1\""
                : $"local -n {_currentOutputName}=\"$1\"");
        }
        foreach (var parameter in statement.Parameters)
        {
            var param = SanitizeVariableName(parameter.Name);
            if (parameter.Name.StartsWith("__sushi_enum_field_", StringComparison.Ordinal))
            {
                _positionalParameterReferences[param] = argIndex;
                argIndex++;
                continue;
            }
            if (parameter.IsVarargs)
            {
                parameterBindings.Add($"local -a {param}=(\"${{@:{argIndex}}}\")");
                _nativeArrayVariables[param] = param;
                if (parameter.DeclaredType.Name == "int") _integerArrayVariables.Add(param);
            }
            else
            {
                if (parameter.DeclaredType.Kind == IrTypeKind.Structural)
                {
                    foreach (var field in parameter.DeclaredType.StructuralFields)
                    {
                        var fieldName = $"{param}_{SanitizeVariableName(field.Name)}";
                        var prefix = field.Type.Name == "int" ? "local -i " : "local ";
                        parameterBindings.Add($"{prefix}{fieldName}=\"${argIndex}\"");
                        argIndex++;
                    }
                    _recordVariables.Add(param);
                    continue;
                }
                else if (parameter.DeclaredType.Name == "array" || IsNativeObjectType(parameter.DeclaredType))
                {
                    if (_dialect.IsZsh)
                    {
                        var referenceName = $"{param}_name";
                        parameterBindings.Add($"local {referenceName}=\"${argIndex}\"");
                        _zshObjectParameterNames[param] = referenceName;
                        if (parameter.Name == "this" && !mutatesReceiver)
                            _zshReadOnlyObjectParameters.Add(param);
                        else
                            parameterBindings.Add($"local -A {param}=( \"${{(@kvP){referenceName}}}\" )");
                    }
                    else
                    {
                        parameterBindings.Add($"local -n {param}=\"${argIndex}\"");
                    }
                    if (parameter.DeclaredType.Name == "array") _nativeArrayVariables[param] = param;
                    else if (!_zshReadOnlyObjectParameters.Contains(param)) _nativeObjectVariables.Add(param);
                }
                else if (parameter.DeclaredType.Name == "int")
                {
                    parameterBindings.Add($"local -i {param}=\"${argIndex}\"");
                }
                else
                {
                    parameterBindings.Add($"local {param}=\"${argIndex}\"");
                }
                argIndex++;
                parameterContracts.Add((
                    parameter.DeclaredType,
                    $"\"${{{param}:-}}\"",
                    $"parameter '{parameter.Name}' of function '{statement.Name}'"));
            }

            if (parameter.DeclaredType.Kind == IrTypeKind.Primitive &&
                parameter.DeclaredType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true)
            {
                _knownIntegerVariables.Add(param);
            }
            else if (parameter.DeclaredType.Kind == IrTypeKind.Primitive &&
                     parameter.DeclaredType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true)
            {
                _knownFloatVariables.Add(param);
            }
        }

        if (parameterBindings.Count > 0)
            WriteLine(string.Join("; ", parameterBindings));
        foreach (var contract in parameterContracts)
            EmitContractCheckForValue(contract.Type, contract.Value, contract.Description);

        EmitStatement(statement.Body, inFunction: true);
        if (!EndsWithReturn(statement.Body))
        {
            EmitZshObjectParameterWritebacks();
            if (_currentFunctionReturnsValue && !isConstructor)
                EmitFunctionOutputAssignment("''");
            WriteLine("return 0");
        }
        _currentFunctionName = previousFunctionName;
        _currentFunctionRole = previousFunctionRole;
        _currentFunctionReturnType = previousReturnType;
        _currentFunctionReturnsValue = previousFunctionReturnsValue;
        _currentOutputName = previousOutputName;
        _knownIntegerVariables = previousKnownIntegers;
        _knownFloatVariables = previousKnownFloats;
        _nativeArrayVariables = previousNativeArrays;
        _nativeObjectVariables = previousNativeObjects;
        _recordVariables = previousRecords;
        _integerArrayVariables = previousIntegerArrays;
        _zshObjectParameterNames = previousZshObjectParameters;
        _zshReadOnlyObjectParameters = previousZshReadOnlyParameters;
        _nativeObjectAliases = previousNativeObjectAliases;
        _indent--;
        WriteLine("}");
    }

    private string EmitGeneratedFunctionComment(IrFunctionDeclarationStatement function)
    {
        var name = function.Name;
        var isTypeMember = function.Role is IrFunctionRole.Method or IrFunctionRole.Constructor or IrFunctionRole.Adapter;
        var parameters = string.Join(", ", function.Parameters
            .Where(parameter => parameter.Name != "this" &&
                                !parameter.Name.StartsWith("__sushi_enum_field_", StringComparison.Ordinal))
            .Select(parameter => $"{FormatCommentType(parameter.DeclaredType)} {parameter.Name}{(parameter.IsVarargs ? "..." : string.Empty)}"));
        var ownerType = function.OwnerTypeId?.StartsWith("type:", StringComparison.Ordinal) == true
            ? function.OwnerTypeId["type:".Length..]
            : null;
        var displayName = function.Role switch
        {
            IrFunctionRole.Method when ownerType != null && name.StartsWith(NativeObjectMetadata.MethodPrefix + ownerType + "_", StringComparison.Ordinal) =>
                name[(NativeObjectMetadata.MethodPrefix.Length + ownerType.Length + 1)..],
            IrFunctionRole.Constructor when name.StartsWith("__sushi_new_", StringComparison.Ordinal) => ownerType ?? name["__sushi_new_".Length..],
            _ => name
        };
        var signature = $"{FormatCommentType(function.ReturnType)} {displayName}({parameters})";
        var headerSignature = function.Role switch
        {
            IrFunctionRole.Method => signature,
            IrFunctionRole.Constructor => $"constructor: {displayName}({parameters})",
            IrFunctionRole.Adapter => signature,
            _ => signature
        };
        if (isTypeMember)
        {
            var type = ownerType ?? name;
            if (_commentedTypes.Add(type))
            {
                WriteLine("");
                var kind = _enumTypeNames.Contains(type) ? "enum" : "class";
                WriteLine($"# --- {kind}: {type} ---");
            }
        }
        return headerSignature;
    }

    private static string FormatCommentType(IrTypeRef type)
    {
        if (type.ElementType != null)
            return $"{FormatCommentType(type.ElementType)}[]";
        return type.Kind switch
        {
            IrTypeKind.Any => "any",
            IrTypeKind.Unknown => "unknown",
            IrTypeKind.Structural => "object",
            _ => type.Name ?? "unknown"
        };
    }

    private static bool IsTypeMemberFunction(string name) =>
        name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal) ||
        name.StartsWith("__sushi_new_", StringComparison.Ordinal) ||
        name.StartsWith("__sushi_adapter_", StringComparison.Ordinal);

    private static HashSet<string> CollectClassTypeNames(IEnumerable<IrStatement> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            if (statement is IrClassDeclarationStatement declaration) names.Add(declaration.Name);
            if (statement is IrBlockStatement block) names.UnionWith(CollectClassTypeNames(block.Statements));
        }
        return names;
    }

    private static HashSet<string> CollectEnumTypeNames(IEnumerable<IrStatement> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case IrEnumDeclarationStatement declaration:
                    names.Add(declaration.Name);
                    break;
                case IrRichEnumDeclarationStatement declaration:
                    names.Add(declaration.Name);
                    break;
                case IrBlockStatement block:
                    names.UnionWith(CollectEnumTypeNames(block.Statements));
                    break;
            }
        }
        return names;
    }

    private void EnsureTopLevelSection(bool inFunction)
    {
        if (inFunction || _emittedTopLevelSection) return;
        WriteLine("");
        WriteLine("# --- script body ---");
        _emittedTopLevelSection = true;
    }

    private static bool IsEnumValueInitializer(IrExpression? initializer, out string type, out string value)
    {
        if (initializer is IrObjectLiteralExpression objectLiteral)
        {
            var name = objectLiteral.Properties.FirstOrDefault(property => property.Name == NativeObjectMetadata.EnumName)?.Value;
            if (name is IrLiteralExpression { Value: string enumValue })
            {
                type = "enum";
                value = enumValue;
                return true;
            }
        }
        type = "";
        value = "";
        return false;
    }

    private bool TryGetGeneratedEnumValue(IrExpression? initializer, out string type, out string value)
    {
        if (initializer is IrConstructionExpression construction &&
            construction.TypeSymbolId is { } typeId &&
            typeId.StartsWith("type:", StringComparison.Ordinal) &&
            construction.EnumValueName is { } enumValue)
        {
            type = typeId["type:".Length..];
            value = enumValue;
            return true;
        }
        type = "";
        value = "";
        return false;
    }

    private static bool EndsWithReturn(IrBlockStatement block) =>
        block.Statements.LastOrDefault() is IrReturnStatement;

    private static bool FunctionReturnsValue(IrFunctionDeclarationStatement function) =>
        function.Role == IrFunctionRole.Constructor ||
        IsNamedObjectType(function.ReturnType) ||
        ContainsValueReturn(function.Body);

    private static bool ContainsValueReturn(IrStatement statement) => statement switch
    {
        IrReturnStatement { Expression: not null } => true,
        IrBlockStatement block => block.Statements.Any(ContainsValueReturn),
        IrIfStatement conditional => ContainsValueReturn(conditional.ThenBlock) ||
                                   (conditional.ElseBlock != null && ContainsValueReturn(conditional.ElseBlock)),
        _ => false
    };

    private string AllocateFunctionOutputName(IrFunctionDeclarationStatement function)
    {
        var parameterNames = function.Parameters
            .Select(parameter => SanitizeVariableName(parameter.Name))
            .ToHashSet(StringComparer.Ordinal);
        if (!parameterNames.Contains("out")) return "out";

        var suffix = 2;
        while (parameterNames.Contains($"out_{suffix}")) suffix++;
        return $"out_{suffix}";
    }

    private void EmitReturn(IrReturnStatement statement, bool inFunction)
    {
        if (inFunction && _currentFunctionRole == IrFunctionRole.Constructor)
        {
            if (_dialect.IsZsh)
            {
                WriteLine("typeset -gA $this_name");
                WriteLine("set -A $this_name \"${(@kv)this}\"");
            }
            WriteLine("return 0");
            return;
        }
        if (inFunction) EmitZshObjectParameterWritebacks();
        if (inFunction && IsNamedObjectType(_currentFunctionReturnType) && statement.Expression != null)
        {
            var source = statement.Expression switch
            {
                IrIdentifierExpression returnedObject
                    when _nativeObjectVariables.Contains(SanitizeVariableName(returnedObject.Name)) =>
                    ResolveNativeObjectName(SanitizeVariableName(returnedObject.Name)),
                IrConstructionExpression construction => PrepareConstructionReference(construction, inFunction),
                _ => ""
            };
            if (source.Length > 0)
            {
                if (_dialect.IsZsh)
                {
                    WriteLine($"typeset -gA ${{{_currentOutputName}}}");
                    WriteLine($"set -A ${{{_currentOutputName}}} \"${{(@kv){source}}}\"");
                }
                else
                {
                    WriteLine($"{_currentOutputName}=()");
                    WriteLine($"for _key in \"${{!{source}[@]}}\"; do {_currentOutputName}[\"$_key\"]=\"${{{source}[$_key]}}\"; done");
                }
                WriteLine("return 0");
                return;
            }
        }
        if (statement.Expression != null)
        {
            if (inFunction && statement.Expression is IrFileQueryExecutionExpression queryExecution)
            {
                EmitFileQueryExecutionDeclaration(_currentOutputName, queryExecution, inFunction, outputAlreadyDeclared: true);
                WriteLine("return 0");
                return;
            }
            if (inFunction && IsBooleanValueExpression(statement.Expression))
            {
                EmitBooleanOutput(statement.Expression);
                WriteLine("return 0");
                return;
            }
                var value = PrepareValue(statement.Expression, inFunction);
            if (inFunction && _currentFunctionName != null && !_currentFunctionReturnType.IsAnyOrUnknown)
            {
                EmitFunctionOutputAssignment(value);
                EmitContractCheckForValue(
                    _currentFunctionReturnType,
                    value,
                    $"return value of function '{_currentFunctionName}'");
            }
            else if (inFunction)
            {
                EmitFunctionOutputAssignment(value);
            }
            else
            {
                WriteLine($"printf '%s\\n' {value}");
            }
        }
        else if (inFunction)
        {
            if (_currentFunctionReturnsValue) EmitFunctionOutputAssignment("''");
        }

        if (inFunction) WriteLine("return 0");
        else WriteLine("exit 0");
    }

    private void EmitExpressionStatement(IrExpression expression, bool inFunction)
    {
        switch (expression)
        {
            case IrIntrinsicCallExpression intrinsicCall:
                if (intrinsicCall.Id is IntrinsicId.Print or IntrinsicId.Println)
                {
                    if (intrinsicCall.Arguments.FirstOrDefault() is IrIntrinsicCallExpression stringPredicate &&
                        stringPredicate.Id is IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or IntrinsicId.StringIsMatch)
                    {
                        EmitPrintedStringPredicate(stringPredicate, intrinsicCall.Id == IntrinsicId.Println, inFunction);
                        return;
                    }

                    if (intrinsicCall.Arguments.FirstOrDefault() is IrCallExpression directCall &&
                        directCall.Arguments.All(argument => argument.Value is IrLiteralExpression or IrIdentifierExpression))
                    {
                        var directValue = PrepareValue(directCall, inFunction);
                        WriteLine(intrinsicCall.Id == IntrinsicId.Println
                            ? $"printf '%s\\n' {directValue}"
                            : $"printf '%s' {directValue}");
                        return;
                    }

                    if (intrinsicCall.Arguments.FirstOrDefault() is { } booleanValue &&
                        IsBooleanValueExpression(booleanValue))
                    {
                        var condition = PrepareCondition(booleanValue, inFunction);
                        var suffix = intrinsicCall.Id == IntrinsicId.Println ? "\\n" : string.Empty;
                        WriteLine($"{condition} && printf '%s{suffix}' 'true' || printf '%s{suffix}' 'false'");
                        return;
                    }

                    var value = intrinsicCall.Arguments.Count == 0
                        ? "''"
                        : PrepareValue(intrinsicCall.Arguments[0], inFunction);
                    WriteLine(intrinsicCall.Id == IntrinsicId.Println
                        ? $"printf '%s\\n' {value}"
                        : $"printf '%s' {value}");
                }
                else
                {
                    WriteLine(EmitIntrinsicCommand(intrinsicCall));
                }
                return;

            case IrCallExpression call:
                _ = PrepareValue(call, inFunction);
                return;

            case IrResolvedMethodCallExpression method:
                _ = PrepareValue(method, inFunction);
                return;

            case IrAdapterCallExpression adapter:
                _ = PrepareValue(adapter, inFunction);
                return;

            case IrConversionExpression conversion:
                _ = PrepareValue(conversion, inFunction);
                return;

            case IrAssignmentExpression assignment:
                EmitPreparedAssignment(assignment, inFunction);
                return;

            case IrMemberAssignmentExpression assignment:
                EmitMemberAssignment(assignment, inFunction);
                return;

            case IrUnaryExpression unary when unary.Operator is "++" or "--":
                if (unary.Operand is IrIdentifierExpression identifier)
                {
                    var op = unary.Operator == "++" ? "+" : "-";
                    var name = SanitizeVariableName(identifier.Name);
                    WriteLine($"{name}=$(( {name} {op} 1 ))");
                    _knownIntegerVariables.Add(name);
                    return;
                }
                break;

            case IrMethodCallExpression methodCall:
                if (methodCall.MethodName == "push" && methodCall.Target is IrIdentifierExpression targetIdentifier)
                {
                    var targetName = SanitizeVariableName(targetIdentifier.Name);
                    if (_nativeArrayVariables.TryGetValue(targetName, out var nativeArray))
                    {
                        var values = methodCall.Arguments.Select(argument => PrepareValue(argument.Value, inFunction));
                        WriteLine($"{nativeArray}+=({string.Join(" ", values)})");
                        return;
                    }
                    var value = PrepareValue(methodCall, inFunction);
                    WriteLine($"{targetName}={value}");
                    return;
                }

                _ = PrepareValue(methodCall, inFunction);
                return;
        }

        _context.Error(UnsupportedEmitCode, $"Unsupported expression statement in Bash emitter: {expression.GetType().Name}");
    }
}
