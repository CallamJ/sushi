Set-StrictMode -Version Latest

function double {
    param([int]$value)
    return ($value * 2)
}
Write-Host (double 21)
