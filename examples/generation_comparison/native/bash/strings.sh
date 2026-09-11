#!/usr/bin/env bash
set -euo pipefail

name='  Alice  '
name="${name#"${name%%[![:space:]]*}"}"
name="${name%"${name##*[![:space:]]}"}"
printf '%s\n' "${name,,}"
