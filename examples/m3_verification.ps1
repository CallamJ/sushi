Set-StrictMode -Version Latest

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

        if ($null -ne $inputText) {
            $stdinFile = [System.IO.Path]::GetTempFileName()
            Set-Content -LiteralPath $stdinFile -Value ([string]$inputText) -NoNewline
            Get-Content -Raw -LiteralPath $stdinFile | & $command @argList > $stdoutFile 2> $stderrFile
        } else {
            & $command @argList > $stdoutFile 2> $stderrFile
        }

        $exitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
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

$failures = 0
Write-Host 'M3 verification started'
$toolName = 'dotnet'
$runArgs = @('--version')
$pipelineStages = @(([PSCustomObject]@{ 'command' = 'dotnet'; 'args' = @('--version') }), ([PSCustomObject]@{ 'command' = 'dotnet'; 'args' = @('--info') }))
$runResult = (__sushi_process_run -command ([string]($toolName)) -argValues $runArgs -cwd $null -envMap $null -inputText $null -timeoutMs ([int](0)) -allowFailure ([bool]($true)) -stream ([bool]($false)))
if (((__sushi_member -target $runResult -name 'code') -ne 0)) {
    Write-Host 'dotnet unavailable, trying git fallback'
    $toolName = 'git'
    $runArgs = @('--version')
    $pipelineStages = @(([PSCustomObject]@{ 'command' = 'git'; 'args' = @('--version') }), ([PSCustomObject]@{ 'command' = 'git'; 'args' = @('hash-object', '--stdin') }))
    $runResult = (__sushi_process_run -command ([string]($toolName)) -argValues $runArgs -cwd $null -envMap $null -inputText $null -timeoutMs ([int](0)) -allowFailure ([bool]($true)) -stream ([bool]($false)))
}
if (((__sushi_member -target $runResult -name 'code') -eq 0)) {
    Write-Host 'PASS process.run exit code'
}
else {
    Write-Host 'FAIL process.run exit code'
    Write-Host (__sushi_member -target $runResult -name 'stderr')
    $failures += 1
}
if (((__sushi_member -target $runResult -name 'stdout') -ne '')) {
    Write-Host 'PASS process.run stdout'
}
else {
    Write-Host 'FAIL process.run stdout'
    $failures += 1
}
$pipelineResult = (__sushi_process_pipeline -stages $pipelineStages -cwd $null -envMap $null -inputText $null -timeoutMs ([int](0)) -allowFailure ([bool]($true)) -stream ([bool]($false)))
if (((__sushi_member -target $pipelineResult -name 'code') -eq 0)) {
    Write-Host 'PASS process.pipeline exit code'
}
else {
    Write-Host 'FAIL process.pipeline exit code'
    Write-Host (__sushi_member -target $pipelineResult -name 'stderr')
    $failures += 1
}
if (((__sushi_member -target $pipelineResult -name 'stdout') -ne '')) {
    Write-Host 'PASS process.pipeline stdout'
}
else {
    Write-Host 'FAIL process.pipeline stdout'
    $failures += 1
}
$payload = ([PSCustomObject]@{ 'tool' = 'sushi'; 'count' = 3; 'ok' = $true })
$jsonText = (__sushi_json_stringify -value $payload -indent ([int](0)))
$parsed = (__sushi_json_parse -text $jsonText)
if (((__sushi_member -target $parsed -name 'tool') -eq 'sushi')) {
    Write-Host 'PASS json.parse field'
}
else {
    Write-Host 'FAIL json.parse field'
    $failures += 1
}
if (((__sushi_member -target $parsed -name 'count') -eq 3)) {
    Write-Host 'PASS json.parse numeric field'
}
else {
    Write-Host 'FAIL json.parse numeric field'
    $failures += 1
}
if (((__sushi_member -target $parsed -name 'ok') -eq $true)) {
    Write-Host 'PASS json.parse bool field'
}
else {
    Write-Host 'FAIL json.parse bool field'
    $failures += 1
}
$verifyPath = $($parts=@('tmp', 'm3_verify.txt'); $j=$parts[0]; for($i=1; $i -lt $parts.Count; $i++){ $j = Join-Path -Path $j -ChildPath $parts[$i] }; $j)
$__sushi_path = [string]($verifyPath); $__sushi_dir = Split-Path -Path $__sushi_path -Parent; if ($__sushi_dir -and -not (Test-Path -LiteralPath $__sushi_dir)) { New-Item -ItemType Directory -Path $__sushi_dir -Force | Out-Null }; if ([bool]$false) { Add-Content -LiteralPath $__sushi_path -Value 'ok' } else { Set-Content -LiteralPath $__sushi_path -Value 'ok' }
$globMatches = (__sushi_fs_glob -pattern ([string]('tmp/m3_verify*.txt')) -cwd $null)
$firstMatch = (__sushi_index -target $globMatches -index 0)
if (($firstMatch -ne $null)) {
    Write-Host 'PASS fs.glob'
}
else {
    Write-Host 'FAIL fs.glob'
    $failures += 1
}
$skipHttp = $($n=[string]('SUSHI_SKIP_HTTP'); if (Test-Path ("Env:" + $n)) { (Get-Item ("Env:" + $n)).Value } else { '0' })
if (($skipHttp -eq '1')) {
    Write-Host 'SKIP http (SUSHI_SKIP_HTTP=1)'
}
else {
    $response = (__sushi_http_get -url ([string]('https://example.com')) -headers $null)
    if (((__sushi_member -target $response -name 'status') -eq 0)) {
        Write-Host 'SKIP http (unavailable in current runtime/network)'
    }
    else {
        if (((__sushi_member -target $response -name 'ok') -eq $true)) {
            Write-Host 'PASS http.get ok'
        }
        else {
            Write-Host 'FAIL http.get ok'
            Write-Host (__sushi_member -target $response -name 'status')
            $failures += 1
        }
        if (((__sushi_member -target $response -name 'body') -ne '')) {
            Write-Host 'PASS http.get body'
        }
        else {
            Write-Host 'FAIL http.get body'
            $failures += 1
        }
    }
}
if (($failures -eq 0)) {
    Write-Host 'M3 verification PASSED'
    exit 0
}
else {
    Write-Host 'M3 verification FAILED'
    Write-Host $failures
    exit 1
}
