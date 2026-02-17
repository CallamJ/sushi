$payload = @{ hello = "world"; n = 1 } | ConvertTo-Json -Compress
& dotnet --version | Out-Null
$code = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
$parsed = $payload | ConvertFrom-Json
$text = "  $($parsed.hello)  "

$i = 0
$output = ""
while ($i -lt 100) {
    $output = $text.Trim().ToUpperInvariant()
    $i++
}

Write-Output $output
Write-Output $code