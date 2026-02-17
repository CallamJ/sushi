#!/usr/bin/env bash
set -euo pipefail

raw="  Alpha,Beta,Gamma42  "
i=0
output=""
while (( i < 200 )); do
  lowered="${raw,,}"
  lowered="${lowered## }"
  lowered="${lowered%% }"
  output="${lowered/gamma42/delta}"
  ((i++))
done
printf '%s\n' "$output"