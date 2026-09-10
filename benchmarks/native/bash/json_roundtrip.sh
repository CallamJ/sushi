#!/usr/bin/env bash
set -euo pipefail

source_text='{"name":"sushi","count":42}'
i=0
text=""
while (( i < 200 )); do
  text="$source_text"
  ((i += 1))
done
printf '%s\n' "$text"
