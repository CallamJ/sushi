#!/usr/bin/env bash
set -euo pipefail
__sushi_result=''
double() {
    local -i value="$1"
    __sushi_result=$(( (value * 2) ))
    return 0
}
double 21 || { __sushi_status=$?; exit "$__sushi_status"; }
__sushi_value_1="${__sushi_result-}"
printf '%s\n' "${__sushi_value_1-}"
