#!/usr/bin/env bash
set -euo pipefail
double() {
    local -n out="$1"
    local -i value="$2"
    out=$(( (value * 2) ))
    return 0
}
_tmp1=''
double '_tmp1' 21 || exit $?
printf '%s\n' "${_tmp1-}"
