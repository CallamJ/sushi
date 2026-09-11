#!/usr/bin/env bash
set -euo pipefail

values=(10 20 30)
declare -A user=([name]='sushi')
printf '%s\n' "${user[name]}:${values[1]}"
