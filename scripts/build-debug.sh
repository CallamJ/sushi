#!/usr/bin/env bash
set -euo pipefail

# Build self-contained binaries using the Debug profile. This delegates to the
# regular platform/output naming logic so debug and release artifacts remain
# interchangeable for local testing.
SUSHI_BUILD_CONFIGURATION=Debug bash "$(dirname "${BASH_SOURCE[0]}")/build.sh" "$@"
