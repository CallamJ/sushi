#!/usr/bin/env zsh
set -eu
set -o pipefail
double() {
    local out="$1"
    local -i value="$2"
    : ${(P)out::=$(( (value * 2) ))}
    return 0
}
_tmp1=''
double '_tmp1' 21 || exit $?
printf '%s\n' "${_tmp1-}"
