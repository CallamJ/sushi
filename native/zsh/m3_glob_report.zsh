#!/usr/bin/env zsh
set -eu
set -o pipefail
setopt typesetsilent

__sushi_j_reset() {
  __sushi_j_src="${1-}"
  __sushi_j_len=${#__sushi_j_src}
  __sushi_j_pos=0
  __sushi_j_err=''
  __sushi_j_last=''
}

__sushi_j_char() {
  if (( __sushi_j_pos >= __sushi_j_len )); then
    printf ''
    return
  fi
  printf '%s' "${__sushi_j_src:$__sushi_j_pos:1}"
}

__sushi_j_skip_ws() {
  while (( __sushi_j_pos < __sushi_j_len )); do
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
    case "$c" in
      ' '|$'\t'|$'\n'|$'\r') __sushi_j_pos=$((__sushi_j_pos + 1)) ;;
      *) break ;;
    esac
  done
}

__sushi_j_fail() {
  __sushi_j_err="${1-parse error}"
  return 1
}

__sushi_j_is_digit() {
  case "${1-}" in
    [0-9]) return 0 ;;
    *) return 1 ;;
  esac
}

__sushi_j_is_integer() {
  local s="${1-}"
  [[ -z "$s" ]] && return 1
  if [[ "${s:0:1}" == "-" ]]; then
    s="${s:1}"
  fi
  [[ -z "$s" ]] && return 1
  case "$s" in
    *[!0-9]*) return 1 ;;
    *) return 0 ;;
  esac
}

__sushi_j_parse_string() {
  local start=$__sushi_j_pos
  [[ "$(__sushi_j_char)" == '"' ]] || __sushi_j_fail 'expected string'
  __sushi_j_pos=$((__sushi_j_pos + 1))

  while (( __sushi_j_pos < __sushi_j_len )); do
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
    if [[ "$c" == '"' ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      __sushi_j_last="${__sushi_j_src:$start:$((__sushi_j_pos - start))}"
      return 0
    fi

    if [[ "$c" == "\\" ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      (( __sushi_j_pos < __sushi_j_len )) || __sushi_j_fail 'unterminated escape'
      local esc="${__sushi_j_src:$__sushi_j_pos:1}"
      case "$esc" in
        '"'|'\\'|'/'|'b'|'f'|'n'|'r'|'t')
          __sushi_j_pos=$((__sushi_j_pos + 1))
          ;;
        'u')
          __sushi_j_pos=$((__sushi_j_pos + 1))
          local i
          for i in 0 1 2 3; do
            (( __sushi_j_pos + i < __sushi_j_len )) || __sushi_j_fail 'bad unicode escape'
            local hex="${__sushi_j_src:$((__sushi_j_pos + i)):1}"
            case "$hex" in
              [0-9a-fA-F]) ;;
              *) __sushi_j_fail 'bad unicode escape' ;;
            esac
          done
          __sushi_j_pos=$((__sushi_j_pos + 4))
          ;;
        *)
          __sushi_j_fail 'bad escape sequence'
          ;;
      esac
    else
      __sushi_j_pos=$((__sushi_j_pos + 1))
    fi
  done

  __sushi_j_fail 'unterminated string'
}

