$sourceText = '{"name":"sushi","count":42}'
$i = 0
$text = ""
while ($i -lt 20) {
    $obj = $sourceText | ConvertFrom-Json
    $text = $obj | ConvertTo-Json -Compress
    $i++
}
Write-Output $text
