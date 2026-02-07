#!/usr/bin/env bash
set -euo pipefail

__sushi_to_json() {
  local value="${1-}"
  if printf '%s' "$value" | jq -e . >/dev/null 2>&1; then
    printf '%s' "$value"
  else
    jq -cn --arg v "$value" '$v'
  fi
}

__sushi_json_array() {
  jq -nc --args '$ARGS.positional | map(try fromjson catch .)' "$@"
}

__sushi_json_object() {
  jq -nc --args '
    $ARGS.positional
    | reduce range(0; length; 2) as $i
      ({};
       . + {($ARGS.positional[$i]): (try ($ARGS.positional[$i + 1] | fromjson) catch $ARGS.positional[$i + 1])})
  ' "$@"
}

__sushi_json_member() {
  local json="${1-}"
  local key="${2-}"
  printf '%s' "$json" | jq -rc --arg key "$key" '.[$key]'
}

__sushi_json_index() {
  local json="${1-}"
  local index="${2-}"
  printf '%s' "$json" | jq -rc --arg idx "$index" 'if ($idx | test("^-?[0-9]+$")) then .[$idx | tonumber] else .[$idx] end'
}

__sushi_json_parse() {
  local text="${1-}"
  printf '%s' "$text" | jq -c .
}

__sushi_json_stringify() {
  local value="${1-}"
  local indent="${2:-0}"
  if printf '%s' "$value" | jq -e . >/dev/null 2>&1; then
    if [[ "$indent" =~ ^[0-9]+$ ]] && [[ "$indent" -gt 0 ]]; then
      printf '%s' "$value" | jq --indent "$indent" .
    else
      printf '%s' "$value" | jq -c .
    fi
  else
    jq -cn --arg v "$value" '$v'
  fi
}

__sushi_process_run() {
  local command="${1-}"
  local args_json="${2-}"
  local cwd="${3-}"
  local env_json="${4-}"
  local input_text="${5-}"
  local timeout_ms="${6-0}"
  local allow_failure="${7-false}"
  local stream="${8-false}"

  local stdout_file stderr_file input_file
  stdout_file="$(mktemp)"
  stderr_file="$(mktemp)"
  input_file="$(mktemp)"
  printf '%s' "$input_text" > "$input_file"

  local -a argv env_pairs
  argv=("$command")
  if [[ -n "$args_json" ]] && [[ "$args_json" != "null" ]]; then
    while IFS= read -r arg; do
      argv+=("$arg")
    done < <(printf '%s' "$args_json" | jq -r '.[]?')
  fi

  if [[ -n "$env_json" ]] && [[ "$env_json" != "null" ]]; then
    while IFS= read -r pair; do
      env_pairs+=("$pair")
    done < <(printf '%s' "$env_json" | jq -r 'to_entries[]? | "\(.key)=\(.value|tostring)"')
  fi

  local exit_code timed_out=false
  set +e
  if [[ -n "$cwd" ]] && [[ "$cwd" != "null" ]]; then
    (
      cd -- "$cwd" || exit 1
      if [[ "$stream" == "true" ]]; then
        env "${env_pairs[@]}" "${argv[@]}" < "$input_file" > >(tee "$stdout_file") 2> >(tee "$stderr_file" >&2)
      else
        env "${env_pairs[@]}" "${argv[@]}" < "$input_file" > "$stdout_file" 2> "$stderr_file"
      fi
    )
    exit_code=$?
  else
    if [[ "$stream" == "true" ]]; then
      env "${env_pairs[@]}" "${argv[@]}" < "$input_file" > >(tee "$stdout_file") 2> >(tee "$stderr_file" >&2)
    else
      env "${env_pairs[@]}" "${argv[@]}" < "$input_file" > "$stdout_file" 2> "$stderr_file"
    fi
    exit_code=$?
  fi
  set -e

  local stdout_text stderr_text command_text ok_json
  stdout_text="$(cat -- "$stdout_file" 2>/dev/null || true)"
  stderr_text="$(cat -- "$stderr_file" 2>/dev/null || true)"
  command_text="$(printf '%q ' "${argv[@]}")"
  command_text="${command_text% }"
  ok_json=false
  if [[ "$exit_code" -eq 0 ]]; then
    ok_json=true
  fi

  local result_json
  result_json="$(jq -cn \
    --argjson code "$exit_code" \
    --arg stdout "$stdout_text" \
    --arg stderr "$stderr_text" \
    --arg command "$command_text" \
    --argjson ok "$ok_json" \
    --argjson timedOut "$timed_out" \
    '{ code: $code, stdout: $stdout, stderr: $stderr, ok: $ok, command: $command, timedOut: $timedOut }')"

  rm -f -- "$stdout_file" "$stderr_file" "$input_file"

  if [[ "$allow_failure" != "true" ]] && [[ "$exit_code" -ne 0 ]]; then
    if [[ -n "$stderr_text" ]]; then
      printf '%s\n' "$stderr_text" >&2
    fi
    exit "$exit_code"
  fi

  printf '%s' "$result_json"
}

