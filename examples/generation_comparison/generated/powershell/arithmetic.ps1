Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function double {
    param([int]$value)
    return ($value * 2)
}
Write-Host (double 21)
