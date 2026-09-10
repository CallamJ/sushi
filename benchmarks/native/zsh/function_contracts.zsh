#!/usr/bin/env zsh
set -euo pipefail

next_value() {
  local x="$1"
  print -r -- $((x + 1))
}

user_label() {
  print -r -- "worker:30"
}

i=0
value=0
while (( i < 1000 )); do
  value="$(next_value "$i")"
  ((i += 1))
done

user_label
print -r -- "$value"
