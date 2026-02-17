#!/usr/bin/env bash
set -euo pipefail

dotnet --list-sdks | sort >/dev/null
printf '%s\n' "$?"