#!/usr/bin/env zsh
set -euo pipefail

text="Alpha42Beta"
i=0
ok=0
while (( i < 50 )); do
  lowered="${(L)text}"
  if [[ "$lowered" =~ '^[a-z]+[0-9]+[a-z]+$' ]]; then
    ok=1
  else
    ok=0
  fi
  ((i += 1))
done
print -r -- "$ok"
