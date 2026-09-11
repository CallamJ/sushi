#!/usr/bin/env bash
set -euo pipefail

declare -A user=([hello]='world' [n]=1)
dotnet --version >/dev/null 2>&1
run_code="$?"
text="  ${user[hello]}  "

i=0
output=""
while (( i < 25 )); do
  trimmed="${text#"${text%%[![:space:]]*}"}"
  trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
  output="${trimmed^^}"
  ((i += 1))
done

printf '%s\n' "$output"
printf '%s\n' "$run_code"
