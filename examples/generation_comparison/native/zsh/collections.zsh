#!/usr/bin/env zsh
set -euo pipefail
setopt ksharrays

values=(10 20 30)
typeset -A user=(name sushi)
printf '%s\n' "${user[name]}:${values[1]}"