__sushi_process_pipeline() {
  local stages_json="${1-}"
  local cwd="${2-}"
  local env_json="${3-}"
  local input_text="${4-}"
  local timeout_ms="${5-0}"
  local allow_failure="${6-false}"
  local stream="${7-false}"

  local next_input="${input_text-}"
  local last_result
  last_result='{"code":0,"stdout":"","stderr":"","ok":true,"command":"","timedOut":false}'

  while IFS= read -r stage; do
    local stage_command stage_args stage_result stage_code stage_stderr
    stage_command="$(printf '%s' "$stage" | jq -r '.command // empty')"
    stage_args="$(printf '%s' "$stage" | jq -c '.args // []')"
    stage_result="$(__sushi_process_run "$stage_command" "$stage_args" "$cwd" "$env_json" "$next_input" "$timeout_ms" "true" "$stream")"
    stage_code="$(printf '%s' "$stage_result" | jq -r '.code')"
    if [[ "$allow_failure" != "true" ]] && [[ "$stage_code" -ne 0 ]]; then
      stage_stderr="$(printf '%s' "$stage_result" | jq -r '.stderr // ""')"
      if [[ -n "$stage_stderr" ]]; then
        printf '%s\n' "$stage_stderr" >&2
      fi
      exit "$stage_code"
    fi
    next_input="$(printf '%s' "$stage_result" | jq -r '.stdout // ""')"
    last_result="$stage_result"
  done < <(printf '%s' "$stages_json" | jq -c '.[]?')

  printf '%s' "$last_result"
}

__sushi_process_fail() {
  local result="${1-}"
  local ok
  ok="$(printf '%s' "$result" | jq -r '.ok // false')"
  if [[ "$ok" == "true" ]]; then
    printf 'false'
  else
    printf 'true'
  fi
}

__sushi_process_require_success() {
  local result="${1-}"
  local code stderr_text
  code="$(printf '%s' "$result" | jq -r '.code // 0')"
  if [[ "$code" -ne 0 ]]; then
    stderr_text="$(printf '%s' "$result" | jq -r '.stderr // ""')"
    if [[ -n "$stderr_text" ]]; then
      printf '%s\n' "$stderr_text" >&2
    fi
    exit "$code"
  fi
  printf '%s' "$result"
}

__sushi_fs_glob() {
  local pattern="${1-}"
  local cwd="${2-}"
  local -a results

  if [[ -n "$cwd" ]] && [[ "$cwd" != "null" ]]; then
    while IFS= read -r line; do
      [[ -n "$line" ]] && results+=("$line")
    done < <(cd -- "$cwd" 2>/dev/null && shopt -s globstar nullglob && compgen -G "$pattern" || true)
  else
    while IFS= read -r line; do
      [[ -n "$line" ]] && results+=("$line")
    done < <(shopt -s globstar nullglob && compgen -G "$pattern" || true)
  fi

  if [[ "${#results[@]}" -eq 0 ]]; then
    jq -cn '[]'
    return
  fi

  printf '%s\n' "${results[@]}" | jq -R -s -c 'split("\n")[:-1]'
}

