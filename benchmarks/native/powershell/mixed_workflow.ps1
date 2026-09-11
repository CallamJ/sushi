$user = [pscustomobject]@{ hello = "world"; n = 1 }
& dotnet --version | Out-Null
$code = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
$text = "  $($user.hello)  "

$i = 0
$output = ""
while ($i -lt 25) {
    $output = $text.Trim().ToUpperInvariant()
    $i++
}

Write-Output $output
Write-Output $code