__sushi_j_parse_number() {
  local start=$__sushi_j_pos
  local c="${__sushi_j_src:$__sushi_j_pos:1}"

  if [[ "$c" == "-" ]]; then
    __sushi_j_pos=$((__sushi_j_pos + 1))
  fi

  (( __sushi_j_pos < __sushi_j_len )) || __sushi_j_fail 'bad number'
  c="${__sushi_j_src:$__sushi_j_pos:1}"
  if [[ "$c" == "0" ]]; then
    __sushi_j_pos=$((__sushi_j_pos + 1))
  else
    __sushi_j_is_digit "$c" || __sushi_j_fail 'bad number'
    while (( __sushi_j_pos < __sushi_j_len )); do
      c="${__sushi_j_src:$__sushi_j_pos:1}"
      __sushi_j_is_digit "$c" || break
      __sushi_j_pos=$((__sushi_j_pos + 1))
    done
  fi

  if (( __sushi_j_pos < __sushi_j_len )) && [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "." ]]; then
    __sushi_j_pos=$((__sushi_j_pos + 1))
    (( __sushi_j_pos < __sushi_j_len )) || __sushi_j_fail 'bad number fraction'
    c="${__sushi_j_src:$__sushi_j_pos:1}"
    __sushi_j_is_digit "$c" || __sushi_j_fail 'bad number fraction'
    while (( __sushi_j_pos < __sushi_j_len )); do
      c="${__sushi_j_src:$__sushi_j_pos:1}"
      __sushi_j_is_digit "$c" || break
      __sushi_j_pos=$((__sushi_j_pos + 1))
    done
  fi

  if (( __sushi_j_pos < __sushi_j_len )); then
    c="${__sushi_j_src:$__sushi_j_pos:1}"
    if [[ "$c" == "e" || "$c" == "E" ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      if (( __sushi_j_pos < __sushi_j_len )); then
        c="${__sushi_j_src:$__sushi_j_pos:1}"
        if [[ "$c" == "+" || "$c" == "-" ]]; then
          __sushi_j_pos=$((__sushi_j_pos + 1))
        fi
      fi
      (( __sushi_j_pos < __sushi_j_len )) || __sushi_j_fail 'bad number exponent'
      c="${__sushi_j_src:$__sushi_j_pos:1}"
      __sushi_j_is_digit "$c" || __sushi_j_fail 'bad number exponent'
      while (( __sushi_j_pos < __sushi_j_len )); do
        c="${__sushi_j_src:$__sushi_j_pos:1}"
        __sushi_j_is_digit "$c" || break
        __sushi_j_pos=$((__sushi_j_pos + 1))
      done
    fi
  fi

  __sushi_j_last="${__sushi_j_src:$start:$((__sushi_j_pos - start))}"
  return 0
}

__sushi_j_parse_literal() {
  local expected="${1-}"
  local n=${#expected}
  local got="${__sushi_j_src:$__sushi_j_pos:$n}"
  [[ "$got" == "$expected" ]] || __sushi_j_fail "expected $expected"
  __sushi_j_pos=$((__sushi_j_pos + n))
  __sushi_j_last="$expected"
  return 0
}

__sushi_j_parse_array() {
  [[ "$(__sushi_j_char)" == "[" ]] || __sushi_j_fail 'expected array'
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "$(__sushi_j_char)" == "]" ]]; then
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_j_last='[]'
    return 0
  fi

  local out='['
  local first=true
  while :; do
    __sushi_j_skip_ws
    __sushi_j_parse_value || return 1
    if [[ "$first" == "true" ]]; then
      first=false
    else
      out+=','
    fi
    out+="$__sushi_j_last"

    __sushi_j_skip_ws
    local c="$(__sushi_j_char)"
    if [[ "$c" == "," ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      continue
    fi
    if [[ "$c" == "]" ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      out+=']'
      __sushi_j_last="$out"
      return 0
    fi
    __sushi_j_fail 'expected , or ]'
  done
}

__sushi_j_parse_object() {
  [[ "$(__sushi_j_char)" == "{" ]] || __sushi_j_fail 'expected object'
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "$(__sushi_j_char)" == "}" ]]; then
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_j_last='{}'
    return 0
  fi

  local out='{'
  local first=true
  while :; do
    __sushi_j_skip_ws
    __sushi_j_parse_string || return 1
    local key_json="$__sushi_j_last"

    __sushi_j_skip_ws
    [[ "$(__sushi_j_char)" == ":" ]] || __sushi_j_fail 'expected :'
    __sushi_j_pos=$((__sushi_j_pos + 1))

    __sushi_j_skip_ws
    __sushi_j_parse_value || return 1
    local value_json="$__sushi_j_last"

    if [[ "$first" == "true" ]]; then
      first=false
    else
      out+=','
    fi
    out+="$key_json:$value_json"

    __sushi_j_skip_ws
    local c="$(__sushi_j_char)"
    if [[ "$c" == "," ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      continue
    fi
    if [[ "$c" == "}" ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      out+='}'
      __sushi_j_last="$out"
      return 0
    fi
    __sushi_j_fail 'expected , or }'
  done
}

__sushi_j_parse_value() {
  __sushi_j_skip_ws
  local c="$(__sushi_j_char)"
  case "$c" in
    '"') __sushi_j_parse_string ;;
    '{') __sushi_j_parse_object ;;
    '[') __sushi_j_parse_array ;;
    't') __sushi_j_parse_literal 'true' ;;
    'f') __sushi_j_parse_literal 'false' ;;
    'n') __sushi_j_parse_literal 'null' ;;
    '-'|[0-9]) __sushi_j_parse_number ;;
    *) __sushi_j_fail 'unexpected character' ;;
  esac
}

