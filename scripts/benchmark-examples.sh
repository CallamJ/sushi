#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "$REPO_ROOT"

ITERATIONS=1
INCLUDE_HTTP=false
BUILD_FIRST=true
TARGETS_CSV="bash,zsh,powershell"
CUSTOM_EXAMPLES=false
declare -a EXAMPLES=()

RESULTS_FILE=""
RUN_TIMED_STATUS=0
RUN_TIMED_ELAPSED="0.000"
RUN_TIMED_OUT_FILE=""
RUN_TIMED_ERR_FILE=""

usage() {
    cat <<'EOF'
Usage: scripts/benchmark-examples.sh [options]

Benchmarks transpile+run for Sushi examples across Bash, Zsh, and PowerShell.

Options:
  --iterations N            Number of benchmark iterations per example/target (default: 1)
  --targets LIST            Comma-separated targets: bash,zsh,powershell (default: all)
  --examples LIST           Comma-separated .sushi paths (default: all examples/*.sushi except known invalid static sample)
  --include-http            Do not set SUSHI_SKIP_HTTP=1 when running examples
  --no-build                Skip initial dotnet build
  -h, --help                Show this help

Examples:
  scripts/benchmark-examples.sh
  scripts/benchmark-examples.sh --iterations 3
  scripts/benchmark-examples.sh --targets bash,zsh --examples examples/m3_verification.sushi,examples/m3_process_pipeline.sushi
EOF
}

split_csv() {
    local csv="${1-}"
    local token
    IFS=',' read -r -a _split_parts <<< "$csv"
    for token in "${_split_parts[@]}"; do
        token="${token#"${token%%[![:space:]]*}"}"
        token="${token%"${token##*[![:space:]]}"}"
        [[ -n "$token" ]] && printf '%s\n' "$token"
    done
}

normalize_target() {
    local value="${1-}"
    case "${value,,}" in
        bash) printf 'bash' ;;
        zsh) printf 'zsh' ;;
        powershell|pwsh|powershell7) printf 'powershell' ;;
        *) printf '' ;;
    esac
}

detect_time_tool() {
    if command -v /usr/bin/time >/dev/null 2>&1; then
        local probe
        probe="$(mktemp)"
        if /usr/bin/time -f '%e' -o "$probe" true >/dev/null 2>&1; then
            rm -f "$probe"
            printf '/usr/bin/time'
            return 0
        fi
        rm -f "$probe"
    fi

    if command -v gtime >/dev/null 2>&1; then
        local probe
        probe="$(mktemp)"
        if gtime -f '%e' -o "$probe" true >/dev/null 2>&1; then
            rm -f "$probe"
            printf 'gtime'
            return 0
        fi
        rm -f "$probe"
    fi

    printf ''
}

run_timed() {
    local time_tool="${1-}"
    shift

    RUN_TIMED_OUT_FILE="$(mktemp)"
    RUN_TIMED_ERR_FILE="$(mktemp)"
    RUN_TIMED_STATUS=0
    RUN_TIMED_ELAPSED="0.000"

    local time_file=""
    if [[ -n "$time_tool" ]]; then
        time_file="$(mktemp)"
    fi

    if [[ -n "$time_tool" ]]; then
        set +e
        "$time_tool" -q -f '%e' -o "$time_file" "$@" >"$RUN_TIMED_OUT_FILE" 2>"$RUN_TIMED_ERR_FILE"
        RUN_TIMED_STATUS=$?
        set -e
        RUN_TIMED_ELAPSED="$(cat "$time_file")"
        rm -f "$time_file"
    else
        local start_ts end_ts
        start_ts="$(date +%s)"
        set +e
        "$@" >"$RUN_TIMED_OUT_FILE" 2>"$RUN_TIMED_ERR_FILE"
        RUN_TIMED_STATUS=$?
        set -e
        end_ts="$(date +%s)"
        RUN_TIMED_ELAPSED="$(awk -v s="$start_ts" -v e="$end_ts" 'BEGIN { printf "%.3f", (e - s) }')"
    fi
}

print_failure_excerpt() {
    local label="${1-}"
    local file_path="${2-}"
    if [[ -s "$file_path" ]]; then
        echo "  ${label}:"
        sed -n '1,6p' "$file_path" | sed 's/^/    /'
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --iterations)
            ITERATIONS="${2-}"
            shift 2
            ;;
        --targets)
            TARGETS_CSV="${2-}"
            shift 2
            ;;
        --examples)
            CUSTOM_EXAMPLES=true
            while IFS= read -r item; do
                EXAMPLES+=("$item")
            done < <(split_csv "${2-}")
            shift 2
            ;;
        --include-http)
            INCLUDE_HTTP=true
            shift
            ;;
        --no-build)
            BUILD_FIRST=false
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if ! [[ "$ITERATIONS" =~ ^[1-9][0-9]*$ ]]; then
    echo "Invalid --iterations value: $ITERATIONS" >&2
    exit 2
fi

if [[ "$CUSTOM_EXAMPLES" == "false" ]]; then
    while IFS= read -r example_path; do
        base_name="$(basename "$example_path")"
        if [[ "$base_name" == "m4_structural_invalid_static.sushi" ]]; then
            continue
        fi
        EXAMPLES+=("$example_path")
    done < <(find examples -maxdepth 1 -type f -name '*.sushi' | sort)
fi

if [[ "${#EXAMPLES[@]}" -eq 0 ]]; then
    echo "No examples selected." >&2
    exit 2
fi

declare -a REQUESTED_TARGETS=()
while IFS= read -r raw_target; do
    normalized="$(normalize_target "$raw_target")"
    if [[ -z "$normalized" ]]; then
        echo "Unsupported target in --targets: $raw_target" >&2
        exit 2
    fi
    REQUESTED_TARGETS+=("$normalized")
done < <(split_csv "$TARGETS_CSV")

if [[ "${#REQUESTED_TARGETS[@]}" -eq 0 ]]; then
    echo "No targets selected." >&2
    exit 2
fi

declare -a ACTIVE_TARGETS=()
for target in "${REQUESTED_TARGETS[@]}"; do
    case "$target" in
        bash)
            if command -v bash >/dev/null 2>&1; then
                ACTIVE_TARGETS+=("$target")
            else
                echo "Skipping bash target: runtime not found."
            fi
            ;;
        zsh)
            if command -v zsh >/dev/null 2>&1; then
                ACTIVE_TARGETS+=("$target")
            else
                echo "Skipping zsh target: runtime not found."
            fi
            ;;
        powershell)
            if command -v pwsh >/dev/null 2>&1; then
                ACTIVE_TARGETS+=("$target")
            else
                echo "Skipping powershell target: pwsh runtime not found."
            fi
            ;;
    esac
done

if [[ "${#ACTIVE_TARGETS[@]}" -eq 0 ]]; then
    echo "No requested targets are available in this environment." >&2
    exit 1
fi

for example_path in "${EXAMPLES[@]}"; do
    if [[ ! -f "$example_path" ]]; then
        echo "Example not found: $example_path" >&2
        exit 2
    fi
    if [[ "${example_path##*.}" != "sushi" ]]; then
        echo "Example must be a .sushi file: $example_path" >&2
        exit 2
    fi
done

if [[ "$BUILD_FIRST" == "true" ]]; then
    echo "Building Sushi CLI (Release)..."
    dotnet build src/Sushi/Sushi.csproj -c Release >/dev/null
fi

TIME_TOOL="$(detect_time_tool)"
if [[ -n "$TIME_TOOL" ]]; then
    echo "Using timing tool: $TIME_TOOL"
else
    echo "Using date-based timing fallback (1s precision)."
fi

WORK_DIR="$(mktemp -d)"
RESULTS_FILE="/tmp/sushi-benchmark-$(date +%Y%m%d-%H%M%S)-$$.tsv"
trap 'rm -rf "$WORK_DIR" "$RUN_TIMED_OUT_FILE" "$RUN_TIMED_ERR_FILE"' EXIT

echo
echo "Benchmark configuration:"
echo "  iterations: $ITERATIONS"
echo "  targets:    ${ACTIVE_TARGETS[*]}"
echo "  examples:   ${#EXAMPLES[@]}"
echo "  includeHttp:$INCLUDE_HTTP"
echo

for iteration in $(seq 1 "$ITERATIONS"); do
    for example_path in "${EXAMPLES[@]}"; do
        example_name="$(basename "$example_path")"
        staged_source="${WORK_DIR}/${example_name}"
        cp "$example_path" "$staged_source"

        for target in "${ACTIVE_TARGETS[@]}"; do
            case "$target" in
                bash)
                    cli_target="Bash"
                    emitted_ext=".sh"
                    run_cmd=(bash)
                    ;;
                zsh)
                    cli_target="Zsh"
                    emitted_ext=".zsh"
                    run_cmd=(zsh)
                    ;;
                powershell)
                    cli_target="Powershell7"
                    emitted_ext=".ps1"
                    run_cmd=(pwsh -NoLogo -NoProfile -File)
                    ;;
                *)
                    continue
                    ;;
            esac

            run_timed "$TIME_TOOL" dotnet run --no-build -c Release --project src/Sushi -- transpile "$staged_source" -t "$cli_target"
            transpile_status="$RUN_TIMED_STATUS"
            transpile_elapsed="$RUN_TIMED_ELAPSED"

            if [[ "$transpile_status" -ne 0 ]]; then
                printf '%s\t%s\t%d\t%s\t%s\t%s\n' "$target" "$example_name" "$iteration" "$transpile_elapsed" "0.000" "transpile_failed" >> "$RESULTS_FILE"
                echo "[${target}] ${example_name} (iter ${iteration}): transpile=${transpile_elapsed}s run=0.000s status=transpile_failed"
                print_failure_excerpt "transpile stderr" "$RUN_TIMED_ERR_FILE"
                rm -f "$RUN_TIMED_OUT_FILE" "$RUN_TIMED_ERR_FILE"
                continue
            fi
            rm -f "$RUN_TIMED_OUT_FILE" "$RUN_TIMED_ERR_FILE"

            emitted_script="${staged_source%.sushi}${emitted_ext}"
            if [[ ! -f "$emitted_script" ]]; then
                printf '%s\t%s\t%d\t%s\t%s\t%s\n' "$target" "$example_name" "$iteration" "$transpile_elapsed" "0.000" "missing_output" >> "$RESULTS_FILE"
                echo "[${target}] ${example_name} (iter ${iteration}): transpile=${transpile_elapsed}s run=0.000s status=missing_output"
                continue
            fi

            if [[ "$INCLUDE_HTTP" == "true" ]]; then
                run_timed "$TIME_TOOL" "${run_cmd[@]}" "$emitted_script"
            else
                run_timed "$TIME_TOOL" env SUSHI_SKIP_HTTP=1 "${run_cmd[@]}" "$emitted_script"
            fi

            run_status="$RUN_TIMED_STATUS"
            run_elapsed="$RUN_TIMED_ELAPSED"

            if [[ "$run_status" -eq 0 ]]; then
                status_label="ok"
            else
                status_label="run_failed"
            fi

            printf '%s\t%s\t%d\t%s\t%s\t%s\n' "$target" "$example_name" "$iteration" "$transpile_elapsed" "$run_elapsed" "$status_label" >> "$RESULTS_FILE"
            echo "[${target}] ${example_name} (iter ${iteration}): transpile=${transpile_elapsed}s run=${run_elapsed}s status=${status_label}"
            if [[ "$run_status" -ne 0 ]]; then
                print_failure_excerpt "run stderr" "$RUN_TIMED_ERR_FILE"
            fi
            rm -f "$RUN_TIMED_OUT_FILE" "$RUN_TIMED_ERR_FILE"
        done
    done
done

echo
echo "Summary by target:"
awk -F '\t' '
{
  total[$1]++
  if ($6 == "ok") {
    ok[$1]++
    transpile[$1] += $4
    run[$1] += $5
  } else {
    failed[$1]++
  }
}
END {
  printf "%-12s %-8s %-8s %-16s %-12s\n", "target", "total", "failed", "avg_transpile_s", "avg_run_s"
  for (target in total) {
    avg_t = (ok[target] > 0) ? (transpile[target] / ok[target]) : 0
    avg_r = (ok[target] > 0) ? (run[target] / ok[target]) : 0
    printf "%-12s %-8d %-8d %-16.3f %-12.3f\n", target, total[target], failed[target] + 0, avg_t, avg_r
  }
}
' "$RESULTS_FILE"

echo
echo "Raw results saved to: $RESULTS_FILE"
