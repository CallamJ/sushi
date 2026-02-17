#!/usr/bin/env bash
set -euo pipefail

mapfile -t files < <(find src -type f -name '*.cs' 2>/dev/null || true)
printf '%s\n' "glob_done"