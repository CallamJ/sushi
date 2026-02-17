#!/usr/bin/env zsh
set -euo pipefail

dotnet --version >/dev/null 2>&1
print -r -- "$?"