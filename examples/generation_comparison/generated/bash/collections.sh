#!/usr/bin/env bash
set -euo pipefail
declare -a values=(10 20 30)
declare -A user=(['name']='sushi')
printf '%s\n' "${user['name']-}:${values[1]-}"
