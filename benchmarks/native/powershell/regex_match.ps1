$text = "Alpha42Beta"
$i = 0
$ok = 0
while ($i -lt 400) {
    if ($text.ToLowerInvariant() -match '^[a-z]+\d+[a-z]+$') {
        $ok = 1
    }
    else {
        $ok = 0
    }
    $i++
}
Write-Output $ok