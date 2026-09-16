namespace Sushi.Transpilation.Backends;

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed class BashEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1100";
    private const string AmbiguousShapeCode = "SUSHI1030";

    private readonly StringBuilder _builder = new();
    private readonly bool _zshMode;
    private EmitContext _context = null!;
    private int _indent;
    private int _valueTempId;
    private string? _currentFunctionName;
    private IrFunctionRole _currentFunctionRole = IrFunctionRole.Function;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;
    private HashSet<string> _knownIntegerVariables = new(StringComparer.Ordinal);
    private HashSet<string> _knownFloatVariables = new(StringComparer.Ordinal);
    private HashSet<string> _integerReturningFunctions = new(StringComparer.Ordinal);
    private HashSet<string> _floatReturningFunctions = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeArrayVariables = new(StringComparer.Ordinal);
    private HashSet<string> _nativeObjectVariables = new(StringComparer.Ordinal);
    private HashSet<string> _recordVariables = new(StringComparer.Ordinal);
    private Dictionary<string, IrArrayLiteralExpression> _arrayInitializers = new(StringComparer.Ordinal);
    private Dictionary<string, IrFunctionDeclarationStatement> _functions = new(StringComparer.Ordinal);
    private HashSet<string> _integerArrayVariables = new(StringComparer.Ordinal);
    private Dictionary<string, string> _zshObjectParameterNames = new(StringComparer.Ordinal);
    private HashSet<string> _zshReadOnlyObjectParameters = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeObjectAliases = new(StringComparer.Ordinal);
    private TargetNameAllocator _names = null!;
    private Dictionary<string, string> _generatedFunctionNames = new(StringComparer.Ordinal);
    private bool _currentFunctionReturnsValue;
    private string _currentOutputName = "";
    private bool _needsDynamicMethodMetadata;
    private HashSet<string> _commentedTypes = new(StringComparer.Ordinal);
    private HashSet<string> _classTypeNames = new(StringComparer.Ordinal);
    private HashSet<string> _enumTypeNames = new(StringComparer.Ordinal);
    private bool _emittedTopLevelSection;
    private Dictionary<string, int> _positionalParameterReferences = new(StringComparer.Ordinal);
    private bool _nativeGlobHelper;

    public BashEmitter()
    {
        _zshMode = false;
    }

    public BashEmitter(bool zshMode)
    {
        _zshMode = zshMode;
    }

    public string Emit(IrProgram program, EmitContext context)
    {
        _builder.Clear();
        _names = new TargetNameAllocator(_zshMode ? TargetLanguage.Zsh : TargetLanguage.Bash, _zshMode);
        _generatedFunctionNames.Clear();
        _context = context;
        _indent = 0;
        _valueTempId = 0;
        _currentFunctionName = null;
        _currentFunctionRole = IrFunctionRole.Function;
        _currentFunctionReturnType = IrTypeRef.Any;
        _knownIntegerVariables.Clear();
        _knownFloatVariables.Clear();
        _nativeArrayVariables.Clear();
        _nativeObjectVariables.Clear();
        _recordVariables.Clear();
        _arrayInitializers.Clear();
        _functions = program.Statements
            .OfType<IrFunctionDeclarationStatement>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        _needsDynamicMethodMetadata = program.Statements.Any(ContainsDynamicMethodDispatch);
        _integerArrayVariables.Clear();
        _zshObjectParameterNames.Clear();
        _zshReadOnlyObjectParameters.Clear();
        _nativeObjectAliases.Clear();
        _commentedTypes.Clear();
        _classTypeNames = CollectClassTypeNames(program.Statements);
        _enumTypeNames = CollectEnumTypeNames(program.Statements);
        _emittedTopLevelSection = false;
        _positionalParameterReferences.Clear();
        _nativeGlobHelper = HasFsGlobImport(program);
        _integerReturningFunctions = program.Statements
            .OfType<IrFunctionDeclarationStatement>()
            .Where(function => function.ReturnType.Kind == IrTypeKind.Primitive &&
                               function.ReturnType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true)
            .Select(function => function.Name)
            .ToHashSet(StringComparer.Ordinal);
        _floatReturningFunctions = program.Statements
            .OfType<IrFunctionDeclarationStatement>()
            .Where(function => function.ReturnType.Kind == IrTypeKind.Primitive &&
                               function.ReturnType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true)
            .Select(function => function.Name)
            .ToHashSet(StringComparer.Ordinal);

        WriteLine(_zshMode ? "#!/usr/bin/env zsh" : "#!/usr/bin/env bash");
        if (_zshMode)
        {
            WriteLine("set -eu");
            WriteLine("set -o pipefail");
            if (EmissionCapabilityAnalyzer.UsesArrays(program))
            {
                WriteLine("setopt ksharrays");
            }
        }
        else
        {
            WriteLine("set -euo pipefail");
        }
        foreach (var import in program.Statements.OfType<IrStandardLibraryImportStatement>())
        {
            var importText = import.Members.Count == 0
                ? import.Module
                : $"{import.Module}.{{{string.Join(", ", import.Members)}}}";
            if (!string.IsNullOrWhiteSpace(import.Alias)) importText += $" as {import.Alias}";
            WriteLine($"# use {importText}");
        }
        if (EmissionCapabilityAnalyzer.UsesFsGlob(program) && HasFsGlobImport(program))
        {
            EmitNativeGlobHelper();
        }
        foreach (var statement in program.Statements)
        {
            EmitStatement(statement, inFunction: false);
        }

        return PrettyPrintBash(_builder.ToString());
    }

    private static bool HasFsGlobImport(IrProgram program)
    {
        var imports = program.Statements.OfType<IrStandardLibraryImportStatement>().ToList();
        return imports.Any(import => (import.Module.Equals("std.fs", StringComparison.Ordinal) || import.Module.Equals("std.fs.glob", StringComparison.Ordinal)) &&
            (import.Members.Count == 0 || import.Members.Contains("glob", StringComparer.Ordinal)));
    }

    private void EmitNativeGlobHelper()
    {
        if (_zshMode)
        {
            AppendStdlibHelperBlock("""
__sushi_fs_glob_into() {
  local out_name="${1-}" pattern="${2-}" cwd="${3-}" base="${PWD}" candidate
  typeset -n output="$out_name"
  output=()
  [[ -n "$cwd" && "$cwd" != 'null' ]] && base="$(cd -- "$cwd" && pwd -P)" || true
  [[ -d "$base" ]] || { print -u2 "std.fs.glob: directory not found: $cwd"; return 1; }
  local -a matches
  matches=( ${(N)~base/$pattern} )
  local item
  local -a ordered
  ordered=( "${matches[@]#$base/}" )
  output=( ${(on)ordered} )
}
""");
            return;
        }
        AppendStdlibHelperBlock("""
__sushi_fs_glob_into() {
  local out_name="${1-}" pattern="${2-}" cwd="${3-}" base="${PWD}" candidate
  local -n output="$out_name"
  output=()
  [[ -n "$cwd" && "$cwd" != 'null' ]] && base="$(cd -- "$cwd" && pwd -P)" || true
  [[ -d "$base" ]] || { printf 'std.fs.glob: directory not found: %s\n' "$cwd" >&2; return 1; }
  shopt -s globstar nullglob
  local -a matches=( "$base"/$pattern )
  for candidate in "${matches[@]}"; do
    [[ -e "$candidate" ]] || continue
    output+=("${candidate#$base/}")
  done
  IFS=$'\n' output=( $(printf '%s\n' "${output[@]}" | LC_ALL=C sort) )
}
""");
    }

    private void AppendStdlibHelperBlock(string text)
    {
        _builder.Append(text.Replace("\r\n", "\n"));
        if (!text.EndsWith("\n", StringComparison.Ordinal))
        {
            _builder.Append('\n');
        }
    }

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
                if (initializer is IrConstructionExpression constructor)
                {
                    WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
                    var arguments = constructor.Arguments.Select(argument => PrepareValue(argument.Value, inFunction));
                    WriteLine($"{SanitizeFunctionName(constructor.ConstructorName)} {Escape.BashSingleQuoted(name)} {string.Join(" ", arguments)}");
                    _nativeObjectVariables.Add(name);
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
                    if (inFunction) WriteLine($"local {name}");
                    EmitCallInto(name, valueCall, valueFunction, inFunction);
                    SetKnownInteger(name, declaredInt || valueFunction.ReturnType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true);
                    SetKnownFloat(name, declaredFloat || valueFunction.ReturnType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true);
                    break;
                }
                if (initializer is IrIdentifierExpression objectAlias &&
                    _nativeObjectVariables.Contains(SanitizeVariableName(objectAlias.Name)))
                {
                    var source = ResolveNativeObjectName(SanitizeVariableName(objectAlias.Name));
                    if (_zshMode)
                    {
                        WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=( \"${{(@kv){source}}}\" )");
                        _nativeObjectAliases[name] = source;
                    }
                    else
                        WriteLine($"{(inFunction ? "local " : "declare ")}-n {name}={Escape.BashSingleQuoted(source)}");
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
                    var entries = (_zshMode
                        ? properties.Select(property =>
                            $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
                        : properties.Select(property =>
                            $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")).ToList();
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

            case IrDoWhileStatement doWhileStatement:
                EmitDoWhileStatement(doWhileStatement, inFunction);
                break;

            case IrFunctionDeclarationStatement function:
                EmitGeneratedFunctionComment(function);
                var typeMemberIndent = function.Role is IrFunctionRole.Method or IrFunctionRole.Constructor or IrFunctionRole.Adapter;
                if (typeMemberIndent) _indent++;
                EmitFunctionDeclaration(function);
                if (typeMemberIndent) _indent--;
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

    private void EmitFunctionDeclaration(IrFunctionDeclarationStatement statement)
    {
        WriteLine($"{SanitizeFunctionName(statement.Name)}() {{");
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
        if (isConstructor)
        {
            if (_zshMode)
            {
                WriteLine("local this_name=\"$1\"");
                WriteLine("local -A this=()");
            }
            else
            {
                WriteLine("local -n this=\"$1\"");
            }
            _nativeObjectVariables.Add("this");
        }
        else if (returnsObject)
        {
            if (_zshMode)
                WriteLine($"local {_currentOutputName}=\"$1\"");
            else
                WriteLine($"local -n {_currentOutputName}=\"$1\"");
        }
        else if (_currentFunctionReturnsValue)
        {
            WriteLine(_zshMode
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
                WriteLine($"local -a {param}=(\"${{@:{argIndex}}}\")");
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
                        WriteLine($"{prefix}{fieldName}=\"${argIndex}\"");
                        argIndex++;
                    }
                    _recordVariables.Add(param);
                    continue;
                }
                else if (parameter.DeclaredType.Name == "array" || IsNativeObjectType(parameter.DeclaredType))
                {
                    if (_zshMode)
                    {
                        var referenceName = $"{param}_name";
                        WriteLine($"local {referenceName}=\"${argIndex}\"");
                        _zshObjectParameterNames[param] = referenceName;
                        if (parameter.Name == "this" && !mutatesReceiver)
                            _zshReadOnlyObjectParameters.Add(param);
                        else
                            WriteLine($"local -A {param}=( \"${{(@kvP){referenceName}}}\" )");
                    }
                    else
                    {
                        WriteLine($"local -n {param}=\"${argIndex}\"");
                    }
                    if (parameter.DeclaredType.Name == "array") _nativeArrayVariables[param] = param;
                    else if (!_zshReadOnlyObjectParameters.Contains(param)) _nativeObjectVariables.Add(param);
                }
                else if (parameter.DeclaredType.Name == "int")
                {
                    WriteLine($"local -i {param}=\"${argIndex}\"");
                }
                else
                {
                    WriteLine($"local {param}=\"${argIndex}\"");
                }
                argIndex++;
                EmitContractCheckForValue(
                    parameter.DeclaredType,
                    $"\"${{{param}:-}}\"",
                    $"parameter '{parameter.Name}' of function '{statement.Name}'");
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

    private void EmitGeneratedFunctionComment(IrFunctionDeclarationStatement function)
    {
        var name = function.Name;
        var isTypeMember = function.Role is IrFunctionRole.Method or IrFunctionRole.Constructor or IrFunctionRole.Adapter;
        var parameters = string.Join(", ", function.Parameters
            .Where(parameter => parameter.Name != "this" &&
                                !parameter.Name.StartsWith("__sushi_enum_field_", StringComparison.Ordinal))
            .Select(parameter => parameter.IsVarargs ? $"{parameter.Name}..." : parameter.Name));
        var signature = $"{name}({parameters})";
        var label = function.Role switch
        {
            IrFunctionRole.Method => $"method: {name}({parameters})",
            IrFunctionRole.Constructor => $"constructor: {name}({parameters})",
            IrFunctionRole.Adapter => $"adapter: {name}({parameters})",
            _ => $"function: {signature}"
        };
        if (isTypeMember)
        {
            var type = function.OwnerTypeId?.StartsWith("type:", StringComparison.Ordinal) == true
                ? function.OwnerTypeId["type:".Length..]
                : name;
            if (_commentedTypes.Add(type))
            {
                WriteLine("");
                var kind = _enumTypeNames.Contains(type) ? "enum" : "class";
                WriteLine($"# --- {kind}: {type} ---");
            }
        }
        WriteLine("");
        if (isTypeMember)
        {
            _indent++;
            WriteLine($"# {label}");
            _indent--;
        }
        else
        {
            WriteLine($"# {label}");
        }
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
            if (_zshMode)
            {
                WriteLine("typeset -gA $this_name");
                WriteLine("set -A $this_name \"${(@kv)this}\"");
            }
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
                if (_zshMode)
                {
                    WriteLine($"typeset -gA ${{{_currentOutputName}}}");
                    WriteLine($"set -A ${{{_currentOutputName}}} \"${{(@kv){source}}}\"");
                }
                else
                {
                    WriteLine($"{_currentOutputName}=()");
                    WriteLine($"for _key in \"${{!{source}[@]}}\"; do {_currentOutputName}[\"$_key\"]=\"${{{source}[$_key]}}\"; done");
                }
                return;
            }
        }
        if (statement.Expression != null)
        {
            if (inFunction && IsBooleanValueExpression(statement.Expression))
            {
                EmitBooleanOutput(statement.Expression);
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

        if (!inFunction) WriteLine("exit 0");
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
        var checkedCurrent = EmitCheckedInteger($"\"${{{name}:-}}\"", $"variable '{assignment.Target.Name}'");
        return $"{name}=$(( {checkedCurrent} {mathOp} {EmitArithmeticExpression(assignment.Value)} ))";
    }

    private void EmitPreparedAssignment(IrAssignmentExpression assignment, bool inFunction)
    {
        var name = SanitizeVariableName(assignment.Target.Name);
        if (assignment.Operator == "=")
        {
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
                if (_zshMode)
                {
                    WriteLine($"{name}=( \"${{(@kv){source}}}\" )");
                    _nativeObjectAliases[name] = source;
                }
                else
                {
                    WriteLine(_nativeObjectAliases.ContainsKey(name) ? $"unset -n {name}" : $"unset {name}");
                    WriteLine($"{(inFunction ? "local " : "declare ")}-n {name}={Escape.BashSingleQuoted(source)}");
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
                var entries = _zshMode
                    ? obj.Properties.Select(property =>
                        $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
                    : obj.Properties.Select(property =>
                        $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}");
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
        var arguments = new List<string> { Escape.BashSingleQuoted(destination) };
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
                arguments.Add(Escape.BashSingleQuoted(ResolveNativeObjectName(SanitizeVariableName(identifier.Name))));
            else if (argument is IrConstructionExpression construction && parameter != null && IsNativeObjectType(parameter.DeclaredType))
                arguments.Add(Escape.BashSingleQuoted(PrepareConstructionReference(construction, inFunction)));
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
        WriteLine($"{SanitizeFunctionName(construction.ConstructorName)} {Escape.BashSingleQuoted(name)} {string.Join(" ", arguments)}");
        _nativeObjectVariables.Add(name);
        return name;
    }

    private void EmitZshObjectParameterWritebacks()
    {
        if (!_zshMode) return;
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
        if (_zshMode)
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
            case IntrinsicId.FsGlob:
            {
                var pattern = PrepareValue(intrinsic.Arguments[0], inFunction);
                var cwd = intrinsic.Arguments.Count > 1 ? intrinsic.Arguments[1] : new IrLiteralExpression(null);
                var root = PrepareValue(cwd, inFunction);
                WriteLine($"{arrayDeclaration}-a {name}=()");
                if (_nativeGlobHelper)
                    WriteLine($"__sushi_fs_glob_into {name} {pattern} {root}");
                else
                {
                    WriteLine($"__sushi_fs_glob_into {pattern} {root}");
                    WriteLine($"while IFS= read -r __sushi_path; do {name}+=(\"$__sushi_path\"); done < <(__sushi_array_each_raw \"${{__sushi_result-}}\")");
                }
                _nativeArrayVariables[name] = name;
                _arrayInitializers.Remove(name);
                return true;
            }

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
                if (_zshMode)
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
            ? Escape.BashSingleQuoted(literal
                .Replace("\\d", "[0-9]", StringComparison.Ordinal)
                .Replace("\\s", "[[:space:]]", StringComparison.Ordinal)
                .Replace("\\w", "[[:alnum:]_]", StringComparison.Ordinal))
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
            invocation = $"timeout {Escape.BashSingleQuoted((timeoutMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + "s")} {invocation}";
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
                    $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}").ToList();
                EmitAssociativeObject(result, entries, inFunction ? "local " : "declare ", false);
                _nativeObjectVariables.Add(result);
                return Escape.BashSingleQuoted(result);
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
                    if (_zshMode && _zshReadOnlyObjectParameters.Contains(sourceName) &&
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
                var targetReference = isArray ? $"{targetName}[@]" : targetName;
                var startText = slice.Start == null ? "0" : EmitNativeSliceBound(slice.Start);
                if (slice.End == null)
                    return $"\"${{{targetReference}:{startText}}}\"";
                var lengthText = $"({EmitNativeSliceBound(slice.End)} - ({startText}))";
                return $"\"${{{targetReference}:{startText}:{lengthText}}}\"";
            }

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
                        arguments.Add(Escape.BashSingleQuoted(PrepareConstructionReference(construction, inFunction)));
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
                        arguments.Add(Escape.BashSingleQuoted(ResolveNativeObjectName(SanitizeVariableName(aggregateIdentifier2.Name))));
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

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id == IntrinsicId.FsGlob:
                return PrepareIntrinsicInto(intrinsic, inFunction, "__sushi_fs_glob_into", 2);

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id == IntrinsicId.ProcessRun:
                return PrepareIntrinsicInto(intrinsic, inFunction, "__sushi_process_run_into", 8);

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id == IntrinsicId.ProcessPipeline:
                return PrepareIntrinsicInto(intrinsic, inFunction, "__sushi_process_pipeline_into", 7);

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
        if (_zshMode)
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
        var entries = (_zshMode
            ? properties.Select(property =>
                $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
            : properties.Select(property =>
                $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")).ToList();
        EmitAssociativeObject(name, entries, inFunction ? "local " : "declare ", false);
        _nativeObjectVariables.Add(name);
    }

    private IReadOnlyList<IrObjectProperty> MetadataProperties(IReadOnlyList<IrObjectProperty> properties)
    {
        if (_needsDynamicMethodMetadata) return properties;
        return properties.Where(property => !property.Name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal)).ToList();
    }

    private static bool ContainsDynamicMethodDispatch(IrStatement statement) => statement switch
    {
        IrBlockStatement block => block.Statements.Any(ContainsDynamicMethodDispatch),
        IrExpressionStatement expression => ContainsDynamicMethodDispatch(expression.Expression),
        IrVariableDeclarationStatement variable => variable.Initializer != null && ContainsDynamicMethodDispatch(variable.Initializer),
        IrIfStatement conditional => ContainsDynamicMethodDispatch(conditional.Condition) ||
                                     ContainsDynamicMethodDispatch(conditional.ThenBlock) ||
                                     (conditional.ElseBlock != null && ContainsDynamicMethodDispatch(conditional.ElseBlock)),
        IrWhileStatement loop => ContainsDynamicMethodDispatch(loop.Condition) || ContainsDynamicMethodDispatch(loop.Body),
        IrDoWhileStatement loop => ContainsDynamicMethodDispatch(loop.Condition) || ContainsDynamicMethodDispatch(loop.Body),
        IrForStatement loop => (loop.Initializer != null && ContainsDynamicMethodDispatch(loop.Initializer)) ||
                              (loop.Condition != null && ContainsDynamicMethodDispatch(loop.Condition)) ||
                              (loop.Increment != null && ContainsDynamicMethodDispatch(loop.Increment)) ||
                              ContainsDynamicMethodDispatch(loop.Body),
        IrFunctionDeclarationStatement function => ContainsDynamicMethodDispatch(function.Body),
        IrReturnStatement result => result.Expression != null && ContainsDynamicMethodDispatch(result.Expression),
        _ => false
    };

    private static bool ContainsDynamicMethodDispatch(IrExpression expression) => expression switch
    {
        IrMethodCallExpression => true,
        IrCallExpression call => call.Arguments.Any(argument => ContainsDynamicMethodDispatch(argument.Value)),
        IrResolvedMethodCallExpression call => ContainsDynamicMethodDispatch(call.Target) ||
                                               call.Arguments.Any(argument => ContainsDynamicMethodDispatch(argument.Value)),
        IrAdapterCallExpression call => ContainsDynamicMethodDispatch(call.Value),
        IrAssignmentExpression assignment => ContainsDynamicMethodDispatch(assignment.Value),
        IrMemberAssignmentExpression assignment => ContainsDynamicMethodDispatch(assignment.Target) || ContainsDynamicMethodDispatch(assignment.Value),
        IrBinaryExpression binary => ContainsDynamicMethodDispatch(binary.Left) || ContainsDynamicMethodDispatch(binary.Right),
        IrUnaryExpression unary => ContainsDynamicMethodDispatch(unary.Operand),
        IrConditionalExpression conditional => ContainsDynamicMethodDispatch(conditional.Condition) ||
                                               ContainsDynamicMethodDispatch(conditional.TrueExpression) ||
                                               ContainsDynamicMethodDispatch(conditional.FalseExpression),
        IrMemberAccessExpression member => ContainsDynamicMethodDispatch(member.Target),
        IrIndexExpression index => ContainsDynamicMethodDispatch(index.Target) || ContainsDynamicMethodDispatch(index.Index),
        IrArrayLiteralExpression array => array.Elements.Any(ContainsDynamicMethodDispatch),
        IrObjectLiteralExpression obj => obj.Properties.Any(property => ContainsDynamicMethodDispatch(property.Value)),
        IrTruthinessExpression truthiness => ContainsDynamicMethodDispatch(truthiness.Operand),
        _ => false
    };

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

    private string PrepareIntrinsicInto(
        IrIntrinsicCallExpression intrinsic,
        bool inFunction,
        string helper,
        int argumentCount)
    {
        var arguments = new List<string>(argumentCount);
        for (var index = 0; index < argumentCount; index++)
        {
            arguments.Add(index < intrinsic.Arguments.Count
                ? PrepareValue(intrinsic.Arguments[index], inFunction)
                : "''");
        }

        WriteLine($"{helper} {string.Join(" ", arguments)}");
        var result = DeclareTemp("\"${__sushi_result-}\"", inFunction);
        return $"\"${{{result}-}}\"";
    }

    private static string? GetKnownJsonKind(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression { Value: null } => "null",
            IrLiteralExpression { Value: string or char } => "string",
            IrLiteralExpression { Value: bool } => "bool",
            IrLiteralExpression { Value: sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal } => "number",
            IrArrayLiteralExpression => "array",
            IrObjectLiteralExpression => "object",
            _ => null
        };
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
                value = EscapeBashDoubleQuotedContent(text);
                return true;
            case IrLiteralExpression { Value: char character }:
                value = EscapeBashDoubleQuotedContent(character.ToString());
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
                if (_zshMode && _zshReadOnlyObjectParameters.Contains(sourceName) &&
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

    private static string EscapeBashDoubleQuotedContent(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("$", "\\$", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal);

    private string EmitObjectSubscript(string memberName) =>
        _zshMode ? memberName : Escape.BashSingleQuoted(memberName);

    private string PrepareCondition(IrExpression expression, bool inFunction)
    {
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
                    WriteLine(_zshMode ? $"{result}=\"${{(L){result}}}\"" : $"{result}=\"${{{result},,}}\"");
                    break;
                case IntrinsicId.StringUpper:
                    WriteLine(_zshMode ? $"{result}=\"${{(U){result}}}\"" : $"{result}=\"${{{result}^^}}\"");
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
            IrIdentifierExpression identifier => EmitVariableValue(identifier.Name),
            IrArrayLiteralExpression array => EmitArrayLiteral(array),
            IrObjectLiteralExpression obj => EmitObjectLiteral(obj),
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
            IrBinaryExpression binary when binary.Operator is "+" =>
                $"\"$(__sushi_add {EmitValueExpression(binary.Left)} {EmitValueExpression(binary.Right)})\"",
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
        var targetReference = isArray ? $"{targetName}[@]" : target;
        var start = slice.Start == null ? "0" : EmitNativeSliceBound(slice.Start);
        if (slice.End == null)
            return $"\"${{{targetReference}:{start}}}\"";
        var length = $"({EmitNativeSliceBound(slice.End)} - ({start}))";
        return $"\"${{{targetReference}:{start}:{length}}}\"";
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
        {
            var target = index.Target is IrIdentifierExpression stringIdentifier
                ? SanitizeVariableName(stringIdentifier.Name)
                : DeclareTemp(EmitValueExpression(index.Target), _currentFunctionName != null);
            var subscript = index.Index switch
            {
                IrLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
                IrIdentifierExpression id => SanitizeVariableName(id.Name),
                _ => EmitArithmeticExpression(index.Index)
            };
            return $"\"${{{target}:{subscript}:1}}\"";
        }

        _context.Error(AmbiguousShapeCode,
            "Indexing requires a statically known native array or string",
            index.Origin?.Line ?? 1, index.Origin?.Column ?? 1);
        return "''";
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
        if (_zshMode && member.Target is IrIdentifierExpression identifier)
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
            $"[{Escape.BashSingleQuoted(property.Name)}]={EmitValueExpression(property.Value)}").ToList();
        EmitAssociativeObject(name, entries, _currentFunctionName != null ? "local " : "declare ", false);
        _nativeObjectVariables.Add(name);
        return Escape.BashSingleQuoted(name);
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
            IrLiteralExpression literal => EmitCheckedInteger(EmitLiteral(literal.Value), "arithmetic literal"),
            IrIdentifierExpression identifier => EmitCheckedInteger(
                $"\"${{{SanitizeVariableName(identifier.Name)}:-}}\"",
                $"variable '{identifier.Name}'"),
            IrMemberAccessExpression { Target: IrIdentifierExpression target } member
                when _nativeObjectVariables.Contains(SanitizeVariableName(target.Name)) =>
                member.ValueType.Name == "int"
                    ? $"${{{ResolveNativeObjectName(SanitizeVariableName(target.Name))}[{EmitObjectSubscript(member.MemberName)}]:-0}}"
                    : EmitCheckedInteger(
                        $"\"${{{ResolveNativeObjectName(SanitizeVariableName(target.Name))}[{EmitObjectSubscript(member.MemberName)}]-}}\"",
                        $"member '{member.MemberName}'"),
            IrUnaryExpression unary when unary.Operator is "+" or "-" =>
                $"{unary.Operator}{EmitArithmeticExpression(unary.Operand)}",
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                $"({EmitArithmeticExpression(binary.Left)} {binary.Operator} {EmitArithmeticExpression(binary.Right)})",
            _ => EmitCheckedInteger(EmitValueExpression(expression), "arithmetic operand")
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

    private string EmitCheckedInteger(string valueExpression, string context)
    {
        return $"$(__sushi_require_integer {valueExpression} {Escape.BashSingleQuoted(context)})";
    }

    private string EmitLiteral(object? value)
    {
        return value switch
        {
            null => "''",
            string str => Escape.BashSingleQuoted(str),
            char ch => Escape.BashSingleQuoted(ch.ToString()),
            bool boolean => Escape.BashSingleQuoted(boolean ? "true" : "false"),
            int or long or double or float or decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            _ => Escape.BashSingleQuoted(value.ToString() ?? "")
        };
    }

    private void WriteLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _builder.Append('\n');
            return;
        }

        _builder.Append(' ', _indent * 4);
        _builder.Append(text);
        _builder.Append('\n');
    }

    private static string PrettyPrintBash(string source)
    {
        var output = new StringBuilder(source.Length + 256);
        foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
        {
            if (TryExpandInlineIf(line, output) ||
                TryExpandInlineLoop(line, output) ||
                TryExpandInlineGuard(line, output))
            {
                continue;
            }

            output.AppendLine(line);
        }

        return output.ToString();
    }

    private static bool TryExpandInlineIf(string line, StringBuilder output)
    {
        var match = Regex.Match(line, @"^(?<indent>\s*)if (?<condition>.+?); then (?<true>.+?);(?: else (?<false>.+?);)? fi$");
        if (!match.Success) return false;

        var indent = match.Groups["indent"].Value;
        output.AppendLine($"{indent}if {match.Groups["condition"].Value}; then");
        output.AppendLine($"{indent}    {match.Groups["true"].Value}");
        if (match.Groups["false"].Success)
        {
            output.AppendLine($"{indent}else");
            output.AppendLine($"{indent}    {match.Groups["false"].Value}");
        }
        output.AppendLine($"{indent}fi");
        return true;
    }

    private static bool TryExpandInlineLoop(string line, StringBuilder output)
    {
        var match = Regex.Match(line, @"^(?<indent>\s*)(?<kind>for|while) (?<header>.+?); do (?<body>.+?); done$");
        if (!match.Success) return false;

        var indent = match.Groups["indent"].Value;
        output.AppendLine($"{indent}{match.Groups["kind"].Value} {match.Groups["header"].Value}; do");
        output.AppendLine($"{indent}    {match.Groups["body"].Value}");
        output.AppendLine($"{indent}done");
        return true;
    }

    private static bool TryExpandInlineGuard(string line, StringBuilder output)
    {
        var match = Regex.Match(line, @"^(?<indent>\s*)(?<command>.+?) \|\| \{ (?<status>[^;]+); (?<failure>.+); \}$");
        if (!match.Success || match.Groups["command"].Value.Contains("||", StringComparison.Ordinal)) return false;

        var indent = match.Groups["indent"].Value;
        output.AppendLine($"{indent}{match.Groups["command"].Value} || {{");
        output.AppendLine($"{indent}    {match.Groups["status"].Value};");
        output.AppendLine($"{indent}    {match.Groups["failure"].Value};");
        output.AppendLine($"{indent}}}");
        return true;
    }

    private string SanitizeFunctionName(string name)
    {
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

    private string EmitIntrinsicCommand(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => EmitPrint(call.Arguments, newline: false),
            IntrinsicId.Println => EmitPrint(call.Arguments, newline: true),
            IntrinsicId.StringTrim => $"{EmitStringTrimInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringLower => $"{EmitStringLowerInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringUpper => $"{EmitStringUpperInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringLength => $"{EmitStringLengthInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringSplit => $"{EmitStringSplitInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringContains => $"{EmitStringContainsInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringStartsWith => $"{EmitStringStartsWithInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringEndsWith => $"{EmitStringEndsWithInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringReplace => $"{EmitStringReplaceInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringIsMatch => $"{EmitStringIsMatchInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringMatch => $"{EmitStringMatchInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.IoWriteText => EmitIoWriteText(call.Arguments),
            IntrinsicId.EnvSet => EmitEnvSet(call.Arguments),
            IntrinsicId.EnvUnset => EmitEnvUnset(call.Arguments),
            IntrinsicId.ProcessExit => EmitProcessExit(call.Arguments),
            IntrinsicId.ProcessSleep => EmitProcessSleep(call.Arguments),
            IntrinsicId.ConsoleError => EmitConsoleError(call.Arguments),
            IntrinsicId.OsChdir => $"cd -- {Arg(call.Arguments, 0)}",
            IntrinsicId.ProcessRun => $"{EmitProcessRunInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessPipeline => $"{EmitProcessPipelineInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessFail => $"{EmitProcessFailInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessRequireSuccess => $"{EmitProcessRequireSuccessInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.FsGlob => $"{EmitFsGlobInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.FsCreateDirectory => EmitFsCreateDirectory(call.Arguments),
            IntrinsicId.FsRemove => EmitFsRemove(call.Arguments),
            IntrinsicId.FsCopy => EmitFsCopy(call.Arguments),
            IntrinsicId.FsMove => EmitFsMove(call.Arguments),
            IntrinsicId.ArchiveZip => EmitArchiveZip(call.Arguments),
            IntrinsicId.ArchiveUnzip => EmitArchiveUnzip(call.Arguments),
            IntrinsicId.HttpGet => $"{EmitHttpGetInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.HttpPost => $"{EmitHttpPostInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.HttpDownload => EmitHttpDownload(call.Arguments),
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Intrinsic '{call.CanonicalName}' cannot be emitted as a statement in Bash")
        };
    }

    private string EmitIntrinsicValue(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => $"$({EmitPrint(call.Arguments, newline: false)})",
            IntrinsicId.Println => $"$({EmitPrint(call.Arguments, newline: true)})",
            IntrinsicId.StringTrim => $"\"$({EmitStringTrimInvocation(call.Arguments)})\"",
            IntrinsicId.StringLower => $"\"$({EmitStringLowerInvocation(call.Arguments)})\"",
            IntrinsicId.StringUpper => $"\"$({EmitStringUpperInvocation(call.Arguments)})\"",
            IntrinsicId.StringLength => $"$({EmitStringLengthInvocation(call.Arguments)})",
            IntrinsicId.StringSplit => $"\"$({EmitStringSplitInvocation(call.Arguments)})\"",
            IntrinsicId.StringContains => $"\"$({EmitStringContainsInvocation(call.Arguments)})\"",
            IntrinsicId.StringStartsWith => $"\"$({EmitStringStartsWithInvocation(call.Arguments)})\"",
            IntrinsicId.StringEndsWith => $"\"$({EmitStringEndsWithInvocation(call.Arguments)})\"",
            IntrinsicId.StringReplace => $"\"$({EmitStringReplaceInvocation(call.Arguments)})\"",
            IntrinsicId.StringIsMatch => $"\"$({EmitStringIsMatchInvocation(call.Arguments)})\"",
            IntrinsicId.StringMatch => $"\"$({EmitStringMatchInvocation(call.Arguments)})\"",
            IntrinsicId.IoReadText => $"$(cat -- {Arg(call.Arguments, 0)})",
            IntrinsicId.IoExists => $"$([[ -e {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.FsIsFile => $"$([[ -f {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.FsIsDirectory => $"$([[ -d {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.FsSize => EmitFsSize(call.Arguments),
            IntrinsicId.PathJoin => EmitPathJoin(call.Arguments),
            IntrinsicId.PathDirname => $"$(dirname -- {Arg(call.Arguments, 0)})",
            IntrinsicId.PathBasename => $"$(basename -- {Arg(call.Arguments, 0)})",
            IntrinsicId.PathExtension => EmitPathExtension(call.Arguments),
            IntrinsicId.PathStem => EmitPathStem(call.Arguments),
            IntrinsicId.EnvGet => EmitEnvGet(call.Arguments),
            IntrinsicId.EnvHas => $"$(__sushi_env_name=$(printf '%s' {Arg(call.Arguments, 0)}); [[ -v $__sushi_env_name ]] && printf 'true' || printf 'false')",
            IntrinsicId.ProcessArgs => "\"$@\"",
            IntrinsicId.ProcessWhich => $"$(command -v -- {Arg(call.Arguments, 0)} 2>/dev/null || true)",
            IntrinsicId.ConsoleReadLine => "$(IFS= read -r __sushi_line; printf '%s' \"$__sushi_line\")",
            IntrinsicId.OsCwd => "$(pwd)",
            IntrinsicId.IoWriteText => $"$({EmitIoWriteText(call.Arguments)})",
            IntrinsicId.EnvSet => $"$({EmitEnvSet(call.Arguments)})",
            IntrinsicId.EnvUnset => $"$({EmitEnvUnset(call.Arguments)})",
            IntrinsicId.ProcessExit => $"$({EmitProcessExit(call.Arguments)})",
            IntrinsicId.ProcessSleep => $"$({EmitProcessSleep(call.Arguments)})",
            IntrinsicId.ConsoleError => $"$({EmitConsoleError(call.Arguments)})",
            IntrinsicId.OsChdir => $"$(cd -- {Arg(call.Arguments, 0)})",
            IntrinsicId.ProcessRun => $"\"$({EmitProcessRunInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessPipeline => $"\"$({EmitProcessPipelineInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessFail => $"\"$({EmitProcessFailInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessRequireSuccess => $"\"$({EmitProcessRequireSuccessInvocation(call.Arguments)})\"",
            IntrinsicId.FsGlob => $"\"$({EmitFsGlobInvocation(call.Arguments)})\"",
            IntrinsicId.FsCreateDirectory => $"$({EmitFsCreateDirectory(call.Arguments)})",
            IntrinsicId.FsRemove => $"$({EmitFsRemove(call.Arguments)})",
            IntrinsicId.FsCopy => $"$({EmitFsCopy(call.Arguments)})",
            IntrinsicId.FsMove => $"$({EmitFsMove(call.Arguments)})",
            IntrinsicId.ArchiveZip => $"$({EmitArchiveZip(call.Arguments)})",
            IntrinsicId.ArchiveUnzip => $"$({EmitArchiveUnzip(call.Arguments)})",
            IntrinsicId.HttpGet => $"\"$({EmitHttpGetInvocation(call.Arguments)})\"",
            IntrinsicId.HttpPost => $"\"$({EmitHttpPostInvocation(call.Arguments)})\"",
            IntrinsicId.HttpDownload => $"$({EmitHttpDownload(call.Arguments)})",
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Unsupported intrinsic expression in Bash: {call.CanonicalName}")
        };
    }

    private string EmitPrint(IReadOnlyList<IrExpression> arguments, bool newline)
    {
        var value = arguments.Count == 0 ? "''" : Arg(arguments, 0);
        return newline
            ? $"printf '%s\\n' {value}"
            : $"printf '%s' {value}";
    }

    private string EmitStringTrimInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_trim {Arg(arguments, 0)}";
    }

    private string EmitStringLowerInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_lower {Arg(arguments, 0)}";
    }

    private string EmitStringUpperInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_upper {Arg(arguments, 0)}";
    }

    private string EmitStringLengthInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"printf '%s' {Arg(arguments, 0)} | wc -m";
    }

    private string EmitStringSplitInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_split {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)}";
    }

    private string EmitStringContainsInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_contains {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringStartsWithInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_starts_with {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringEndsWithInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_ends_with {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringReplaceInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_replace {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)}";
    }

    private string EmitStringIsMatchInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_is_match {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringMatchInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_match {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitIoWriteText(IReadOnlyList<IrExpression> arguments)
    {
        var path = Arg(arguments, 0);
        var text = Arg(arguments, 1);
        var append = Arg(arguments, 2);
        return EmitIoWriteText(path, text, append);
    }

    private static string EmitIoWriteText(string path, string text, string append)
    {
        return "__sushi_path=$(printf '%s' " + path + "); " +
               "__sushi_dir=$(dirname -- \"$__sushi_path\"); " +
               "if [[ \"$__sushi_dir\" != \".\" && ! -d \"$__sushi_dir\" ]]; then mkdir -p -- \"$__sushi_dir\"; fi; " +
               "if [[ " + append + " == 'true' ]]; then printf '%s' " + text + " >> \"$__sushi_path\"; else printf '%s' " + text + " > \"$__sushi_path\"; fi";
    }

    private string EmitFsCreateDirectory(IReadOnlyList<IrExpression> arguments) =>
        $"mkdir -p -- {Arg(arguments, 0)}";

    private string EmitFsSize(IReadOnlyList<IrExpression> arguments) =>
        _context.TargetProfile.Platform == TargetPlatform.Macos
            ? $"$(if [[ -f {Arg(arguments, 0)} ]]; then stat -f '%z' -- {Arg(arguments, 0)}; else printf 'std.fs.size: regular file required\\n' >&2; exit 1; fi)"
            : $"$(if [[ -f {Arg(arguments, 0)} ]]; then stat -c '%s' -- {Arg(arguments, 0)}; else printf 'std.fs.size: regular file required\\n' >&2; exit 1; fi)";

    private string EmitFsRemove(IReadOnlyList<IrExpression> arguments) =>
        $"if [[ {Arg(arguments, 1)} == 'true' ]]; then rm -rf -- {Arg(arguments, 0)}; else rm -f -- {Arg(arguments, 0)}; fi";

    private string EmitFsCopy(IReadOnlyList<IrExpression> arguments) =>
        $"if [[ {Arg(arguments, 2)} == 'true' ]]; then cp -R -- {Arg(arguments, 0)} {Arg(arguments, 1)}; else cp -- {Arg(arguments, 0)} {Arg(arguments, 1)}; fi";

    private string EmitFsMove(IReadOnlyList<IrExpression> arguments) =>
        $"mv -f -- {Arg(arguments, 0)} {Arg(arguments, 1)}";

    private string EmitArchiveZip(IReadOnlyList<IrExpression> arguments) =>
        $"zip -r -- {Arg(arguments, 1)} {Arg(arguments, 0)}";

    private string EmitArchiveUnzip(IReadOnlyList<IrExpression> arguments) =>
        $"unzip -o -- {Arg(arguments, 0)} -d {Arg(arguments, 1)}";

    private string EmitEnvSet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var value = Arg(arguments, 1);
        return EmitEnvSet(name, value);
    }

    private static string EmitEnvSet(string name, string value)
    {
        return $"__sushi_env_name=$(printf '%s' {name}); __sushi_env_value=$(printf '%s' {value}); export \"$__sushi_env_name=$__sushi_env_value\"";
    }

    private string EmitEnvUnset(IReadOnlyList<IrExpression> arguments) =>
        $"__sushi_env_name=$(printf '%s' {Arg(arguments, 0)}); unset \"$__sushi_env_name\"";

    private string EmitProcessSleep(IReadOnlyList<IrExpression> arguments) =>
        $"sleep \"$(({Arg(arguments, 0)} / 1000)).$(({Arg(arguments, 0)} % 1000))\"";

    private string EmitConsoleError(IReadOnlyList<IrExpression> arguments) =>
        $"printf '%s\\n' {Arg(arguments, 0)} >&2";

    private string EmitPathExtension(IReadOnlyList<IrExpression> arguments) =>
        $"$(__sushi_base=$(basename -- {Arg(arguments, 0)}); if [[ \"$__sushi_base\" == *.* && \"$__sushi_base\" != .* ]]; then printf '.%s' \"${{__sushi_base##*.}}\"; fi)";

    private string EmitPathStem(IReadOnlyList<IrExpression> arguments) =>
        $"$(__sushi_base=$(basename -- {Arg(arguments, 0)}); if [[ \"$__sushi_base\" == *.* && \"$__sushi_base\" != .* ]]; then printf '%s' \"${{__sushi_base%.*}}\"; else printf '%s' \"$__sushi_base\"; fi)";

    private string EmitProcessExit(IReadOnlyList<IrExpression> arguments)
    {
        return $"exit {EmitArithmeticExpression(arguments[0])}";
    }

    private string EmitPathJoin(IReadOnlyList<IrExpression> arguments)
    {
        if (arguments.Count == 0)
        {
            return "''";
        }

        if (arguments.Count == 1)
        {
            return Arg(arguments, 0);
        }

        var first = Arg(arguments, 0);
        var rest = arguments.Skip(1).Select(argument => $"printf '/%s' {EmitValueExpression(argument)}");
        var commands = string.Join("; ", new[] { $"printf '%s' {first}" }.Concat(rest));
        return $"$({commands})";
    }

    private string EmitEnvGet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var fallback = Arg(arguments, 1);
        return "$(__sushi_env_name=$(printf '%s' " + name + "); " +
               "__sushi_env_value=$(printenv \"$__sushi_env_name\" 2>/dev/null || true); " +
               "if printenv \"$__sushi_env_name\" >/dev/null 2>&1; then printf '%s' \"$__sushi_env_value\"; else printf '%s' " + fallback + "; fi)";
    }

    private string EmitProcessRunInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return "__sushi_process_run " +
               $"{Arg(arguments, 0)} " +
               $"{Arg(arguments, 1)} " +
               $"{Arg(arguments, 2)} " +
               $"{Arg(arguments, 3)} " +
               $"{Arg(arguments, 4)} " +
               $"{Arg(arguments, 5)} " +
               $"{Arg(arguments, 6)} " +
               $"{Arg(arguments, 7)}";
    }

    private string EmitProcessPipelineInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return "__sushi_process_pipeline " +
               $"{Arg(arguments, 0)} " +
               $"{Arg(arguments, 1)} " +
               $"{Arg(arguments, 2)} " +
               $"{Arg(arguments, 3)} " +
               $"{Arg(arguments, 4)} " +
               $"{Arg(arguments, 5)} " +
               $"{Arg(arguments, 6)}";
    }

    private string EmitProcessFailInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_process_fail {Arg(arguments, 0)}";
    }

    private string EmitProcessRequireSuccessInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_process_require_success {Arg(arguments, 0)}";
    }

    private string EmitFsGlobInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_fs_glob {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitHttpGetInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_http_get {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitHttpPostInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_http_post {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)} {Arg(arguments, 3)}";
    }

    private string EmitHttpDownload(IReadOnlyList<IrExpression> arguments) =>
        $"curl -fsSL -- {Arg(arguments, 0)} -o {Arg(arguments, 1)}";

    private string Arg(IReadOnlyList<IrExpression> arguments, int index)
    {
        return index < arguments.Count ? EmitValueExpression(arguments[index]) : "''";
    }
}
