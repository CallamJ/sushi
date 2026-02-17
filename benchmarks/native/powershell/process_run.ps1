& dotnet --version | Out-Null
$code = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
Write-Output $code