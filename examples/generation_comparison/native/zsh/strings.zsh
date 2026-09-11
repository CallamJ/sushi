#!/usr/bin/env zsh
set -euo pipefail

name='  Alice  '
name="${name#"${name%%[![:space:]]*}"}"
name="${name%"${name##*[![:space:]]}"}"
printf '%s\n' "${name:l}"
