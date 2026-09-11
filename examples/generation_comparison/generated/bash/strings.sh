#!/usr/bin/env bash
set -euo pipefail
name='  Alice  '
__sushi_value_1="${name:-}"
__sushi_value_1="${__sushi_value_1#"${__sushi_value_1%%[![:space:]]*}"}"
__sushi_value_1="${__sushi_value_1%"${__sushi_value_1##*[![:space:]]}"}"
__sushi_value_1="${__sushi_value_1,,}"
printf '%s\n' "${__sushi_value_1-}"
