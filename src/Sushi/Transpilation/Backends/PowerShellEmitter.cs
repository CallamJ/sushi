namespace Sushi.Transpilation.Backends;

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed class PowerShellEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1200";
    private const string AmbiguousShapeCode = "SUSHI1030";

    private readonly StringBuilder _builder = new();
    private EmitContext _context = null!;
    private int _indent;
    private string? _currentFunctionName;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;
    private HashSet<string> _knownIntegerVariables = new(StringComparer.Ordinal);
    private HashSet<string> _knownFloatVariables = new(StringComparer.Ordinal);
    private HashSet<string> _integerReturningFunctions = new(StringComparer.Ordinal);
    private HashSet<string> _floatReturningFunctions = new(StringComparer.Ordinal);
    private Dictionary<string, IrArrayLiteralExpression> _arrayInitializers = new(StringComparer.Ordinal);
    private TargetNameAllocator _names = null!;
    private Dictionary<string, string> _generatedFunctionNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeClassNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeEnumNames = new(StringComparer.Ordinal);
    private Dictionary<string, IrClassDeclarationStatement> _nativeClasses = new(StringComparer.Ordinal);
    private Dictionary<string, IrClassMethod> _nativeMethods = new(StringComparer.Ordinal);
    private Dictionary<string, (string Type, string Value, int Ordinal)> _nativeEnumValues = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeEnumVariableTypes = new(StringComparer.Ordinal);
    private Dictionary<string, (string Type, string Value)> _richEnumValues = new(StringComparer.Ordinal);
    private Dictionary<string, string> _richEnumVariableTypes = new(StringComparer.Ordinal);
    private string? _currentRichEnumReceiver;
    private string? _fallbackReceiverName;
    private HashSet<string>? _runtimeFunctionFilter;

    public string Emit(IrProgram program, EmitContext context)
    {
        _builder.Clear();
        _names = new TargetNameAllocator(TargetLanguage.Powershell51);
        _generatedFunctionNames.Clear();
        _nativeClassNames.Clear();
        _nativeEnumNames.Clear();
        _nativeClasses.Clear();
        _nativeMethods.Clear();
        _nativeEnumValues.Clear();
        _nativeEnumVariableTypes.Clear();
        _richEnumValues.Clear();
        _richEnumVariableTypes.Clear();
        _currentRichEnumReceiver = null;
        _fallbackReceiverName = null;
        _runtimeFunctionFilter = null;
        _context = context;
        _indent = 0;
        _currentFunctionName = null;
        _currentFunctionReturnType = IrTypeRef.Any;
        _knownIntegerVariables = new HashSet<string>(StringComparer.Ordinal);
        _knownFloatVariables = new HashSet<string>(StringComparer.Ordinal);
        _arrayInitializers.Clear();
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

        WriteLine("Set-StrictMode -Version Latest");
        WriteLine("$ErrorActionPreference = 'Stop'");
        WriteLine("");
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
            _runtimeFunctionFilter = program.Statements.OfType<IrStandardLibraryImportStatement>().Any()
                ? new HashSet<string>(new[] { "__sushi_glob_regex", "__sushi_fs_glob" }, StringComparer.Ordinal)
                : null;
            var runtimeStart = _builder.Length;
            EmitRuntimeHelpers();
            if (_runtimeFunctionFilter is { } filter)
            {
                var runtimeText = _builder.ToString(runtimeStart, _builder.Length - runtimeStart);
                _builder.Remove(runtimeStart, _builder.Length - runtimeStart);
                _builder.Append(FilterRuntimeBlock(runtimeText, filter));
            }
            _runtimeFunctionFilter = null;
        }
        var classes = CollectClasses(program.Statements).ToList();
        foreach (var declaration in classes)
        {
            var name = _names.Source(TargetNameKind.Type, declaration.Name);
            _nativeClassNames[declaration.Name] = name;
            _nativeClasses[declaration.Name] = declaration;
            foreach (var method in declaration.Methods.Concat(declaration.Adapters))
                _nativeMethods[method.LegacyName] = method;
        }
        var enums = CollectEnums(program.Statements).ToList();
        foreach (var declaration in enums)
            _nativeEnumNames[declaration.Name] = _names.Source(TargetNameKind.Type, declaration.Name);
        foreach (var declaration in enums)
        {
            var enumName = _nativeEnumNames[declaration.Name];
            WriteLine($"enum {enumName} {{");
            _indent++;
            foreach (var value in declaration.Values)
            {
                var valueName = SanitizeMemberName(value.Name);
                WriteLine($"{valueName} = {value.Value}");
                _nativeEnumValues[$"{declaration.Name}_{value.Name}"] = (enumName, valueName, value.Ordinal);
            }
            _indent--;
            WriteLine("}");
            WriteLine("");
        }
        var richEnums = CollectRichEnums(program.Statements).ToList();
        foreach (var declaration in richEnums)
        {
            _nativeClassNames[declaration.Name] = _names.Source(TargetNameKind.Type, declaration.Name);
            foreach (var method in declaration.Methods.Concat(declaration.Adapters))
                _nativeMethods[method.LegacyName] = method;
        }
        foreach (var declaration in richEnums)
        {
            EmitRichEnum(declaration);
            WriteLine("");
        }
        foreach (var declaration in OrderClasses(classes))
        {
            EmitNativeClass(declaration);
            WriteLine("");
        }
        foreach (var statement in program.Statements)
        {
            EmitStatement(statement);
        }

        return _builder.ToString();
    }

    private static bool HasFsGlobImport(IrProgram program)
    {
        var imports = program.Statements.OfType<IrStandardLibraryImportStatement>().ToList();
        return imports.Count == 0 || imports.Any(import => (import.Module.Equals("std.fs", StringComparison.Ordinal) || import.Module.Equals("std.fs.glob", StringComparison.Ordinal)) &&
            (import.Members.Count == 0 || import.Members.Contains("glob", StringComparer.Ordinal)));
    }

    private static IEnumerable<IrClassDeclarationStatement> CollectClasses(IEnumerable<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is IrClassDeclarationStatement declaration) yield return declaration;
            if (statement is IrBlockStatement block)
                foreach (var nestedDeclaration in CollectClasses(block.Statements)) yield return nestedDeclaration;
        }
    }

    private static IEnumerable<IrEnumDeclarationStatement> CollectEnums(IEnumerable<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is IrEnumDeclarationStatement declaration) yield return declaration;
            if (statement is IrBlockStatement block)
                foreach (var nestedDeclaration in CollectEnums(block.Statements)) yield return nestedDeclaration;
        }
    }

    private static IEnumerable<IrRichEnumDeclarationStatement> CollectRichEnums(IEnumerable<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is IrRichEnumDeclarationStatement declaration) yield return declaration;
            if (statement is IrBlockStatement block)
                foreach (var nestedDeclaration in CollectRichEnums(block.Statements)) yield return nestedDeclaration;
        }
    }

    private void EmitRichEnum(IrRichEnumDeclarationStatement declaration)
    {
        var typeName = _names.Source(TargetNameKind.Type, declaration.Name);
        var fields = declaration.Values.SelectMany(value => value.Properties)
            .Where(property => !property.Name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal) &&
                               property.Name != NativeObjectMetadata.EnumValue)
            .Select(property => property.Name).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name == NativeObjectMetadata.EnumName ? 1 :
                             name == NativeObjectMetadata.EnumOrdinal ? 2 : 0)
            .ToList();
        foreach (var value in declaration.Values)
            _richEnumValues[$"{declaration.Name}_{value.Name}"] = (typeName, SanitizeMemberName(value.Name));
        WriteLine($"class {typeName} {{");
        _indent++;
        foreach (var field in fields) WriteLine($"[object] ${SanitizeMemberName(field)}");
        var parameters = string.Join(", ", fields.Select(field => $"[object]${SanitizeName(field)}"));
        WriteLine($"{typeName}({parameters}) {{");
        _indent++;
        foreach (var field in fields)
        {
            if (declaration.ConstructorBody != null && declaration.ConstructorParameters.Any(parameter => parameter.Name == field) &&
                ConstructorAssignsParameter(declaration.ConstructorBody, field, field))
                continue;
            WriteLine($"$this.{SanitizeMemberName(field)} = ${SanitizeName(field)}");
        }
        if (declaration.ConstructorBody != null)
            EmitClassBody(declaration.ConstructorBody, IrTypeRef.Any);
        _indent--;
        WriteLine("}");
        foreach (var method in declaration.Methods.Concat(declaration.Adapters))
        {
            var returnType = ContainsValueReturn(method.Body) ? EmitPowerShellType(method.ReturnType, declaration.Name) : "[void]";
            var methodParameters = string.Join(", ", method.Parameters.Select(parameter => EmitPowerShellParameter(parameter, declaration.Name)));
            WriteLine($"{returnType} {SanitizeMemberName(NativeMethodName(method.Name))}({methodParameters}) {{");
            _indent++;
            EmitClassBody(method.Body, method.ReturnType);
            _indent--;
            WriteLine("}");
        }
        foreach (var value in declaration.Values)
        {
            var valuesByField = value.Properties.ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var arguments = string.Join(", ", fields.Select(field =>
            {
                var parameterIndex = declaration.ConstructorParameters.FindIndex(parameter => parameter.Name == field);
                if (parameterIndex >= 0 && parameterIndex < value.ConstructorArguments.Count)
                    return EmitValueExpression(value.ConstructorArguments[parameterIndex]);
                return valuesByField.TryGetValue(field, out var expression) ? EmitValueExpression(expression) : "$null";
            }));
            var valueName = SanitizeMemberName(value.Name);
            WriteLine($"static [{typeName}] ${valueName} = [{typeName}]::new({arguments})");
            // Explicit enum constructors are only the portable construction
            // path. Static native instances above replace them on PowerShell.
        }
        _indent--;
        WriteLine("}");
    }

    private IEnumerable<IrClassDeclarationStatement> OrderClasses(IEnumerable<IrClassDeclarationStatement> classes)
    {
        var remaining = classes.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(c => ReferencedClassTypes(c).All(type => !remaining.ContainsKey(type) || type == c.Name))
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ToList();
            // Cycles use [object] on the relevant member annotations, so any
            // deterministic order is valid once no acyclic declaration remains.
            if (ready.Count == 0) ready.Add(remaining.Values.OrderBy(c => c.Name, StringComparer.Ordinal).First());
            foreach (var declaration in ready)
            {
                remaining.Remove(declaration.Name);
                emitted.Add(declaration.Name);
                yield return declaration;
            }
        }
    }

    private static IEnumerable<string> ReferencedClassTypes(IrClassDeclarationStatement declaration) =>
        declaration.Fields.Select(field => field.Type)
            .Concat(declaration.ConstructorParameters.Select(parameter => parameter.DeclaredType))
            .Concat(declaration.Methods.Concat(declaration.Adapters).SelectMany(method =>
                method.Parameters.Select(parameter => parameter.DeclaredType).Append(method.ReturnType)))
            .Where(type => type.Kind == IrTypeKind.Primitive && type.Name is not null)
            .Select(type => type.Name!);

    private void EmitNativeClass(IrClassDeclarationStatement declaration)
    {
        var className = _nativeClassNames[declaration.Name];
        WriteLine($"class {className} {{");
        _indent++;
        foreach (var field in declaration.Fields)
            WriteLine($"{EmitPowerShellType(field.Type, declaration.Name)} ${SanitizeMemberName(field.Name)}");

        var constructorParameters = string.Join(", ", declaration.ConstructorParameters.Select(p => EmitPowerShellParameter(p, declaration.Name)));
        WriteLine($"{className}({constructorParameters}) {{");
        _indent++;
        foreach (var field in declaration.Fields)
        {
            var matchingParameter = declaration.ConstructorParameters.FirstOrDefault(p => p.Name == field.Name);
            if (matchingParameter != null && ConstructorAssignsParameter(declaration.ConstructorBody, field.Name, matchingParameter.Name))
                continue;
            var value = matchingParameter != null
                ? "$" + SanitizeName(matchingParameter.Name)
                : field.Initializer != null ? EmitValueExpression(field.Initializer) : "$null";
            WriteLine($"$this.{SanitizeMemberName(field.Name)} = {value}");
        }
        EmitClassBody(declaration.ConstructorBody, IrTypeRef.Any);
        _indent--;
        WriteLine("}");

        foreach (var method in declaration.Methods.Concat(declaration.Adapters))
        {
            var returnType = ContainsValueReturn(method.Body)
                ? EmitPowerShellType(method.ReturnType, declaration.Name)
                : "[void]";
            var parameters = string.Join(", ", method.Parameters.Select(p => EmitPowerShellParameter(p, declaration.Name)));
            WriteLine($"{returnType} {SanitizeMemberName(NativeMethodName(method.Name))}({parameters}) {{");
            _indent++;
            EmitClassBody(method.Body, method.ReturnType);
            _indent--;
            WriteLine("}");
        }
        _indent--;
        WriteLine("}");
    }

    private static bool ConstructorAssignsParameter(IrBlockStatement body, string fieldName, string parameterName) =>
        body.Statements.Any(statement => statement is IrExpressionStatement
        {
            Expression: IrMemberAssignmentExpression
            {
                Operator: "=",
                Target: IrIdentifierExpression { Name: "this" },
                MemberName: var assignedField,
                Value: IrIdentifierExpression { Name: var assignedParameter }
            }
        } && assignedField == fieldName && assignedParameter == parameterName);

    private void EmitClassBody(IrBlockStatement body, IrTypeRef returnType)
    {
        var previousName = _currentFunctionName;
        var previousReturn = _currentFunctionReturnType;
        var previousIntegers = _knownIntegerVariables;
        _currentFunctionName = null;
        _currentFunctionReturnType = returnType;
        _knownIntegerVariables = new HashSet<string>(StringComparer.Ordinal);
        EmitStatement(body);
        _currentFunctionName = previousName;
        _currentFunctionReturnType = previousReturn;
        _knownIntegerVariables = previousIntegers;
    }

    private static bool ContainsValueReturn(IrStatement statement) => statement switch
    {
        IrReturnStatement { Expression: not null } => true,
        IrBlockStatement block => block.Statements.Any(ContainsValueReturn),
        IrIfStatement conditional => ContainsValueReturn(conditional.ThenBlock) ||
                                     (conditional.ElseBlock != null && ContainsValueReturn(conditional.ElseBlock)),
        IrWhileStatement loop => ContainsValueReturn(loop.Body),
        IrForStatement loop => ContainsValueReturn(loop.Body),
        IrDoWhileStatement loop => ContainsValueReturn(loop.Body),
        _ => false
    };

    private static string NativeMethodName(string name) => name.ToLowerInvariant() switch
    {
        "string" => "ToString",
        // Sushi `int` is a 64-bit integer across targets. Keep index casts below
        // as [int] where the PowerShell API specifically requires Int32 indices.
        "int" => "ToInt64",
        "float" => "ToDouble",
        "bool" => "ToBoolean",
        "array" => "ToArray",
        "object" => "ToObject",
        _ => name
    };

    private void EmitRuntimeHelpers()
    {
        _builder.AppendLine(
"""
function __sushi_member {
    param($target, [string]$name)
    if ($null -eq $target) { return $null }
    if ($target -is [System.Collections.IDictionary]) { return $target[$name] }
    $prop = $target.PSObject.Properties[$name]
    if ($null -ne $prop) { return $prop.Value }
    return $null
}

function __sushi_index {
    param($target, $index)
    if ($null -eq $target) { return $null }
    if ($target -is [System.Collections.IDictionary]) { return $target[[string]$index] }
    if ($target -is [System.Collections.IList]) {
        $i = [int]$index
        if ($i -lt 0) { $i = $target.Count + $i }
        if ($i -lt 0 -or $i -ge $target.Count) { return $null }
        return $target[$i]
    }
    $prop = $target.PSObject.Properties[[string]$index]
    if ($null -ne $prop) { return $prop.Value }
    return $null
}

function __sushi_to_array {
    param($value)
    if ($null -eq $value) { return @() }
    if ($value -is [string]) { return ,$value }
    if ($value -is [System.Collections.IEnumerable]) { return @($value) }
    return ,([string]$value)
}

function __sushi_to_map {
    param($value)
    $map = @{}
    if ($null -eq $value) { return $map }
    if ($value -is [System.Collections.IDictionary]) {
        foreach ($key in $value.Keys) { $map[[string]$key] = [string]$value[$key] }
        return $map
    }
    foreach ($prop in $value.PSObject.Properties) { $map[[string]$prop.Name] = [string]$prop.Value }
    return $map
}

function __sushi_process_run {
    param(
        [string]$command,
        $argValues = $null,
        [string]$cwd = $null,
        $envMap = $null,
        [string]$inputText = $null,
        [int]$timeoutMs = 0,
        [bool]$allowFailure = $false,
        [bool]$stream = $false
    )

    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()
    $stdinFile = $null
    $timedOut = $false
    $exitCode = 0
    $argList = @(__sushi_to_array $argValues)
    $commandText = if ($argList.Count -gt 0) { $command + ' ' + (($argList | ForEach-Object { [string]$_ }) -join ' ') } else { $command }
    $savedLocation = Get-Location
    $savedEnv = @{}
    $envEntries = __sushi_to_map $envMap

    try {
        foreach ($entry in $envEntries.GetEnumerator()) {
            $savedEnv[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
        }

        if (-not [string]::IsNullOrWhiteSpace($cwd)) {
            Set-Location -LiteralPath $cwd
        }

        if ($timeoutMs -gt 0) {
            $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = $command
            $startInfo.UseShellExecute = $false
            $startInfo.RedirectStandardInput = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            if (-not [string]::IsNullOrWhiteSpace($cwd)) {
                $startInfo.WorkingDirectory = (Get-Location).Path
            }

            $quotedArgs = @($argList | ForEach-Object {
                $arg = [string]$_
                if ($arg.Length -eq 0) { return '""' }
                $arg = $arg.Replace('"', '\"')
                if ($arg -match '\s') { return '"' + $arg + '"' }
                return $arg
            })
            $startInfo.Arguments = ($quotedArgs -join ' ')

            $process = [System.Diagnostics.Process]::new()
            $process.StartInfo = $startInfo
            $null = $process.Start()

            if ($null -ne $inputText) {
                $process.StandardInput.Write([string]$inputText)
            }
            $process.StandardInput.Close()

            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()

            if (-not $process.WaitForExit($timeoutMs)) {
                $timedOut = $true
                try { $process.Kill() } catch { }
            }

            $process.WaitForExit()
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            Set-Content -LiteralPath $stdoutFile -Value $stdout -NoNewline
            Set-Content -LiteralPath $stderrFile -Value $stderr -NoNewline

            $exitCode = if ($timedOut) { 124 } else { [int]$process.ExitCode }
            $process.Dispose()
        } else {
            if ($null -ne $inputText) {
                $stdinFile = [System.IO.Path]::GetTempFileName()
                Set-Content -LiteralPath $stdinFile -Value ([string]$inputText) -NoNewline
                Get-Content -Raw -LiteralPath $stdinFile | & $command @argList > $stdoutFile 2> $stderrFile
            } else {
                & $command @argList > $stdoutFile 2> $stderrFile
            }

            $exitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
        }
    } catch {
        $exitCode = 1
        $_ | Out-String | Set-Content -LiteralPath $stderrFile
    } finally {
        foreach ($entry in $savedEnv.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
        Set-Location -LiteralPath $savedLocation.Path
    }

    $stdout = ''
    $stderr = ''
    if (Test-Path -LiteralPath $stdoutFile) { $stdout = Get-Content -Raw -LiteralPath $stdoutFile }
    if (Test-Path -LiteralPath $stderrFile) { $stderr = Get-Content -Raw -LiteralPath $stderrFile }
    if ($stream) {
        if ($stdout) { [Console]::Out.Write($stdout) }
        if ($stderr) { [Console]::Error.Write($stderr) }
    }

    $result = [PSCustomObject]@{
        code = [int]$exitCode
        stdout = [string]$stdout
        stderr = [string]$stderr
        ok = [bool]($exitCode -eq 0)
        command = [string]$commandText
        timedOut = [bool]$timedOut
    }

    if (Test-Path -LiteralPath $stdoutFile) { Remove-Item -LiteralPath $stdoutFile -Force }
    if (Test-Path -LiteralPath $stderrFile) { Remove-Item -LiteralPath $stderrFile -Force }
    if ($null -ne $stdinFile -and (Test-Path -LiteralPath $stdinFile)) { Remove-Item -LiteralPath $stdinFile -Force }

    if ((-not $allowFailure) -and ($exitCode -ne 0)) {
        if ($stderr) { [Console]::Error.WriteLine($stderr) }
        exit $exitCode
    }

    return $result
}

function __sushi_process_pipeline {
    param(
        $stages,
        [string]$cwd = $null,
        $envMap = $null,
        [string]$inputText = $null,
        [int]$timeoutMs = 0,
        [bool]$allowFailure = $false,
        [bool]$stream = $false
    )

    $stageList = __sushi_to_array $stages
    $last = $null
    $nextInput = $inputText
    foreach ($stage in $stageList) {
        $stageCommand = [string](__sushi_member $stage 'command')
        $stageArgs = __sushi_member $stage 'args'
        $last = __sushi_process_run -command $stageCommand -argValues $stageArgs -cwd $cwd -envMap $envMap -inputText $nextInput -timeoutMs $timeoutMs -allowFailure $true -stream $stream
        if ((-not $allowFailure) -and (-not [bool]$last.ok)) { exit [int]$last.code }
        $nextInput = [string]$last.stdout
    }

    if ($null -eq $last) {
        return [PSCustomObject]@{ code = 0; stdout = ''; stderr = ''; ok = $true; command = ''; timedOut = $false }
    }

    return $last
}

function __sushi_process_fail {
    param($result)
    if ($null -eq $result) { return $true }
    return (-not [bool](__sushi_member $result 'ok'))
}

function __sushi_process_require_success {
    param($result)
    if (__sushi_process_fail $result) {
        $code = [int](__sushi_member $result 'code')
        $stderr = [string](__sushi_member $result 'stderr')
        if ($stderr) { [Console]::Error.WriteLine($stderr) }
        exit $code
    }
    return $result
}

function __sushi_json_parse {
    param($text)
    if ($null -eq $text) { return $null }
    if ($text -isnot [string]) { return $text }
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return ($text | ConvertFrom-Json) } catch { return $text }
}

function __sushi_json_sort_value {
    param($value)
    if ($null -eq $value) { return $null }
    if ($value -is [System.Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($value.Keys | Sort-Object)) {
            $ordered[[string]$key] = __sushi_json_sort_value $value[$key]
        }
        return [PSCustomObject]$ordered
    }
    if ($value -is [PSCustomObject]) {
        $ordered = [ordered]@{}
        foreach ($property in @($value.PSObject.Properties | Sort-Object Name)) {
            $ordered[$property.Name] = __sushi_json_sort_value $property.Value
        }
        return [PSCustomObject]$ordered
    }
    if ($value -is [System.Collections.IEnumerable] -and $value -isnot [string]) {
        $items = @()
        foreach ($item in $value) {
            $items += ,(__sushi_json_sort_value $item)
        }
        return ,$items
    }
    return $value
}

function __sushi_json_stringify {
    param($value, [int]$indent = 0)
    $normalized = __sushi_json_sort_value $value
    if ($indent -gt 0) { return ($normalized | ConvertTo-Json -Depth 100) }
    return ($normalized | ConvertTo-Json -Compress -Depth 100)
}

function __sushi_glob_regex {
    param([string]$pattern)
    $pattern = $pattern.Replace('\\', '/')
    $regex = [System.Text.StringBuilder]::new()
    for ($index = 0; $index -lt $pattern.Length; $index++) {
        $character = $pattern[$index]
        switch ($character) {
            '*' {
                if ($index + 1 -lt $pattern.Length -and $pattern[$index + 1] -eq '*') {
                    if ($index + 2 -lt $pattern.Length -and $pattern[$index + 2] -eq '/') {
                        [void]$regex.Append('([^/]*/)*')
                        $index += 2
                    } else {
                        [void]$regex.Append('.*')
                        $index++
                    }
                } else {
                    [void]$regex.Append('[^/]*')
                }
            }
            '?' { [void]$regex.Append('[^/]') }
            '[' {
                $end = $pattern.IndexOf(']', $index + 1)
                if ($end -lt 0) { [void]$regex.Append('\\[') }
                else {
                    $characterClass = $pattern.Substring($index + 1, $end - $index - 1)
                    if ($characterClass.StartsWith('!')) { $characterClass = '^' + $characterClass.Substring(1) }
                    [void]$regex.Append('[').Append($characterClass).Append(']')
                    $index = $end
                }
            }
            default { [void]$regex.Append([regex]::Escape([string]$character)) }
        }
    }
    return '^' + $regex.ToString() + '$'
}

function __sushi_fs_glob {
    param([string]$pattern, [string]$cwd = $null)
    $basePath = if ([string]::IsNullOrWhiteSpace($cwd)) { (Get-Location).Path } else { (Resolve-Path -LiteralPath $cwd -ErrorAction Stop).Path }
    if (-not (Test-Path -LiteralPath $basePath -PathType Container)) { throw "std.fs.glob: directory not found: $cwd" }
    $isAbsolutePattern = [System.IO.Path]::IsPathRooted($pattern)
    $matcher = [regex]::new((__sushi_glob_regex $pattern), [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $results = [System.Collections.Generic.List[string]]::new()
    foreach ($item in @(Get-ChildItem -LiteralPath $basePath -Force -Recurse -ErrorAction Stop)) {
        $fullPath = $item.FullName.Replace('\\', '/')
        $relativePath = $item.FullName.Substring($basePath.Length).TrimStart([char]92, [char]47).Replace('\\', '/')
        $matchPath = if ($isAbsolutePattern) { $fullPath } else { $relativePath }
        if ($matcher.IsMatch($matchPath)) { $results.Add($relativePath) }
    }
    $ordered = [string[]]$results.ToArray()
    [System.Array]::Sort($ordered, [System.StringComparer]::Ordinal)
    return ,$ordered
}

function __sushi_relative_path {
    param([string]$basePath, [string]$path)

    $baseFull = [System.IO.Path]::GetFullPath($basePath)
    $pathFull = [System.IO.Path]::GetFullPath($path)
    if (-not [string]::Equals(
        [System.IO.Path]::GetPathRoot($baseFull),
        [System.IO.Path]::GetPathRoot($pathFull),
        [System.StringComparison]::OrdinalIgnoreCase)) {
        return $pathFull
    }

    $separator = [string][System.IO.Path]::DirectorySeparatorChar
    if (-not $baseFull.EndsWith($separator)) { $baseFull += $separator }
    [Uri]$baseUri = $baseFull
    [Uri]$pathUri = $pathFull
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', $separator)
}

function __sushi_http_request {
    param(
        [string]$method,
        [string]$url,
        $body = $null,
        $headers = $null,
        [string]$contentType = 'application/json'
    )

    $headerMap = __sushi_to_map $headers
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }
    if (-not ("System.Net.Http.HttpClient" -as [type])) {
        try { Add-Type -AssemblyName System.Net.Http } catch { }
    }
    $client = $null
    try {
        $client = [System.Net.Http.HttpClient]::new()
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), $url)
        if ($method -ne 'GET') {
            $payload = if ($null -eq $body) { '' } else { [string]$body }
            $mediaType = if ([string]::IsNullOrWhiteSpace($contentType)) { 'application/json' } else { $contentType }
            $request.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, $mediaType)
        }
        foreach ($entry in $headerMap.GetEnumerator()) {
            if (-not $request.Headers.TryAddWithoutValidation($entry.Key, [string]$entry.Value)) {
                if ($null -eq $request.Content) { $request.Content = [System.Net.Http.StringContent]::new('') }
                $null = $request.Content.Headers.TryAddWithoutValidation($entry.Key, [string]$entry.Value)
            }
        }

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $bodyText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $headerObject = [ordered]@{}
        foreach ($header in $response.Headers) { $headerObject[$header.Key] = ($header.Value -join ', ') }
        foreach ($header in $response.Content.Headers) { $headerObject[$header.Key] = ($header.Value -join ', ') }
        $ok = ($status -ge 200 -and $status -lt 300)
        $json = $null
        if (-not [string]::IsNullOrWhiteSpace($bodyText)) {
            try { $json = ($bodyText | ConvertFrom-Json) } catch { $json = $null }
        }

        return [PSCustomObject]@{
            status = [int]$status
            ok = [bool]$ok
            headers = [PSCustomObject]$headerObject
            body = [string]$bodyText
            json = $json
            url = [string]$url
        }
    } catch {
        return [PSCustomObject]@{
            status = 0
            ok = $false
            headers = [PSCustomObject]@{}
            body = ''
            json = $null
            url = [string]$url
        }
    } finally {
        if ($null -ne $client) {
            $client.Dispose()
        }
    }
}

function __sushi_http_get {
    param([string]$url, $headers = $null)
    return (__sushi_http_request -method 'GET' -url $url -headers $headers)
}

function __sushi_http_post {
    param([string]$url, $body, $headers = $null, [string]$contentType = 'application/json')
    return (__sushi_http_request -method 'POST' -url $url -body $body -headers $headers -contentType $contentType)
}

function __sushi_require_string_receiver {
    param($value, [string]$method)
    if ($null -eq $value) {
        [Console]::Error.WriteLine("Type contract violation: string receiver for '$method' expected non-null value")
        exit 2
    }
    return [string]$value
}

function __sushi_string_trim {
    param($value)
    $s = __sushi_require_string_receiver -value $value -method 'trim'
    return $s.Trim()
}

function __sushi_string_lower {
    param($value)
    $s = __sushi_require_string_receiver -value $value -method 'lower'
    return $s.ToLowerInvariant()
}

function __sushi_string_upper {
    param($value)
    $s = __sushi_require_string_receiver -value $value -method 'upper'
    return $s.ToUpperInvariant()
}

function __sushi_string_split {
    param($value, $sep, [int]$limit = 0)
    $s = __sushi_require_string_receiver -value $value -method 'split'
    $delimiter = [string]$sep
    if ($limit -gt 0) {
        return ,($s.Split(@($delimiter), $limit, [System.StringSplitOptions]::None))
    }

    return ,($s.Split(@($delimiter), [System.StringSplitOptions]::None))
}

function __sushi_string_contains {
    param($value, $needle)
    $s = __sushi_require_string_receiver -value $value -method 'contains'
    return $s.Contains([string]$needle)
}

function __sushi_string_starts_with {
    param($value, $prefix)
    $s = __sushi_require_string_receiver -value $value -method 'startsWith'
    return $s.StartsWith([string]$prefix)
}

function __sushi_string_ends_with {
    param($value, $suffix)
    $s = __sushi_require_string_receiver -value $value -method 'endsWith'
    return $s.EndsWith([string]$suffix)
}

function __sushi_string_replace {
    param($value, $oldValue, $newValue)
    $s = __sushi_require_string_receiver -value $value -method 'replace'
    return $s.Replace([string]$oldValue, [string]$newValue)
}

function __sushi_string_is_match {
    param($value, $pattern)
    $s = __sushi_require_string_receiver -value $value -method 'isMatch'
    return [System.Text.RegularExpressions.Regex]::IsMatch($s, [string]$pattern)
}

function __sushi_string_match {
    param($value, $pattern)
    $s = __sushi_require_string_receiver -value $value -method 'match'
    $m = [System.Text.RegularExpressions.Regex]::Match($s, [string]$pattern)
    if (-not $m.Success) {
        return [PSCustomObject]@{
            ok = $false
            value = ''
            index = -1
            groups = @()
        }
    }

    $groups = @()
    foreach ($g in $m.Groups) {
        $groups += ,([string]$g.Value)
    }

    return [PSCustomObject]@{
        ok = $true
        value = [string]$m.Value
        index = [int]$m.Index
        groups = $groups
    }
}
""");

        _builder.AppendLine(
"""
function __sushi_detect_type {
    param($value)
    if ($null -eq $value) { return 'null' }
    if ($value -is [bool]) { return 'bool' }
    if ($value -is [sbyte] -or $value -is [byte] -or $value -is [int16] -or $value -is [uint16] -or
        $value -is [int] -or $value -is [uint32] -or $value -is [long] -or $value -is [uint64]) { return 'int' }
    if ($value -is [float] -or $value -is [double] -or $value -is [decimal]) { return 'float' }
    if ($value -is [System.Collections.IList] -and $value -isnot [string]) { return 'array' }
    if ($value -is [System.Collections.IDictionary]) { return 'object' }
    if ($value -is [string]) {
        if ($value -eq 'true' -or $value -eq 'false') { return 'bool' }
        if ($value -match '^-?\d+$') { return 'int' }
        if ($value -match '^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$') { return 'float' }
        $trimmed = $value.Trim()
        if ($trimmed.StartsWith('{') -or $trimmed.StartsWith('[')) {
            try {
                $parsed = $trimmed | ConvertFrom-Json
                if ($parsed -is [System.Collections.IList]) { return 'array' }
                if ($parsed -is [System.Collections.IDictionary] -or $parsed.PSObject -ne $null) { return 'object' }
            } catch { }
        }
        return 'string'
    }
    if ($value.PSObject -ne $null) { return 'object' }
    return 'unknown'
}

function __sushi_add {
    param($left, $right)
    $leftType = __sushi_detect_type $left
    $rightType = __sushi_detect_type $right
    $leftNumeric = ($leftType -eq 'int' -or $leftType -eq 'float')
    $rightNumeric = ($rightType -eq 'int' -or $rightType -eq 'float')
    if ($leftNumeric -and $rightNumeric) {
        if ($leftType -eq 'int' -and $rightType -eq 'int') {
            return ([int64]$left + [int64]$right)
        }
        return ([double]$left + [double]$right)
    }

    return ([string]$left + [string]$right)
}

function __sushi_type_check {
    param(
        $value,
        [string]$type = 'any',
        [string]$context = 'value'
    )

    if ([string]::IsNullOrWhiteSpace($type) -or $type -eq 'any' -or $type -eq 'unknown') {
        return $true
    }

    $actual = __sushi_detect_type $value
    $ok = $false
    switch ($type) {
        'string' { $ok = ($actual -eq 'string') }
        'int' { $ok = ($actual -eq 'int') }
        'float' { $ok = ($actual -eq 'float' -or $actual -eq 'int') }
        'bool' { $ok = ($actual -eq 'bool') }
        'array' { $ok = ($actual -eq 'array') }
        'object' { $ok = ($actual -eq 'object') }
        default { $ok = $true }
    }

    if ($ok) { return $true }

    [Console]::Error.WriteLine("Type contract violation: $context expected $type, got $actual")
    return $false
}

function __sushi_has_member {
    param($target, [string]$name)
    if ($null -eq $target) { return $false }
    if ($target -is [System.Collections.IDictionary]) { return $target.Contains($name) }
    $prop = $target.PSObject.Properties[$name]
    return ($null -ne $prop)
}

function __sushi_struct_check {
    param(
        $value,
        [string]$spec,
        [string]$context = 'value'
    )

    if (-not (__sushi_type_check -value $value -type 'object' -context $context)) {
        return $false
    }

    if ([string]::IsNullOrWhiteSpace($spec)) {
        return $true
    }

    $parts = @($spec -split ',')
    foreach ($part in $parts) {
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        $segments = @($part -split ':')
        if ($segments.Count -lt 3) { continue }
        $fieldName = [string]$segments[0]
        $fieldType = [string]$segments[1]
        $fieldRequired = [string]$segments[2]

        if (-not (__sushi_has_member -target $value -name $fieldName)) {
            if ($fieldRequired -eq 'req') {
                [Console]::Error.WriteLine("Type contract violation: $context missing required field $fieldName")
                return $false
            }
            continue
        }

        $fieldValue = __sushi_member $value $fieldName
        if (-not (__sushi_type_check -value $fieldValue -type $fieldType -context "$context.$fieldName")) {
            return $false
        }
    }

    return $true
}
""");

        _builder.AppendLine(
"""
function __sushi_slice {
    param($target, $start = $null, $end = $null)
    $items = @(__sushi_to_array $target)
    $len = $items.Count
    $s = if ($null -eq $start -or [string]::IsNullOrWhiteSpace([string]$start)) { 0 } else { [int]$start }
    $e = if ($null -eq $end -or [string]::IsNullOrWhiteSpace([string]$end)) { $len } else { [int]$end }
    if ($s -lt 0) { $s = $len + $s }
    if ($e -lt 0) { $e = $len + $e }
    if ($s -lt 0) { $s = 0 }
    if ($e -lt 0) { $e = 0 }
    if ($s -gt $len) { $s = $len }
    if ($e -gt $len) { $e = $len }
    if ($e -le $s) { return @() }
    return @($items[$s..($e - 1)])
}

function __sushi_array_push {
    param($target, $values)
    $items = @(__sushi_to_array $target)
    $items += @(__sushi_to_array $values)
    return ,$items
}

function __sushi_call_callable {
    param($fn, $argValues)
    $argsList = @(__sushi_to_array $argValues)
    if ($null -eq $fn) { return $null }
    if ($fn -is [scriptblock]) { return (& $fn @argsList) }
    $name = [string]$fn
    if ([string]::IsNullOrWhiteSpace($name)) { return $null }
    return (& $name @argsList)
}

function __sushi_method_map {
    param($target, $fn)
    $out = @()
    foreach ($item in @(__sushi_to_array $target)) {
        $out += ,(__sushi_call_callable $fn @($item))
    }
    return ,$out
}

function __sushi_method_filter {
    param($target, $fn)
    $out = @()
    foreach ($item in @(__sushi_to_array $target)) {
        $keep = __sushi_call_callable $fn @($item)
        if ([bool]$keep) {
            $out += ,$item
        }
    }
    return ,$out
}

function __sushi_method_reduce {
    param($target, $fn, $hasInitial = $false, $initial = $null)
    $items = @(__sushi_to_array $target)
    if ($items.Count -eq 0 -and -not $hasInitial) { return $null }
    $acc = if ($hasInitial) { $initial } else { $items[0] }
    $start = if ($hasInitial) { 0 } else { 1 }
    for ($i = $start; $i -lt $items.Count; $i++) {
        $acc = __sushi_call_callable $fn @($acc, $items[$i])
    }
    return $acc
}

function __sushi_call_method {
    param($target, [string]$method, $argValues = $null)
    $argsList = @(__sushi_to_array $argValues)
    switch ($method) {
        'name' { return (__sushi_member $target '_name') }
        'ordinal' { return (__sushi_member $target '_ord') }
        'value' { return (__sushi_member $target '_value') }
        'length' { return @($target).Count }
        'push' { return (__sushi_array_push $target $argsList) }
        'map' { return (__sushi_method_map $target $argsList[0]) }
        'filter' { return (__sushi_method_filter $target $argsList[0]) }
        'reduce' {
            if ($argsList.Count -gt 1) { return (__sushi_method_reduce $target $argsList[0] $true $argsList[1]) }
            return (__sushi_method_reduce $target $argsList[0] $false $null)
        }
    }

    $fn = __sushi_member $target ("_m_" + $method)
    if ($null -eq $fn -or [string]::IsNullOrWhiteSpace([string]$fn)) {
        return $null
    }

    return (& ([string]$fn) $target @argsList)
}
""");
    }

    private static string FilterRuntimeBlock(string text, HashSet<string> allowed)
    {
        var output = new StringBuilder();
        foreach (Match match in Regex.Matches(text, @"(?ms)^function\s+(__sushi_[A-Za-z0-9_]+)\s*\{.*?(?=^function\s+__sushi_|\z)"))
        {
            if (!allowed.Contains(match.Groups[1].Value)) continue;
            output.AppendLine(match.Value.TrimEnd());
        }
        return output.ToString();
    }

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

            case IrClassDeclarationStatement:
                // Emitted in the declaration preamble so classes are available
                // to all top-level code, including constructors in other files.
                break;

            case IrEnumDeclarationStatement:
            case IrRichEnumDeclarationStatement:
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
                WriteLine($"${name} = @({EmitFsGlob(intrinsic.Arguments)})");
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

            case IrAssignmentExpression assignment:
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
            IrMemberAccessExpression member => EmitMemberAccess(member),
            IrIndexExpression index => $"({EmitValueExpression(index.Target)})[{EmitValueExpression(index.Index)}]",
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
            IrCallExpression { Callee: "__sushi_slice", Arguments: [var target, var start, var end] } slice =>
                EmitNativeStringSlice(slice),
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

    private string EmitSwitchExpression(IrConditionalExpression root)
    {
        var arms = new List<(IrExpression Value, IrExpression Result)>();
        IrExpression current = root;
        while (current is IrConditionalExpression conditional && conditional.IsSwitchExpression &&
               TryGetSwitchComparison(conditional.Condition, out var comparison))
        {
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

    private string EmitNativeStringSlice(IrCallExpression slice)
    {
        var target = EmitValueExpression(slice.Arguments[0].Value);
        var start = slice.Arguments[1].Value is IrLiteralExpression { Value: null }
            ? "0"
            : EmitValueExpression(slice.Arguments[1].Value);
        var isArray = slice.Arguments[0].Value is IrIdentifierExpression identifier &&
                      _arrayInitializers.ContainsKey(SanitizeName(identifier.Name));
        if (isArray)
        {
            if (slice.Arguments[2].Value is IrLiteralExpression { Value: null })
                return "@(" + target + ")[([int]" + start + ")..($(@(" + target + ").Count) - 1)]";
            var arrayEnd = EmitValueExpression(slice.Arguments[2].Value);
            return "@(" + target + ")[([int]" + start + ")..([int](" + arrayEnd + ") - 1)]";
        }
        if (slice.Arguments[2].Value is IrLiteralExpression { Value: null })
            return $"({target}).Substring([int]({start}))";

        var end = EmitValueExpression(slice.Arguments[2].Value);
        return $"({target}).Substring([int]({start}), [int](({end}) - ({start})))";
    }

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
            "bool" => $"([string]({value}) -eq 'true')",
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
        if (string.IsNullOrEmpty(text))
        {
            _builder.AppendLine();
            return;
        }

        _builder.Append(' ', _indent * 4);
        _builder.AppendLine(text);
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

    private string EmitIntrinsicCommand(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => EmitPrint(call.Arguments, newline: false),
            IntrinsicId.Println => EmitPrint(call.Arguments, newline: true),
            IntrinsicId.StringTrim => $"$null = {EmitStringTrim(call.Arguments)}",
            IntrinsicId.StringLower => $"$null = {EmitStringLower(call.Arguments)}",
            IntrinsicId.StringUpper => $"$null = {EmitStringUpper(call.Arguments)}",
            IntrinsicId.StringLength => $"$null = {EmitStringLength(call.Arguments)}",
            IntrinsicId.StringSplit => $"$null = {EmitStringSplit(call.Arguments)}",
            IntrinsicId.StringContains => $"$null = {EmitStringContains(call.Arguments)}",
            IntrinsicId.StringStartsWith => $"$null = {EmitStringStartsWith(call.Arguments)}",
            IntrinsicId.StringEndsWith => $"$null = {EmitStringEndsWith(call.Arguments)}",
            IntrinsicId.StringReplace => $"$null = {EmitStringReplace(call.Arguments)}",
            IntrinsicId.StringIsMatch => $"$null = {EmitStringIsMatch(call.Arguments)}",
            IntrinsicId.StringMatch => $"$null = {EmitStringMatch(call.Arguments)}",
            IntrinsicId.IoWriteText => EmitIoWriteText(call.Arguments),
            IntrinsicId.EnvSet => EmitEnvSet(call.Arguments),
            IntrinsicId.EnvUnset => $"Remove-Item -Path (\"Env:\" + [string]({Arg(call.Arguments, 0)})) -ErrorAction SilentlyContinue",
            IntrinsicId.ProcessExit => $"exit {EmitValueExpression(call.Arguments[0])}",
            IntrinsicId.ProcessSleep => $"Start-Sleep -Milliseconds {Arg(call.Arguments, 0)}",
            IntrinsicId.ConsoleError => $"[Console]::Error.WriteLine([string]({Arg(call.Arguments, 0)}))",
            IntrinsicId.OsChdir => $"Set-Location -LiteralPath {Arg(call.Arguments, 0)}",
            IntrinsicId.ProcessRun => $"$null = {EmitProcessRun(call.Arguments)}",
            IntrinsicId.ProcessPipeline => $"$null = {EmitProcessPipeline(call.Arguments)}",
            IntrinsicId.ProcessFail => $"$null = {EmitProcessFail(call.Arguments)}",
            IntrinsicId.ProcessRequireSuccess => $"$null = {EmitProcessRequireSuccess(call.Arguments)}",
            IntrinsicId.FsGlob => $"$null = {EmitFsGlob(call.Arguments)}",
            IntrinsicId.FsCreateDirectory => $"$null = {EmitFsCreateDirectory(call.Arguments)}",
            IntrinsicId.FsRemove => $"$null = {EmitFsRemove(call.Arguments)}",
            IntrinsicId.FsCopy => $"$null = {EmitFsCopy(call.Arguments)}",
            IntrinsicId.FsMove => $"$null = {EmitFsMove(call.Arguments)}",
            IntrinsicId.ArchiveZip => $"$null = {EmitArchiveZip(call.Arguments)}",
            IntrinsicId.ArchiveUnzip => $"$null = {EmitArchiveUnzip(call.Arguments)}",
            IntrinsicId.HttpGet => $"$null = {EmitHttpGet(call.Arguments)}",
            IntrinsicId.HttpPost => $"$null = {EmitHttpPost(call.Arguments)}",
            IntrinsicId.HttpDownload => $"$null = {EmitHttpDownload(call.Arguments)}",
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Intrinsic '{call.CanonicalName}' cannot be emitted as a statement in PowerShell", "$null")
        };
    }

    private string EmitIntrinsicValue(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => $"({EmitPrint(call.Arguments, newline: false)})",
            IntrinsicId.Println => $"({EmitPrint(call.Arguments, newline: true)})",
            IntrinsicId.StringTrim => EmitStringTrim(call.Arguments),
            IntrinsicId.StringLower => EmitStringLower(call.Arguments),
            IntrinsicId.StringUpper => EmitStringUpper(call.Arguments),
            IntrinsicId.StringLength => EmitStringLength(call.Arguments),
            IntrinsicId.StringSplit => EmitStringSplit(call.Arguments),
            IntrinsicId.StringContains => EmitStringContains(call.Arguments),
            IntrinsicId.StringStartsWith => EmitStringStartsWith(call.Arguments),
            IntrinsicId.StringEndsWith => EmitStringEndsWith(call.Arguments),
            IntrinsicId.StringReplace => EmitStringReplace(call.Arguments),
            IntrinsicId.StringIsMatch => EmitStringIsMatch(call.Arguments),
            IntrinsicId.StringMatch => EmitStringMatch(call.Arguments),
            IntrinsicId.IoReadText => $"(Get-Content -Raw -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.IoExists => $"(Test-Path -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.FsIsFile => $"(Test-Path -LiteralPath {Arg(call.Arguments, 0)} -PathType Leaf)",
            IntrinsicId.FsIsDirectory => $"(Test-Path -LiteralPath {Arg(call.Arguments, 0)} -PathType Container)",
            IntrinsicId.FsSize => $"([int64](Get-Item -LiteralPath {Arg(call.Arguments, 0)}).Length)",
            IntrinsicId.PathJoin => EmitPathJoin(call.Arguments),
            IntrinsicId.PathDirname => $"(Split-Path -Path {Arg(call.Arguments, 0)} -Parent)",
            IntrinsicId.PathBasename => $"(Split-Path -Path {Arg(call.Arguments, 0)} -Leaf)",
            IntrinsicId.PathExtension => $"([IO.Path]::GetExtension([string]({Arg(call.Arguments, 0)})))",
            IntrinsicId.PathStem => $"([IO.Path]::GetFileNameWithoutExtension([string]({Arg(call.Arguments, 0)})))",
            IntrinsicId.EnvGet => EmitEnvGet(call.Arguments),
            IntrinsicId.EnvHas => $"(Test-Path (\"Env:\" + [string]({Arg(call.Arguments, 0)})))",
            IntrinsicId.ProcessArgs => "$args",
            IntrinsicId.ProcessWhich => "$($commandInfo = Get-Command -Name ([string]({Arg(call.Arguments, 0)})) -ErrorAction SilentlyContinue; if ($null -eq $commandInfo) {{ $null }} else {{ $commandInfo.Source }})",
            IntrinsicId.ConsoleReadLine => "([Console]::ReadLine())",
            IntrinsicId.OsCwd => "((Get-Location).Path)",
            IntrinsicId.IoWriteText => $"({EmitIoWriteText(call.Arguments)})",
            IntrinsicId.EnvSet => $"({EmitEnvSet(call.Arguments)})",
            IntrinsicId.EnvUnset => $"(Remove-Item -Path (\"Env:\" + [string]({Arg(call.Arguments, 0)})) -ErrorAction SilentlyContinue)",
            IntrinsicId.ProcessExit => $"(exit {EmitValueExpression(call.Arguments[0])})",
            IntrinsicId.ProcessSleep => $"(Start-Sleep -Milliseconds {Arg(call.Arguments, 0)})",
            IntrinsicId.ConsoleError => $"([Console]::Error.WriteLine([string]({Arg(call.Arguments, 0)})))",
            IntrinsicId.OsChdir => $"(Set-Location -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.ProcessRun => EmitProcessRun(call.Arguments),
            IntrinsicId.ProcessPipeline => EmitProcessPipeline(call.Arguments),
            IntrinsicId.ProcessFail => EmitProcessFail(call.Arguments),
            IntrinsicId.ProcessRequireSuccess => EmitProcessRequireSuccess(call.Arguments),
            IntrinsicId.FsGlob => EmitFsGlob(call.Arguments),
            IntrinsicId.FsCreateDirectory => EmitFsCreateDirectory(call.Arguments),
            IntrinsicId.FsRemove => EmitFsRemove(call.Arguments),
            IntrinsicId.FsCopy => EmitFsCopy(call.Arguments),
            IntrinsicId.FsMove => EmitFsMove(call.Arguments),
            IntrinsicId.ArchiveZip => EmitArchiveZip(call.Arguments),
            IntrinsicId.ArchiveUnzip => EmitArchiveUnzip(call.Arguments),
            IntrinsicId.HttpGet => EmitHttpGet(call.Arguments),
            IntrinsicId.HttpPost => EmitHttpPost(call.Arguments),
            IntrinsicId.HttpDownload => EmitHttpDownload(call.Arguments),
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Unsupported intrinsic expression in PowerShell: {call.CanonicalName}", "$null")
        };
    }

    private string EmitPrint(IReadOnlyList<IrExpression> arguments, bool newline)
    {
        var value = arguments.Count == 0 ? "''" : Arg(arguments, 0);
        // println is part of the script's output stream; Write-Output preserves
        // that composability while print intentionally remains terminal-style.
        return newline ? $"Write-Output {value}" : $"Write-Host -NoNewline {value}";
    }

    private string EmitStringTrim(IReadOnlyList<IrExpression> arguments)
    {
        return $"([string]({Arg(arguments, 0)})).Trim()";
    }

    private string EmitStringLower(IReadOnlyList<IrExpression> arguments)
    {
        return $"([string]({Arg(arguments, 0)})).ToLowerInvariant()";
    }

    private string EmitStringUpper(IReadOnlyList<IrExpression> arguments)
    {
        return $"([string]({Arg(arguments, 0)})).ToUpperInvariant()";
    }

    private string EmitStringLength(IReadOnlyList<IrExpression> arguments)
    {
        return $"([string]({Arg(arguments, 0)})).Length";
    }

    private string EmitStringSplit(IReadOnlyList<IrExpression> arguments)
    {
        return $"@(([string]({Arg(arguments, 0)})).Split([string]({Arg(arguments, 1)}), [int]({Arg(arguments, 2)})))";
    }

    private string EmitStringContains(IReadOnlyList<IrExpression> arguments)
    {
        return $"([string]({Arg(arguments, 0)})).Contains([string]({Arg(arguments, 1)}))";
    }

    private string EmitStringStartsWith(IReadOnlyList<IrExpression> arguments)
    {
        return $"([string]({Arg(arguments, 0)})).StartsWith([string]({Arg(arguments, 1)}))";
    }

    private string EmitStringEndsWith(IReadOnlyList<IrExpression> arguments)
    {
        return $"([string]({Arg(arguments, 0)})).EndsWith([string]({Arg(arguments, 1)}))";
    }

    private string EmitStringReplace(IReadOnlyList<IrExpression> arguments)
    {
        return $"([string]({Arg(arguments, 0)})).Replace([string]({Arg(arguments, 1)}), [string]({Arg(arguments, 2)}))";
    }

    private string EmitStringIsMatch(IReadOnlyList<IrExpression> arguments)
    {
        return $"[regex]::IsMatch([string]({Arg(arguments, 0)}), [string]({Arg(arguments, 1)}))";
    }

    private string EmitStringMatch(IReadOnlyList<IrExpression> arguments)
    {
        return $"$($m=[regex]::Match([string]({Arg(arguments, 0)}), [string]({Arg(arguments, 1)})); [pscustomobject]@{{ ok=$m.Success; value=$m.Value; index=$m.Index; groups=@($m.Groups | ForEach-Object Value) }})";
    }

    private string EmitIoWriteText(IReadOnlyList<IrExpression> arguments)
    {
        var path = Arg(arguments, 0);
        var text = Arg(arguments, 1);
        var append = Arg(arguments, 2);
        return "$__sushi_path = [string](" + path + "); " +
               "$__sushi_dir = Split-Path -Path $__sushi_path -Parent; " +
               "if ($__sushi_dir -and -not (Test-Path -LiteralPath $__sushi_dir)) { New-Item -ItemType Directory -Path $__sushi_dir -Force | Out-Null }; " +
               "if ([bool]" + append + ") { Add-Content -LiteralPath $__sushi_path -Value " + text + " } else { Set-Content -LiteralPath $__sushi_path -Value " + text + " }";
    }

    private string EmitEnvSet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var value = Arg(arguments, 1);
        return $"Set-Item -Path (\"Env:\" + [string]({name})) -Value {value}";
    }

    private string EmitEnvGet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var fallback = Arg(arguments, 1);
        return $"$($n=[string]({name}); if (Test-Path (\"Env:\" + $n)) {{ (Get-Item (\"Env:\" + $n)).Value }} else {{ {fallback} }})";
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

        var args = string.Join(", ", arguments.Select(EmitValueExpression));
        return $"$($parts=@({args}); $j=$parts[0]; for($i=1; $i -lt $parts.Count; $i++){{ $j = Join-Path -Path $j -ChildPath $parts[$i] }}; $j)";
    }

    private string EmitProcessRun(IReadOnlyList<IrExpression> arguments)
    {
        return "(__sushi_process_run " +
               "-command ([string](" + Arg(arguments, 0) + ")) " +
               "-argValues " + Arg(arguments, 1) + " " +
               "-cwd " + Arg(arguments, 2) + " " +
               "-envMap " + Arg(arguments, 3) + " " +
               "-inputText " + Arg(arguments, 4) + " " +
               "-timeoutMs ([int](" + Arg(arguments, 5) + ")) " +
               "-allowFailure ([bool](" + Arg(arguments, 6) + ")) " +
               "-stream ([bool](" + Arg(arguments, 7) + ")))";
    }

    private string EmitProcessPipeline(IReadOnlyList<IrExpression> arguments)
    {
        return "(__sushi_process_pipeline " +
               "-stages " + Arg(arguments, 0) + " " +
               "-cwd " + Arg(arguments, 1) + " " +
               "-envMap " + Arg(arguments, 2) + " " +
               "-inputText " + Arg(arguments, 3) + " " +
               "-timeoutMs ([int](" + Arg(arguments, 4) + ")) " +
               "-allowFailure ([bool](" + Arg(arguments, 5) + ")) " +
               "-stream ([bool](" + Arg(arguments, 6) + ")))";
    }

    private string EmitProcessFail(IReadOnlyList<IrExpression> arguments)
    {
        return $"(__sushi_process_fail -result {Arg(arguments, 0)})";
    }

    private string EmitProcessRequireSuccess(IReadOnlyList<IrExpression> arguments)
    {
        return $"(__sushi_process_require_success -result {Arg(arguments, 0)})";
    }

    private string EmitFsGlob(IReadOnlyList<IrExpression> arguments)
    {
        return $"(__sushi_fs_glob -pattern ([string]({Arg(arguments, 0)})) -cwd {Arg(arguments, 1)})";
    }

    private string EmitFsSize(IReadOnlyList<IrExpression> arguments) =>
        $"([int64]$(if ((Get-Item -LiteralPath {Arg(arguments, 0)}).PSIsContainer) {{ throw 'std.fs.size: regular file required' }} else {{ (Get-Item -LiteralPath {Arg(arguments, 0)}).Length }}))";

    private string EmitFsCreateDirectory(IReadOnlyList<IrExpression> arguments) =>
        $"(New-Item -ItemType Directory -Force -Path {Arg(arguments, 0)})";

    private string EmitFsRemove(IReadOnlyList<IrExpression> arguments) =>
        $"$(if ([bool]({Arg(arguments, 1)})) {{ Remove-Item -LiteralPath {Arg(arguments, 0)} -Force -Recurse }} else {{ Remove-Item -LiteralPath {Arg(arguments, 0)} -Force }})";

    private string EmitFsCopy(IReadOnlyList<IrExpression> arguments) =>
        $"$(if ([bool]({Arg(arguments, 2)})) {{ Copy-Item -LiteralPath {Arg(arguments, 0)} -Destination {Arg(arguments, 1)} -Recurse -Force }} else {{ Copy-Item -LiteralPath {Arg(arguments, 0)} -Destination {Arg(arguments, 1)} -Force }})";

    private string EmitFsMove(IReadOnlyList<IrExpression> arguments) =>
        $"(Move-Item -LiteralPath {Arg(arguments, 0)} -Destination {Arg(arguments, 1)} -Force)";

    private string EmitArchiveZip(IReadOnlyList<IrExpression> arguments) =>
        $"(Compress-Archive -Path {Arg(arguments, 0)} -DestinationPath {Arg(arguments, 1)} -Force)";

    private string EmitArchiveUnzip(IReadOnlyList<IrExpression> arguments) =>
        $"(Expand-Archive -LiteralPath {Arg(arguments, 0)} -DestinationPath {Arg(arguments, 1)} -Force)";

    private string EmitHttpGet(IReadOnlyList<IrExpression> arguments)
    {
        return $"(__sushi_http_get -url ([string]({Arg(arguments, 0)})) -headers {Arg(arguments, 1)})";
    }

    private string EmitHttpPost(IReadOnlyList<IrExpression> arguments)
    {
        return "(__sushi_http_post " +
               "-url ([string](" + Arg(arguments, 0) + ")) " +
               "-body " + Arg(arguments, 1) + " " +
               "-headers " + Arg(arguments, 2) + " " +
               "-contentType ([string](" + Arg(arguments, 3) + ")))";
    }

    private string EmitHttpDownload(IReadOnlyList<IrExpression> arguments) =>
        $"(Invoke-WebRequest -UseBasicParsing -Uri {Arg(arguments, 0)} -OutFile {Arg(arguments, 1)})";

    private string Arg(IReadOnlyList<IrExpression> arguments, int index)
    {
        return index < arguments.Count ? EmitValueExpression(arguments[index]) : "$null";
    }
}
