#!/usr/bin/env zsh
set -eu
set -o pipefail
setopt ksharrays
declare -a values=(10 20 30)
declare -A user=('name' 'sushi')
printf '%s\n' "${user[name]-}:${values[1]-}"
