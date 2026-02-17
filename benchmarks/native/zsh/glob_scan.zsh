#!/usr/bin/env zsh
set -euo pipefail

files=($(find src -type f -name '*.cs' 2>/dev/null || true))
print -r -- "glob_done"