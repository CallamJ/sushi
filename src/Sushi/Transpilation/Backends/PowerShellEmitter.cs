namespace Sushi.Transpilation.Backends;

using System.Globalization;
using System.Linq;
using System.Text;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed class PowerShellEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1200";

    private readonly StringBuilder _builder = new();
    private EmitContext _context = null!;
    private int _indent;

    public string Emit(IrProgram program, EmitContext context)
    {
        _builder.Clear();
        _context = context;
        _indent = 0;

        WriteLine("Set-StrictMode -Version Latest");
        WriteLine("");
        EmitRuntimeHelpers();
        WriteLine("");

        foreach (var statement in program.Statements)
        {
            EmitStatement(statement);
        }

        return _builder.ToString();
    }

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
    if ($target -is [System.Collections.IList]) { return $target[[int]$index] }
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
                try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
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

function __sushi_json_stringify {
    param($value, [int]$indent = 0)
    if ($indent -gt 0) { return ($value | ConvertTo-Json -Depth 100) }
    return ($value | ConvertTo-Json -Compress -Depth 100)
}

function __sushi_fs_glob {
    param([string]$pattern, [string]$cwd = $null)
    $resolvedPattern = if (-not [string]::IsNullOrWhiteSpace($cwd)) { Join-Path -Path $cwd -ChildPath $pattern } else { $pattern }
    $items = @()
    if ($resolvedPattern.Contains('**')) {
        $parts = $resolvedPattern -split '\*\*', 2
        $basePath = if ([string]::IsNullOrWhiteSpace($parts[0])) { '.' } else { $parts[0].TrimEnd('/', '\') }
        $tailPattern = $parts[1].TrimStart('/', '\')
        if ([string]::IsNullOrWhiteSpace($tailPattern)) { $tailPattern = '*' }
        $items = @(Get-ChildItem -Path $basePath -Recurse -Filter $tailPattern -ErrorAction SilentlyContinue)
    } else {
        $items = @(Get-ChildItem -Path $resolvedPattern -ErrorAction SilentlyContinue)
    }
    if ([string]::IsNullOrWhiteSpace($cwd)) { return ,@($items | ForEach-Object { $_.FullName }) }
    $cwdPath = (Resolve-Path -LiteralPath $cwd).Path
    return ,@($items | ForEach-Object { [System.IO.Path]::GetRelativePath($cwdPath, $_.FullName) })
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
""");
    }

    private void EmitStatement(IrStatement statement)
    {
        switch (statement)
        {
            case IrBlockStatement block:
                foreach (var child in block.Statements)
                {
                    EmitStatement(child);
                }
                break;

            case IrVariableDeclarationStatement variable:
                WriteLine($"${SanitizeName(variable.Name)} = {EmitValueExpression(variable.Initializer ?? new IrLiteralExpression(null))}");
                break;

            case IrExpressionStatement expressionStatement:
                EmitExpressionStatement(expressionStatement.Expression);
                break;

            case IrIfStatement ifStatement:
                EmitIfStatement(ifStatement);
                break;

            case IrWhileStatement whileStatement:
                EmitWhileStatement(whileStatement);
                break;

            case IrForStatement forStatement:
                EmitForStatement(forStatement);
                break;

            case IrFunctionDeclarationStatement function:
                EmitFunction(function);
                break;

            case IrReturnStatement returnStatement:
                if (returnStatement.Expression != null)
                {
                    WriteLine($"return {EmitValueExpression(returnStatement.Expression)}");
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

    private void EmitFunction(IrFunctionDeclarationStatement statement)
    {
        WriteLine($"function {SanitizeName(statement.Name)} {{");
        _indent++;

        if (statement.Parameters.Count > 0)
        {
            var parameterList = string.Join(", ", statement.Parameters.Select(p => $"${SanitizeName(p)}"));
            WriteLine($"param({parameterList})");
        }

        EmitStatement(statement.Body);
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

            case IrAssignmentExpression assignment:
                WriteLine(EmitAssignmentExpression(assignment));
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
        }

        _context.Error(UnsupportedEmitCode, $"Unsupported expression statement in PowerShell emitter: {expression.GetType().Name}");
    }

    private string EmitAssignmentExpression(IrAssignmentExpression assignment)
    {
        var name = SanitizeName(assignment.Target.Name);
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
        var callee = SanitizeName(call.Callee);
        var arguments = call.Arguments.Select(EmitValueExpression).ToList();
        return arguments.Count > 0
            ? $"{callee} {string.Join(" ", arguments)}"
            : callee;
    }

    private string EmitConditionExpression(IrExpression expression)
    {
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
            IrIdentifierExpression identifier => $"${SanitizeName(identifier.Name)}",
            IrArrayLiteralExpression array => EmitArrayLiteral(array),
            IrObjectLiteralExpression obj => EmitObjectLiteral(obj),
            IrMemberAccessExpression member => $"(__sushi_member -target {EmitValueExpression(member.Target)} -name {Escape.PowerShellSingleQuoted(member.MemberName)})",
            IrIndexExpression index => $"(__sushi_index -target {EmitValueExpression(index.Target)} -index {EmitValueExpression(index.Index)})",
            IrUnaryExpression unary when unary.Operator is "!" =>
                $"(-not {EmitValueExpression(unary.Operand)})",
            IrUnaryExpression unary when unary.Operator is "-" or "+" =>
                $"({unary.Operator}{EmitValueExpression(unary.Operand)})",
            IrBinaryExpression binary =>
                $"({EmitValueExpression(binary.Left)} {MapBinaryOperator(binary.Operator)} {EmitValueExpression(binary.Right)})",
            IrIntrinsicCallExpression intrinsicCall =>
                EmitIntrinsicValue(intrinsicCall),
            IrCallExpression call =>
                $"({EmitCallCommand(call)})",
            IrAssignmentExpression assignment =>
                $"({EmitAssignmentExpression(assignment)}; ${SanitizeName(assignment.Target.Name)})",
            _ => "$null"
        };
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

        var properties = string.Join("; ", expression.Properties.Select(property =>
            $"{Escape.PowerShellSingleQuoted(property.Name)} = {EmitValueExpression(property.Value)}"));
        return $"([PSCustomObject]@{{ {properties} }})";
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

    private static string SanitizeName(string name)
    {
        return name.Replace(".", "_").Replace("-", "_");
    }

    private string EmitIntrinsicCommand(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => EmitPrint(call.Arguments, newline: false),
            IntrinsicId.Println => EmitPrint(call.Arguments, newline: true),
            IntrinsicId.IoWriteText => EmitIoWriteText(call.Arguments),
            IntrinsicId.EnvSet => EmitEnvSet(call.Arguments),
            IntrinsicId.ProcessExit => $"exit {EmitValueExpression(call.Arguments[0])}",
            IntrinsicId.OsChdir => $"Set-Location -LiteralPath {Arg(call.Arguments, 0)}",
            IntrinsicId.ProcessRun => $"$null = {EmitProcessRun(call.Arguments)}",
            IntrinsicId.ProcessPipeline => $"$null = {EmitProcessPipeline(call.Arguments)}",
            IntrinsicId.ProcessFail => $"$null = {EmitProcessFail(call.Arguments)}",
            IntrinsicId.ProcessRequireSuccess => $"$null = {EmitProcessRequireSuccess(call.Arguments)}",
            IntrinsicId.JsonParse => $"$null = {EmitJsonParse(call.Arguments)}",
            IntrinsicId.JsonStringify => $"$null = {EmitJsonStringify(call.Arguments)}",
            IntrinsicId.FsGlob => $"$null = {EmitFsGlob(call.Arguments)}",
            IntrinsicId.HttpGet => $"$null = {EmitHttpGet(call.Arguments)}",
            IntrinsicId.HttpPost => $"$null = {EmitHttpPost(call.Arguments)}",
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Intrinsic '{call.CanonicalName}' cannot be emitted as a statement in PowerShell", "$null")
        };
    }

    private string EmitIntrinsicValue(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => $"({EmitPrint(call.Arguments, newline: false)})",
            IntrinsicId.Println => $"({EmitPrint(call.Arguments, newline: true)})",
            IntrinsicId.IoReadText => $"(Get-Content -Raw -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.IoExists => $"(Test-Path -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.PathJoin => EmitPathJoin(call.Arguments),
            IntrinsicId.PathDirname => $"(Split-Path -Path {Arg(call.Arguments, 0)} -Parent)",
            IntrinsicId.PathBasename => $"(Split-Path -Path {Arg(call.Arguments, 0)} -Leaf)",
            IntrinsicId.EnvGet => EmitEnvGet(call.Arguments),
            IntrinsicId.ProcessArgs => "$args",
            IntrinsicId.OsCwd => "((Get-Location).Path)",
            IntrinsicId.IoWriteText => $"({EmitIoWriteText(call.Arguments)})",
            IntrinsicId.EnvSet => $"({EmitEnvSet(call.Arguments)})",
            IntrinsicId.ProcessExit => $"(exit {EmitValueExpression(call.Arguments[0])})",
            IntrinsicId.OsChdir => $"(Set-Location -LiteralPath {Arg(call.Arguments, 0)})",
            IntrinsicId.ProcessRun => EmitProcessRun(call.Arguments),
            IntrinsicId.ProcessPipeline => EmitProcessPipeline(call.Arguments),
            IntrinsicId.ProcessFail => EmitProcessFail(call.Arguments),
            IntrinsicId.ProcessRequireSuccess => EmitProcessRequireSuccess(call.Arguments),
            IntrinsicId.JsonParse => EmitJsonParse(call.Arguments),
            IntrinsicId.JsonStringify => EmitJsonStringify(call.Arguments),
            IntrinsicId.FsGlob => EmitFsGlob(call.Arguments),
            IntrinsicId.HttpGet => EmitHttpGet(call.Arguments),
            IntrinsicId.HttpPost => EmitHttpPost(call.Arguments),
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Unsupported intrinsic expression in PowerShell: {call.CanonicalName}", "$null")
        };
    }

    private string EmitPrint(IReadOnlyList<IrExpression> arguments, bool newline)
    {
        var value = arguments.Count == 0 ? "''" : Arg(arguments, 0);
        return newline ? $"Write-Host {value}" : $"Write-Host -NoNewline {value}";
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

    private string EmitJsonParse(IReadOnlyList<IrExpression> arguments)
    {
        return $"(__sushi_json_parse -text {Arg(arguments, 0)})";
    }

    private string EmitJsonStringify(IReadOnlyList<IrExpression> arguments)
    {
        return $"(__sushi_json_stringify -value {Arg(arguments, 0)} -indent ([int]({Arg(arguments, 1)})))";
    }

    private string EmitFsGlob(IReadOnlyList<IrExpression> arguments)
    {
        return $"(__sushi_fs_glob -pattern ([string]({Arg(arguments, 0)})) -cwd {Arg(arguments, 1)})";
    }

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

    private string Arg(IReadOnlyList<IrExpression> arguments, int index)
    {
        return index < arguments.Count ? EmitValueExpression(arguments[index]) : "$null";
    }
}
