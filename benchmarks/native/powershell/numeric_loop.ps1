function Compute([int]$n) {
    $i = 0
    $total = 0
    while ($i -lt $n) {
        $total = $total + ($i * 3) - 1
        $i++
    }
    return $total
}

Write-Output (Compute 5000)
