$sourceText = '{"name":"sushi","count":42}'
$i = 0
$text = ""
while ($i -lt 20) {
    $obj = $sourceText | ConvertFrom-Json
    $text = [ordered]@{ count = $obj.count; name = $obj.name } | ConvertTo-Json -Compress
    $i++
}
Write-Output $text
