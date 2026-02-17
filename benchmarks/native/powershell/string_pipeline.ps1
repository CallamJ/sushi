$raw = "  Alpha,Beta,Gamma42  "
$i = 0
$output = ""
while ($i -lt 200) {
    $output = $raw.Trim().ToLowerInvariant().Replace("gamma42", "delta")
    $i++
}
Write-Output $output