__sushi_json_quote() {
  local value="${1-}"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  value="${value//$'\f'/\\f}"
  value="${value//$'\b'/\\b}"
  printf '"%s"' "$value"
}

__sushi_json_try_compact() {
  local text="${1-}"
  __sushi_j_reset "$text"
  __sushi_j_skip_ws
  __sushi_j_parse_value || return 1
  local compact="$__sushi_j_last"
  __sushi_j_skip_ws
  (( __sushi_j_pos == __sushi_j_len )) || return 1
  printf '%s' "$compact"
}

__sushi_j_unescape_string() {
  local json="${1-}"
  local len=${#json}
  if (( len < 2 )); then
    printf ''
    return
  fi

  local i=1
  local out=''
  while (( i < len - 1 )); do
    local c="${json:$i:1}"
    if [[ "$c" == "\\" ]]; then
      i=$((i + 1))
      (( i < len - 1 )) || break
      local esc="${json:$i:1}"
      case "$esc" in
        '"') out+='"' ;;
        '\\') out+='\\' ;;
        '/') out+='/' ;;
        'b') out+=$'\b' ;;
        'f') out+=$'\f' ;;
        'n') out+=$'\n' ;;
        'r') out+=$'\r' ;;
        't') out+=$'\t' ;;
        'u')
          if (( i + 4 < len )); then
            local hex="${json:$((i + 1)):4}"
            if [[ "$hex" == [0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F] ]]; then
              local code=$((16#$hex))
              if (( code >= 32 && code <= 126 )); then
                out+=$(printf "\\$(printf '%03o' "$code")")
              else
                out+="\\u$hex"
              fi
              i=$((i + 4))
            else
              out+='u'
            fi
          else
            out+='u'
          fi
          ;;
        *)
          out+="\\$esc"
          ;;
      esac
    else
      out+="$c"
    fi
    i=$((i + 1))
  done

  printf '%s' "$out"
}

__sushi_json_value_to_raw() {
  local value_json="${1-}"
  if [[ -z "$value_json" ]]; then
    printf ''
    return
  fi

  case "$value_json" in
    null) printf '' ;;
    true|false) printf '%s' "$value_json" ;;
    \{*|\[* ) printf '%s' "$value_json" ;;
    \"*) __sushi_j_unescape_string "$value_json" ;;
    *) printf '%s' "$value_json" ;;
  esac
}

__sushi_json_array() {
  local out='['
  local first=true
  local raw compact
  for raw in "$@"; do
    if [[ "$first" == "true" ]]; then
      first=false
    else
      out+=','
    fi

    if compact="$(__sushi_json_try_compact "$raw")"; then
      out+="$compact"
    else
      out+="$(__sushi_json_quote "$raw")"
    fi
  done
  out+=']'
  printf '%s' "$out"
}

__sushi_json_object() {
  local out='{'
  local first=true
  local key raw value_json key_json
  while (( "$#" > 1 )); do
    key="$1"
    raw="$2"
    shift 2

    if [[ "$first" == "true" ]]; then
      first=false
    else
      out+=','
    fi

    key_json="$(__sushi_json_quote "$key")"
    if value_json="$(__sushi_json_try_compact "$raw")"; then
      :
    else
      value_json="$(__sushi_json_quote "$raw")"
    fi
    out+="$key_json:$value_json"
  done
  out+='}'
  printf '%s' "$out"
}

