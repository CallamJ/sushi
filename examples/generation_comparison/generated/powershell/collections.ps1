Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$values = @(10, 20, 30)
$user = ([PSCustomObject]@{ 'name' = 'sushi' })
Write-Host ((($user).name + ':') + ($values)[1])
