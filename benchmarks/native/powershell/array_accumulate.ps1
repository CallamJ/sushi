function Sum10 {
    $total = 0
    for ($i = 1; $i -le 10; $i++) {
        $total += $i
    }
    return $total
}

$i = 0
$total = 0
while ($i -lt 5) {
    $total += (Sum10)
    $i++
}
Write-Output $total
