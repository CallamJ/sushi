#!/usr/bin/env bash
set -euo pipefail

sum10() {
  local total=0
  local i=1
  while (( i <= 10 )); do
    total=$((total + i))
    ((i += 1))
  done
  printf '%s' "$total"
}

i=0
total=0
while (( i < 5 )); do
  total=$(( total + $(sum10) ))
  ((i += 1))
done
printf '%s\n' "$total"
