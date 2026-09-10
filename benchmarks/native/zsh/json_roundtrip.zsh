#!/usr/bin/env zsh
set -euo pipefail

source_text='{"name":"sushi","count":42}'
i=0
text=""
while (( i < 20 )); do
  text="$(print -r -- "$source_text" | jq -cS .)"
  ((i += 1))
done
print -r -- "$text"
