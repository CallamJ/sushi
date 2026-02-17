#!/usr/bin/env bash
set -euo pipefail

next_value() {
  local x="$1"
  printf '%s' $((x + 1))
}

user_label() {
  printf 'worker:30'
}

i=0
value=0
while (( i < 300 )); do
  value="$(next_value "$i")"
  ((i++))
done

printf '%s\n' "$(user_label)"
printf '%s\n' "$value"