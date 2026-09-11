Set-StrictMode -Version Latest

$values = @(10, 20, 30)
$user = [pscustomobject]@{ name = 'sushi' }
Write-Host ($user.name + ':' + $values[1])
