#!/usr/bin/env zsh
set -euo pipefail

compute() {
  local n="$1"
  local i=0
  local total=0
  while (( i < n )); do
    total=$(( total + (i * 3) - 1 ))
    ((i += 1))
  done
  print -r -- "$total"
}

compute 20000
