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
    private void EmitStatement(IrStatement statement)
    {
        switch (statement)
        {
            case IrStandardLibraryImportStatement import:
                break;

            case IrBlockStatement block:
                foreach (var child in block.Statements)
                {
                    EmitStatement(child);
                }
                break;

            case IrVariableDeclarationStatement variable:
            {
                if (_nativeEnumValues.ContainsKey(variable.Name) || _richEnumValues.ContainsKey(variable.Name)) break;
                var initializer = variable.Initializer ?? new IrLiteralExpression(null);
                var variableName = SanitizeName(variable.Name);
                if (TryGetNativeEnumType(initializer, out var enumType))
                    _nativeEnumVariableTypes[variableName] = enumType;
                else
                    _nativeEnumVariableTypes.Remove(variableName);
                if (TryGetRichEnumType(initializer, out var richEnumType))
                    _richEnumVariableTypes[variableName] = richEnumType;
                else
                    _richEnumVariableTypes.Remove(variableName);
                if (initializer is IrArrayLiteralExpression array)
                {
                    _arrayInitializers[variableName] = array;
                }
                else
                {
                    _arrayInitializers.Remove(variableName);
                }
                if (initializer is IrIntrinsicCallExpression intrinsic &&
                    EmitNativeIntrinsicDeclaration(variableName, intrinsic))
                {
                    SetKnownInteger(variableName, false);
                    break;
                }
                if (initializer is IrConditionalExpression { IsSwitchExpression: true } switchExpression)
                {
                    EmitSwitchExpressionInto($"${variableName}", switchExpression);
                    SetKnownInteger(variableName, variable.DeclaredType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true);
                    SetKnownFloat(variableName, variable.DeclaredType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true);
                    break;
                }
                var initializerText = EmitValueExpression(initializer);
                if (variable.DeclaredType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true &&
                    variable.Initializer != null)
                {
                    initializerText = $"[double]({initializerText})";
                }
                WriteLine($"${SanitizeName(variable.Name)} = {initializerText}");
                var emittedName = SanitizeName(variable.Name);
                SetKnownInteger(emittedName, variable.DeclaredType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true || IsDefinitelyInteger(initializer));
                SetKnownFloat(emittedName, variable.DeclaredType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true || IsDefinitelyFloat(initializer));
                break;
            }

            case IrExpressionStatement expressionStatement:
                EmitExpressionStatement(expressionStatement.Expression);
                break;

            case IrIfStatement ifStatement:
                EmitIfStatement(ifStatement);
                break;

            case IrSwitchStatement switchStatement:
                EmitSwitchStatement(switchStatement);
                break;

            case IrWhileStatement whileStatement:
                EmitWhileStatement(whileStatement);
                break;

            case IrForStatement forStatement:
                EmitForStatement(forStatement);
                break;

            case IrForEachStatement forEach:
                EmitForEachStatement(forEach);
                break;

            case IrDoWhileStatement doWhileStatement:
                EmitDoWhileStatement(doWhileStatement);
                break;

            case IrFunctionDeclarationStatement function:
                // Native class/enum members are emitted by their declaration. The
                // portable lowering is only emitted for standalone functions.
                var nativeMember = function.Role is IrFunctionRole.Method or IrFunctionRole.Constructor or IrFunctionRole.Adapter;
                var ownerType = function.OwnerTypeId is { } owner && owner.StartsWith("type:", StringComparison.Ordinal)
                    ? owner["type:".Length..]
                    : null;
                if (!nativeMember || ownerType is null || !_nativeClasses.ContainsKey(ownerType))
                    EmitFunction(function);
                break;

            case IrClassDeclarationStatement declaration:
                EmitNativeClass(declaration);
                WriteLine("");
                break;

            case IrEnumDeclarationStatement declaration:
                EmitNativeEnum(declaration);
                WriteLine("");
                break;

            case IrRichEnumDeclarationStatement declaration:
                EmitRichEnum(declaration);
                WriteLine("");
                break;

            case IrReturnStatement returnStatement:
                if (returnStatement.Expression != null)
                {
                    var value = EmitValueExpression(returnStatement.Expression);
                    if (_currentFunctionReturnType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true)
                        value = $"[double]({value})";
                    WriteLine($"return {value}");
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

    private void EmitSwitchStatement(IrSwitchStatement statement)
    {
        WriteLine($"switch ({EmitValueExpression(statement.Value)}) {{");
        _indent++;
        foreach (var @case in statement.Cases)
        {
            foreach (var match in @case.Matches)
            {
                WriteLine($"{EmitValueExpression(match)} {{");
                _indent++;
                EmitStatement(@case.Body);
                WriteLine("break");
                _indent--;
                WriteLine("}");
            }
        }
        if (statement.DefaultBody != null)
        {
            WriteLine("default {");
            _indent++;
            EmitStatement(statement.DefaultBody);
            _indent--;
            WriteLine("}");
        }
        _indent--;
        WriteLine("}");
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

    private void EmitForEachStatement(IrForEachStatement statement)
    {
        var item = SanitizeName(statement.ItemName);
        var collection = EmitValueExpression(statement.Collection);
        var index = statement.IndexName == null ? null : SanitizeName(statement.IndexName);
        if (index != null) WriteLine($"${index} = 0");
        WriteLine($"foreach (${item} in {collection}) {{");
        _indent++;
        EmitStatement(statement.Body);
        if (index != null) WriteLine($"${index}++");
        _indent--;
        WriteLine("}");
    }

    private void EmitDoWhileStatement(IrDoWhileStatement statement)
    {
        WriteLine("do {");
        _indent++;
        EmitStatement(statement.Body);
        _indent--;
        WriteLine($"}} while ({EmitConditionExpression(statement.Condition)})");
    }

    private void EmitFunction(IrFunctionDeclarationStatement statement)
    {
        WriteLine($"function {SanitizeFunctionName(statement.Name)} {{");
        _indent++;

        var previousFunctionName = _currentFunctionName;
        var previousReturnType = _currentFunctionReturnType;
        var previousKnownIntegers = _knownIntegerVariables;
        var previousKnownFloats = _knownFloatVariables;
        _currentFunctionName = statement.Name;
        _currentFunctionReturnType = statement.ReturnType;
        _knownIntegerVariables = new HashSet<string>(StringComparer.Ordinal);
        _knownFloatVariables = new HashSet<string>(StringComparer.Ordinal);
        var previousReceiverName = _fallbackReceiverName;
        var previousRichEnumReceiver = _currentRichEnumReceiver;
        _fallbackReceiverName = statement.Parameters.Any(parameter => parameter.Name == "this") ? "self" : null;
        _currentRichEnumReceiver = GetRichEnumReceiver(statement.Name);

        var regularParameters = statement.Parameters
            .Where(parameter => !parameter.IsVarargs)
            .Select(parameter => EmitPowerShellParameter(parameter))
            .ToList();
        var varargsParameter = statement.Parameters.FirstOrDefault(parameter => parameter.IsVarargs);

        if (regularParameters.Count > 0)
        {
            var parameterList = string.Join(", ", regularParameters);
            WriteLine($"param({parameterList})");
        }

        if (varargsParameter != null)
        {
            var varargName = SanitizeName(varargsParameter.Name);
            WriteLine($"${varargName} = @()");
            WriteLine("foreach ($__sushi_vararg in @($args)) {");
            _indent++;
            WriteLine("if ($__sushi_vararg -is [System.Collections.IList] -and -not ($__sushi_vararg -is [string])) {");
            _indent++;
            WriteLine($"${varargName} += @($__sushi_vararg)");
            _indent--;
            WriteLine("} else {");
            _indent++;
            WriteLine($"${varargName} += ,$__sushi_vararg");
            _indent--;
            WriteLine("}");
            _indent--;
            WriteLine("}");
        }

        foreach (var parameter in statement.Parameters)
        {
            var paramName = SanitizeName(parameter.Name);
            if (parameter.DeclaredType.Kind == IrTypeKind.Primitive &&
                parameter.DeclaredType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true)
            {
                _knownIntegerVariables.Add(paramName);
            }
            if (parameter.DeclaredType.Kind == IrTypeKind.Primitive &&
                parameter.DeclaredType.Name?.Equals("float", StringComparison.OrdinalIgnoreCase) == true)
            {
                _knownFloatVariables.Add(paramName);
            }
            if (parameter.IsVarargs)
            {
                if (parameter.DeclaredType.IsAnyOrUnknown)
                {
                    continue;
                }

                WriteLine($"foreach ($__sushi_vararg_item in @(${paramName})) {{");
                _indent++;
                EmitContractCheckForValue(
                    parameter.DeclaredType,
                    "$__sushi_vararg_item",
                    $"varargs parameter '{parameter.Name}' of function '{statement.Name}'");
                _indent--;
                WriteLine("}");
                continue;
            }

            EmitContractCheckForValue(
                parameter.DeclaredType,
                $"${paramName}",
                $"parameter '{parameter.Name}' of function '{statement.Name}'");
        }

        EmitStatement(statement.Body);
        _currentFunctionName = previousFunctionName;
        _currentFunctionReturnType = previousReturnType;
        _knownIntegerVariables = previousKnownIntegers;
        _knownFloatVariables = previousKnownFloats;
        _fallbackReceiverName = previousReceiverName;
        _currentRichEnumReceiver = previousRichEnumReceiver;
        _indent--;
        WriteLine("}");
    }

    private bool EmitNativeIntrinsicDeclaration(string name, IrIntrinsicCallExpression intrinsic)
    {
        switch (intrinsic.Id)
        {
            case IntrinsicId.FsGlob:
            {
                if (!_nativeGlobHelper)
                {
                    _context.Error(AmbiguousShapeCode,
                        "std.fs.glob requires an explicit std.fs.glob import before it can be emitted.",
                        intrinsic.Origin?.Line ?? 1, intrinsic.Origin?.Column ?? 1);
                    return false;
                }
                WriteLine($"${name} = @({EmitExplicitFsGlob(intrinsic.Arguments)})");
                return true;
            }

            case IntrinsicId.ProcessArgs:
                WriteLine($"${name} = @($args)");
                return true;

            case IntrinsicId.ProcessRun:
                EmitNativeProcessRun(name, intrinsic.Arguments);
                return true;

            case IntrinsicId.ProcessPipeline:
                EmitNativeProcessPipeline(name, intrinsic.Arguments);
                return true;

            case IntrinsicId.HttpGet:
            case IntrinsicId.HttpPost:
                EmitNativeHttp(name, intrinsic);
                return true;

            default:
                return false;
        }
    }

    private void EmitNativeProcessRun(string name, IReadOnlyList<IrExpression> arguments)
    {
        var command = EmitValueExpression(arguments[0]);
        var args = arguments.Count > 1 && arguments[1] is IrArrayLiteralExpression array
            ? string.Join(", ", array.Elements.Select(EmitValueExpression))
            : arguments.Count > 1 ? $"@({EmitValueExpression(arguments[1])})" : "";
        WriteLine($"${name}_stdout_file = New-TemporaryFile");
        WriteLine($"${name}_stderr_file = New-TemporaryFile");
        var timeoutMs = arguments.Count > 5 && arguments[5] is IrLiteralExpression { Value: int timeout } ? timeout : 0;
        var invocation = $"Start-Process -FilePath ([string]({command})) -ArgumentList @({args}) -PassThru -NoNewWindow -RedirectStandardOutput ${name}_stdout_file -RedirectStandardError ${name}_stderr_file";
        if (arguments.Count > 2 && arguments[2] is not IrLiteralExpression { Value: null })
        {
            invocation += $" -WorkingDirectory {EmitValueExpression(arguments[2])}";
        }
        if (timeoutMs > 0)
        {
            WriteLine($"${name}_process = {invocation}");
            WriteLine($"${name}_timedOut = $false");
            WriteLine($"if (-not ${name}_process.WaitForExit({timeoutMs})) {{ ${name}_timedOut = $true; ${name}_process.Kill(); ${name}_process.WaitForExit() }}");
        }
        else
        {
            WriteLine($"${name}_process = {invocation} -Wait");
            WriteLine($"${name}_timedOut = $false");
        }
        WriteLine($"${name}_code = $(if (${name}_timedOut) {{ 124 }} else {{ ${name}_process.ExitCode }})");
        WriteLine($"${name} = [pscustomobject]@{{ code=${name}_code; stdout=(Get-Content -Raw -LiteralPath ${name}_stdout_file); stderr=(Get-Content -Raw -LiteralPath ${name}_stderr_file); ok=(${name}_code -eq 0); command=[string]({command}); timedOut=${name}_timedOut }}");
        WriteLine($"Remove-Item -Force ${name}_stdout_file, ${name}_stderr_file");
        if (arguments.Count <= 6 || arguments[6] is not IrLiteralExpression { Value: true })
        {
            WriteLine($"if (-not ${name}.ok) {{ exit ${name}.code }}");
        }
    }

    private void EmitNativeProcessPipeline(string name, IReadOnlyList<IrExpression> arguments)
    {
        IrArrayLiteralExpression? stages = arguments[0] as IrArrayLiteralExpression;
        if (stages == null && arguments[0] is IrIdentifierExpression identifier)
        {
            _arrayInitializers.TryGetValue(SanitizeName(identifier.Name), out stages);
        }
        if (stages == null)
        {
            _context.Error(AmbiguousShapeCode, "Process pipeline stages must have a statically known array shape");
            return;
        }
        var commands = new List<string>();
        foreach (var stage in stages.Elements.OfType<IrObjectLiteralExpression>())
        {
            var command = stage.Properties.FirstOrDefault(property => property.Name == "command")?.Value;
            var args = stage.Properties.FirstOrDefault(property => property.Name == "args")?.Value as IrArrayLiteralExpression;
            if (command == null) continue;
            var rendered = new List<string> { $"& {EmitValueExpression(command)}" };
            if (args != null) rendered.AddRange(args.Elements.Select(EmitValueExpression));
            commands.Add(string.Join(" ", rendered));
        }
        WriteLine($"${name}_stdout = @({string.Join(" | ", commands)}) -join [Environment]::NewLine");
        WriteLine($"${name}_code = $LASTEXITCODE");
        WriteLine($"${name} = [pscustomobject]@{{ code=${name}_code; stdout=${name}_stdout; stderr=''; ok=(${name}_code -eq 0); command='pipeline'; timedOut=$false }}");
        if (arguments.Count <= 5 || arguments[5] is not IrLiteralExpression { Value: true })
        {
            WriteLine($"if (-not ${name}.ok) {{ exit ${name}.code }}");
        }
    }

    private void EmitNativeHttp(string name, IrIntrinsicCallExpression intrinsic)
    {
        var url = EmitValueExpression(intrinsic.Arguments[0]);
        var method = intrinsic.Id == IntrinsicId.HttpPost ? "Post" : "Get";
        var extras = intrinsic.Id == IntrinsicId.HttpPost
            ? $" -Body {EmitValueExpression(intrinsic.Arguments[1])} -ContentType ([string]({EmitValueExpression(intrinsic.Arguments[3])}))"
            : "";
        WriteLine("try {");
        _indent++;
        WriteLine("try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }");
        WriteLine($"${name}_response = Invoke-WebRequest -UseBasicParsing -Method {method} -Uri {url}{extras}");
        WriteLine($"${name}_url = if (${name}_response.BaseResponse.PSObject.Properties['RequestMessage']) {{ [string]${name}_response.BaseResponse.RequestMessage.RequestUri }} elseif (${name}_response.BaseResponse.PSObject.Properties['ResponseUri']) {{ [string]${name}_response.BaseResponse.ResponseUri }} else {{ [string]({url}) }}");
        WriteLine($"${name} = [pscustomobject]@{{ status=[int]${name}_response.StatusCode; ok=([int]${name}_response.StatusCode -ge 200 -and [int]${name}_response.StatusCode -lt 300); headers=${name}_response.Headers; body=[string]${name}_response.Content; url=${name}_url }}");
        _indent--;
        WriteLine("} catch {");
        _indent++;
        WriteLine($"${name} = [pscustomobject]@{{ status=0; ok=$false; headers=@{{}}; body=''; url=[string]({url}) }}");
        _indent--;
        WriteLine("}");
    }
}