__sushi_http_request() {
  local method="${1-GET}"
  local url="${2-}"
  local body="${3-}"
  local headers_json="${4-}"
  local content_type="${5-application/json}"
  local body_file header_file
  body_file="$(mktemp)"
  header_file="$(mktemp)"
  local -a curl_args
  curl_args=(-sS -L -D "$header_file" -o "$body_file" -X "$method")

  if [[ -n "$headers_json" ]] && [[ "$headers_json" != "null" ]]; then
    while IFS= read -r header; do
      curl_args+=(-H "$header")
    done < <(printf '%s' "$headers_json" | jq -r 'to_entries[]? | "\(.key): \(.value|tostring)"')
  fi

  if [[ "$method" != "GET" ]]; then
    if [[ -n "$content_type" ]] && [[ "$content_type" != "null" ]]; then
      curl_args+=(-H "Content-Type: $content_type")
    fi
    curl_args+=(--data-raw "${body-}")
  fi

  set +e
  curl "${curl_args[@]}" "$url"
  local curl_code=$?
  set -e

  local status
  status="$(awk '/^HTTP\// { code = $2 } END { print code + 0 }' "$header_file")"
  if [[ -z "$status" ]]; then
    status=0
  fi

  local body_text headers_obj ok_json json_body
  body_text="$(cat -- "$body_file" 2>/dev/null || true)"
  headers_obj='{}'
  while IFS= read -r line; do
    line="${line%$'\r'}"
    [[ -z "$line" ]] && continue
    if [[ "$line" == HTTP/* ]]; then
      headers_obj='{}'
      continue
    fi
    if [[ "$line" == *:* ]]; then
      local key value
      key="${line%%:*}"
      value="${line#*:}"
      value="${value#"${value%%[![:space:]]*}"}"
      headers_obj="$(printf '%s' "$headers_obj" | jq -c --arg k "$key" --arg v "$value" '. + {($k): $v}')"
    fi
  done < "$header_file"

  ok_json=false
  if [[ "$status" -ge 200 ]] && [[ "$status" -lt 300 ]]; then
    ok_json=true
  fi

  if [[ "$curl_code" -ne 0 ]] && [[ "$status" -eq 0 ]]; then
    ok_json=false
  fi

  json_body='null'
  if [[ -n "$body_text" ]] && printf '%s' "$body_text" | jq -e . >/dev/null 2>&1; then
    json_body="$(printf '%s' "$body_text" | jq -c .)"
  fi

  jq -cn \
    --argjson status "$status" \
    --argjson ok "$ok_json" \
    --argjson headers "$headers_obj" \
    --arg body "$body_text" \
    --argjson json "$json_body" \
    --arg url "$url" \
    '{ status: $status, ok: $ok, headers: $headers, body: $body, json: $json, url: $url }'

  rm -f -- "$body_file" "$header_file"
}

__sushi_http_get() {
  local url="${1-}"
  local headers_json="${2-null}"
  __sushi_http_request "GET" "$url" "" "$headers_json" "application/json"
}

__sushi_http_post() {
  local url="${1-}"
  local body="${2-}"
  local headers_json="${3-null}"
  local content_type="${4-application/json}"
  __sushi_http_request "POST" "$url" "$body" "$headers_json" "$content_type"
}

files="$(__sushi_fs_glob 'src/**/*.cs' '')"
printf '%s\n' 'matched files:'
printf '%s\n' "$(__sushi_json_stringify "${files:-}" 2)"
outPath=$(printf '%s' 'tmp'; printf '/%s' 'glob-report.json')
__sushi_path=$(printf '%s' "${outPath:-}"); __sushi_dir=$(dirname -- "$__sushi_path"); if [[ "$__sushi_dir" != "." && ! -d "$__sushi_dir" ]]; then mkdir -p -- "$__sushi_dir"; fi; if [[ 'false' == 'true' ]]; then printf '%s' "$(__sushi_json_stringify "${files:-}" 2)" >> "$__sushi_path"; else printf '%s' "$(__sushi_json_stringify "${files:-}" 2)" > "$__sushi_path"; fi
printf '%s\n' 'wrote report:'
printf '%s\n' "${outPath:-}"
