$pipelineOutput = @(& dotnet --list-sdks | Sort-Object)
$code = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
Write-Output $code
