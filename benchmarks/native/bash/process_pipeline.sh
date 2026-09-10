#!/usr/bin/env bash
set -euo pipefail

pipeline_output="$(dotnet --list-sdks | sort)"
code=$?
printf '%s\n' "$code"
