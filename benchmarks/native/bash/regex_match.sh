#!/usr/bin/env bash
set -euo pipefail

text="Alpha42Beta"
i=0
ok=0
while (( i < 400 )); do
  lowered="${text,,}"
  if [[ "$lowered" =~ ^[a-z]+[0-9]+[a-z]+$ ]]; then
    ok=1
  else
    ok=0
  fi
  ((i++))
done
printf '%s\n' "$ok"