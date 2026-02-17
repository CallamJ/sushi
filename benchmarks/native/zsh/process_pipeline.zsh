#!/usr/bin/env zsh
set -euo pipefail

dotnet --list-sdks | sort >/dev/null
print -r -- "$?"