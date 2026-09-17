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
    private void EmitNativeEnum(IrEnumDeclarationStatement declaration)
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

    // Legacy helper definitions are retained as a source catalog only. They
    // are never emitted wholesale; EmitImportedStdlibHelpers selects the
    // definitions required by explicit stdlib imports.
    private void EmitStdlibHelperDefinitions()
    {
        _document.Template(
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

        _document.Template(
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

        _document.Template(
"""
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

    private static string FilterStdlibHelpers(string text, HashSet<string> allowed)
    {
        var output = new StringBuilder();
        foreach (Match match in Regex.Matches(text, @"(?ms)^function\s+(__sushi_[A-Za-z0-9_]+)\s*\{.*?(?=^function\s+__sushi_|\z)"))
        {
            if (!allowed.Contains(match.Groups[1].Value)) continue;
            output.AppendLine(match.Value.TrimEnd());
        }
        return output.ToString();
    }
}
