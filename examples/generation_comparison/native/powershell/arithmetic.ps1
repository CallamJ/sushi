Set-StrictMode -Version Latest

function Double([int]$value) {
    return $value * 2
}

Write-Host (Double 21)
