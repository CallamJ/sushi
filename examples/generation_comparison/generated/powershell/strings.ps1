Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$name = '  Alice  '
Write-Host ([string](([string]($name)).Trim())).ToLowerInvariant()
