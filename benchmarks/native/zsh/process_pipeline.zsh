#!/usr/bin/env zsh
set -euo pipefail

pipeline_output="$(dotnet --list-sdks | sort)"
code=$?
print -r -- "$code"
