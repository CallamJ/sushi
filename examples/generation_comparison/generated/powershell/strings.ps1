Set-StrictMode -Version Latest

$name = '  Alice  '
Write-Host ([string](([string]($name)).Trim())).ToLowerInvariant()
