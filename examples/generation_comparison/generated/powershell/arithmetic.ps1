Set-StrictMode -Version Latest

function double {
    param([int]$value)
    $__sushi_return_value = ($value * 2)
    return $__sushi_return_value
}
Write-Host (double 21)
