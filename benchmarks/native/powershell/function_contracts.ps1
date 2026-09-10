function Next-Value([int]$x) {
    return ($x + 1)
}

function Get-UserLabel {
    return "worker:30"
}

$i = 0
$value = 0
while ($i -lt 100) {
    $value = Next-Value $i
    $i++
}

Write-Output (Get-UserLabel)
Write-Output $value
