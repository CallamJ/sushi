#!/usr/bin/env bash
set -euo pipefail

compute() {
  local n="$1"
  local i=0
  local total=0
  while (( i < n )); do
    total=$(( total + (i * 3) - 1 ))
    ((i++))
  done
  printf '%s' "$total"
}

compute 20000
printf '\n'