__sushi_json_array_each_json() {
  local json="${1-}"
  __sushi_j_reset "$json"
  __sushi_j_skip_ws
  [[ "$(__sushi_j_char)" == "[" ]] || return 0
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "$(__sushi_j_char)" == "]" ]]; then
    return 0
  fi

  while :; do
    __sushi_j_parse_value || return 0
    printf '%s\n' "$__sushi_j_last"
    __sushi_j_skip_ws
    local c="$(__sushi_j_char)"
    if [[ "$c" == "," ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      __sushi_j_skip_ws
      continue
    fi
    [[ "$c" == "]" ]] && return 0
    return 0
  done
}

__sushi_json_array_each_raw() {
  local json="${1-}"
  local item
  while IFS= read -r item; do
    __sushi_json_value_to_raw "$item"
    printf '\n'
  done < <(__sushi_json_array_each_json "$json")
}

__sushi_json_object_each_kv() {
  local json="${1-}"
  local compact
  compact="$(__sushi_json_try_compact "$json")" || return 0

  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  [[ "$(__sushi_j_char)" == "{" ]] || return 0
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "$(__sushi_j_char)" == "}" ]]; then
    return 0
  fi

  while :; do
    __sushi_j_parse_string || return 0
    local key_json="$__sushi_j_last"
    local key_raw="$(__sushi_j_unescape_string "$key_json")"

    __sushi_j_skip_ws
    [[ "$(__sushi_j_char)" == ":" ]] || return 0
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_j_skip_ws

    __sushi_j_parse_value || return 0
    local value_json="$__sushi_j_last"
    local value_raw="$(__sushi_json_value_to_raw "$value_json")"

    key_raw="${key_raw//$'\t'/ }"
    key_raw="${key_raw//$'\n'/ }"
    value_raw="${value_raw//$'\t'/ }"
    value_raw="${value_raw//$'\n'/ }"
    printf '%s\t%s\n' "$key_raw" "$value_raw"

    __sushi_j_skip_ws
    local c="$(__sushi_j_char)"
    if [[ "$c" == "," ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      __sushi_j_skip_ws
      continue
    fi
    [[ "$c" == "}" ]] && return 0
    return 0
  done
}

__sushi_json_member() {
  local json="${1-}"
  local key="${2-}"
  local target_json="$(__sushi_json_quote "$key")"
  local compact
  compact="$(__sushi_json_try_compact "$json")" || return 0

  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  [[ "$(__sushi_j_char)" == "{" ]] || return 0
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "$(__sushi_j_char)" == "}" ]]; then
    return 0
  fi

  while :; do
    __sushi_j_parse_string || return 0
    local key_json="$__sushi_j_last"
    __sushi_j_skip_ws
    [[ "$(__sushi_j_char)" == ":" ]] || return 0
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_j_skip_ws
    __sushi_j_parse_value || return 0
    local value_json="$__sushi_j_last"

    if [[ "$key_json" == "$target_json" ]]; then
      __sushi_json_value_to_raw "$value_json"
      return 0
    fi

    __sushi_j_skip_ws
    local c="$(__sushi_j_char)"
    if [[ "$c" == "," ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      __sushi_j_skip_ws
      continue
    fi
    [[ "$c" == "}" ]] && return 0
    return 0
  done
}

__sushi_json_index() {
  local json="${1-}"
  local index="${2-}"
  local compact="$json"
  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  local first_char="$(__sushi_j_char)"

  if [[ "$first_char" == "{" ]]; then
    __sushi_json_member "$compact" "$index"
    return 0
  fi

  if [[ "$first_char" != "[" ]]; then
    return 0
  fi

  __sushi_j_is_integer "$index" || return 0
  local idx=$index
  local count=0
  while IFS= read -r _line; do
    count=$((count + 1))
  done < <(__sushi_json_array_each_json "$compact")

  if (( idx < 0 )); then
    idx=$((count + idx))
  fi
  (( idx >= 0 && idx < count )) || return 0

  local i=0
  local item
  while IFS= read -r item; do
    if (( i == idx )); then
      __sushi_json_value_to_raw "$item"
      return 0
    fi
    i=$((i + 1))
  done < <(__sushi_json_array_each_json "$compact")
}

__sushi_j_indent() {
  local count="${1-0}"
  local out=''
  local i=0
  while (( i < count )); do
    out+=' '
    i=$((i + 1))
  done
  printf '%s' "$out"
}

__sushi_j_pretty_json() {
  local compact="${1-}"
  local indent="${2-2}"
  local len=${#compact}
  local i=0
  local out=''
  local in_string=false
  local escaped=false
  local depth=0

  while (( i < len )); do
    local c="${compact:$i:1}"

    if [[ "$in_string" == "true" ]]; then
      out+="$c"
      if [[ "$escaped" == "true" ]]; then
        escaped=false
      elif [[ "$c" == "\\" ]]; then
        escaped=true
      elif [[ "$c" == '"' ]]; then
        in_string=false
      fi
      i=$((i + 1))
      continue
    fi

    case "$c" in
      '"')
        in_string=true
        out+="$c"
        ;;
      '{'|'[')
        out+="$c"
        local next="${compact:$((i + 1)):1}"
        if [[ "$next" != "}" && "$next" != "]" ]]; then
          depth=$((depth + 1))
          out+=$'\n'
          out+="$(__sushi_j_indent $((depth * indent)))"
        fi
        ;;
      '}'|']')
        local prev="${compact:$((i - 1)):1}"
        if [[ "$prev" != "{" && "$prev" != "[" ]]; then
          out+=$'\n'
          out+="$(__sushi_j_indent $(((depth - 1) * indent)))"
        fi
        depth=$((depth - 1))
        (( depth < 0 )) && depth=0
        out+="$c"
        ;;
      ',')
        out+=","
        out+=$'\n'
        out+="$(__sushi_j_indent $((depth * indent)))"
        ;;
      ':')
        out+=": "
        ;;
      *)
        out+="$c"
        ;;
    esac

    i=$((i + 1))
  done

  printf '%s' "$out"
}

