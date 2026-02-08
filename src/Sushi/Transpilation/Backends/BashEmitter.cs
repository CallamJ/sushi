namespace Sushi.Transpilation.Backends;

using System.Globalization;
using System.Linq;
using System.Text;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed class BashEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1100";

    private readonly StringBuilder _builder = new();
    private readonly bool _zshMode;
    private EmitContext _context = null!;
    private int _indent;
    private string? _currentFunctionName;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;

    public BashEmitter()
    {
        _zshMode = false;
    }

    public BashEmitter(bool zshMode)
    {
        _zshMode = zshMode;
    }

    public string Emit(IrProgram program, EmitContext context)
    {
        _builder.Clear();
        _context = context;
        _indent = 0;
        _currentFunctionName = null;
        _currentFunctionReturnType = IrTypeRef.Any;

        WriteLine(_zshMode ? "#!/usr/bin/env zsh" : "#!/usr/bin/env bash");
        if (_zshMode)
        {
            WriteLine("set -eu");
            WriteLine("set -o pipefail");
            WriteLine("setopt typesetsilent");
        }
        else
        {
            WriteLine("set -euo pipefail");
        }
        WriteLine("");
        EmitRuntimeHelpers();
        WriteLine("");

        foreach (var statement in program.Statements)
        {
            EmitStatement(statement, inFunction: false);
        }

        return _builder.ToString();
    }

    private void EmitRuntimeHelpers()
    {
        AppendRuntimeBlock(
"""
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
""");

        AppendRuntimeBlock(
"""
__sushi_json_member_json() {
  local json="${1-}"
  local key="${2-}"
  local target_json compact
  target_json="$(__sushi_json_quote "$key")"
  compact="$(__sushi_json_try_compact "$json")" || return 1

  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  [[ "$(__sushi_j_char)" == "{" ]] || return 1
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  [[ "$(__sushi_j_char)" == "}" ]] && return 1

  while :; do
    __sushi_j_parse_string || return 1
    local key_json="$__sushi_j_last"
    __sushi_j_skip_ws
    [[ "$(__sushi_j_char)" == ":" ]] || return 1
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_j_skip_ws
    __sushi_j_parse_value || return 1
    local value_json="$__sushi_j_last"

    if [[ "$key_json" == "$target_json" ]]; then
      printf '%s' "$value_json"
      return 0
    fi

    __sushi_j_skip_ws
    local c="$(__sushi_j_char)"
    if [[ "$c" == "," ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      __sushi_j_skip_ws
      continue
    fi
    [[ "$c" == "}" ]] && return 1
    return 1
  done
}

__sushi_json_has_member() {
  __sushi_json_member_json "${1-}" "${2-}" >/dev/null
}

__sushi_is_float() {
  local s="${1-}"
  [[ "$s" =~ ^-?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?$ ]]
}

__sushi_json_is_array() {
  local compact
  compact="$(__sushi_json_try_compact "${1-}")" || return 1
  [[ "${compact:0:1}" == "[" ]]
}

__sushi_json_is_object() {
  local compact
  compact="$(__sushi_json_try_compact "${1-}")" || return 1
  [[ "${compact:0:1}" == "{" ]]
}

__sushi_detect_type() {
  local value="${1-}"
  if [[ "$value" == "true" || "$value" == "false" ]]; then
    printf 'bool'
    return 0
  fi
  if __sushi_j_is_integer "$value"; then
    printf 'int'
    return 0
  fi
  if __sushi_is_float "$value"; then
    printf 'float'
    return 0
  fi
  if __sushi_json_is_array "$value"; then
    printf 'array'
    return 0
  fi
  if __sushi_json_is_object "$value"; then
    printf 'object'
    return 0
  fi
  printf 'string'
}

__sushi_type_check() {
  local value="${1-}"
  local expected="${2-any}"
  local context="${3-value}"
  local actual

  case "$expected" in
    any|unknown|'') return 0 ;;
  esac

  actual="$(__sushi_detect_type "$value")"

  case "$expected" in
    string)
      [[ "$actual" == "string" ]] && return 0
      ;;
    int)
      [[ "$actual" == "int" ]] && return 0
      ;;
    float)
      [[ "$actual" == "float" || "$actual" == "int" ]] && return 0
      ;;
    bool)
      [[ "$actual" == "bool" ]] && return 0
      ;;
    array)
      [[ "$actual" == "array" ]] && return 0
      ;;
    object)
      [[ "$actual" == "object" ]] && return 0
      ;;
    *)
      return 0
      ;;
  esac

  printf 'Type contract violation: %s expected %s, got %s\n' "$context" "$expected" "$actual" >&2
  return 1
}

__sushi_struct_check() {
  local value="${1-}"
  local spec="${2-}"
  local context="${3-value}"

  if ! __sushi_json_is_object "$value"; then
    printf 'Type contract violation: %s expected object, got %s\n' "$context" "$(__sushi_detect_type "$value")" >&2
    return 1
  fi

  [[ -z "$spec" ]] && return 0

  local remaining="$spec"
  while :; do
    local field
    if [[ "$remaining" == *","* ]]; then
      field="${remaining%%,*}"
      remaining="${remaining#*,}"
    else
      field="$remaining"
      remaining=''
    fi

    if [[ -z "$field" ]]; then
      [[ -z "$remaining" ]] && break
      continue
    fi
    local field_name field_type field_required rest
    field_name="${field%%:*}"
    rest="${field#*:}"
    field_type="${rest%%:*}"
    field_required="${rest#*:}"
    [[ -z "$field_name" ]] && continue

    if ! __sushi_json_has_member "$value" "$field_name"; then
      if [[ "$field_required" == "req" ]]; then
        printf 'Type contract violation: %s missing required field %s\n' "$context" "$field_name" >&2
        return 1
      fi
      continue
    fi

    local field_json field_raw
    field_json="$(__sushi_json_member_json "$value" "$field_name")" || {
      if [[ "$field_required" == "req" ]]; then
        printf 'Type contract violation: %s missing required field %s\n' "$context" "$field_name" >&2
        return 1
      fi
      continue
    }
    field_raw="$(__sushi_json_value_to_raw "$field_json")"

    if ! __sushi_type_check "$field_raw" "$field_type" "$context.$field_name"; then
      return 1
    fi

    [[ -z "$remaining" ]] && break
  done

  return 0
}

__sushi_require_integer() {
  local value="${1-}"
  local context="${2-arithmetic operand}"
  if __sushi_j_is_integer "$value"; then
    printf '%s' "$value"
    return 0
  fi

  printf 'Type contract violation: %s expected int, got %s\n' "$context" "$(__sushi_detect_type "$value")" >&2
  exit 2
}
""");

        AppendRuntimeBlock(
"""
__sushi_is_json_array() {
  local compact
  compact="$(__sushi_json_try_compact "${1-}")" || return 1
  [[ "${compact:0:1}" == "[" ]]
}

__sushi_json_length() {
  local value="${1-}"
  local compact
  compact="$(__sushi_json_try_compact "$value")" || {
    printf '%s' "${#value}"
    return 0
  }

  local first="${compact:0:1}"
  if [[ "$first" == "[" ]]; then
    local count=0
    while IFS= read -r _item; do
      count=$((count + 1))
    done < <(__sushi_json_array_each_json "$compact")
    printf '%s' "$count"
    return 0
  fi

  if [[ "$first" == "{" ]]; then
    local count=0
    while IFS= read -r _item; do
      count=$((count + 1))
    done < <(__sushi_json_object_each_kv "$compact")
    printf '%s' "$count"
    return 0
  fi

  local raw
  raw="$(__sushi_json_value_to_raw "$compact")"
  printf '%s' "${#raw}"
}

__sushi_slice() {
  local array_json="${1-[]}"
  local start_raw="${2-}"
  local end_raw="${3-}"
  local compact
  compact="$(__sushi_json_try_compact "$array_json")" || {
    printf '[]'
    return 0
  }
  [[ "${compact:0:1}" == "[" ]] || {
    printf '[]'
    return 0
  }

  local len
  len="$(__sushi_json_length "$compact")"
  local start=0
  local end="$len"

  if [[ -n "$start_raw" ]]; then
    start="$start_raw"
  fi
  if [[ -n "$end_raw" ]]; then
    end="$end_raw"
  fi

  if (( start < 0 )); then start=$((len + start)); fi
  if (( end < 0 )); then end=$((len + end)); fi
  if (( start < 0 )); then start=0; fi
  if (( end < 0 )); then end=0; fi
  if (( start > len )); then start=$len; fi
  if (( end > len )); then end=$len; fi
  if (( end < start )); then
    printf '[]'
    return 0
  fi

  local idx=0
  local -a out=()
  local item
  while IFS= read -r item; do
    if (( idx >= start && idx < end )); then
      out+=("$item")
    fi
    idx=$((idx + 1))
  done < <(__sushi_json_array_each_raw "$compact")

  __sushi_json_array "${out[@]}"
}

__sushi_array_push() {
  local array_json="${1-[]}"
  shift
  local -a out=()
  local item
  while IFS= read -r item; do
    out+=("$item")
  done < <(__sushi_json_array_each_raw "$array_json")

  local arg
  for arg in "$@"; do
    out+=("$arg")
  done

  __sushi_json_array "${out[@]}"
}

__sushi_call_callable() {
  local fn="${1-}"
  shift
  if [[ -z "$fn" ]]; then
    printf ''
    return 0
  fi

  "$fn" "$@"
}

__sushi_method_map() {
  local target="${1-[]}"
  local fn="${2-}"
  local -a out=()
  local item mapped
  while IFS= read -r item; do
    mapped="$(__sushi_call_callable "$fn" "$item")"
    out+=("$mapped")
  done < <(__sushi_json_array_each_raw "$target")
  __sushi_json_array "${out[@]}"
}

__sushi_method_filter() {
  local target="${1-[]}"
  local fn="${2-}"
  local -a out=()
  local item keep
  while IFS= read -r item; do
    keep="$(__sushi_call_callable "$fn" "$item")"
    case "$keep" in
      true|TRUE|True|1) out+=("$item") ;;
    esac
  done < <(__sushi_json_array_each_raw "$target")
  __sushi_json_array "${out[@]}"
}

__sushi_method_reduce() {
  local target="${1-[]}"
  local fn="${2-}"
  local has_init="${3-0}"
  local acc="${4-}"
  local started=0
  local item
  while IFS= read -r item; do
    if (( started == 0 )) && (( has_init == 0 )); then
      acc="$item"
      started=1
      continue
    fi
    if (( started == 0 )); then
      started=1
    fi
    acc="$(__sushi_call_callable "$fn" "$acc" "$item")"
  done < <(__sushi_json_array_each_raw "$target")
  printf '%s' "$acc"
}

__sushi_call_method() {
  local target="${1-}"
  local method="${2-}"
  shift 2

  case "$method" in
    name) __sushi_json_member "$target" "__sushi_enum_name"; return 0 ;;
    ordinal) __sushi_json_member "$target" "__sushi_enum_ordinal"; return 0 ;;
    value) __sushi_json_member "$target" "__sushi_enum_value"; return 0 ;;
    length) __sushi_json_length "$target"; return 0 ;;
    push) __sushi_array_push "$target" "$@"; return 0 ;;
    map) __sushi_method_map "$target" "${1-}"; return 0 ;;
    filter) __sushi_method_filter "$target" "${1-}"; return 0 ;;
    reduce)
      local fn="${1-}"
      if (( $# >= 2 )); then
        __sushi_method_reduce "$target" "$fn" 1 "${2-}"
      else
        __sushi_method_reduce "$target" "$fn" 0 ""
      fi
      return 0
      ;;
  esac

  local fn
  fn="$(__sushi_json_member "$target" "__sushi_method_${method}")"
  if [[ -z "$fn" ]]; then
    printf ''
    return 0
  fi

  __sushi_call_callable "$fn" "$target" "$@"
}

__sushi_add() {
  local left="${1-}"
  local right="${2-}"
  if __sushi_j_is_integer "$left" && __sushi_j_is_integer "$right"; then
    printf '%s' $((left + right))
    return 0
  fi

  printf '%s' "${left}${right}"
}

__sushi_require_string_receiver() {
  local value="${1-}"
  local method="${2-string method}"
  if [[ "$value" == "__sushi_null__" ]]; then
    printf 'Type contract violation: string receiver for %s expected non-null value\n' "$method" >&2
    exit 2
  fi
  printf '%s' "$value"
}

__sushi_string_trim() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "trim")"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

__sushi_string_lower() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "lower")"
  printf '%s' "$value" | tr '[:upper:]' '[:lower:]'
}

__sushi_string_upper() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "upper")"
  printf '%s' "$value" | tr '[:lower:]' '[:upper:]'
}

__sushi_string_split() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "split")"
  local sep="${2-}"
  local limit="${3-0}"
  if ! __sushi_j_is_integer "$limit"; then
    limit=0
  fi

  local -a out=()
  if [[ -z "$sep" ]]; then
    local len=${#value}
    if (( limit > 0 )); then
      if (( limit == 1 )); then
        __sushi_json_array "$value"
        return 0
      fi

      local i=0
      while (( i < len )) && (( ${#out[@]} < limit - 1 )); do
        out+=("${value:$i:1}")
        i=$((i + 1))
      done
      out+=("${value:$i}")
      __sushi_json_array "${out[@]}"
      return 0
    fi

    local i=0
    while (( i < len )); do
      out+=("${value:$i:1}")
      i=$((i + 1))
    done
    __sushi_json_array "${out[@]}"
    return 0
  fi

  local remaining="$value"
  local part
  if (( limit > 0 )); then
    if (( limit == 1 )); then
      __sushi_json_array "$remaining"
      return 0
    fi

    while [[ "$remaining" == *"$sep"* ]] && (( ${#out[@]} < limit - 1 )); do
      part="${remaining%%"$sep"*}"
      out+=("$part")
      remaining="${remaining#*"$sep"}"
    done
    out+=("$remaining")
    __sushi_json_array "${out[@]}"
    return 0
  fi

  while [[ "$remaining" == *"$sep"* ]]; do
    part="${remaining%%"$sep"*}"
    out+=("$part")
    remaining="${remaining#*"$sep"}"
  done
  out+=("$remaining")
  __sushi_json_array "${out[@]}"
}

__sushi_string_contains() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "contains")"
  local needle="${2-}"
  if [[ "$value" == *"$needle"* ]]; then
    printf 'true'
  else
    printf 'false'
  fi
}

__sushi_string_starts_with() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "startsWith")"
  local prefix="${2-}"
  if [[ "$value" == "$prefix"* ]]; then
    printf 'true'
  else
    printf 'false'
  fi
}

__sushi_string_ends_with() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "endsWith")"
  local suffix="${2-}"
  if [[ "$value" == *"$suffix" ]]; then
    printf 'true'
  else
    printf 'false'
  fi
}

__sushi_string_replace() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "replace")"
  local old="${2-}"
  local new="${3-}"
  if [[ -z "$old" ]]; then
    printf '%s' "$value"
    return 0
  fi

  local replaced="${value//"$old"/"$new"}"
  printf '%s' "$replaced"
}

__sushi_string_is_match() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "isMatch")"
  local pattern="${2-}"
  if printf '%s\n' "$value" | grep -E -q -- "$pattern"; then
    printf 'true'
  else
    printf 'false'
  fi
}

__sushi_string_match() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "match")"
  local pattern="${2-}"
  local matched
  matched="$(printf '%s\n' "$value" | grep -E -o -- "$pattern" | head -n 1 || true)"
  if [[ -z "$matched" ]]; then
    __sushi_json_object "ok" "false" "value" "" "index" "-1" "groups" "[]"
    return 0
  fi

  local prefix="${value%%"$matched"*}"
  local index=${#prefix}
  local groups_json
  groups_json="$(__sushi_json_array "$matched")"
  __sushi_json_object "ok" "true" "value" "$matched" "index" "$index" "groups" "$groups_json"
}

__sushi_truthy() {
  local value="${1-}"
  case "$value" in
    ''|false|FALSE|False|null|NULL|Null)
      return 1
      ;;
  esac

  if __sushi_j_is_integer "$value"; then
    (( value != 0 ))
    return $?
  fi

  return 0
}
""");
    }

    private void AppendRuntimeBlock(string text)
    {
        _builder.Append(text.Replace("\r\n", "\n"));
        if (!text.EndsWith("\n", StringComparison.Ordinal))
        {
            _builder.Append('\n');
        }
    }

    private void EmitStatement(IrStatement statement, bool inFunction)
    {
        switch (statement)
        {
            case IrBlockStatement block:
                foreach (var child in block.Statements)
                {
                    EmitStatement(child, inFunction);
                }
                break;

            case IrVariableDeclarationStatement variable:
                WriteLine($"{SanitizeVariableName(variable.Name)}={EmitValueExpression(variable.Initializer ?? new IrLiteralExpression(null))}");
                break;

            case IrExpressionStatement expressionStatement:
                EmitExpressionStatement(expressionStatement.Expression);
                break;

            case IrIfStatement ifStatement:
                EmitIfStatement(ifStatement, inFunction);
                break;

            case IrWhileStatement whileStatement:
                EmitWhileStatement(whileStatement, inFunction);
                break;

            case IrForStatement forStatement:
                EmitForStatement(forStatement, inFunction);
                break;

            case IrDoWhileStatement doWhileStatement:
                EmitDoWhileStatement(doWhileStatement, inFunction);
                break;

            case IrFunctionDeclarationStatement function:
                EmitFunctionDeclaration(function);
                break;

            case IrReturnStatement returnStatement:
                EmitReturn(returnStatement, inFunction);
                break;

            case IrBreakStatement:
                WriteLine("break");
                break;

            case IrContinueStatement:
                WriteLine("continue");
                break;

            default:
                _context.Error(UnsupportedEmitCode, $"Unsupported IR statement for Bash emitter: {statement.GetType().Name}");
                break;
        }
    }

    private void EmitIfStatement(IrIfStatement statement, bool inFunction)
    {
        WriteLine($"if {EmitConditionCommand(statement.Condition)}; then");
        _indent++;
        EmitStatement(statement.ThenBlock, inFunction);
        _indent--;

        if (statement.ElseBlock != null)
        {
            WriteLine("else");
            _indent++;
            EmitStatement(statement.ElseBlock, inFunction);
            _indent--;
        }

        WriteLine("fi");
    }

    private void EmitWhileStatement(IrWhileStatement statement, bool inFunction)
    {
        WriteLine($"while {EmitConditionCommand(statement.Condition)}; do");
        _indent++;
        EmitStatement(statement.Body, inFunction);
        _indent--;
        WriteLine("done");
    }

    private void EmitForStatement(IrForStatement statement, bool inFunction)
    {
        if (statement.Initializer != null)
        {
            EmitStatement(statement.Initializer, inFunction);
        }

        var condition = statement.Condition != null ? EmitConditionCommand(statement.Condition) : "true";

        WriteLine($"while {condition}; do");
        _indent++;
        EmitStatement(statement.Body, inFunction);

        if (statement.Increment != null)
        {
            EmitExpressionStatement(statement.Increment);
        }

        _indent--;
        WriteLine("done");
    }

    private void EmitDoWhileStatement(IrDoWhileStatement statement, bool inFunction)
    {
        WriteLine("while true; do");
        _indent++;
        EmitStatement(statement.Body, inFunction);
        WriteLine($"if ! {EmitConditionCommand(statement.Condition)}; then");
        _indent++;
        WriteLine("break");
        _indent--;
        WriteLine("fi");
        _indent--;
        WriteLine("done");
    }

    private void EmitFunctionDeclaration(IrFunctionDeclarationStatement statement)
    {
        WriteLine($"{SanitizeFunctionName(statement.Name)}() {{");
        _indent++;

        var previousFunctionName = _currentFunctionName;
        var previousReturnType = _currentFunctionReturnType;
        _currentFunctionName = statement.Name;
        _currentFunctionReturnType = statement.ReturnType;

        var argIndex = 1;
        foreach (var parameter in statement.Parameters)
        {
            var param = SanitizeVariableName(parameter.Name);
            if (parameter.IsVarargs)
            {
                var varargsArray = $"__sushi_varargs_{param}";
                var flatVarargsArray = $"__sushi_flat_varargs_{param}";
                WriteLine($"local -a {varargsArray}=(\"${{@:{argIndex}}}\")");
                WriteLine($"local -a {flatVarargsArray}=()");
                WriteLine("local __sushi_vararg_candidate");
                WriteLine($"for __sushi_vararg_candidate in \"${{{varargsArray}[@]}}\"; do");
                _indent++;
                WriteLine("if __sushi_is_json_array \"$__sushi_vararg_candidate\"; then");
                _indent++;
                WriteLine("local __sushi_vararg_expanded");
                WriteLine("while IFS= read -r __sushi_vararg_expanded; do");
                _indent++;
                WriteLine($"{flatVarargsArray}+=(\"$__sushi_vararg_expanded\")");
                _indent--;
                WriteLine("done < <(__sushi_json_array_each_raw \"$__sushi_vararg_candidate\")");
                _indent--;
                WriteLine("else");
                _indent++;
                WriteLine($"{flatVarargsArray}+=(\"$__sushi_vararg_candidate\")");
                _indent--;
                WriteLine("fi");
                _indent--;
                WriteLine("done");
                WriteLine($"local {param}=\"$(__sushi_json_array \"${{{flatVarargsArray}[@]}}\")\"");
                EmitVarargsContractCheck(parameter, param, statement.Name);
            }
            else
            {
                WriteLine($"local {param}=\"${argIndex}\"");
                argIndex++;
                EmitContractCheckForValue(
                    parameter.DeclaredType,
                    $"\"${{{param}:-}}\"",
                    $"parameter '{parameter.Name}' of function '{statement.Name}'");
            }
        }

        EmitStatement(statement.Body, inFunction: true);
        _currentFunctionName = previousFunctionName;
        _currentFunctionReturnType = previousReturnType;
        _indent--;
        WriteLine("}");
    }

    private void EmitReturn(IrReturnStatement statement, bool inFunction)
    {
        if (statement.Expression != null)
        {
            if (inFunction && _currentFunctionName != null && !_currentFunctionReturnType.IsAnyOrUnknown)
            {
                WriteLine($"local __sushi_return_value={EmitValueExpression(statement.Expression)}");
                EmitContractCheckForValue(
                    _currentFunctionReturnType,
                    "\"${__sushi_return_value:-}\"",
                    $"return value of function '{_currentFunctionName}'");
                WriteLine("printf '%s\\n' \"${__sushi_return_value:-}\"");
            }
            else
            {
                WriteLine($"printf '%s\\n' {EmitValueExpression(statement.Expression)}");
            }
        }

        WriteLine(inFunction ? "return 0" : "exit 0");
    }

    private void EmitExpressionStatement(IrExpression expression)
    {
        switch (expression)
        {
            case IrIntrinsicCallExpression intrinsicCall:
                WriteLine(EmitIntrinsicCommand(intrinsicCall));
                return;

            case IrCallExpression call:
                WriteLine(EmitCallCommand(call));
                return;

            case IrAssignmentExpression assignment:
                WriteLine(EmitAssignmentExpression(assignment));
                return;

            case IrUnaryExpression unary when unary.Operator is "++" or "--":
                if (unary.Operand is IrIdentifierExpression identifier)
                {
                    var op = unary.Operator == "++" ? "+" : "-";
                    var name = SanitizeVariableName(identifier.Name);
                    var checkedCurrent = EmitCheckedInteger($"\"${{{name}:-}}\"", $"variable '{identifier.Name}'");
                    WriteLine($"{name}=$(( {checkedCurrent} {op} 1 ))");
                    return;
                }
                break;

            case IrMethodCallExpression methodCall:
                if (methodCall.MethodName == "push" && methodCall.Target is IrIdentifierExpression targetIdentifier)
                {
                    var targetName = SanitizeVariableName(targetIdentifier.Name);
                    WriteLine($"{targetName}=\"$({EmitMethodCallCommand(methodCall)})\"");
                    return;
                }

                WriteLine($"{EmitMethodCallCommand(methodCall)} >/dev/null");
                return;
        }

        _context.Error(UnsupportedEmitCode, $"Unsupported expression statement in Bash emitter: {expression.GetType().Name}");
    }

    private string EmitAssignmentExpression(IrAssignmentExpression assignment)
    {
        var name = SanitizeVariableName(assignment.Target.Name);
        if (assignment.Operator == "=")
        {
            return $"{name}={EmitValueExpression(assignment.Value)}";
        }

        var mathOp = assignment.Operator[0];
        var checkedCurrent = EmitCheckedInteger($"\"${{{name}:-}}\"", $"variable '{assignment.Target.Name}'");
        return $"{name}=$(( {checkedCurrent} {mathOp} {EmitArithmeticExpression(assignment.Value)} ))";
    }

    private string EmitCallCommand(IrCallExpression call)
    {
        var callee = SanitizeFunctionName(call.Callee);
        var arguments = call.Arguments.Select(argument => EmitValueExpression(argument.Value)).ToList();
        return arguments.Count > 0
            ? $"{callee} {string.Join(" ", arguments)}"
            : callee;
    }

    private string EmitMethodCallCommand(IrMethodCallExpression call)
    {
        var arguments = call.Arguments.Select(argument => EmitValueExpression(argument.Value)).ToList();
        var allArguments = new List<string>
        {
            EmitValueExpression(call.Target),
            Escape.BashSingleQuoted(call.MethodName)
        };
        allArguments.AddRange(arguments);
        return $"__sushi_call_method {string.Join(" ", allArguments)}";
    }

    private string EmitConditionCommand(IrExpression expression)
    {
        if (expression is IrLiteralExpression literal && literal.Value is bool booleanValue)
        {
            return booleanValue ? "true" : "false";
        }

        if (expression is IrBinaryExpression binary && binary.Operator is "&&" or "||")
        {
            return $"{EmitConditionCommand(binary.Left)} {binary.Operator} {EmitConditionCommand(binary.Right)}";
        }

        if (expression is IrBinaryExpression comparison && comparison.Operator is "==" or "!=")
        {
            var op = comparison.Operator == "==" ? "==" : "!=";
            return $"[[ {EmitComparableValue(comparison.Left)} {op} {EmitComparableValue(comparison.Right)} ]]";
        }

        if (expression is IrBinaryExpression relational && relational.Operator is "<" or ">" or "<=" or ">=")
        {
            return $"(( {EmitArithmeticExpression(relational.Left)} {relational.Operator} {EmitArithmeticExpression(relational.Right)} ))";
        }

        if (expression is IrIdentifierExpression identifier)
        {
            return $"__sushi_truthy \"${{{SanitizeVariableName(identifier.Name)}:-}}\"";
        }

        return $"__sushi_truthy {EmitValueExpression(expression)}";
    }

    private string EmitComparableValue(IrExpression expression)
    {
        return expression switch
        {
            IrIdentifierExpression identifier => $"\"${{{SanitizeVariableName(identifier.Name)}:-}}\"",
            IrLiteralExpression literal when literal.Value is string str => Escape.BashSingleQuoted(str),
            IrLiteralExpression literal when literal.Value is char ch => Escape.BashSingleQuoted(ch.ToString()),
            IrLiteralExpression literal when literal.Value is bool boolean => Escape.BashSingleQuoted(boolean ? "true" : "false"),
            IrLiteralExpression literal when literal.Value == null => "''",
            IrLiteralExpression literal => literal.Value?.ToString() ?? "''",
            _ => EmitValueExpression(expression)
        };
    }

    private string EmitValueExpression(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => EmitLiteral(literal.Value),
            IrIdentifierExpression identifier => $"\"${{{SanitizeVariableName(identifier.Name)}:-}}\"",
            IrArrayLiteralExpression array => EmitArrayLiteral(array),
            IrObjectLiteralExpression obj => EmitObjectLiteral(obj),
            IrMemberAccessExpression member => $"\"$(__sushi_json_member {EmitValueExpression(member.Target)} {Escape.BashSingleQuoted(member.MemberName)})\"",
            IrIndexExpression index => $"\"$(__sushi_json_index {EmitValueExpression(index.Target)} {EmitValueExpression(index.Index)})\"",
            IrUnaryExpression unary when unary.Operator == "!" =>
                $"\"$(if {EmitConditionCommand(unary.Operand)}; then printf '%s' 'false'; else printf '%s' 'true'; fi)\"",
            IrUnaryExpression unary when unary.Operator is "-" or "+" =>
                $"$(( {unary.Operator}{EmitArithmeticExpression(unary.Operand)} ))",
            IrBinaryExpression binary when binary.Operator is "+" =>
                $"\"$(__sushi_add {EmitValueExpression(binary.Left)} {EmitValueExpression(binary.Right)})\"",
            IrBinaryExpression binary when binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" =>
                $"\"$(if {EmitConditionCommand(binary)}; then printf '%s' 'true'; else printf '%s' 'false'; fi)\"",
            IrBinaryExpression binary when binary.Operator is "-" or "*" or "/" or "%" =>
                $"$(( {EmitArithmeticExpression(binary)} ))",
            IrConditionalExpression conditional =>
                $"\"$(if {EmitConditionCommand(conditional.Condition)}; then printf '%s' {EmitValueExpression(conditional.TrueExpression)}; else printf '%s' {EmitValueExpression(conditional.FalseExpression)}; fi)\"",
            IrIntrinsicCallExpression intrinsicCall => EmitIntrinsicValue(intrinsicCall),
            IrCallExpression call => $"$({EmitCallCommand(call)})",
            IrMethodCallExpression methodCall => $"\"$({EmitMethodCallCommand(methodCall)})\"",
            IrAssignmentExpression assignment => $"$({EmitAssignmentExpression(assignment)}; printf '%s' \"${{{SanitizeVariableName(assignment.Target.Name)}:-}}\")",
            _ => "''"
        };
    }

    private string EmitArrayLiteral(IrArrayLiteralExpression expression)
    {
        if (expression.Elements.Count == 0)
        {
            return "\"$(__sushi_json_array)\"";
        }

        var elements = string.Join(" ", expression.Elements.Select(EmitValueExpression));
        return $"\"$(__sushi_json_array {elements})\"";
    }

    private string EmitObjectLiteral(IrObjectLiteralExpression expression)
    {
        if (expression.Properties.Count == 0)
        {
            return "\"$(__sushi_json_object)\"";
        }

        var args = string.Join(" ", expression.Properties.Select(property =>
            $"{Escape.BashSingleQuoted(property.Name)} {EmitValueExpression(property.Value)}"));
        return $"\"$(__sushi_json_object {args})\"";
    }

    private void EmitVarargsContractCheck(IrFunctionParameter parameter, string parameterName, string functionName)
    {
        if (parameter.DeclaredType.IsAnyOrUnknown)
        {
            return;
        }

        WriteLine("local __sushi_vararg_item");
        WriteLine($"while IFS= read -r __sushi_vararg_item; do");
        _indent++;
        EmitContractCheckForValue(
            parameter.DeclaredType,
            "\"${__sushi_vararg_item:-}\"",
            $"varargs parameter '{parameter.Name}' of function '{functionName}'");
        _indent--;
        WriteLine($"done < <(__sushi_json_array_each_raw \"${{{parameterName}:-[]}}\")");
    }

    private void EmitContractCheckForValue(IrTypeRef type, string valueExpression, string context)
    {
        if (type.IsAnyOrUnknown)
        {
            return;
        }

        var contextLiteral = Escape.BashSingleQuoted(context);
        if (type.Kind == IrTypeKind.Structural)
        {
            var structuralSpec = Escape.BashSingleQuoted(EncodeStructuralSpec(type));
            WriteLine($"__sushi_struct_check {valueExpression} {structuralSpec} {contextLiteral} || exit 2");
            return;
        }

        var typeLiteral = Escape.BashSingleQuoted(EncodeRuntimeType(type));
        WriteLine($"__sushi_type_check {valueExpression} {typeLiteral} {contextLiteral} || exit 2");
    }

    private static string EncodeRuntimeType(IrTypeRef type)
    {
        if (type.Kind == IrTypeKind.Structural)
        {
            return "object";
        }

        return type.Name ?? "any";
    }

    private static string EncodeStructuralSpec(IrTypeRef type)
    {
        if (type.Kind != IrTypeKind.Structural || type.StructuralFields.Count == 0)
        {
            return "";
        }

        return string.Join(
            ",",
            type.StructuralFields.Select(field =>
                $"{field.Name}:{EncodeRuntimeType(field.Type)}:{(field.Optional ? "opt" : "req")}"));
    }

    private string EmitArithmeticExpression(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal when literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong
                => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
            IrLiteralExpression literal => EmitCheckedInteger(EmitLiteral(literal.Value), "arithmetic literal"),
            IrIdentifierExpression identifier => EmitCheckedInteger(
                $"\"${{{SanitizeVariableName(identifier.Name)}:-}}\"",
                $"variable '{identifier.Name}'"),
            IrUnaryExpression unary when unary.Operator is "+" or "-" =>
                $"{unary.Operator}{EmitArithmeticExpression(unary.Operand)}",
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                $"({EmitArithmeticExpression(binary.Left)} {binary.Operator} {EmitArithmeticExpression(binary.Right)})",
            _ => EmitCheckedInteger(EmitValueExpression(expression), "arithmetic operand")
        };
    }

    private string EmitCheckedInteger(string valueExpression, string context)
    {
        return $"$(__sushi_require_integer {valueExpression} {Escape.BashSingleQuoted(context)})";
    }

    private string EmitLiteral(object? value)
    {
        return value switch
        {
            null => "''",
            string str => Escape.BashSingleQuoted(str),
            char ch => Escape.BashSingleQuoted(ch.ToString()),
            bool boolean => Escape.BashSingleQuoted(boolean ? "true" : "false"),
            int or long or double or float or decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            _ => Escape.BashSingleQuoted(value.ToString() ?? "")
        };
    }

    private void WriteLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _builder.Append('\n');
            return;
        }

        _builder.Append(' ', _indent * 4);
        _builder.Append(text);
        _builder.Append('\n');
    }

    private static string SanitizeFunctionName(string name)
    {
        return name.Replace(".", "_").Replace("-", "_");
    }

    private string SanitizeVariableName(string name)
    {
        var sanitized = SanitizeFunctionName(name);
        if (_zshMode && IsZshReservedVariableName(sanitized))
        {
            return $"__sushi_var_{sanitized}";
        }

        return sanitized;
    }

    private static bool IsZshReservedVariableName(string name)
    {
        return string.Equals(name, "status", StringComparison.Ordinal) ||
               string.Equals(name, "pipestatus", StringComparison.Ordinal) ||
               string.Equals(name, "_", StringComparison.Ordinal);
    }

    private string EmitIntrinsicCommand(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => EmitPrint(call.Arguments, newline: false),
            IntrinsicId.Println => EmitPrint(call.Arguments, newline: true),
            IntrinsicId.StringTrim => $"{EmitStringTrimInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringLower => $"{EmitStringLowerInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringUpper => $"{EmitStringUpperInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringSplit => $"{EmitStringSplitInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringContains => $"{EmitStringContainsInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringStartsWith => $"{EmitStringStartsWithInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringEndsWith => $"{EmitStringEndsWithInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringReplace => $"{EmitStringReplaceInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringIsMatch => $"{EmitStringIsMatchInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.StringMatch => $"{EmitStringMatchInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.IoWriteText => EmitIoWriteText(call.Arguments),
            IntrinsicId.EnvSet => EmitEnvSet(call.Arguments),
            IntrinsicId.ProcessExit => EmitProcessExit(call.Arguments),
            IntrinsicId.OsChdir => $"cd -- {Arg(call.Arguments, 0)}",
            IntrinsicId.ProcessRun => $"{EmitProcessRunInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessPipeline => $"{EmitProcessPipelineInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessFail => $"{EmitProcessFailInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessRequireSuccess => $"{EmitProcessRequireSuccessInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.JsonParse => $"{EmitJsonParseInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.JsonStringify => $"{EmitJsonStringifyInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.FsGlob => $"{EmitFsGlobInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.HttpGet => $"{EmitHttpGetInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.HttpPost => $"{EmitHttpPostInvocation(call.Arguments)} >/dev/null",
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Intrinsic '{call.CanonicalName}' cannot be emitted as a statement in Bash")
        };
    }

    private string EmitIntrinsicValue(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => $"$({EmitPrint(call.Arguments, newline: false)})",
            IntrinsicId.Println => $"$({EmitPrint(call.Arguments, newline: true)})",
            IntrinsicId.StringTrim => $"\"$({EmitStringTrimInvocation(call.Arguments)})\"",
            IntrinsicId.StringLower => $"\"$({EmitStringLowerInvocation(call.Arguments)})\"",
            IntrinsicId.StringUpper => $"\"$({EmitStringUpperInvocation(call.Arguments)})\"",
            IntrinsicId.StringSplit => $"\"$({EmitStringSplitInvocation(call.Arguments)})\"",
            IntrinsicId.StringContains => $"\"$({EmitStringContainsInvocation(call.Arguments)})\"",
            IntrinsicId.StringStartsWith => $"\"$({EmitStringStartsWithInvocation(call.Arguments)})\"",
            IntrinsicId.StringEndsWith => $"\"$({EmitStringEndsWithInvocation(call.Arguments)})\"",
            IntrinsicId.StringReplace => $"\"$({EmitStringReplaceInvocation(call.Arguments)})\"",
            IntrinsicId.StringIsMatch => $"\"$({EmitStringIsMatchInvocation(call.Arguments)})\"",
            IntrinsicId.StringMatch => $"\"$({EmitStringMatchInvocation(call.Arguments)})\"",
            IntrinsicId.IoReadText => $"$(cat -- {Arg(call.Arguments, 0)})",
            IntrinsicId.IoExists => $"$([[ -e {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.PathJoin => EmitPathJoin(call.Arguments),
            IntrinsicId.PathDirname => $"$(dirname -- {Arg(call.Arguments, 0)})",
            IntrinsicId.PathBasename => $"$(basename -- {Arg(call.Arguments, 0)})",
            IntrinsicId.EnvGet => EmitEnvGet(call.Arguments),
            IntrinsicId.ProcessArgs => "\"$*\"",
            IntrinsicId.OsCwd => "$(pwd)",
            IntrinsicId.IoWriteText => $"$({EmitIoWriteText(call.Arguments)})",
            IntrinsicId.EnvSet => $"$({EmitEnvSet(call.Arguments)})",
            IntrinsicId.ProcessExit => $"$({EmitProcessExit(call.Arguments)})",
            IntrinsicId.OsChdir => $"$(cd -- {Arg(call.Arguments, 0)})",
            IntrinsicId.ProcessRun => $"\"$({EmitProcessRunInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessPipeline => $"\"$({EmitProcessPipelineInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessFail => $"\"$({EmitProcessFailInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessRequireSuccess => $"\"$({EmitProcessRequireSuccessInvocation(call.Arguments)})\"",
            IntrinsicId.JsonParse => $"\"$({EmitJsonParseInvocation(call.Arguments)})\"",
            IntrinsicId.JsonStringify => $"\"$({EmitJsonStringifyInvocation(call.Arguments)})\"",
            IntrinsicId.FsGlob => $"\"$({EmitFsGlobInvocation(call.Arguments)})\"",
            IntrinsicId.HttpGet => $"\"$({EmitHttpGetInvocation(call.Arguments)})\"",
            IntrinsicId.HttpPost => $"\"$({EmitHttpPostInvocation(call.Arguments)})\"",
            _ => _context.ErrorAndReturn(UnsupportedEmitCode, $"Unsupported intrinsic expression in Bash: {call.CanonicalName}")
        };
    }

    private string EmitPrint(IReadOnlyList<IrExpression> arguments, bool newline)
    {
        var value = arguments.Count == 0 ? "''" : Arg(arguments, 0);
        return newline
            ? $"printf '%s\\n' {value}"
            : $"printf '%s' {value}";
    }

    private string EmitStringTrimInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_trim {Arg(arguments, 0)}";
    }

    private string EmitStringLowerInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_lower {Arg(arguments, 0)}";
    }

    private string EmitStringUpperInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_upper {Arg(arguments, 0)}";
    }

    private string EmitStringSplitInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_split {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)}";
    }

    private string EmitStringContainsInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_contains {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringStartsWithInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_starts_with {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringEndsWithInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_ends_with {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringReplaceInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_replace {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)}";
    }

    private string EmitStringIsMatchInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_is_match {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitStringMatchInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_string_match {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitIoWriteText(IReadOnlyList<IrExpression> arguments)
    {
        var path = Arg(arguments, 0);
        var text = Arg(arguments, 1);
        var append = Arg(arguments, 2);
        return "__sushi_path=$(printf '%s' " + path + "); " +
               "__sushi_dir=$(dirname -- \"$__sushi_path\"); " +
               "if [[ \"$__sushi_dir\" != \".\" && ! -d \"$__sushi_dir\" ]]; then mkdir -p -- \"$__sushi_dir\"; fi; " +
               "if [[ " + append + " == 'true' ]]; then printf '%s' " + text + " >> \"$__sushi_path\"; else printf '%s' " + text + " > \"$__sushi_path\"; fi";
    }

    private string EmitEnvSet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var value = Arg(arguments, 1);
        return $"__sushi_env_name=$(printf '%s' {name}); __sushi_env_value=$(printf '%s' {value}); export \"$__sushi_env_name=$__sushi_env_value\"";
    }

    private string EmitProcessExit(IReadOnlyList<IrExpression> arguments)
    {
        return $"exit {EmitArithmeticExpression(arguments[0])}";
    }

    private string EmitPathJoin(IReadOnlyList<IrExpression> arguments)
    {
        if (arguments.Count == 0)
        {
            return "''";
        }

        if (arguments.Count == 1)
        {
            return Arg(arguments, 0);
        }

        var first = Arg(arguments, 0);
        var rest = arguments.Skip(1).Select(argument => $"printf '/%s' {EmitValueExpression(argument)}");
        var commands = string.Join("; ", new[] { $"printf '%s' {first}" }.Concat(rest));
        return $"$({commands})";
    }

    private string EmitEnvGet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var fallback = Arg(arguments, 1);
        return "$(__sushi_env_name=$(printf '%s' " + name + "); " +
               "__sushi_env_value=$(printenv \"$__sushi_env_name\" 2>/dev/null || true); " +
               "if printenv \"$__sushi_env_name\" >/dev/null 2>&1; then printf '%s' \"$__sushi_env_value\"; else printf '%s' " + fallback + "; fi)";
    }

    private string EmitProcessRunInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return "__sushi_process_run " +
               $"{Arg(arguments, 0)} " +
               $"{Arg(arguments, 1)} " +
               $"{Arg(arguments, 2)} " +
               $"{Arg(arguments, 3)} " +
               $"{Arg(arguments, 4)} " +
               $"{Arg(arguments, 5)} " +
               $"{Arg(arguments, 6)} " +
               $"{Arg(arguments, 7)}";
    }

    private string EmitProcessPipelineInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return "__sushi_process_pipeline " +
               $"{Arg(arguments, 0)} " +
               $"{Arg(arguments, 1)} " +
               $"{Arg(arguments, 2)} " +
               $"{Arg(arguments, 3)} " +
               $"{Arg(arguments, 4)} " +
               $"{Arg(arguments, 5)} " +
               $"{Arg(arguments, 6)}";
    }

    private string EmitProcessFailInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_process_fail {Arg(arguments, 0)}";
    }

    private string EmitProcessRequireSuccessInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_process_require_success {Arg(arguments, 0)}";
    }

    private string EmitJsonParseInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_json_parse {Arg(arguments, 0)}";
    }

    private string EmitJsonStringifyInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_json_stringify {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitFsGlobInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_fs_glob {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitHttpGetInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_http_get {Arg(arguments, 0)} {Arg(arguments, 1)}";
    }

    private string EmitHttpPostInvocation(IReadOnlyList<IrExpression> arguments)
    {
        return $"__sushi_http_post {Arg(arguments, 0)} {Arg(arguments, 1)} {Arg(arguments, 2)} {Arg(arguments, 3)}";
    }

    private string Arg(IReadOnlyList<IrExpression> arguments, int index)
    {
        return index < arguments.Count ? EmitValueExpression(arguments[index]) : "''";
    }
}
