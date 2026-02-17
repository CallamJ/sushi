$null = Get-ChildItem -Path src -Recurse -Filter *.cs -ErrorAction SilentlyContinue
Write-Output "glob_done"