__sushi_json_parse() {
  local text="${1-}"
  local compact
  if compact="$(__sushi_json_try_compact "$text")"; then
    printf '%s' "$compact"
  else
    printf '%s' "$text"
  fi
}

__sushi_json_last_compact() {
  local text="${1-}"
  local line compact last=''
  while IFS= read -r line || [[ -n "$line" ]]; do
    if compact="$(__sushi_json_try_compact "$line")"; then
      last="$compact"
    fi
  done <<< "$text"

  if [[ -n "$last" ]]; then
    printf '%s' "$last"
  else
    printf '%s' "$text"
  fi
}

__sushi_json_stringify() {
  local value="${1-}"
  local indent="${2:-0}"
  local compact
  if compact="$(__sushi_json_try_compact "$value")"; then
    if __sushi_j_is_integer "$indent" && (( indent > 0 )); then
      __sushi_j_pretty_json "$compact" "$indent"
    else
      printf '%s' "$compact"
    fi
  else
    __sushi_json_quote "$value"
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

  local -a cmd_argv env_pairs
  cmd_argv=("$command")
  if [[ -n "$args_json" ]] && [[ "$args_json" != "null" ]]; then
    while IFS= read -r arg; do
      cmd_argv+=("$arg")
    done < <(__sushi_json_array_each_raw "$args_json")
  fi

  if [[ -n "$env_json" ]] && [[ "$env_json" != "null" ]]; then
    while IFS=$'\t' read -r key value; do
      [[ -z "$key" ]] && continue
      env_pairs+=("${key}=${value}")
    done < <(__sushi_json_object_each_kv "$env_json")
  fi

  local exit_code timed_out=false
  local timeout_enabled=false
  if __sushi_j_is_integer "$timeout_ms" && (( timeout_ms > 0 )); then
    timeout_enabled=true
  fi

  set +e
  if [[ "$timeout_enabled" == "true" ]]; then
    local timeout_flag timeout_pid command_pid timeout_seconds
    timeout_flag="$(mktemp)"
    rm -f -- "$timeout_flag"
    timeout_seconds="$(awk -v ms="$timeout_ms" 'BEGIN { if (ms <= 0) { print "0" } else { printf "%.3f", ms / 1000 } }')"

    if [[ -n "$cwd" ]] && [[ "$cwd" != "null" ]]; then
      (
        cd -- "$cwd" || exit 1
        if [[ "$stream" == "true" ]]; then
          env "${env_pairs[@]}" "${cmd_argv[@]}" < "$input_file" > >(tee "$stdout_file") 2> >(tee "$stderr_file" >&2)
        else
          env "${env_pairs[@]}" "${cmd_argv[@]}" < "$input_file" > "$stdout_file" 2> "$stderr_file"
        fi
      ) &
    else
      (
        if [[ "$stream" == "true" ]]; then
          env "${env_pairs[@]}" "${cmd_argv[@]}" < "$input_file" > >(tee "$stdout_file") 2> >(tee "$stderr_file" >&2)
        else
          env "${env_pairs[@]}" "${cmd_argv[@]}" < "$input_file" > "$stdout_file" 2> "$stderr_file"
        fi
      ) &
    fi

    command_pid=$!
    (
      sleep "$timeout_seconds"
      if kill -0 "$command_pid" 2>/dev/null; then
        printf '1' > "$timeout_flag"
        kill -TERM "$command_pid" 2>/dev/null || true
        sleep 1
        kill -KILL "$command_pid" 2>/dev/null || true
      fi
    ) &
    timeout_pid=$!

    wait "$command_pid"
    exit_code=$?
    kill "$timeout_pid" 2>/dev/null || true
    wait "$timeout_pid" 2>/dev/null || true

    if [[ -s "$timeout_flag" ]]; then
      timed_out=true
      exit_code=124
    fi

    rm -f -- "$timeout_flag"
  elif [[ -n "$cwd" ]] && [[ "$cwd" != "null" ]]; then
    (
      cd -- "$cwd" || exit 1
      if [[ "$stream" == "true" ]]; then
        env "${env_pairs[@]}" "${cmd_argv[@]}" < "$input_file" > >(tee "$stdout_file") 2> >(tee "$stderr_file" >&2)
      else
        env "${env_pairs[@]}" "${cmd_argv[@]}" < "$input_file" > "$stdout_file" 2> "$stderr_file"
      fi
    )
    exit_code=$?
  else
    if [[ "$stream" == "true" ]]; then
      env "${env_pairs[@]}" "${cmd_argv[@]}" < "$input_file" > >(tee "$stdout_file") 2> >(tee "$stderr_file" >&2)
    else
      env "${env_pairs[@]}" "${cmd_argv[@]}" < "$input_file" > "$stdout_file" 2> "$stderr_file"
    fi
    exit_code=$?
  fi
  set -e

  local stdout_text stderr_text command_text ok_json
  stdout_text="$(cat -- "$stdout_file" 2>/dev/null || true)"
  stderr_text="$(cat -- "$stderr_file" 2>/dev/null || true)"
  command_text="$(printf '%q ' "${cmd_argv[@]}")"
  command_text="${command_text% }"
  ok_json=false
  if [[ "$exit_code" -eq 0 ]]; then
    ok_json=true
  fi

  local stdout_json stderr_json command_json result_json
  stdout_json="$(__sushi_json_quote "$stdout_text")"
  stderr_json="$(__sushi_json_quote "$stderr_text")"
  command_json="$(__sushi_json_quote "$command_text")"
  result_json="$(__sushi_json_object \
    code "$exit_code" \
    stdout "$stdout_json" \
    stderr "$stderr_json" \
    command "$command_json" \
    ok "$ok_json" \
    timedOut "$timed_out")"

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
    stage_command="$(__sushi_json_member "$stage" "command")"
    stage_args="$(__sushi_json_member "$stage" "args")"
    stage_result="$(__sushi_process_run "$stage_command" "$stage_args" "$cwd" "$env_json" "$next_input" "$timeout_ms" "true" "$stream")"
    stage_result="$(__sushi_json_last_compact "$stage_result")"
    stage_code="$(__sushi_json_member "$stage_result" "code")"
    if [[ "$allow_failure" != "true" ]] && [[ "$stage_code" -ne 0 ]]; then
      stage_stderr="$(__sushi_json_member "$stage_result" "stderr")"
      if [[ -n "$stage_stderr" ]]; then
        printf '%s\n' "$stage_stderr" >&2
      fi
      exit "$stage_code"
    fi
    next_input="$(__sushi_json_member "$stage_result" "stdout")"
    last_result="$stage_result"
  done < <(__sushi_json_array_each_json "$stages_json")

  printf '%s' "$last_result"
}

