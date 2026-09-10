#!/usr/bin/env zsh
set -euo pipefail

raw="  Alpha,Beta,Gamma42  "
i=0
output=""
while (( i < 200 )); do
  lowered="${(L)raw}"
  lowered="${lowered#"${lowered%%[![:space:]]*}"}"
  lowered="${lowered%"${lowered##*[![:space:]]}"}"
  output="${lowered//gamma42/delta}"
  ((i += 1))
done
print -r -- "$output"
