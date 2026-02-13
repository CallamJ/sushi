#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "$REPO_ROOT"

ITERATIONS=3
INCLUDE_HTTP=false
BUILD_FIRST=true
TARGETS_CSV="bash,zsh,powershell"
CUSTOM_EXAMPLES=false
declare -a EXAMPLES=()
NATIVE_ROOT="native"

RUN_TIMED_STATUS=0
RUN_TIMED_ELAPSED="0.000"
RUN_TIMED_OUT_FILE=""
RUN_TIMED_ERR_FILE=""

usage() {
    cat <<'EOF'
Usage: scripts/benchmark-native-vs-transpiled.sh [options]

Compares native script runtime vs transpiled Sushi runtime for matching examples.
For each target/example:
  1) times transpile once
  2) runs transpiled script N times
  3) runs native script N times

Native script mapping:
  Bash       -> native/bash/<example>.sh
  Zsh        -> native/zsh/<example>.zsh
  Powershell -> native/powershell/<example>.ps1

Options:
  --iterations N            Number of run iterations per mode (default: 3)
  --targets LIST            Comma-separated targets: bash,zsh,powershell (default: all)
  --examples LIST           Comma-separated .sushi paths (default: examples/*.sushi except known invalid static sample)
  --include-http            Do not set SUSHI_SKIP_HTTP=1 when running examples
  --no-build                Skip initial dotnet build
  -h, --help                Show this help

Examples:
  scripts/benchmark-native-vs-transpiled.sh
  scripts/benchmark-native-vs-transpiled.sh --iterations 5 --targets bash,zsh
  scripts/benchmark-native-vs-transpiled.sh --examples examples/m3_verification.sushi
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
        if /usr/bin/time -q -f '%e' -o "$probe" true >/dev/null 2>&1; then
            rm -f "$probe"
            printf '/usr/bin/time'
            return 0
        fi
        rm -f "$probe"
    fi

    if command -v gtime >/dev/null 2>&1; then
        local probe
        probe="$(mktemp)"
        if gtime -q -f '%e' -o "$probe" true >/dev/null 2>&1; then
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
            command -v bash >/dev/null 2>&1 && ACTIVE_TARGETS+=("$target") || echo "Skipping bash target: runtime not found."
            ;;
        zsh)
            command -v zsh >/dev/null 2>&1 && ACTIVE_TARGETS+=("$target") || echo "Skipping zsh target: runtime not found."
            ;;
        powershell)
            command -v pwsh >/dev/null 2>&1 && ACTIVE_TARGETS+=("$target") || echo "Skipping powershell target: pwsh runtime not found."
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
RESULTS_FILE="/tmp/sushi-native-vs-transpiled-$(date +%Y%m%d-%H%M%S)-$$.tsv"
trap 'rm -rf "$WORK_DIR" "$RUN_TIMED_OUT_FILE" "$RUN_TIMED_ERR_FILE"' EXIT

echo
echo "Benchmark configuration:"
echo "  iterations: $ITERATIONS"
echo "  targets:    ${ACTIVE_TARGETS[*]}"
echo "  examples:   ${#EXAMPLES[@]}"
echo "  includeHttp:$INCLUDE_HTTP"
echo "  nativeRoot: $NATIVE_ROOT"
echo

for example_path in "${EXAMPLES[@]}"; do
    example_name="$(basename "$example_path")"
    example_stem="${example_name%.sushi}"
    staged_source="${WORK_DIR}/${example_name}"
    cp "$example_path" "$staged_source"

    for target in "${ACTIVE_TARGETS[@]}"; do
        case "$target" in
            bash)
                cli_target="Bash"
                emitted_ext=".sh"
                native_script="${NATIVE_ROOT}/bash/${example_stem}.sh"
                run_cmd=(bash)
                ;;
            zsh)
                cli_target="Zsh"
                emitted_ext=".zsh"
                native_script="${NATIVE_ROOT}/zsh/${example_stem}.zsh"
                run_cmd=(zsh)
                ;;
            powershell)
                cli_target="Powershell7"
                emitted_ext=".ps1"
                native_script="${NATIVE_ROOT}/powershell/${example_stem}.ps1"
                run_cmd=(pwsh -NoLogo -NoProfile -File)
                ;;
            *)
                continue
                ;;
        esac

        if [[ ! -f "$native_script" ]]; then
            echo "[${target}] ${example_name}: skipped (missing native script: ${native_script})"
            continue
        fi

        run_timed "$TIME_TOOL" dotnet run --no-build -c Release --project src/Sushi -- transpile "$staged_source" -t "$cli_target"
        transpile_status="$RUN_TIMED_STATUS"
        transpile_elapsed="$RUN_TIMED_ELAPSED"
        if [[ "$transpile_status" -ne 0 ]]; then
            printf '%s\t%s\t%s\t%d\t%s\t%s\t%s\n' "$target" "$example_name" "transpile_step" 0 "$transpile_elapsed" "0.000" "transpile_failed" >> "$RESULTS_FILE"
            echo "[${target}] ${example_name}: transpile failed (${transpile_elapsed}s)"
            print_failure_excerpt "transpile stderr" "$RUN_TIMED_ERR_FILE"
            rm -f "$RUN_TIMED_OUT_FILE" "$RUN_TIMED_ERR_FILE"
            continue
        fi
        rm -f "$RUN_TIMED_OUT_FILE" "$RUN_TIMED_ERR_FILE"

        emitted_script="${staged_source%.sushi}${emitted_ext}"
        if [[ ! -f "$emitted_script" ]]; then
            printf '%s\t%s\t%s\t%d\t%s\t%s\t%s\n' "$target" "$example_name" "transpile_step" 0 "$transpile_elapsed" "0.000" "missing_output" >> "$RESULTS_FILE"
            echo "[${target}] ${example_name}: transpile output missing"
            continue
        fi

        printf '%s\t%s\t%s\t%d\t%s\t%s\t%s\n' "$target" "$example_name" "transpile_step" 0 "$transpile_elapsed" "0.000" "ok" >> "$RESULTS_FILE"

        for iteration in $(seq 1 "$ITERATIONS"); do
            if [[ "$INCLUDE_HTTP" == "true" ]]; then
                run_timed "$TIME_TOOL" "${run_cmd[@]}" "$emitted_script"
            else
                run_timed "$TIME_TOOL" env SUSHI_SKIP_HTTP=1 "${run_cmd[@]}" "$emitted_script"
            fi
            transpiled_status="$RUN_TIMED_STATUS"
            transpiled_elapsed="$RUN_TIMED_ELAPSED"
            transpiled_label="ok"
            if [[ "$transpiled_status" -ne 0 ]]; then
                transpiled_label="run_failed"
            fi
            printf '%s\t%s\t%s\t%d\t%s\t%s\t%s\n' "$target" "$example_name" "transpiled_run" "$iteration" "0.000" "$transpiled_elapsed" "$transpiled_label" >> "$RESULTS_FILE"

            if [[ "$INCLUDE_HTTP" == "true" ]]; then
                run_timed "$TIME_TOOL" "${run_cmd[@]}" "$native_script"
            else
                run_timed "$TIME_TOOL" env SUSHI_SKIP_HTTP=1 "${run_cmd[@]}" "$native_script"
            fi
            native_status="$RUN_TIMED_STATUS"
            native_elapsed="$RUN_TIMED_ELAPSED"
            native_label="ok"
            if [[ "$native_status" -ne 0 ]]; then
                native_label="run_failed"
            fi
            printf '%s\t%s\t%s\t%d\t%s\t%s\t%s\n' "$target" "$example_name" "native_run" "$iteration" "0.000" "$native_elapsed" "$native_label" >> "$RESULTS_FILE"

            echo "[${target}] ${example_name} (iter ${iteration}): transpileOnce=${transpile_elapsed}s transpiledRun=${transpiled_elapsed}s nativeRun=${native_elapsed}s"
        done
    done
done

echo
echo "Summary by target:"
awk -F '\t' '
{
  target = $1
  mode = $3
  status = $7

  if (mode == "transpile_step") {
    transpile_total[target]++
    if (status == "ok") {
      transpile_ok[target]++
      transpile_sum[target] += $5
    }
  } else if (mode == "transpiled_run") {
    trans_run_total[target]++
    if (status == "ok") {
      trans_run_ok[target]++
      trans_run_sum[target] += $6
    }
  } else if (mode == "native_run") {
    native_run_total[target]++
    if (status == "ok") {
      native_run_ok[target]++
      native_run_sum[target] += $6
    }
  }
}
END {
  printf "%-12s %-15s %-15s %-15s %-10s\n", "target", "avg_transpile_s", "avg_transpiled_s", "avg_native_s", "ratio_t/n"
  for (target in transpile_total) {
    avg_t = (transpile_ok[target] > 0) ? (transpile_sum[target] / transpile_ok[target]) : 0
    avg_tr = (trans_run_ok[target] > 0) ? (trans_run_sum[target] / trans_run_ok[target]) : 0
    avg_nr = (native_run_ok[target] > 0) ? (native_run_sum[target] / native_run_ok[target]) : 0
    ratio = (avg_nr > 0) ? (avg_tr / avg_nr) : 0
    printf "%-12s %-15.3f %-15.3f %-15.3f %-10.3f\n", target, avg_t, avg_tr, avg_nr, ratio
  }
}
' "$RESULTS_FILE"

echo
echo "Raw results saved to: $RESULTS_FILE"
echo "TSV columns: target,example,mode,iteration,transpile_s,run_s,status"