__sushi_process_fail() {
  local result="${1-}"
  local ok
  ok="$(__sushi_json_member "$result" "ok")"
  if [[ "$ok" == "true" ]]; then
    printf 'false'
  else
    printf 'true'
  fi
}

__sushi_process_require_success() {
  local result="${1-}"
  local code stderr_text
  code="$(__sushi_json_member "$result" "code")"
  if [[ "$code" -ne 0 ]]; then
    stderr_text="$(__sushi_json_member "$result" "stderr")"
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
      line="${line#./}"
      [[ -n "$line" ]] && results+=("$line")
    done < <(cd -- "$cwd" 2>/dev/null && find . -path "./$pattern" -print 2>/dev/null || true)
  else
    while IFS= read -r line; do
      line="${line#./}"
      [[ -n "$line" ]] && results+=("$line")
    done < <(find . -path "./$pattern" -print 2>/dev/null || true)
  fi

  __sushi_json_array "${results[@]}"
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
    while IFS=$'\t' read -r key value; do
      [[ -z "$key" ]] && continue
      curl_args+=(-H "$key: $value")
    done < <(__sushi_json_object_each_kv "$headers_json")
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

  local http_status
  http_status="$(awk '/^HTTP\// { code = $2 } END { print code + 0 }' "$header_file")"
  if [[ -z "$http_status" ]]; then
    http_status=0
  fi

  local body_text headers_obj ok_json json_body body_json url_json
  local -a header_pairs
  body_text="$(cat -- "$body_file" 2>/dev/null || true)"
  headers_obj="$(__sushi_json_object)"
  while IFS= read -r line; do
    line="${line%$'\r'}"
    [[ -z "$line" ]] && continue
    if [[ "$line" == HTTP/* ]]; then
      header_pairs=()
      continue
    fi
    if [[ "$line" == *:* ]]; then
      local key value
      key="${line%%:*}"
      value="${line#*:}"
      value="${value#"${value%%[![:space:]]*}"}"
      header_pairs+=("$key" "$value")
    fi
  done < "$header_file"
  if [[ "${#header_pairs[@]}" -gt 0 ]]; then
    headers_obj="$(__sushi_json_object "${header_pairs[@]}")"
  fi

  ok_json=false
  if [[ "$http_status" -ge 200 ]] && [[ "$http_status" -lt 300 ]]; then
    ok_json=true
  fi

  if [[ "$curl_code" -ne 0 ]] && [[ "$http_status" -eq 0 ]]; then
    ok_json=false
  fi

  json_body='null'
  if [[ -n "$body_text" ]]; then
    local compact_body
    if compact_body="$(__sushi_json_try_compact "$body_text")"; then
      json_body="$compact_body"
    fi
  fi

  body_json="$(__sushi_json_quote "$body_text")"
  url_json="$(__sushi_json_quote "$url")"
  __sushi_json_object \
    status "$http_status" \
    ok "$ok_json" \
    headers "$headers_obj" \
    body "$body_json" \
    json "$json_body" \
    url "$url_json"

  rm -f -- "$body_file" "$header_file"
}

__sushi_http_get() {
  local url="${1-}"
  local headers_json="${2-null}"
  local raw
  raw="$(__sushi_http_request "GET" "$url" "" "$headers_json" "application/json")"
  __sushi_json_last_compact "$raw"
}

__sushi_http_post() {
  local url="${1-}"
  local body="${2-}"
  local headers_json="${3-null}"
  local content_type="${4-application/json}"
  local raw
  raw="$(__sushi_http_request "POST" "$url" "$body" "$headers_json" "$content_type")"
  __sushi_json_last_compact "$raw"
}

files="$(__sushi_fs_glob 'src/**/*.cs' '')"
printf '%s\n' 'matched files:'
printf '%s\n' "$(__sushi_json_stringify "${files:-}" 2)"
outPath=$(printf '%s' 'tmp'; printf '/%s' 'glob-report.json')
__sushi_path=$(printf '%s' "${outPath:-}"); __sushi_dir=$(dirname -- "$__sushi_path"); if [[ "$__sushi_dir" != "." && ! -d "$__sushi_dir" ]]; then mkdir -p -- "$__sushi_dir"; fi; if [[ 'false' == 'true' ]]; then printf '%s' "$(__sushi_json_stringify "${files:-}" 2)" >> "$__sushi_path"; else printf '%s' "$(__sushi_json_stringify "${files:-}" 2)" > "$__sushi_path"; fi
printf '%s\n' 'wrote report:'
printf '%s\n' "${outPath:-}"
