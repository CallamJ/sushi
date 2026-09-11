$values = @(10, 20, 30)
$user = [pscustomobject]@{ name = 'sushi' }
Write-Output ($user.name + ':' + $values[1])
