#!/usr/bin/env zsh
set -euo pipefail

double() {
  local value="$1"
  printf '%s\n' "$((value * 2))"
}

double 21
