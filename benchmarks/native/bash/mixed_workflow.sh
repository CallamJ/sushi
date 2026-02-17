#!/usr/bin/env bash
set -euo pipefail

payload='{"hello":"world","n":1}'
dotnet --version >/dev/null 2>&1
run_code="$?"
text="  world  "

i=0
output=""
while (( i < 100 )); do
  trimmed="${text## }"
  trimmed="${trimmed%% }"
  output="${trimmed^^}"
  ((i++))
done

printf '%s\n' "$output"
printf '%s\n' "$run_code"