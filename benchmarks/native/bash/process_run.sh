#!/usr/bin/env bash
set -euo pipefail

dotnet --version >/dev/null 2>&1
printf '%s\n' "$?"