#!/usr/bin/env zsh
set -euo pipefail

typeset -A user=(hello world n 1)
dotnet --version >/dev/null 2>&1
run_code="$?"
text="  ${user[hello]}  "

i=0
output=""
while (( i < 25 )); do
  trimmed="${text#"${text%%[![:space:]]*}"}"
  trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
  output="${(U)trimmed}"
  ((i += 1))
done

print -r -- "$output"
print -r -- "$run_code"
