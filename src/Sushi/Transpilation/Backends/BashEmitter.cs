namespace Sushi.Transpilation.Backends;

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sushi.Application;
using Sushi.Transpilation.IR;
using Sushi.Transpilation.Intrinsics;

public sealed class BashEmitter : IBackendEmitter
{
    private const string UnsupportedEmitCode = "SUSHI1100";
    private const string AmbiguousShapeCode = "SUSHI1030";

    private readonly StringBuilder _builder = new();
    private readonly bool _zshMode;
    private EmitContext _context = null!;
    private int _indent;
    private int _valueTempId;
    private string? _currentFunctionName;
    private IrTypeRef _currentFunctionReturnType = IrTypeRef.Any;
    private HashSet<string> _knownIntegerVariables = new(StringComparer.Ordinal);
    private HashSet<string> _integerReturningFunctions = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeArrayVariables = new(StringComparer.Ordinal);
    private HashSet<string> _nativeObjectVariables = new(StringComparer.Ordinal);
    private HashSet<string> _recordVariables = new(StringComparer.Ordinal);
    private Dictionary<string, IrArrayLiteralExpression> _arrayInitializers = new(StringComparer.Ordinal);
    private Dictionary<string, IrFunctionDeclarationStatement> _functions = new(StringComparer.Ordinal);
    private HashSet<string> _integerArrayVariables = new(StringComparer.Ordinal);
    private Dictionary<string, string> _zshObjectParameterNames = new(StringComparer.Ordinal);
    private HashSet<string> _zshReadOnlyObjectParameters = new(StringComparer.Ordinal);
    private Dictionary<string, string> _nativeObjectAliases = new(StringComparer.Ordinal);
    private TargetNameAllocator _names = null!;
    private Dictionary<string, string> _generatedFunctionNames = new(StringComparer.Ordinal);
    private bool _currentFunctionReturnsValue;
    private string _currentOutputName = "";
    private bool _needsDynamicMethodMetadata;
    private HashSet<string> _commentedTypes = new(StringComparer.Ordinal);
    private HashSet<string> _classTypeNames = new(StringComparer.Ordinal);
    private HashSet<string> _enumTypeNames = new(StringComparer.Ordinal);
    private bool _emittedTopLevelSection;
    private Dictionary<string, int> _positionalParameterReferences = new(StringComparer.Ordinal);

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
        _names = new TargetNameAllocator(_zshMode ? TargetLanguage.Zsh : TargetLanguage.Bash, _zshMode);
        _generatedFunctionNames.Clear();
        _context = context;
        _indent = 0;
        _valueTempId = 0;
        _currentFunctionName = null;
        _currentFunctionReturnType = IrTypeRef.Any;
        _knownIntegerVariables.Clear();
        _nativeArrayVariables.Clear();
        _nativeObjectVariables.Clear();
        _recordVariables.Clear();
        _arrayInitializers.Clear();
        _functions = program.Statements
            .OfType<IrFunctionDeclarationStatement>()
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        _needsDynamicMethodMetadata = program.Statements.Any(ContainsDynamicMethodDispatch);
        _integerArrayVariables.Clear();
        _zshObjectParameterNames.Clear();
        _zshReadOnlyObjectParameters.Clear();
        _nativeObjectAliases.Clear();
        _commentedTypes.Clear();
        _classTypeNames = CollectClassTypeNames(program.Statements);
        _enumTypeNames = CollectEnumTypeNames(program.Statements);
        _emittedTopLevelSection = false;
        _positionalParameterReferences.Clear();
        _integerReturningFunctions = program.Statements
            .OfType<IrFunctionDeclarationStatement>()
            .Where(function => function.ReturnType.Kind == IrTypeKind.Primitive &&
                               function.ReturnType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true)
            .Select(function => function.Name)
            .ToHashSet(StringComparer.Ordinal);

        WriteLine(_zshMode ? "#!/usr/bin/env zsh" : "#!/usr/bin/env bash");
        if (_zshMode)
        {
            WriteLine("set -eu");
            WriteLine("set -o pipefail");
            if (EmissionCapabilityAnalyzer.UsesArrays(program))
            {
                WriteLine("setopt ksharrays");
            }
        }
        else
        {
            WriteLine("set -euo pipefail");
        }
        if (EmissionCapabilityAnalyzer.UsesFsGlob(program))
        {
            EmitCoreRuntimeHelpers();
            EmitRuntimeHelpers();
        }
        foreach (var statement in program.Statements)
        {
            EmitStatement(statement, inFunction: false);
        }

        return PrettyPrintBash(_builder.ToString());
    }

    private void EmitCoreRuntimeHelpers()
    {
        AppendRuntimeBlock(
"""
__sushi_j_is_integer() {
  [[ "${1-}" =~ ^-?[0-9]+$ ]]
}

__sushi_validate_integer() {
  local value="${1-}"
  local context="${2-arithmetic operand}"
  __sushi_j_is_integer "$value" && return 0
  printf 'Type contract violation: %s expected int\n' "$context" >&2
  exit 2
}

__sushi_validate_string_receiver() {
  local value="${1-}"
  local method="${2-string method}"
  if [[ "$value" == "__sushi_null__" ]]; then
    printf 'Type contract violation: string receiver for %s expected non-null value\n' "$method" >&2
    exit 2
  fi
}

__sushi_regex_to_ere_into() {
  local pattern="${1-}"
  pattern="${pattern//\\d/[0-9]}"
  pattern="${pattern//\\D/[^0-9]}"
  pattern="${pattern//\\w/[[:alnum:]_]}"
  pattern="${pattern//\\W/[^[:alnum:]_]}"
  pattern="${pattern//\\s/[[:space:]]}"
  pattern="${pattern//\\S/[^[:space:]]}"
  __sushi_result="$pattern"
}

__sushi_infer_kind_into() {
  local value="${1-}"
  case "$value" in
    @a:__sushi_array_*) __sushi_kind='array' ;;
    @o:__sushi_object_*) __sushi_kind='object' ;;
    true|false) __sushi_kind='bool' ;;
    null) __sushi_kind='null' ;;
    '') __sushi_kind='string' ;;
    *)
      if __sushi_j_is_integer "$value"; then __sushi_kind='number'
      elif [[ "$value" =~ ^-?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?$ ]]; then __sushi_kind='number'
      else __sushi_kind='string'
      fi
      ;;
  esac
}
""");

        AppendRuntimeBlock(_zshMode
            ? """
__sushi_array_seq=0
__sushi_is_array_handle() {
  local handle="${1-}" name="${1#@a:}"
  [[ "$handle" == @a:__sushi_array_<-> ]] || return 1
  (( ${+parameters[$name]} ))
}
__sushi_array_new() {
  __sushi_array_seq=$((__sushi_array_seq + 1))
  local name="__sushi_array_${__sushi_array_seq}" kinds_name="__sushi_array_${__sushi_array_seq}__kinds"
  local item index=0
  typeset -g -a "$name"
  typeset -g -a "$kinds_name"
  eval "$name=()"
  eval "$kinds_name=()"
  for item in "$@"; do
    eval "$name[$index]=${(q)item}"
    __sushi_infer_kind_into "$item"
    eval "$kinds_name[$index]=${(q)__sushi_kind}"
    (( index += 1 ))
  done
  __sushi_result="@a:$name"
}
__sushi_array_get_into() {
  local handle="${1-}" index="${2-0}" name="${1#@a:}"
  __sushi_is_array_handle "$handle" || { __sushi_result=''; return 0; }
  eval "local length=\${#$name[@]}"
  if (( index < 0 )); then index=$((length + index)); fi
  if (( index < 0 || index >= length )); then __sushi_result=''; return 0; fi
  eval "__sushi_result=\"\${$name[$index]-}\""
}
__sushi_array_each_raw() {
  local handle="${1-}" name="${1#@a:}"
  __sushi_is_array_handle "$handle" || return 0
  local -a values
  eval "values=(\"\${$name[@]}\")"
  local item
  for item in "${values[@]}"; do printf '%s\n' "$item"; done
}
__sushi_array_length_into() {
  local handle="${1-}" name="${1#@a:}"
  __sushi_is_array_handle "$handle" || { __sushi_result=0; return 0; }
  eval "__sushi_result=\${#$name[@]}"
}
__sushi_array_kind_into() {
  local handle="${1-}" index="${2-0}" name="${1#@a:}__kinds"
  eval "__sushi_kind=\"\${$name[$index]-string}\""
}
__sushi_array_set_kind() {
  local handle="${1-}" index="${2-0}" kind="${3-string}" name="${1#@a:}__kinds"
  eval "$name[$index]=${(q)kind}"
}
__sushi_array_append_to() {
  local handle="${1-}" destination="${2-}" name="${1#@a:}"
  __sushi_is_array_handle "$handle" || return 1
  eval "$destination+=(\"\${$name[@]}\")"
}
"""
            : """
__sushi_array_seq=0
__sushi_is_array_handle() {
  local handle="${1-}" name="${1#@a:}"
  [[ "$handle" =~ ^@a:__sushi_array_[0-9]+$ ]] || return 1
  declare -p "$name" >/dev/null 2>&1
}
__sushi_array_new() {
  __sushi_array_seq=$((__sushi_array_seq + 1))
  local name="__sushi_array_${__sushi_array_seq}" kinds_name="__sushi_array_${__sushi_array_seq}__kinds"
  declare -g -a "$name"
  declare -g -a "$kinds_name"
  local -n target="$name" kinds="$kinds_name"
  target=("$@")
  kinds=()
  local item
  for item in "$@"; do __sushi_infer_kind_into "$item"; kinds+=("$__sushi_kind"); done
  __sushi_result="@a:$name"
}
__sushi_array_get_into() {
  local handle="${1-}" index="${2-0}" name="${1#@a:}"
  __sushi_is_array_handle "$handle" || { __sushi_result=''; return 0; }
  local -n values="$name"
  local length=${#values[@]}
  if (( index < 0 )); then index=$((length + index)); fi
  if (( index < 0 || index >= length )); then __sushi_result=''; return 0; fi
  __sushi_result="${values[$index]-}"
}
__sushi_array_each_raw() {
  local handle="${1-}" name="${1#@a:}"
  __sushi_is_array_handle "$handle" || return 0
  local -n values="$name"
  local item
  for item in "${values[@]}"; do printf '%s\n' "$item"; done
}
__sushi_array_length_into() {
  local handle="${1-}" name="${1#@a:}"
  __sushi_is_array_handle "$handle" || { __sushi_result=0; return 0; }
  local -n values="$name"
  __sushi_result="${#values[@]}"
}
__sushi_array_kind_into() {
  local handle="${1-}" index="${2-0}" name="${1#@a:}__kinds"
  local -n kinds="$name"
  __sushi_kind="${kinds[$index]-string}"
}
__sushi_array_set_kind() {
  local handle="${1-}" index="${2-0}" kind="${3-string}" name="${1#@a:}__kinds"
  local -n kinds="$name"
  kinds[$index]="$kind"
}
__sushi_array_append_to() {
  local handle="${1-}" destination="${2-}" name="${1#@a:}"
  __sushi_is_array_handle "$handle" || return 1
  local -n source="$name" target="$destination"
  target+=("${source[@]}")
}
""");
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
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == '"' ]] || __sushi_j_fail 'expected string'
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
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "[" ]] || __sushi_j_fail 'expected array'
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "]" ]]; then
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
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
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
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "{" ]] || __sushi_j_fail 'expected object'
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "}" ]]; then
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
    [[ "${__sushi_j_src:$__sushi_j_pos:1}" == ":" ]] || __sushi_j_fail 'expected :'
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
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
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
  local c="${__sushi_j_src:$__sushi_j_pos:1}"
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

__sushi_json_quote_into() {
  local value="${1-}"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  value="${value//$'\f'/\\f}"
  value="${value//$'\b'/\\b}"
  __sushi_result="\"$value\""
}

__sushi_json_quote() {
  __sushi_json_quote_into "${1-}"
  printf '%s' "${__sushi_result-}"
}

__sushi_is_obj_handle() {
  case "${1-}" in
    @o:\{*|@o:__sushi_object_*) return 0 ;;
    *) return 1 ;;
  esac
}

__sushi_obj_new() {
  local compact="${1-\{\}}"
  printf '@o:%s' "$compact"
}

__sushi_obj_get() {
  local handle="${1-}"
  local key="${2-}"
  if [[ "$handle" == @o:__sushi_object_* ]]; then
    __sushi_native_obj_get_into "$handle" "$key"
    printf '%s' "${__sushi_result-}"
    return 0
  fi
  __sushi_is_obj_handle "$handle" || {
    printf ''
    return 0
  }
  __sushi_json_member "${handle#@o:}" "$key"
}

__sushi_obj_get_into() {
  local handle="${1-}"
  local key="${2-}"
  if [[ "$handle" == @o:__sushi_object_* ]]; then
    __sushi_native_obj_get_into "$handle" "$key"
  else
    __sushi_result="$(__sushi_json_member "$handle" "$key")"
  fi
}

__sushi_obj_to_json() {
  local handle="${1-}"
  if [[ "$handle" == @o:__sushi_object_* ]]; then
    __sushi_native_obj_to_json "$handle"
    return 0
  fi
  __sushi_is_obj_handle "$handle" || {
    printf '{}'
    return 0
  }

  printf '%s' "${handle#@o:}"
}

__sushi_obj_to_json_into() {
  local handle="${1-}"
  if [[ "$handle" == @o:__sushi_object_* ]]; then
    __sushi_native_obj_to_json_into "$handle"
  elif __sushi_is_obj_handle "$handle"; then
    __sushi_result="${handle#@o:}"
  else
    __sushi_result='{}'
  fi
}

__sushi_json_try_compact() {
  local text="${1-}"
  if __sushi_is_obj_handle "$text"; then
    __sushi_obj_to_json "$text"
    return 0
  fi
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
    \{* ) __sushi_json_object_from_compact "$value_json" ;;
    \[* ) printf '%s' "$value_json" ;;
    \"*) __sushi_j_unescape_string "$value_json" ;;
    *) printf '%s' "$value_json" ;;
  esac
}

__sushi_json_object_from_compact() {
  local compact="${1-}"
  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "{" ]] || {
    printf '%s' "$compact"
    return 0
  }
  __sushi_obj_new "$compact"
}

__sushi_value_to_json() {
  local value="${1-}"
  if __sushi_is_array_handle "$value"; then
    __sushi_array_to_json "$value"
    return 0
  fi
  if __sushi_is_obj_handle "$value"; then
    __sushi_obj_to_json "$value"
    return 0
  fi

  local compact
  if compact="$(__sushi_json_try_compact "$value")"; then
    printf '%s' "$compact"
  else
    __sushi_json_quote "$value"
  fi
}

__sushi_value_to_json_kind() {
  local value="${1-}" kind="${2-auto}"
  case "$kind" in
    string) __sushi_json_quote "$value" ;;
    null) printf 'null' ;;
    bool|number) printf '%s' "$value" ;;
    array) __sushi_array_to_json "$value" ;;
    object) __sushi_obj_to_json "$value" ;;
    *) __sushi_value_to_json "$value" ;;
  esac
}

__sushi_value_to_json_kind_into() {
  local value="${1-}" kind="${2-auto}"
  case "$kind" in
    string) __sushi_json_quote_into "$value" ;;
    null) __sushi_result='null' ;;
    bool|number) __sushi_result="$value" ;;
    array) __sushi_array_to_json_into "$value" ;;
    object) __sushi_obj_to_json_into "$value" ;;
    *) __sushi_result="$(__sushi_value_to_json "$value")" ;;
  esac
}

__sushi_json_array() {
  local out='['
  local first=true
  local raw value_json
  for raw in "$@"; do
    if [[ "$first" == "true" ]]; then
      first=false
    else
      out+=','
    fi

    value_json="$(__sushi_value_to_json "$raw")"
    out+="$value_json"
  done
  out+=']'
  printf '%s' "$out"
}

__sushi_array_to_json_into() {
  local handle="${1-}"
  __sushi_is_array_handle "$handle" || { __sushi_result='[]'; return 0; }
  local out='[' first=true index length value kind
  __sushi_array_length_into "$handle"; length="${__sushi_result-0}"
  for (( index=0; index<length; index++ )); do
    __sushi_array_get_into "$handle" "$index"; value="${__sushi_result-}"
    __sushi_array_kind_into "$handle" "$index"; kind="${__sushi_kind-auto}"
    [[ "$first" == true ]] || out+=','; first=false
    __sushi_value_to_json_kind_into "$value" "$kind"
    out+="${__sushi_result-}"
  done
  __sushi_result="$out]"
}

__sushi_array_to_json() {
  __sushi_array_to_json_into "${1-}"
  printf '%s' "${__sushi_result-}"
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
    value_json="$(__sushi_value_to_json "$raw")"
    out+="$key_json:$value_json"
  done
  out+='}'
  __sushi_obj_new "$out"
}

__sushi_json_array_each_json() {
  local json="${1-}"
  if __sushi_is_array_handle "$json"; then
    local raw
    while IFS= read -r raw; do
      __sushi_value_to_json "$raw"
      printf '\n'
    done < <(__sushi_array_each_raw "$json")
    return 0
  fi
  __sushi_j_reset "$json"
  __sushi_j_skip_ws
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "[" ]] || return 0
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "]" ]]; then
    return 0
  fi

  while :; do
    __sushi_j_parse_value || return 0
    printf '%s\n' "$__sushi_j_last"
    __sushi_j_skip_ws
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
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
  if __sushi_is_array_handle "$json"; then
    __sushi_array_each_raw "$json"
    return 0
  fi
  local item
  while IFS= read -r item; do
    __sushi_json_value_to_raw "$item"
    printf '\n'
  done < <(__sushi_json_array_each_json "$json")
}

__sushi_json_object_each_kv() {
  local json="${1-}"
  if [[ "$json" == @o:__sushi_object_* ]]; then
    __sushi_native_obj_each_kv "$json"
    return 0
  fi
  if __sushi_is_obj_handle "$json"; then
    json="${json#@o:}"
  fi

  local compact
  compact="$(__sushi_json_try_compact "$json")" || return 0

  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "{" ]] || return 0
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "}" ]]; then
    return 0
  fi

  while :; do
    __sushi_j_parse_string || return 0
    local key_json="$__sushi_j_last"
    local key_raw="$(__sushi_j_unescape_string "$key_json")"

    __sushi_j_skip_ws
    [[ "${__sushi_j_src:$__sushi_j_pos:1}" == ":" ]] || return 0
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
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
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
  if __sushi_is_obj_handle "$json"; then
    __sushi_obj_get "$json" "$key"
    return 0
  fi

  local target_json="$(__sushi_json_quote "$key")"
  local compact
  compact="$(__sushi_json_try_compact "$json")" || return 0

  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "{" ]] || return 0
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  if [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "}" ]]; then
    return 0
  fi

  while :; do
    __sushi_j_parse_string || return 0
    local key_json="$__sushi_j_last"
    __sushi_j_skip_ws
    [[ "${__sushi_j_src:$__sushi_j_pos:1}" == ":" ]] || return 0
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_j_skip_ws
    __sushi_j_parse_value || return 0
    local value_json="$__sushi_j_last"

    if [[ "$key_json" == "$target_json" ]]; then
      __sushi_json_value_to_raw "$value_json"
      return 0
    fi

    __sushi_j_skip_ws
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
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
  if __sushi_is_array_handle "$json"; then
    __sushi_array_get_into "$json" "$index"
    printf '%s' "${__sushi_result-}"
    return 0
  fi
  if __sushi_is_obj_handle "$json"; then
    __sushi_obj_get "$json" "$index"
    return 0
  fi

  local compact="$json"
  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  local first_char="${__sushi_j_src:$__sushi_j_pos:1}"

  if [[ "$first_char" == "{" ]]; then
    __sushi_json_member "$compact" "$index"
    return 0
  fi

  if [[ "$first_char" != "[" ]]; then
    return 0
  fi

  __sushi_j_is_integer "$index" || return 0
  local idx=$index
  local i=0
  local item
  local selected=''
  local found=false

  if (( idx >= 0 )); then
    while IFS= read -r item; do
      if (( i == idx )); then
        selected="$item"
        found=true
      fi
      i=$((i + 1))
    done < <(__sushi_json_array_each_json "$compact")

    [[ "$found" == "true" ]] && __sushi_json_value_to_raw "$selected"
    return 0
  fi

  local count=0
  while IFS= read -r _line; do
    count=$((count + 1))
  done < <(__sushi_json_array_each_json "$compact")

  idx=$((count + idx))
  (( idx >= 0 && idx < count )) || return 0

  i=0
  while IFS= read -r item; do
    if (( i == idx )); then
      selected="$item"
      found=true
    fi
    i=$((i + 1))
  done < <(__sushi_json_array_each_json "$compact")

  [[ "$found" == "true" ]] && __sushi_json_value_to_raw "$selected"
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

__sushi_j_parse_native_array() {
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  local -a values=() kinds=()
  if [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "]" ]]; then
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_array_new
    __sushi_kind='array'
    return 0
  fi

  while :; do
    __sushi_j_parse_native_value || return 1
    values+=("${__sushi_result-}")
    kinds+=("${__sushi_kind-auto}")
    __sushi_j_skip_ws
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
    if [[ "$c" == "," ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      __sushi_j_skip_ws
      continue
    fi
    [[ "$c" == "]" ]] || return 1
    __sushi_j_pos=$((__sushi_j_pos + 1))
    break
  done

  __sushi_array_new "${values[@]}"
  local handle="${__sushi_result-}" index
  for (( index=0; index<${#kinds[@]}; index++ )); do
    __sushi_array_set_kind "$handle" "$index" "${kinds[$index]}"
  done
  __sushi_result="$handle"
  __sushi_kind='array'
}

__sushi_j_parse_native_object() {
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  local -a keys=() values=() kinds=()
  if [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "}" ]]; then
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_native_obj_new
    __sushi_kind='object'
    return 0
  fi

  while :; do
    __sushi_j_parse_string || return 1
    local key_json="$__sushi_j_last"
    local key_raw="$(__sushi_j_unescape_string "$key_json")"
    __sushi_j_skip_ws
    [[ "${__sushi_j_src:$__sushi_j_pos:1}" == ":" ]] || return 1
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_j_skip_ws
    __sushi_j_parse_native_value || return 1
    keys+=("$key_raw")
    values+=("${__sushi_result-}")
    kinds+=("${__sushi_kind-auto}")
    __sushi_j_skip_ws
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
    if [[ "$c" == "," ]]; then
      __sushi_j_pos=$((__sushi_j_pos + 1))
      __sushi_j_skip_ws
      continue
    fi
    [[ "$c" == "}" ]] || return 1
    __sushi_j_pos=$((__sushi_j_pos + 1))
    break
  done

  local -a pairs=()
  local index
  for (( index=0; index<${#keys[@]}; index++ )); do
    pairs+=("${keys[$index]}" "${values[$index]}")
  done
  __sushi_native_obj_new "${pairs[@]}"
  local handle="${__sushi_result-}"
  for (( index=0; index<${#keys[@]}; index++ )); do
    __sushi_native_obj_set_kind "$handle" "${keys[$index]}" "${kinds[$index]}"
  done
  __sushi_result="$handle"
  __sushi_kind='object'
}

__sushi_j_parse_native_value() {
  __sushi_j_skip_ws
  local c="${__sushi_j_src:$__sushi_j_pos:1}"
  case "$c" in
    '"')
      __sushi_j_parse_string || return 1
      __sushi_result="$(__sushi_j_unescape_string "$__sushi_j_last")"
      __sushi_kind='string'
      ;;
    '{') __sushi_j_parse_native_object ;;
    '[') __sushi_j_parse_native_array ;;
    't') __sushi_j_parse_literal true || return 1; __sushi_result=true; __sushi_kind='bool' ;;
    'f') __sushi_j_parse_literal false || return 1; __sushi_result=false; __sushi_kind='bool' ;;
    'n') __sushi_j_parse_literal null || return 1; __sushi_result=''; __sushi_kind='null' ;;
    '-'|[0-9]) __sushi_j_parse_number || return 1; __sushi_result="$__sushi_j_last"; __sushi_kind='number' ;;
    *) return 1 ;;
  esac
}

__sushi_json_parse_into() {
  local text="${1-}"
  __sushi_j_reset "$text"
  if __sushi_j_parse_native_value; then
    local parsed="${__sushi_result-}" parsed_kind="${__sushi_kind-auto}"
    __sushi_j_skip_ws
    if (( __sushi_j_pos == __sushi_j_len )); then
      __sushi_result="$parsed"
      __sushi_kind="$parsed_kind"
      return 0
    fi
  fi
  __sushi_result="$text"
  __sushi_kind='string'
}

__sushi_json_parse() {
  __sushi_json_parse_into "${1-}"
  case "${__sushi_kind-auto}" in
    object)
      local compact="$(__sushi_obj_to_json "${__sushi_result-}")"
      __sushi_obj_new "$compact"
      ;;
    array) __sushi_array_to_json "${__sushi_result-}" ;;
    *) printf '%s' "${__sushi_result-}" ;;
  esac
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
  if __sushi_is_array_handle "$value"; then
    compact="$(__sushi_array_to_json "$value")"
  elif __sushi_is_obj_handle "$value"; then
    compact="$(__sushi_obj_to_json "$value")"
  elif compact="$(__sushi_json_try_compact "$value")"; then
    :
  else
    __sushi_json_quote "$value"
    return 0
  fi

  if __sushi_j_is_integer "$indent" && (( indent > 0 )); then
    __sushi_j_pretty_json "$compact" "$indent"
  else
    printf '%s' "$compact"
  fi
}

__sushi_json_stringify_into() {
  local value="${1-}" indent="${2:-0}" compact
  if __sushi_is_array_handle "$value"; then
    __sushi_array_to_json_into "$value"; compact="${__sushi_result-}"
  elif __sushi_is_obj_handle "$value"; then
    __sushi_obj_to_json_into "$value"; compact="${__sushi_result-}"
  elif compact="$(__sushi_json_try_compact "$value")"; then
    :
  else
    __sushi_json_quote_into "$value"; compact="${__sushi_result-}"
  fi
  if __sushi_j_is_integer "$indent" && (( indent > 0 )); then
    __sushi_result="$(__sushi_j_pretty_json "$compact" "$indent")"
  else
    __sushi_result="$compact"
  fi
  __sushi_kind='string'
}

__sushi_process_run_into() {
  local command="${1-}"
  local args_json="${2-}"
  local cwd="${3-}"
  local env_json="${4-}"
  local input_text="${5-}"
  local timeout_ms="${6-0}"
  local allow_failure="${7-false}"
  local stream="${8-false}"
  local shared_stderr_file="${9-}"

  local stdout_file='' stderr_file='' input_file=''
  local -a cmd_argv=() env_pairs=() env_prefix=()
  cmd_argv=("$command")
  if [[ -n "$args_json" ]] && [[ "$args_json" != "null" ]]; then
    if __sushi_is_array_handle "$args_json"; then
      __sushi_array_append_to "$args_json" cmd_argv
    else
      while IFS= read -r arg; do
        cmd_argv+=("$arg")
      done < <(__sushi_json_array_each_raw "$args_json")
    fi
  fi

  if [[ -n "$env_json" ]] && [[ "$env_json" != "null" ]]; then
    while IFS=$'\t' read -r key value; do
      [[ -z "$key" ]] && continue
      env_pairs+=("${key}=${value}")
    done < <(__sushi_json_object_each_kv "$env_json")
  fi
  if (( ${#env_pairs[@]} > 0 )); then
    env_prefix=(env "${env_pairs[@]}")
  fi

  local exit_code timed_out=false
  local timeout_enabled=false
  if __sushi_j_is_integer "$timeout_ms" && (( timeout_ms > 0 )); then
    timeout_enabled=true
  fi

  local fast_capture=false
  local cleanup_stderr_file=false
  if [[ "$timeout_enabled" != "true" && "$stream" != "true" ]]; then
    fast_capture=true
    if [[ -n "$shared_stderr_file" ]]; then
      stderr_file="$shared_stderr_file"
      : > "$stderr_file"
    else
      stderr_file="$(mktemp)"
      cleanup_stderr_file=true
    fi
  else
    stdout_file="$(mktemp)"
    stderr_file="$(mktemp)"
    cleanup_stderr_file=true
    input_file="$(mktemp)"
    printf '%s' "$input_text" > "$input_file"
  fi

  local stdout_text='' stderr_text=''
  set +e
  if [[ "$fast_capture" == "true" ]]; then
    if [[ -n "$cwd" ]] && [[ "$cwd" != "null" ]]; then
      if [[ -n "$input_text" ]]; then
        stdout_text="$(cd -- "$cwd" 2>/dev/null && "${env_prefix[@]}" "${cmd_argv[@]}" < <(printf '%s' "$input_text") 2> "$stderr_file")"
      else
        stdout_text="$(cd -- "$cwd" 2>/dev/null && "${env_prefix[@]}" "${cmd_argv[@]}" < /dev/null 2> "$stderr_file")"
      fi
    elif [[ -n "$input_text" ]]; then
      stdout_text="$("${env_prefix[@]}" "${cmd_argv[@]}" < <(printf '%s' "$input_text") 2> "$stderr_file")"
    else
      stdout_text="$("${env_prefix[@]}" "${cmd_argv[@]}" < /dev/null 2> "$stderr_file")"
    fi
    exit_code=$?
  elif [[ "$timeout_enabled" == "true" ]]; then
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

  local command_text ok_json
  if [[ "$fast_capture" == "true" ]]; then
    stderr_text="$(< "$stderr_file")"
  else
    stdout_text="$(cat -- "$stdout_file" 2>/dev/null || true)"
    stderr_text="$(cat -- "$stderr_file" 2>/dev/null || true)"
  fi
  command_text="$(printf '%q ' "${cmd_argv[@]}")"
  command_text="${command_text% }"
  ok_json=false
  if [[ "$exit_code" -eq 0 ]]; then
    ok_json=true
  fi

  __sushi_native_obj_new \
    code "$exit_code" \
    stdout "$stdout_text" \
    stderr "$stderr_text" \
    command "$command_text" \
    ok "$ok_json" \
    timedOut "$timed_out"
  local result_handle="${__sushi_result-}"
  __sushi_native_obj_set_kind "$result_handle" code number
  __sushi_native_obj_set_kind "$result_handle" stdout string
  __sushi_native_obj_set_kind "$result_handle" stderr string
  __sushi_native_obj_set_kind "$result_handle" command string
  __sushi_native_obj_set_kind "$result_handle" ok bool
  __sushi_native_obj_set_kind "$result_handle" timedOut bool

  [[ -z "$stdout_file" ]] || rm -f -- "$stdout_file"
  [[ "$cleanup_stderr_file" != "true" ]] || rm -f -- "$stderr_file"
  [[ -z "$input_file" ]] || rm -f -- "$input_file"

  if [[ "$allow_failure" != "true" ]] && [[ "$exit_code" -ne 0 ]]; then
    if [[ -n "$stderr_text" ]]; then
      printf '%s\n' "$stderr_text" >&2
    fi
    exit "$exit_code"
  fi

  __sushi_result="$result_handle"
}

__sushi_process_run() {
  __sushi_process_run_into "$@"
  __sushi_obj_to_json "${__sushi_result-}"
}

__sushi_process_pipeline_into() {
  local stages_json="${1-}"
  local cwd="${2-}"
  local env_json="${3-}"
  local input_text="${4-}"
  local timeout_ms="${5-0}"
  local allow_failure="${6-false}"
  local stream="${7-false}"

  local next_input="${input_text-}"
  local -a pipeline_stages=()
  if __sushi_is_array_handle "$stages_json"; then
    __sushi_array_append_to "$stages_json" pipeline_stages
  else
    while IFS= read -r stage; do pipeline_stages+=("$stage"); done < <(__sushi_json_array_each_json "$stages_json")
  fi
  local pipeline_stderr_file=''
  if [[ "$timeout_ms" == "0" && "$stream" != "true" ]]; then
    pipeline_stderr_file="$(mktemp)"
  fi
  __sushi_native_obj_new code 0 stdout '' stderr '' ok true command '' timedOut false
  local last_result="${__sushi_result-}"
  __sushi_native_obj_set_kind "$last_result" code number
  __sushi_native_obj_set_kind "$last_result" stdout string
  __sushi_native_obj_set_kind "$last_result" stderr string
  __sushi_native_obj_set_kind "$last_result" command string
  __sushi_native_obj_set_kind "$last_result" ok bool
  __sushi_native_obj_set_kind "$last_result" timedOut bool

  local stage
  for stage in "${pipeline_stages[@]}"; do
    local stage_command stage_args stage_result stage_code stage_stderr
    __sushi_obj_get_into "$stage" command
    stage_command="${__sushi_result-}"
    __sushi_obj_get_into "$stage" args
    stage_args="${__sushi_result-}"
    __sushi_process_run_into "$stage_command" "$stage_args" "$cwd" "$env_json" "$next_input" "$timeout_ms" "true" "$stream" "$pipeline_stderr_file"
    stage_result="${__sushi_result-}"
    __sushi_obj_get_into "$stage_result" code
    stage_code="${__sushi_result-}"
    if [[ "$allow_failure" != "true" ]] && [[ "$stage_code" -ne 0 ]]; then
      __sushi_obj_get_into "$stage_result" stderr
      stage_stderr="${__sushi_result-}"
      if [[ -n "$stage_stderr" ]]; then
        printf '%s\n' "$stage_stderr" >&2
      fi
      [[ -z "$pipeline_stderr_file" ]] || rm -f -- "$pipeline_stderr_file"
      exit "$stage_code"
    fi
    __sushi_obj_get_into "$stage_result" stdout
    next_input="${__sushi_result-}"
    last_result="$stage_result"
  done

  [[ -z "$pipeline_stderr_file" ]] || rm -f -- "$pipeline_stderr_file"
  __sushi_result="$last_result"
}

__sushi_process_pipeline() {
  __sushi_process_pipeline_into "$@"
  __sushi_obj_to_json "${__sushi_result-}"
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

__sushi_glob_regex_into() {
  local pattern="${1-}" regex='' character next class_end class_text index=0 length
  length=${#pattern}
  while (( index < length )); do
    character="${pattern:$index:1}"
    case "$character" in
      '*')
        next="${pattern:$((index + 1)):1}"
        if [[ "$next" == '*' ]]; then
          next="${pattern:$((index + 2)):1}"
          if [[ "$next" == '/' ]]; then regex+='([^/]*/)*'; (( index += 3 )); continue; fi
          regex+='.*'; (( index += 2 )); continue
        fi
        regex+='[^/]*'
        ;;
      '?') regex+='[^/]' ;;
      '[')
        class_end=$((index + 1))
        while (( class_end < length )) && [[ "${pattern:$class_end:1}" != ']' ]]; do (( class_end += 1 )); done
        if (( class_end >= length )); then regex+='\\['
        else
          class_text="${pattern:$((index + 1)):$((class_end - index - 1))}"
          [[ "$class_text" == '!'* ]] && class_text="^${class_text:1}"
          regex+="[$class_text]"
          index=$class_end
        fi
        ;;
      [\\.^\$+\(\)\{\}\|]) regex+="\\$character" ;;
      *) regex+="$character" ;;
    esac
    (( index += 1 ))
  done
  __sushi_result="^${regex}$"
}

__sushi_fs_glob_into() {
  local pattern="${1-}" cwd="${2-}" base candidate relative match_path regex index
  local -a results=()
  if [[ -n "$cwd" ]] && [[ "$cwd" != 'null' ]]; then
    base="$(cd -- "$cwd" && pwd -P)" || { printf 'std.fs.glob: directory not found: %s\n' "$cwd" >&2; return 1; }
  else
    base="$(pwd -P)"
  fi
  __sushi_glob_regex_into "$pattern"
  regex="$__sushi_result"
  while IFS= read -r -d '' candidate; do
    relative="${candidate#"$base"/}"
    match_path="$relative"
    [[ "$pattern" == /* ]] && match_path="$candidate"
    [[ "$match_path" =~ $regex ]] || continue
    results+=("$relative")
  done < <(find "$base" -mindepth 1 -print0)

  # Keep output deterministic without depending on GNU sort's non-portable -z.
  local item previous
  for (( index=1; index<${#results[@]}; index++ )); do
    item="${results[$index]}"
    previous=$((index - 1))
    while (( previous >= 0 )) && [[ "${results[$previous]}" > "$item" ]]; do
      results[$((previous + 1))]="${results[$previous]}"
      previous=$((previous - 1))
    done
    results[$((previous + 1))]="$item"
  done

  __sushi_array_new "${results[@]}"
  local result_handle="${__sushi_result-}" kind_index
  for (( kind_index=0; kind_index<${#results[@]}; kind_index++ )); do
    __sushi_array_set_kind "$result_handle" "$kind_index" string
  done
  __sushi_result="$result_handle"
}

__sushi_fs_glob() {
  __sushi_fs_glob_into "$@"
  __sushi_array_to_json "${__sushi_result-}"
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

  local body_text headers_obj ok_json json_body
  local -a header_pairs=()
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

  json_body=''
  if [[ -n "$body_text" ]]; then
    local compact_body
    if compact_body="$(__sushi_json_try_compact "$body_text")"; then
      json_body="$(__sushi_json_parse "$compact_body")"
    fi
  fi

  __sushi_json_object \
    status "$http_status" \
    ok "$ok_json" \
    headers "$headers_obj" \
    body "$body_text" \
    json "$json_body" \
    url "$url"

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
  if [[ "$json" == @o:__sushi_object_* ]]; then
    __sushi_native_obj_has "$json" "$key" || return 1
    __sushi_native_obj_get_into "$json" "$key"
    __sushi_value_to_json "${__sushi_result-}"
    return 0
  fi
  if __sushi_is_obj_handle "$json"; then
    json="${json#@o:}"
  fi

  local target_json compact
  target_json="$(__sushi_json_quote "$key")"
  compact="$(__sushi_json_try_compact "$json")" || return 1

  __sushi_j_reset "$compact"
  __sushi_j_skip_ws
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "{" ]] || return 1
  __sushi_j_pos=$((__sushi_j_pos + 1))
  __sushi_j_skip_ws
  [[ "${__sushi_j_src:$__sushi_j_pos:1}" == "}" ]] && return 1

  while :; do
    __sushi_j_parse_string || return 1
    local key_json="$__sushi_j_last"
    __sushi_j_skip_ws
    [[ "${__sushi_j_src:$__sushi_j_pos:1}" == ":" ]] || return 1
    __sushi_j_pos=$((__sushi_j_pos + 1))
    __sushi_j_skip_ws
    __sushi_j_parse_value || return 1
    local value_json="$__sushi_j_last"

    if [[ "$key_json" == "$target_json" ]]; then
      printf '%s' "$value_json"
      return 0
    fi

    __sushi_j_skip_ws
    local c="${__sushi_j_src:$__sushi_j_pos:1}"
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
  local json="${1-}"
  local key="${2-}"
  __sushi_json_member_json "$json" "$key" >/dev/null
}

__sushi_is_float() {
  local s="${1-}"
  [[ "$s" =~ ^-?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?$ ]]
}

__sushi_json_is_array() {
  __sushi_is_array_handle "${1-}" && return 0
  __sushi_is_obj_handle "${1-}" && return 1
  local compact
  compact="$(__sushi_json_try_compact "${1-}")" || return 1
  [[ "${compact:0:1}" == "[" ]]
}

__sushi_json_is_object() {
  __sushi_is_obj_handle "${1-}" && return 0
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

__sushi_validate_integer() {
  local value="${1-}"
  local context="${2-arithmetic operand}"
  if __sushi_j_is_integer "$value"; then
    return 0
  fi

  printf 'Type contract violation: %s expected int, got %s\n' "$context" "$(__sushi_detect_type "$value")" >&2
  exit 2
}
""");

        AppendRuntimeBlock(
"""
__sushi_is_json_array() {
  local value="${1-}"
  __sushi_is_array_handle "$value" && return 0
  [[ "${value:0:1}" == "[" ]] || return 1
  local compact
  compact="$(__sushi_json_try_compact "$value")" || return 1
  [[ "${compact:0:1}" == "[" ]]
}

__sushi_json_length() {
  local value="${1-}"
  if __sushi_is_array_handle "$value"; then
    local name="${value#@a:}"
    if [[ -n "${ZSH_VERSION-}" ]]; then
      eval "printf '%s' \${#$name[@]}"
    else
      local -n values="$name"
      printf '%s' "${#values[@]}"
    fi
    return 0
  fi
  if __sushi_is_obj_handle "$value"; then
    value="${value#@o:}"
  fi

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
    name) __sushi_json_member "$target" "_name"; return 0 ;;
    ordinal) __sushi_json_member "$target" "_ord"; return 0 ;;
    value) __sushi_json_member "$target" "_value"; return 0 ;;
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
  fn="$(__sushi_json_member "$target" "_m_${method}")"
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

__sushi_validate_string_receiver() {
  local value="${1-}"
  local method="${2-string method}"
  if [[ "$value" == "__sushi_null__" ]]; then
    printf 'Type contract violation: string receiver for %s expected non-null value\n' "$method" >&2
    exit 2
  fi
  return 0
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

__sushi_regex_to_ere() {
  local pattern="${1-}"
  pattern="${pattern//\\d/[0-9]}"
  pattern="${pattern//\\D/[^0-9]}"
  pattern="${pattern//\\w/[[:alnum:]_]}"
  pattern="${pattern//\\W/[^[:alnum:]_]}"
  pattern="${pattern//\\s/[[:space:]]}"
  pattern="${pattern//\\S/[^[:space:]]}"
  printf '%s' "$pattern"
}

__sushi_regex_to_ere_into() {
  local pattern="${1-}"
  pattern="${pattern//\\d/[0-9]}"
  pattern="${pattern//\\D/[^0-9]}"
  pattern="${pattern//\\w/[[:alnum:]_]}"
  pattern="${pattern//\\W/[^[:alnum:]_]}"
  pattern="${pattern//\\s/[[:space:]]}"
  pattern="${pattern//\\S/[^[:space:]]}"
  __sushi_result="$pattern"
}

__sushi_string_is_match() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "isMatch")"
  local pattern
  pattern="$(__sushi_regex_to_ere "${2-}")"
  if printf '%s\n' "$value" | grep -E -q -- "$pattern"; then
    printf 'true'
  else
    printf 'false'
  fi
}

__sushi_string_match() {
  local value
  value="$(__sushi_require_string_receiver "${1-}" "match")"
  local pattern
  pattern="$(__sushi_regex_to_ere "${2-}")"
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

""");

        AppendRuntimeBlock(_zshMode
            ? """
__sushi_native_object_seq=0
__sushi_native_obj_new() {
  __sushi_native_object_seq=$((__sushi_native_object_seq + 1))
  local name="__sushi_object_${__sushi_native_object_seq}" kinds_name="__sushi_object_${__sushi_native_object_seq}__kinds" key value
  typeset -gA "$name"; eval "$name=()"
  typeset -gA "$kinds_name"; eval "$kinds_name=()"
  while (( $# > 1 )); do
    key="$1"; value="$2"; shift 2
    eval "$name[${(q)key}]=${(q)value}"
    __sushi_infer_kind_into "$value"
    eval "$kinds_name[${(q)key}]=${(q)__sushi_kind}"
  done
  __sushi_result="@o:$name"
}
__sushi_native_obj_get_into() {
  local handle="${1-}" key="${2-}" name="${1#@o:}"
  [[ "$handle" == @o:__sushi_object_<-> ]] && (( ${+parameters[$name]} )) || { __sushi_result=''; return 0; }
  eval "__sushi_result=\"\${$name[${(q)key}]-}\""
}
__sushi_native_obj_has() {
  local handle="${1-}" key="${2-}" name="${1#@o:}"
  [[ "$handle" == @o:__sushi_object_<-> ]] && (( ${+parameters[$name]} )) || return 1
  eval "(( \${+$name[${(q)key}]} ))"
}
__sushi_native_obj_kind_into() {
  local handle="${1-}" key="${2-}" name="${1#@o:}__kinds"
  eval "__sushi_kind=\"\${$name[${(q)key}]-auto}\""
}
__sushi_native_obj_set_kind() {
  local handle="${1-}" key="${2-}" kind="${3-auto}" name="${1#@o:}__kinds"
  eval "$name[${(q)key}]=${(q)kind}"
}
__sushi_native_obj_each_kv() {
  local handle="${1-}" name="${1#@o:}" key value
  local -a keys; eval "keys=(\"\${(k)$name[@]}\")"
  for key in "${keys[@]}"; do
    eval "value=\"\${$name[${(q)key}]-}\""
    key="${key//$'\t'/ }"; key="${key//$'\n'/ }"
    value="${value//$'\t'/ }"; value="${value//$'\n'/ }"
    printf '%s\t%s\n' "$key" "$value"
  done
}
__sushi_native_obj_to_json_into() {
  local handle="${1-}" name="${1#@o:}" out='{' first=true key value kind
  [[ "$handle" == @o:__sushi_object_<-> ]] || { __sushi_result='{}'; return 0; }
  local -a keys; eval "keys=(\"\${(k)$name[@]}\")"; keys=(${(on)keys[@]})
  for key in "${keys[@]}"; do
    eval "value=\"\${$name[${(q)key}]-}\""
    __sushi_native_obj_kind_into "$handle" "$key"; kind="${__sushi_kind-auto}"
    [[ "$first" == true ]] || out+=','; first=false
    __sushi_json_quote_into "$key"; out+="${__sushi_result-}:"
    __sushi_value_to_json_kind_into "$value" "$kind"; out+="${__sushi_result-}"
  done
  __sushi_result="$out}"
}
__sushi_native_obj_to_json() {
  __sushi_native_obj_to_json_into "${1-}"
  printf '%s' "${__sushi_result-}"
}
"""
            : """
__sushi_native_object_seq=0
__sushi_native_obj_new() {
  __sushi_native_object_seq=$((__sushi_native_object_seq + 1))
  local name="__sushi_object_${__sushi_native_object_seq}" kinds_name="__sushi_object_${__sushi_native_object_seq}__kinds" key value
  declare -gA "$name"; declare -gA "$kinds_name"
  local -n values="$name" kinds="$kinds_name"
  values=(); kinds=()
  while (( $# > 1 )); do
    key="$1"; value="$2"; shift 2; values["$key"]="$value"
    __sushi_infer_kind_into "$value"; kinds["$key"]="$__sushi_kind"
  done
  __sushi_result="@o:$name"
}
__sushi_native_obj_get_into() {
  local handle="${1-}" key="${2-}" name="${1#@o:}"
  [[ "$handle" =~ ^@o:__sushi_object_[0-9]+$ ]] && declare -p "$name" >/dev/null 2>&1 || { __sushi_result=''; return 0; }
  local -n values="$name"; __sushi_result="${values["$key"]-}"
}
__sushi_native_obj_has() {
  local handle="${1-}" key="${2-}" name="${1#@o:}"
  [[ "$handle" =~ ^@o:__sushi_object_[0-9]+$ ]] && declare -p "$name" >/dev/null 2>&1 || return 1
  local -n values="$name"
  [[ -n "${values["$key"]+present}" ]]
}
__sushi_native_obj_kind_into() {
  local handle="${1-}" key="${2-}" name="${1#@o:}__kinds"
  local -n kinds="$name"
  __sushi_kind="${kinds["$key"]-auto}"
}
__sushi_native_obj_set_kind() {
  local handle="${1-}" key="${2-}" kind="${3-auto}" name="${1#@o:}__kinds"
  local -n kinds="$name"
  kinds["$key"]="$kind"
}
__sushi_native_obj_each_kv() {
  local handle="${1-}" name="${1#@o:}" key value
  local -n values="$name"
  for key in "${!values[@]}"; do
    value="${values["$key"]-}"
    key="${key//$'\t'/ }"; key="${key//$'\n'/ }"
    value="${value//$'\t'/ }"; value="${value//$'\n'/ }"
    printf '%s\t%s\n' "$key" "$value"
  done
}
__sushi_native_obj_to_json_into() {
  local handle="${1-}" name="${1#@o:}" kinds_name="${1#@o:}__kinds" out='{' first=true key kind
  [[ "$handle" =~ ^@o:__sushi_object_[0-9]+$ ]] || { __sushi_result='{}'; return 0; }
  local -n values="$name" kinds="$kinds_name"
  local -a keys=("${!values[@]}")
  local index cursor candidate
  local LC_ALL=C
  for (( index=1; index<${#keys[@]}; index++ )); do
    candidate="${keys[$index]}"
    cursor=$index
    while (( cursor > 0 )) && [[ "$candidate" < "${keys[$((cursor - 1))]}" ]]; do
      keys[$cursor]="${keys[$((cursor - 1))]}"
      cursor=$((cursor - 1))
    done
    keys[$cursor]="$candidate"
  done
  for key in "${keys[@]}"; do
    [[ "$first" == true ]] || out+=','; first=false
    kind="${kinds["$key"]-auto}"
    __sushi_json_quote_into "$key"; out+="${__sushi_result-}:"
    __sushi_value_to_json_kind_into "${values["$key"]-}" "$kind"; out+="${__sushi_result-}"
  done
  __sushi_result="$out}"
}
__sushi_native_obj_to_json() {
  __sushi_native_obj_to_json_into "${1-}"
  printf '%s' "${__sushi_result-}"
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

            // Native-class metadata is consumed by the PowerShell backend.  The
            // portable functions and object constructors which follow it remain
            // the Bash/Zsh representation.
            case IrClassDeclarationStatement:
                break;

            case IrEnumDeclarationStatement:
                break;

            case IrRichEnumDeclarationStatement:
                break;

            case IrVariableDeclarationStatement variable:
            {
                var enumType = "";
                var enumValue = "";
                var separator = variable.Name.LastIndexOf('_');
                var isEnumValue = !inFunction &&
                                  (IsEnumValueInitializer(variable.Initializer, out enumType, out enumValue) ||
                                   TryGetGeneratedEnumValue(variable.Initializer, out enumType, out enumValue));
                if (isEnumValue)
                {
                    if (separator > 0) enumType = variable.Name[..separator];
                    if (_commentedTypes.Add(enumType))
                    {
                        WriteLine("");
                        WriteLine($"# --- enum: {enumType} ---");
                    }
                    _indent++;
                }
                try
                {
                    if (isEnumValue)
                    {
                        WriteLine("");
                        if (string.IsNullOrEmpty(enumValue) && separator > 0)
                            enumValue = variable.Name[(separator + 1)..];
                        WriteLine($"# enum value: {enumType}.{enumValue}");
                    }
                    
                var initializer = variable.Initializer ?? new IrLiteralExpression(null);
                var name = SanitizeVariableName(variable.Name);
                if (initializer is IrConstructionExpression constructor)
                {
                    WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
                    var arguments = constructor.Arguments.Select(argument => PrepareValue(argument.Value, inFunction));
                    WriteLine($"{SanitizeFunctionName(constructor.ConstructorName)} {Escape.BashSingleQuoted(name)} {string.Join(" ", arguments)}");
                    _nativeObjectVariables.Add(name);
                    break;
                }
                if (TryGetObjectReturningCall(initializer, out var objectCall, out var objectFunction))
                {
                    WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
                    EmitCallInto(name, objectCall, objectFunction, inFunction);
                    _nativeObjectVariables.Add(name);
                    break;
                }
                if (TryGetReturningCall(initializer, out var valueCall, out var valueFunction))
                {
                    if (inFunction) WriteLine($"local {name}");
                    EmitCallInto(name, valueCall, valueFunction, inFunction);
                    if (valueFunction.ReturnType.Name == "int") _knownIntegerVariables.Add(name);
                    break;
                }
                if (initializer is IrIdentifierExpression objectAlias &&
                    _nativeObjectVariables.Contains(SanitizeVariableName(objectAlias.Name)))
                {
                    var source = ResolveNativeObjectName(SanitizeVariableName(objectAlias.Name));
                    if (_zshMode)
                    {
                        WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=( \"${{(@kv){source}}}\" )");
                        _nativeObjectAliases[name] = source;
                    }
                    else
                        WriteLine($"{(inFunction ? "local " : "declare ")}-n {name}={Escape.BashSingleQuoted(source)}");
                    _nativeObjectAliases[name] = source;
                    _nativeObjectVariables.Add(name);
                    break;
                }
                if (initializer is IrArrayLiteralExpression array)
                {
                    var values = array.Elements.Any(element => element is IrObjectLiteralExpression)
                        ? new List<string>()
                        : array.Elements.Select(element => PrepareValue(element, inFunction)).ToList();
                    WriteLine($"{(inFunction ? "local " : "declare ")}-a {name}=({string.Join(" ", values)})");
                    _nativeArrayVariables[name] = name;
                    _arrayInitializers[name] = array;
                    _nativeObjectVariables.Remove(name);
                    _recordVariables.Remove(name);
                    break;
                }

                if (initializer is IrObjectLiteralExpression obj)
                {
                    var properties = MetadataProperties(obj.Properties);
                    var entries = (_zshMode
                        ? properties.Select(property =>
                            $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
                        : properties.Select(property =>
                            $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")).ToList();
                    EmitAssociativeObject(name, entries, inFunction ? "local " : "declare ",
                        name == "this" && _currentFunctionName?.StartsWith("__sushi_new_", StringComparison.Ordinal) == true);
                    _nativeObjectVariables.Add(name);
                    _nativeArrayVariables.Remove(name);
                    _arrayInitializers.Remove(name);
                    _recordVariables.Remove(name);
                    break;
                }

                if (initializer is IrIntrinsicCallExpression intrinsic &&
                    EmitNativeIntrinsicDeclaration(name, intrinsic, inFunction))
                {
                    break;
                }

                if (IsBooleanValueExpression(initializer))
                {
                    EmitBooleanAssignment(name, initializer, inFunction);
                    SetKnownInteger(name, false);
                    break;
                }

                if (initializer is IrMethodCallExpression method &&
                    EmitNativeMethodDeclaration(name, method, inFunction))
                {
                    break;
                }

                if (initializer is IrIntrinsicCallExpression stringIntrinsic &&
                    IsInlineStringIntrinsic(stringIntrinsic.Id))
                {
                    EmitStringChain(name, stringIntrinsic, inFunction, declareResult: true);
                    break;
                }

                if (initializer is IrIntrinsicCallExpression directIntrinsic &&
                    directIntrinsic.Id is IntrinsicId.IoReadText or IntrinsicId.IoExists or IntrinsicId.PathJoin or IntrinsicId.PathDirname or IntrinsicId.PathBasename or IntrinsicId.EnvGet or IntrinsicId.OsCwd)
                {
                    WriteLine($"{(inFunction ? "local " : "")}{name}={EmitValueExpression(directIntrinsic)}");
                    break;
                }

                var value = PrepareValue(initializer, inFunction);
                WriteLine($"{(inFunction ? "local " : "")}{name}={value}");
                SetKnownInteger(name, IsDefinitelyInteger(initializer));
                SetKnownArray(name, initializer is IrArrayLiteralExpression);
                    break;
                }
                finally
                {
                    if (isEnumValue) _indent--;
                }
            }

            case IrExpressionStatement expressionStatement:
                EnsureTopLevelSection(inFunction);
                EmitExpressionStatement(expressionStatement.Expression, inFunction);
                break;

            case IrIfStatement ifStatement:
                EnsureTopLevelSection(inFunction);
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
                EmitGeneratedFunctionComment(function);
                var typeMemberIndent = IsTypeMemberFunction(function.Name);
                if (typeMemberIndent) _indent++;
                EmitFunctionDeclaration(function);
                if (typeMemberIndent) _indent--;
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
        var condition = PrepareCondition(statement.Condition, inFunction);
        WriteLine($"if {condition}; then");
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
        if (CanEmitDirectLoopCondition(statement.Condition))
        {
            var directCondition = PrepareCondition(statement.Condition, inFunction);
            WriteLine($"while {directCondition}; do");
            _indent++;
            EmitStatement(statement.Body, inFunction);
            _indent--;
            WriteLine("done");
            return;
        }

        WriteLine("while true; do");
        _indent++;
        var condition = PrepareCondition(statement.Condition, inFunction);
        WriteLine($"if ! {condition}; then");
        _indent++;
        WriteLine("break");
        _indent--;
        WriteLine("fi");
        EmitStatement(statement.Body, inFunction);
        _indent--;
        WriteLine("done");
    }

    private bool CanEmitDirectLoopCondition(IrExpression expression) => expression switch
    {
        IrLiteralExpression => true,
        IrIdentifierExpression => true,
        IrUnaryExpression { Operator: "!" } unary => CanEmitDirectLoopCondition(unary.Operand),
        IrBinaryExpression binary when binary.Operator is "<" or ">" or "<=" or ">=" =>
            CanEmitInlineInteger(binary.Left) && CanEmitInlineInteger(binary.Right),
        IrBinaryExpression binary when binary.Operator is "==" or "!=" =>
            binary.Left is IrLiteralExpression or IrIdentifierExpression &&
            binary.Right is IrLiteralExpression or IrIdentifierExpression,
        _ => false
    };

    private void EmitForStatement(IrForStatement statement, bool inFunction)
    {
        if (statement.Initializer != null)
        {
            EmitStatement(statement.Initializer, inFunction);
        }

        WriteLine("while true; do");
        _indent++;
        if (statement.Condition != null)
        {
            var condition = PrepareCondition(statement.Condition, inFunction);
            WriteLine($"if ! {condition}; then");
            _indent++;
            WriteLine("break");
            _indent--;
            WriteLine("fi");
        }
        EmitStatement(statement.Body, inFunction);

        if (statement.Increment != null)
        {
            EmitExpressionStatement(statement.Increment, inFunction);
        }

        _indent--;
        WriteLine("done");
    }

    private void EmitDoWhileStatement(IrDoWhileStatement statement, bool inFunction)
    {
        WriteLine("while true; do");
        _indent++;
        EmitStatement(statement.Body, inFunction);
        var condition = PrepareCondition(statement.Condition, inFunction);
        WriteLine($"if ! {condition}; then");
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
        var previousFunctionReturnsValue = _currentFunctionReturnsValue;
        var previousOutputName = _currentOutputName;
        var previousKnownIntegers = _knownIntegerVariables;
        var previousNativeArrays = _nativeArrayVariables;
        var previousNativeObjects = _nativeObjectVariables;
        var previousRecords = _recordVariables;
        var previousIntegerArrays = _integerArrayVariables;
        var previousZshObjectParameters = _zshObjectParameterNames;
        var previousZshReadOnlyParameters = _zshReadOnlyObjectParameters;
        var previousNativeObjectAliases = _nativeObjectAliases;
        _currentFunctionName = statement.Name;
        _currentFunctionReturnType = statement.ReturnType;
        _currentFunctionReturnsValue = FunctionReturnsValue(statement);
        _currentOutputName = _currentFunctionReturnsValue
            ? AllocateFunctionOutputName(statement)
            : "";
        _knownIntegerVariables = new HashSet<string>(StringComparer.Ordinal);
        _nativeArrayVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        _nativeObjectVariables = new HashSet<string>(StringComparer.Ordinal);
        _recordVariables = new HashSet<string>(StringComparer.Ordinal);
        _integerArrayVariables = new HashSet<string>(StringComparer.Ordinal);
        _zshObjectParameterNames = new Dictionary<string, string>(StringComparer.Ordinal);
        _zshReadOnlyObjectParameters = new HashSet<string>(StringComparer.Ordinal);
        _nativeObjectAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        _positionalParameterReferences = new Dictionary<string, int>(StringComparer.Ordinal);

        var isConstructor = statement.Name.StartsWith("__sushi_new_", StringComparison.Ordinal);
        var mutatesReceiver = FunctionMutatesReceiver(statement.Body);
        var returnsObject = !isConstructor && IsNamedObjectType(statement.ReturnType);
        var argIndex = _currentFunctionReturnsValue ? 2 : 1;
        if (isConstructor)
        {
            if (_zshMode)
            {
                WriteLine("local this_name=\"$1\"");
                WriteLine("local -A this=()");
            }
            else
            {
                WriteLine("local -n this=\"$1\"");
            }
            _nativeObjectVariables.Add("this");
        }
        else if (returnsObject)
        {
            if (_zshMode)
                WriteLine($"local {_currentOutputName}=\"$1\"");
            else
                WriteLine($"local -n {_currentOutputName}=\"$1\"");
        }
        else if (_currentFunctionReturnsValue)
        {
            WriteLine(_zshMode
                ? $"local {_currentOutputName}=\"$1\""
                : $"local -n {_currentOutputName}=\"$1\"");
        }
        foreach (var parameter in statement.Parameters)
        {
            var param = SanitizeVariableName(parameter.Name);
            if (parameter.Name.StartsWith("__sushi_enum_field_", StringComparison.Ordinal))
            {
                _positionalParameterReferences[param] = argIndex;
                argIndex++;
                continue;
            }
            if (parameter.IsVarargs)
            {
                WriteLine($"local -a {param}=(\"${{@:{argIndex}}}\")");
                _nativeArrayVariables[param] = param;
                if (parameter.DeclaredType.Name == "int") _integerArrayVariables.Add(param);
            }
            else
            {
                if (parameter.DeclaredType.Kind == IrTypeKind.Structural)
                {
                    foreach (var field in parameter.DeclaredType.StructuralFields)
                    {
                        var fieldName = $"{param}_{SanitizeVariableName(field.Name)}";
                        var prefix = field.Type.Name == "int" ? "local -i " : "local ";
                        WriteLine($"{prefix}{fieldName}=\"${argIndex}\"");
                        argIndex++;
                    }
                    _recordVariables.Add(param);
                    continue;
                }
                else if (parameter.DeclaredType.Name == "array" || IsNativeObjectType(parameter.DeclaredType))
                {
                    if (_zshMode)
                    {
                        var referenceName = $"{param}_name";
                        WriteLine($"local {referenceName}=\"${argIndex}\"");
                        _zshObjectParameterNames[param] = referenceName;
                        if (parameter.Name == "this" && !mutatesReceiver)
                            _zshReadOnlyObjectParameters.Add(param);
                        else
                            WriteLine($"local -A {param}=( \"${{(@kvP){referenceName}}}\" )");
                    }
                    else
                    {
                        WriteLine($"local -n {param}=\"${argIndex}\"");
                    }
                    if (parameter.DeclaredType.Name == "array") _nativeArrayVariables[param] = param;
                    else if (!_zshReadOnlyObjectParameters.Contains(param)) _nativeObjectVariables.Add(param);
                }
                else if (parameter.DeclaredType.Name == "int")
                {
                    WriteLine($"local -i {param}=\"${argIndex}\"");
                }
                else
                {
                    WriteLine($"local {param}=\"${argIndex}\"");
                }
                argIndex++;
                EmitContractCheckForValue(
                    parameter.DeclaredType,
                    $"\"${{{param}:-}}\"",
                    $"parameter '{parameter.Name}' of function '{statement.Name}'");
            }

            if (parameter.DeclaredType.Kind == IrTypeKind.Primitive &&
                parameter.DeclaredType.Name?.Equals("int", StringComparison.OrdinalIgnoreCase) == true)
            {
                _knownIntegerVariables.Add(param);
            }
        }

        EmitStatement(statement.Body, inFunction: true);
        if (!EndsWithReturn(statement.Body))
        {
            EmitZshObjectParameterWritebacks();
            if (_currentFunctionReturnsValue && !isConstructor)
                EmitFunctionOutputAssignment("''");
            WriteLine("return 0");
        }
        _currentFunctionName = previousFunctionName;
        _currentFunctionReturnType = previousReturnType;
        _currentFunctionReturnsValue = previousFunctionReturnsValue;
        _currentOutputName = previousOutputName;
        _knownIntegerVariables = previousKnownIntegers;
        _nativeArrayVariables = previousNativeArrays;
        _nativeObjectVariables = previousNativeObjects;
        _recordVariables = previousRecords;
        _integerArrayVariables = previousIntegerArrays;
        _zshObjectParameterNames = previousZshObjectParameters;
        _zshReadOnlyObjectParameters = previousZshReadOnlyParameters;
        _nativeObjectAliases = previousNativeObjectAliases;
        _indent--;
        WriteLine("}");
    }

    private void EmitGeneratedFunctionComment(IrFunctionDeclarationStatement function)
    {
        var name = function.Name;
        var isTypeMember = IsTypeMemberFunction(name);
        var parameters = string.Join(", ", function.Parameters
            .Where(parameter => parameter.Name != "this" &&
                                !parameter.Name.StartsWith("__sushi_enum_field_", StringComparison.Ordinal))
            .Select(parameter => parameter.IsVarargs ? $"{parameter.Name}..." : parameter.Name));
        var signature = $"{name}({parameters})";
        var label = name switch
        {
            var value when value.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal) =>
                $"method: {value[NativeObjectMetadata.MethodPrefix.Length..].Replace('_', '.')}({parameters})",
            var value when value.StartsWith("__sushi_new_", StringComparison.Ordinal) =>
                $"constructor: {value["__sushi_new_".Length..]}({parameters})",
            var value when value.StartsWith("__sushi_adapter_", StringComparison.Ordinal) =>
                $"adapter: {value["__sushi_adapter_".Length..].Replace('_', '.')}({parameters})",
            _ => $"function: {signature}"
        };
        if (isTypeMember)
        {
            var type = name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal)
                ? name[NativeObjectMetadata.MethodPrefix.Length..].Split('_')[0]
                : name.StartsWith("__sushi_new_", StringComparison.Ordinal)
                    ? name["__sushi_new_".Length..].Split('_')[0]
                    : name["__sushi_adapter_".Length..].Split('_')[0];
            if (_commentedTypes.Add(type))
            {
                WriteLine("");
                var kind = _enumTypeNames.Contains(type) ? "enum" : "class";
                WriteLine($"# --- {kind}: {type} ---");
            }
        }
        WriteLine("");
        if (isTypeMember)
        {
            _indent++;
            WriteLine($"# {label}");
            _indent--;
        }
        else
        {
            WriteLine($"# {label}");
        }
    }

    private static bool IsTypeMemberFunction(string name) =>
        name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal) ||
        name.StartsWith("__sushi_new_", StringComparison.Ordinal) ||
        name.StartsWith("__sushi_adapter_", StringComparison.Ordinal);

    private static HashSet<string> CollectClassTypeNames(IEnumerable<IrStatement> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            if (statement is IrClassDeclarationStatement declaration) names.Add(declaration.Name);
            if (statement is IrBlockStatement block) names.UnionWith(CollectClassTypeNames(block.Statements));
        }
        return names;
    }

    private static HashSet<string> CollectEnumTypeNames(IEnumerable<IrStatement> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case IrEnumDeclarationStatement declaration:
                    names.Add(declaration.Name);
                    break;
                case IrRichEnumDeclarationStatement declaration:
                    names.Add(declaration.Name);
                    break;
                case IrBlockStatement block:
                    names.UnionWith(CollectEnumTypeNames(block.Statements));
                    break;
            }
        }
        return names;
    }

    private void EnsureTopLevelSection(bool inFunction)
    {
        if (inFunction || _emittedTopLevelSection) return;
        WriteLine("");
        WriteLine("# --- script body ---");
        _emittedTopLevelSection = true;
    }

    private static bool IsEnumValueInitializer(IrExpression? initializer, out string type, out string value)
    {
        if (initializer is IrObjectLiteralExpression objectLiteral)
        {
            var name = objectLiteral.Properties.FirstOrDefault(property => property.Name == NativeObjectMetadata.EnumName)?.Value;
            if (name is IrLiteralExpression { Value: string enumValue })
            {
                type = "enum";
                value = enumValue;
                return true;
            }
        }
        type = "";
        value = "";
        return false;
    }

    private bool TryGetGeneratedEnumValue(IrExpression? initializer, out string type, out string value)
    {
        if (initializer is IrConstructionExpression construction &&
            construction.ConstructorName.StartsWith("__sushi_new_", StringComparison.Ordinal))
        {
            var suffix = construction.ConstructorName["__sushi_new_".Length..];
            var enumType = _enumTypeNames
                .Where(candidate => suffix.StartsWith(candidate + "_", StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Length)
                .FirstOrDefault();
            if (enumType != null)
            {
                type = enumType;
                value = suffix[(enumType.Length + 1)..];
                return true;
            }
            enumType = _enumTypeNames.FirstOrDefault(candidate =>
                string.Equals(suffix, candidate, StringComparison.Ordinal));
            if (enumType != null)
            {
                type = enumType;
                value = "";
                return true;
            }
        }
        type = "";
        value = "";
        return false;
    }

    private static bool EndsWithReturn(IrBlockStatement block) =>
        block.Statements.LastOrDefault() is IrReturnStatement;

    private static bool FunctionReturnsValue(IrFunctionDeclarationStatement function) =>
        function.Name.StartsWith("__sushi_new_", StringComparison.Ordinal) ||
        IsNamedObjectType(function.ReturnType) ||
        ContainsValueReturn(function.Body);

    private static bool ContainsValueReturn(IrStatement statement) => statement switch
    {
        IrReturnStatement { Expression: not null } => true,
        IrBlockStatement block => block.Statements.Any(ContainsValueReturn),
        IrIfStatement conditional => ContainsValueReturn(conditional.ThenBlock) ||
                                   (conditional.ElseBlock != null && ContainsValueReturn(conditional.ElseBlock)),
        _ => false
    };

    private string AllocateFunctionOutputName(IrFunctionDeclarationStatement function)
    {
        var parameterNames = function.Parameters
            .Select(parameter => SanitizeVariableName(parameter.Name))
            .ToHashSet(StringComparer.Ordinal);
        if (!parameterNames.Contains("out")) return "out";

        var suffix = 2;
        while (parameterNames.Contains($"out_{suffix}")) suffix++;
        return $"out_{suffix}";
    }

    private void EmitReturn(IrReturnStatement statement, bool inFunction)
    {
        if (inFunction && _currentFunctionName?.StartsWith("__sushi_new_", StringComparison.Ordinal) == true)
        {
            if (_zshMode)
            {
                WriteLine("typeset -gA $this_name");
                WriteLine("set -A $this_name \"${(@kv)this}\"");
            }
            return;
        }
        if (inFunction) EmitZshObjectParameterWritebacks();
        if (inFunction && IsNamedObjectType(_currentFunctionReturnType) && statement.Expression != null)
        {
            var source = statement.Expression switch
            {
                IrIdentifierExpression returnedObject
                    when _nativeObjectVariables.Contains(SanitizeVariableName(returnedObject.Name)) =>
                    ResolveNativeObjectName(SanitizeVariableName(returnedObject.Name)),
                IrConstructionExpression construction => PrepareConstructionReference(construction, inFunction),
                _ => ""
            };
            if (source.Length > 0)
            {
                if (_zshMode)
                {
                    WriteLine($"typeset -gA ${{{_currentOutputName}}}");
                    WriteLine($"set -A ${{{_currentOutputName}}} \"${{(@kv){source}}}\"");
                }
                else
                {
                    WriteLine($"{_currentOutputName}=()");
                    WriteLine($"for _key in \"${{!{source}[@]}}\"; do {_currentOutputName}[\"$_key\"]=\"${{{source}[$_key]}}\"; done");
                }
                return;
            }
        }
        if (statement.Expression != null)
        {
            if (inFunction && IsBooleanValueExpression(statement.Expression))
            {
                EmitBooleanOutput(statement.Expression);
                return;
            }
                var value = PrepareValue(statement.Expression, inFunction);
            if (inFunction && _currentFunctionName != null && !_currentFunctionReturnType.IsAnyOrUnknown)
            {
                EmitFunctionOutputAssignment(value);
                EmitContractCheckForValue(
                    _currentFunctionReturnType,
                    value,
                    $"return value of function '{_currentFunctionName}'");
            }
            else if (inFunction)
            {
                EmitFunctionOutputAssignment(value);
            }
            else
            {
                WriteLine($"printf '%s\\n' {value}");
            }
        }
        else if (inFunction)
        {
            if (_currentFunctionReturnsValue) EmitFunctionOutputAssignment("''");
        }

        if (!inFunction) WriteLine("exit 0");
    }

    private void EmitExpressionStatement(IrExpression expression, bool inFunction)
    {
        switch (expression)
        {
            case IrIntrinsicCallExpression intrinsicCall:
                if (intrinsicCall.Id is IntrinsicId.Print or IntrinsicId.Println)
                {
                    if (intrinsicCall.Arguments.FirstOrDefault() is IrIntrinsicCallExpression stringPredicate &&
                        stringPredicate.Id is IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or IntrinsicId.StringIsMatch)
                    {
                        EmitPrintedStringPredicate(stringPredicate, intrinsicCall.Id == IntrinsicId.Println, inFunction);
                        return;
                    }

                    if (intrinsicCall.Arguments.FirstOrDefault() is IrCallExpression directCall &&
                        directCall.Arguments.All(argument => argument.Value is IrLiteralExpression or IrIdentifierExpression))
                    {
                        var directValue = PrepareValue(directCall, inFunction);
                        WriteLine(intrinsicCall.Id == IntrinsicId.Println
                            ? $"printf '%s\\n' {directValue}"
                            : $"printf '%s' {directValue}");
                        return;
                    }

                    if (intrinsicCall.Arguments.FirstOrDefault() is { } booleanValue &&
                        IsBooleanValueExpression(booleanValue))
                    {
                        var condition = PrepareCondition(booleanValue, inFunction);
                        var suffix = intrinsicCall.Id == IntrinsicId.Println ? "\\n" : string.Empty;
                        WriteLine($"{condition} && printf '%s{suffix}' 'true' || printf '%s{suffix}' 'false'");
                        return;
                    }

                    var value = intrinsicCall.Arguments.Count == 0
                        ? "''"
                        : PrepareValue(intrinsicCall.Arguments[0], inFunction);
                    WriteLine(intrinsicCall.Id == IntrinsicId.Println
                        ? $"printf '%s\\n' {value}"
                        : $"printf '%s' {value}");
                }
                else
                {
                    WriteLine(EmitIntrinsicCommand(intrinsicCall));
                }
                return;

            case IrCallExpression call:
                _ = PrepareValue(call, inFunction);
                return;

            case IrResolvedMethodCallExpression method:
                _ = PrepareValue(method, inFunction);
                return;

            case IrAdapterCallExpression adapter:
                _ = PrepareValue(adapter, inFunction);
                return;

            case IrAssignmentExpression assignment:
                EmitPreparedAssignment(assignment, inFunction);
                return;

            case IrMemberAssignmentExpression assignment:
                EmitMemberAssignment(assignment, inFunction);
                return;

            case IrUnaryExpression unary when unary.Operator is "++" or "--":
                if (unary.Operand is IrIdentifierExpression identifier)
                {
                    var op = unary.Operator == "++" ? "+" : "-";
                    var name = SanitizeVariableName(identifier.Name);
                    WriteLine($"{name}=$(( {name} {op} 1 ))");
                    _knownIntegerVariables.Add(name);
                    return;
                }
                break;

            case IrMethodCallExpression methodCall:
                if (methodCall.MethodName == "push" && methodCall.Target is IrIdentifierExpression targetIdentifier)
                {
                    var targetName = SanitizeVariableName(targetIdentifier.Name);
                    if (_nativeArrayVariables.TryGetValue(targetName, out var nativeArray))
                    {
                        var values = methodCall.Arguments.Select(argument => PrepareValue(argument.Value, inFunction));
                        WriteLine($"{nativeArray}+=({string.Join(" ", values)})");
                        return;
                    }
                    var value = PrepareValue(methodCall, inFunction);
                    WriteLine($"{targetName}={value}");
                    return;
                }

                _ = PrepareValue(methodCall, inFunction);
                return;
        }

        _context.Error(UnsupportedEmitCode, $"Unsupported expression statement in Bash emitter: {expression.GetType().Name}");
    }

    private void EmitPrintedStringPredicate(IrIntrinsicCallExpression predicate, bool newline, bool inFunction)
    {
        var value = PrepareValue(predicate.Arguments[0], inFunction);
        var test = predicate.Id switch
        {
            IntrinsicId.StringContains => $"{value} == *{PrepareValue(predicate.Arguments[1], inFunction)}*",
            IntrinsicId.StringStartsWith => $"{value} == {PrepareValue(predicate.Arguments[1], inFunction)}*",
            IntrinsicId.StringEndsWith => $"{value} == *{PrepareValue(predicate.Arguments[1], inFunction)}",
            IntrinsicId.StringIsMatch => $"{value} =~ {PrepareRegex(predicate.Arguments[1], inFunction)}",
            _ => throw new InvalidOperationException($"Unexpected string predicate '{predicate.Id}'.")
        };
        var suffix = newline ? "\\n" : string.Empty;
        WriteLine($"if [[ {test} ]]; then printf '%s{suffix}' 'true'; else printf '%s{suffix}' 'false'; fi");
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

    private void EmitPreparedAssignment(IrAssignmentExpression assignment, bool inFunction)
    {
        var name = SanitizeVariableName(assignment.Target.Name);
        if (assignment.Operator == "=")
        {
            if (IsBooleanValueExpression(assignment.Value))
            {
                EmitBooleanAssignment(name, assignment.Value, inFunction);
                SetKnownInteger(name, false);
                return;
            }
            if (assignment.Value is IrIdentifierExpression objectAlias &&
                _nativeObjectVariables.Contains(SanitizeVariableName(objectAlias.Name)))
            {
                var source = ResolveNativeObjectName(SanitizeVariableName(objectAlias.Name));
                if (_zshMode)
                {
                    WriteLine($"{name}=( \"${{(@kv){source}}}\" )");
                    _nativeObjectAliases[name] = source;
                }
                else
                {
                    WriteLine(_nativeObjectAliases.ContainsKey(name) ? $"unset -n {name}" : $"unset {name}");
                    WriteLine($"{(inFunction ? "local " : "declare ")}-n {name}={Escape.BashSingleQuoted(source)}");
                    _nativeObjectAliases[name] = source;
                }
                _nativeObjectVariables.Add(name);
                return;
            }
            if (TryGetObjectReturningCall(assignment.Value, out var objectCall, out var objectFunction))
            {
                if (!_nativeObjectVariables.Contains(name))
                    WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
                EmitCallInto(name, objectCall, objectFunction, inFunction);
                _nativeObjectVariables.Add(name);
                return;
            }
            if (TryGetReturningCall(assignment.Value, out var valueCall, out var valueFunction))
            {
                EmitCallInto(name, valueCall, valueFunction, inFunction);
                SetKnownInteger(name, valueFunction.ReturnType.Name == "int");
                return;
            }
            if (assignment.Value is IrArrayLiteralExpression array)
            {
                var values = array.Elements.Any(element => element is IrObjectLiteralExpression)
                    ? new List<string>()
                    : array.Elements.Select(element => PrepareValue(element, inFunction)).ToList();
                WriteLine($"{name}=({string.Join(" ", values)})");
                _nativeArrayVariables[name] = name;
                _arrayInitializers[name] = array;
                _nativeObjectVariables.Remove(name);
                _recordVariables.Remove(name);
                return;
            }

            if (assignment.Value is IrObjectLiteralExpression obj)
            {
                var entries = _zshMode
                    ? obj.Properties.Select(property =>
                        $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
                    : obj.Properties.Select(property =>
                        $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}");
                WriteLine($"{name}=({string.Join(" ", entries)})");
                _nativeObjectVariables.Add(name);
                _nativeArrayVariables.Remove(name);
                _arrayInitializers.Remove(name);
                _recordVariables.Remove(name);
                return;
            }

            if (assignment.Value is IrIntrinsicCallExpression intrinsic &&
                EmitNativeIntrinsicDeclaration(name, intrinsic, inFunction))
            {
                return;
            }
        }

        var value = PrepareValue(assignment.Value, inFunction);
        if (assignment.Operator == "=")
        {
            WriteLine($"{name}={value}");
            SetKnownInteger(name, IsDefinitelyInteger(assignment.Value));
            SetKnownArray(name, assignment.Value is IrArrayLiteralExpression);
            return;
        }

        var right = DeclareTemp(value, inFunction);
        WriteLine($"{name}=$(( {name} {assignment.Operator[0]} {right} ))");
        _knownIntegerVariables.Add(name);
    }

    private void EmitMemberAssignment(IrMemberAssignmentExpression assignment, bool inFunction)
    {
        if (assignment.Target is IrIdentifierExpression identifier)
        {
            var target = ResolveNativeObjectName(SanitizeVariableName(identifier.Name));
            var member = EmitObjectSubscript(assignment.MemberName);
            if (assignment.Operator == "=")
                WriteLine($"{target}[{member}]={PrepareValue(assignment.Value, inFunction)}");
            else
                WriteLine($"{target}[{member}]=$(( ${{{target}[{member}]:-0}} {assignment.Operator[0]} {EmitArithmeticExpression(assignment.Value)} ))");
            return;
        }

        _context.Error(AmbiguousShapeCode, "Member assignment requires a statically known native object.");
    }

    private void EmitCallInto(
        string destination,
        IrCallExpression call,
        IrFunctionDeclarationStatement function,
        bool inFunction)
    {
        var arguments = new List<string> { Escape.BashSingleQuoted(destination) };
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            var argument = call.Arguments[index].Value;
            var parameter = index < function.Parameters.Count ? function.Parameters[index] : null;
            if (argument is IrObjectLiteralExpression objectLiteral &&
                parameter?.DeclaredType.Kind == IrTypeKind.Structural)
            {
                foreach (var field in parameter.DeclaredType.StructuralFields)
                {
                    var property = objectLiteral.Properties.FirstOrDefault(item => item.Name == field.Name);
                    arguments.Add(property == null ? "''" : PrepareValue(property.Value, inFunction));
                }
            }
            else if (argument is IrIdentifierExpression structuralIdentifier &&
                     parameter?.DeclaredType.Kind == IrTypeKind.Structural)
            {
                var aggregateName = SanitizeVariableName(structuralIdentifier.Name);
                foreach (var field in parameter.DeclaredType.StructuralFields)
                {
                    arguments.Add(_nativeObjectVariables.Contains(aggregateName)
                        ? $"\"${{{aggregateName}[{EmitObjectSubscript(field.Name)}]-}}\""
                        : $"\"${{{aggregateName}_{SanitizeVariableName(field.Name)}-}}\"");
                }
            }
            else if (argument is IrIdentifierExpression identifier && parameter != null && IsNativeObjectType(parameter.DeclaredType))
                arguments.Add(Escape.BashSingleQuoted(ResolveNativeObjectName(SanitizeVariableName(identifier.Name))));
            else if (argument is IrConstructionExpression construction && parameter != null && IsNativeObjectType(parameter.DeclaredType))
                arguments.Add(Escape.BashSingleQuoted(PrepareConstructionReference(construction, inFunction)));
            else
                arguments.Add(PrepareValue(argument, inFunction));
        }
        WriteLine($"{SanitizeFunctionName(call.Callee)} {string.Join(" ", arguments)}");
    }

    private bool TryGetObjectReturningCall(
        IrExpression expression,
        out IrCallExpression call,
        out IrFunctionDeclarationStatement function)
    {
        call = expression switch
        {
            IrCallExpression direct => direct,
            IrResolvedMethodCallExpression method => method.AsFunctionCall(),
            IrAdapterCallExpression adapter => adapter.AsFunctionCall(),
            _ => null!
        };
        if (call != null && _functions.TryGetValue(call.Callee, out function!) && IsNamedObjectType(function.ReturnType))
            return true;
        function = null!;
        return false;
    }

    private bool TryGetReturningCall(
        IrExpression expression,
        out IrCallExpression call,
        out IrFunctionDeclarationStatement function)
    {
        call = expression switch
        {
            IrCallExpression direct => direct,
            IrResolvedMethodCallExpression method => method.AsFunctionCall(),
            IrAdapterCallExpression adapter => adapter.AsFunctionCall(),
            _ => null!
        };
        if (call != null && _functions.TryGetValue(call.Callee, out function!) && FunctionReturnsValue(function))
            return true;
        function = null!;
        return false;
    }

    private string PrepareConstructionReference(IrConstructionExpression construction, bool inFunction)
    {
        var name = _names.Generated(TargetNameKind.Variable, "_object" + (++_valueTempId));
        WriteLine($"{(inFunction ? "local " : "declare ")}-A {name}=()");
        var arguments = construction.Arguments.Select(argument => PrepareValue(argument.Value, inFunction));
        WriteLine($"{SanitizeFunctionName(construction.ConstructorName)} {Escape.BashSingleQuoted(name)} {string.Join(" ", arguments)}");
        _nativeObjectVariables.Add(name);
        return name;
    }

    private void EmitZshObjectParameterWritebacks()
    {
        if (!_zshMode) return;
        foreach (var item in _zshObjectParameterNames)
        {
            if (_zshReadOnlyObjectParameters.Contains(item.Key)) continue;
            WriteLine($"typeset -gA ${{{item.Value}}}");
            WriteLine($"set -A ${{{item.Value}}} \"${{(@kv){item.Key}}}\"");
        }
    }

    private static bool FunctionMutatesReceiver(IrStatement statement) => statement switch
    {
        IrExpressionStatement { Expression: IrMemberAssignmentExpression { Target: IrIdentifierExpression { Name: "this" } } } => true,
        IrBlockStatement block => block.Statements.Any(FunctionMutatesReceiver),
        IrIfStatement conditional => FunctionMutatesReceiver(conditional.ThenBlock) ||
                                     (conditional.ElseBlock != null && FunctionMutatesReceiver(conditional.ElseBlock)),
        IrWhileStatement loop => FunctionMutatesReceiver(loop.Body),
        IrForStatement loop => FunctionMutatesReceiver(loop.Body),
        IrDoWhileStatement loop => FunctionMutatesReceiver(loop.Body),
        _ => false
    };

    private void EmitFunctionOutputAssignment(string value)
    {
        if (_zshMode)
        {
            WriteLine($": ${{(P){_currentOutputName}::={value}}}");
            return;
        }

        WriteLine($"{_currentOutputName}={value}");
    }

    private static bool IsNamedObjectType(IrTypeRef type) =>
        type.Kind == IrTypeKind.Primitive && type.Name != null &&
        type.Name.ToLowerInvariant() is not ("string" or "int" or "float" or "bool" or "array" or "object" or "any");

    private static bool IsNativeObjectType(IrTypeRef type) =>
        type.Kind == IrTypeKind.Structural || type.Name == "object" || IsNamedObjectType(type);

    private string ResolveNativeObjectName(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (_nativeObjectAliases.TryGetValue(name, out var target) && seen.Add(name)) name = target;
        return name;
    }

    private bool EmitNativeIntrinsicDeclaration(
        string name,
        IrIntrinsicCallExpression intrinsic,
        bool inFunction)
    {
        var declaration = inFunction ? "local " : "";
        var arrayDeclaration = inFunction ? "local " : "declare ";
        switch (intrinsic.Id)
        {
            case IntrinsicId.FsGlob:
            {
                var pattern = PrepareValue(intrinsic.Arguments[0], inFunction);
                var cwd = intrinsic.Arguments.Count > 1 ? intrinsic.Arguments[1] : new IrLiteralExpression(null);
                var root = PrepareValue(cwd, inFunction);
                WriteLine($"__sushi_fs_glob_into {pattern} {root}");
                WriteLine($"{arrayDeclaration}-a {name}=()");
                WriteLine($"while IFS= read -r __sushi_path; do {name}+=(\"$__sushi_path\"); done < <(__sushi_array_each_raw \"${{__sushi_result-}}\")");
                _nativeArrayVariables[name] = name;
                _arrayInitializers.Remove(name);
                return true;
            }

            case IntrinsicId.ProcessArgs:
                WriteLine($"{arrayDeclaration}-a {name}=(\"$@\")");
                _nativeArrayVariables[name] = name;
                _arrayInitializers.Remove(name);
                return true;

            case IntrinsicId.StringMatch:
            {
                var value = PrepareValue(intrinsic.Arguments[0], inFunction);
                var pattern = PrepareRegex(intrinsic.Arguments[1], inFunction);
                WriteLine($"{declaration}{name}_ok='false'");
                WriteLine($"{declaration}{name}_value=''");
                WriteLine($"{declaration}{name}_index=-1");
                WriteLine($"{arrayDeclaration}-a {name}_groups=()");
                WriteLine($"if [[ {value} =~ {pattern} ]]; then");
                _indent++;
                WriteLine($"{name}_ok='true'");
                WriteLine($"{name}_value=\"${{BASH_REMATCH[0]-}}\"");
                WriteLine($"{name}_groups=(\"${{BASH_REMATCH[@]}}\")");
                _indent--;
                WriteLine("fi");
                _recordVariables.Add(name);
                return true;
            }

            case IntrinsicId.StringSplit:
            {
                var value = PrepareValue(intrinsic.Arguments[0], inFunction);
                var separator = PrepareValue(intrinsic.Arguments[1], inFunction);
                WriteLine($"{arrayDeclaration}-a {name}=()");
                if (_zshMode)
                {
                    WriteLine($"{name}=(${{(s:{separator}:)${{:-{value}}}}})");
                }
                else
                {
                    WriteLine($"IFS={separator} read -r -a {name} <<< {value}");
                }
                _nativeArrayVariables[name] = name;
                return true;
            }

            case IntrinsicId.ProcessRun:
                EmitNativeProcessRun(name, intrinsic.Arguments, inFunction);
                _recordVariables.Add(name);
                return true;

            case IntrinsicId.ProcessPipeline:
                EmitNativeProcessPipeline(name, intrinsic.Arguments, inFunction);
                _recordVariables.Add(name);
                return true;

            case IntrinsicId.HttpGet:
            case IntrinsicId.HttpPost:
                EmitNativeHttp(name, intrinsic, inFunction);
                _recordVariables.Add(name);
                return true;

            default:
                return false;
        }
    }

    private bool EmitNativeMethodDeclaration(string name, IrMethodCallExpression method, bool inFunction)
    {
        if (method.Target is not IrIdentifierExpression identifier ||
            !_nativeArrayVariables.TryGetValue(SanitizeVariableName(identifier.Name), out var source))
        {
            return false;
        }
        var declaration = inFunction ? "local " : "declare ";
        switch (method.MethodName)
        {
            case "push":
                WriteLine($"{declaration}-a {name}=(\"${{{source}[@]}}\" {string.Join(" ", method.Arguments.Select(a => PrepareValue(a.Value, inFunction)))})");
                _nativeArrayVariables[name] = name;
                return true;
            case "map" when method.Arguments.Count == 1:
            case "filter" when method.Arguments.Count == 1:
            {
                var callback = PrepareValue(method.Arguments[0].Value, inFunction);
                WriteLine($"{declaration}-a {name}=()");
                var callbackResult = _names.Generated(TargetNameKind.Variable, "_callback");
                WriteLine($"{declaration}{callbackResult}=''");
                WriteLine($"for _item in \"${{{source}[@]}}\"; do");
                _indent++;
                WriteLine($"{callback} {callbackResult} \"$_item\"");
                if (method.MethodName == "map")
                {
                    WriteLine($"{name}+=(\"${{{callbackResult}-}}\")");
                }
                else
                {
                    WriteLine($"[[ -n \"${{{callbackResult}-}}\" && \"${{{callbackResult}-}}\" != false ]] && {name}+=(\"$_item\")");
                }
                _indent--;
                WriteLine("done");
                _nativeArrayVariables[name] = name;
                return true;
            }
            case "reduce" when method.Arguments.Count >= 1:
            {
                var callback = PrepareValue(method.Arguments[0].Value, inFunction);
                var seed = method.Arguments.Count > 1 ? PrepareValue(method.Arguments[1].Value, inFunction) : $"\"${{{source}[0]-}}\"";
                var start = method.Arguments.Count > 1 ? 0 : 1;
                WriteLine($"{declaration}{name}={seed}");
                var callbackResult = _names.Generated(TargetNameKind.Variable, "_callback");
                WriteLine($"{declaration}{callbackResult}=''");
                WriteLine($"for ((_i={start}; _i<${{#{source}[@]}}; _i++)); do");
                _indent++;
                WriteLine($"{callback} {callbackResult} \"${{{name}-}}\" \"${{{source}[_i]}}\"");
                WriteLine($"{name}=\"${{{callbackResult}-}}\"");
                _indent--;
                WriteLine("done");
                return true;
            }
            default:
                return false;
        }
    }

    private string PrepareRegex(IrExpression expression, bool inFunction)
    {
        var pattern = expression is IrLiteralExpression { Value: string literal }
            ? Escape.BashSingleQuoted(literal
                .Replace("\\d", "[0-9]", StringComparison.Ordinal)
                .Replace("\\s", "[[:space:]]", StringComparison.Ordinal)
                .Replace("\\w", "[[:alnum:]_]", StringComparison.Ordinal))
            : PrepareValue(expression, inFunction);
        var name = $"__sushi_regex_{++_valueTempId}";
        WriteLine($"{(inFunction ? "local " : "")}{name}={pattern}");
        // Bash/Zsh interpret a quoted RHS of =~ literally; expand a temporary unquoted instead.
        return $"${{{name}-}}";
    }

    private void EmitNativeProcessRun(string name, IReadOnlyList<IrExpression> arguments, bool inFunction)
    {
        var declaration = inFunction ? "local " : "";
        var command = PrepareValue(arguments[0], inFunction);
        var commandParts = new List<string> { command };
        if (arguments.Count > 1 && arguments[1] is IrArrayLiteralExpression args)
        {
            commandParts.AddRange(args.Elements.Select(arg => PrepareValue(arg, inFunction)));
        }
        else if (arguments.Count > 1 && arguments[1] is IrIdentifierExpression argsIdentifier &&
                 _nativeArrayVariables.TryGetValue(SanitizeVariableName(argsIdentifier.Name), out var argsName))
        {
            commandParts.Add($"\"${{{argsName}[@]}}\"");
        }

        var invocation = string.Join(" ", commandParts);
        if (arguments.Count > 5 && arguments[5] is IrLiteralExpression { Value: int timeoutMs } && timeoutMs > 0)
        {
            invocation = $"timeout {Escape.BashSingleQuoted((timeoutMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + "s")} {invocation}";
        }
        if (arguments.Count > 2 && arguments[2] is not IrLiteralExpression { Value: null })
        {
            invocation = $"(cd -- {PrepareValue(arguments[2], inFunction)} && {invocation})";
        }
        if (arguments.Count > 4 && arguments[4] is not IrLiteralExpression { Value: null })
        {
            invocation += $" <<< {PrepareValue(arguments[4], inFunction)}";
        }

        WriteLine($"{declaration}{name}_stderr_file=$(mktemp)");
        WriteLine($"if {name}_stdout=$({invocation} 2>\"${{{name}_stderr_file}}\"); then {name}_code=0; else {name}_code=$?; fi");
        WriteLine($"{declaration}{name}_stderr=$(<\"${{{name}_stderr_file}}\")");
        WriteLine($"rm -f -- \"${{{name}_stderr_file}}\"");
        WriteLine($"{declaration}{name}_ok=$([[ ${{{name}_code}} -eq 0 ]] && printf true || printf false)");
        WriteLine($"{declaration}{name}_command={command}");
        WriteLine($"{declaration}{name}_timedOut=$([[ ${{{name}_code}} -eq 124 ]] && printf true || printf false)");
        if (arguments.Count <= 6 || arguments[6] is not IrLiteralExpression { Value: true })
        {
            WriteLine($"(( {name}_code == 0 )) || exit \"${{{name}_code}}\"");
        }
    }

    private void EmitNativeProcessPipeline(string name, IReadOnlyList<IrExpression> arguments, bool inFunction)
    {
        var stagesExpression = arguments[0];
        IrArrayLiteralExpression? stages = stagesExpression as IrArrayLiteralExpression;
        if (stages == null && stagesExpression is IrIdentifierExpression identifier)
        {
            _arrayInitializers.TryGetValue(SanitizeVariableName(identifier.Name), out stages);
        }
        if (stages == null)
        {
            _context.Error(AmbiguousShapeCode, "Process pipeline stages must have a statically known array shape");
            return;
        }

        var commands = new List<string>();
        foreach (var stage in stages.Elements.OfType<IrObjectLiteralExpression>())
        {
            var commandProperty = stage.Properties.FirstOrDefault(property => property.Name == "command");
            var argsProperty = stage.Properties.FirstOrDefault(property => property.Name == "args");
            if (commandProperty == null) continue;
            var parts = new List<string> { PrepareValue(commandProperty.Value, inFunction) };
            if (argsProperty?.Value is IrArrayLiteralExpression stageArgs)
            {
                parts.AddRange(stageArgs.Elements.Select(arg => PrepareValue(arg, inFunction)));
            }
            commands.Add(string.Join(" ", parts));
        }
        var invocation = string.Join(" | ", commands);
        var declaration = inFunction ? "local " : "";
        WriteLine($"{declaration}{name}_stderr_file=$(mktemp)");
        WriteLine($"if {name}_stdout=$({invocation} 2>\"${{{name}_stderr_file}}\"); then {name}_code=0; else {name}_code=$?; fi");
        WriteLine($"{declaration}{name}_stderr=$(<\"${{{name}_stderr_file}}\")");
        WriteLine($"rm -f -- \"${{{name}_stderr_file}}\"");
        WriteLine($"{declaration}{name}_ok=$([[ ${{{name}_code}} -eq 0 ]] && printf true || printf false)");
        WriteLine($"{declaration}{name}_command='pipeline'");
        WriteLine($"{declaration}{name}_timedOut='false'");
        if (arguments.Count <= 5 || arguments[5] is not IrLiteralExpression { Value: true })
        {
            WriteLine($"(( {name}_code == 0 )) || exit \"${{{name}_code}}\"");
        }
    }

    private void EmitNativeHttp(string name, IrIntrinsicCallExpression intrinsic, bool inFunction)
    {
        var declaration = inFunction ? "local " : "";
        var url = PrepareValue(intrinsic.Arguments[0], inFunction);
        var method = intrinsic.Id == IntrinsicId.HttpPost ? "POST" : "GET";
        WriteLine($"{declaration}{name}_body_file=$(mktemp)");
        var curl = $"curl -sS -o \"${{{name}_body_file}}\" -w '%{{http_code}}' -X {method}";
        if (intrinsic.Id == IntrinsicId.HttpPost)
        {
            curl += $" -H 'Content-Type: '" + PrepareValue(intrinsic.Arguments[3], inFunction) + $" --data {PrepareValue(intrinsic.Arguments[1], inFunction)}";
        }
        WriteLine($"if {name}_status=$({curl} {url}); then {name}_transport_ok=true; else {name}_transport_ok=false; {name}_status=0; fi");
        WriteLine($"{declaration}{name}_body=$(<\"${{{name}_body_file}}\")");
        WriteLine($"rm -f -- \"${{{name}_body_file}}\"");
        WriteLine($"{declaration}{name}_ok=$([[ ${{{name}_transport_ok}} == true && ${{{name}_status}} -ge 200 && ${{{name}_status}} -lt 300 ]] && printf true || printf false)");
        WriteLine($"{declaration}{name}_url={url}");
        WriteLine($"{declaration}{name}_headers=''");
    }

    private string PrepareValue(IrExpression expression, bool inFunction)
    {
        switch (expression)
        {
            case IrTruthinessExpression:
            case IrUnaryExpression { Operator: "!" }:
            case IrBinaryExpression { Operator: "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" }:
            {
                return PrepareBooleanValue(expression, inFunction);
            }
            case IrLiteralExpression or IrIdentifierExpression:
                return EmitValueExpression(expression);

            case IrArrayLiteralExpression array:
            {
                var elements = array.Elements.Select(element => PrepareValue(element, inFunction)).ToList();
                WriteLine(elements.Count == 0
                    ? "__sushi_array_new"
                    : $"__sushi_array_new {string.Join(" ", elements)}");
                var result = DeclareTemp("\"${__sushi_result-}\"", inFunction);
                for (var index = 0; index < array.Elements.Count; index++)
                {
                    if (GetKnownJsonKind(array.Elements[index]) is { } kind)
                    {
                        WriteLine($"__sushi_array_set_kind \"${{{result}-}}\" {index} {Escape.BashSingleQuoted(kind)}");
                    }
                }
                return $"\"${{{result}-}}\"";
            }

            case IrObjectLiteralExpression obj:
            {
                var pairs = new List<string>();
                foreach (var property in obj.Properties)
                {
                    pairs.Add(Escape.BashSingleQuoted(property.Name));
                    pairs.Add(PrepareValue(property.Value, inFunction));
                }
                WriteLine(pairs.Count == 0 ? "__sushi_native_obj_new" : $"__sushi_native_obj_new {string.Join(" ", pairs)}");
                var result = DeclareTemp("\"${__sushi_result-}\"", inFunction);
                foreach (var property in obj.Properties)
                {
                    if (GetKnownJsonKind(property.Value) is { } kind)
                    {
                        WriteLine($"__sushi_native_obj_set_kind \"${{{result}-}}\" {Escape.BashSingleQuoted(property.Name)} {Escape.BashSingleQuoted(kind)}");
                    }
                }
                return $"\"${{{result}-}}\"";
            }

            case IrMemberAccessExpression member:
            {
                if (member.Target is IrConstructionExpression construction)
                {
                    var objectName = PrepareConstructionReference(construction, inFunction);
                    return $"\"${{{objectName}[{EmitObjectSubscript(member.MemberName)}]-}}\"";
                }
                if (member.Target is IrIntrinsicCallExpression recordIntrinsic)
                {
                    var recordName = $"__sushi_record_{++_valueTempId}";
                    if (EmitNativeIntrinsicDeclaration(recordName, recordIntrinsic, inFunction))
                    {
                        return $"\"${{{recordName}_{SanitizeVariableName(member.MemberName)}-}}\"";
                    }
                }
                if (member.Target is IrIdentifierExpression directIdentifier)
                {
                    var sourceName = SanitizeVariableName(directIdentifier.Name);
                    var directName = ResolveNativeObjectName(sourceName);
                    if (_zshMode && _zshReadOnlyObjectParameters.Contains(sourceName) &&
                        _zshObjectParameterNames.TryGetValue(sourceName, out var readOnlyReference))
                    {
                        return "\"${${(@P)" + readOnlyReference + "}[" + EmitObjectSubscript(member.MemberName) + "]-}\"";
                    }
                    if (_nativeObjectVariables.Contains(directName))
                    {
                        return $"\"${{{directName}[{EmitObjectSubscript(member.MemberName)}]-}}\"";
                    }
                    if (_recordVariables.Contains(directName))
                    {
                        return $"\"${{{directName}_{SanitizeVariableName(member.MemberName)}-}}\"";
                    }
                }

                _context.Error(AmbiguousShapeCode, $"Member '{member.MemberName}' requires a statically known object shape");
                return "''";
            }

            case IrIndexExpression index when index.Target is IrIdentifierExpression identifier &&
                                             _nativeArrayVariables.TryGetValue(SanitizeVariableName(identifier.Name), out var arrayName):
            {
                if (arrayName.Length > 0 && IsDefinitelyInteger(index.Index))
                {
                    var directIndex = index.Index switch
                    {
                        IrIdentifierExpression indexIdentifier => SanitizeVariableName(indexIdentifier.Name),
                        IrLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
                        _ => EmitArithmeticExpression(index.Index)
                    };
                    return $"\"${{{arrayName}[{directIndex}]-}}\"";
                }
                var indexValue = DeclareTemp(PrepareValue(index.Index, inFunction), inFunction);
                if (arrayName.Length > 0)
                {
                    WriteLine($"if (( {indexValue} < 0 )); then {indexValue}=$(( ${{#{arrayName}[@]}} + {indexValue} )); fi");
                    var directResult = DeclareTemp($"\"${{{arrayName}[{indexValue}]-}}\"", inFunction);
                    return $"\"${{{directResult}-}}\"";
                }

                _context.Error(AmbiguousShapeCode, "Array index requires native array storage");
                return "''";
            }

            case IrIndexExpression:
                _context.Error(AmbiguousShapeCode, "Indexing requires a statically known native array or object shape");
                return "''";

            case IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%":
                return PrepareArithmetic(binary, inFunction);

            case IrUnaryExpression unary when unary.Operator is "+" or "-":
            {
                var operand = PrepareValue(unary.Operand, inFunction);
                var operandTemp = DeclareTemp(operand, inFunction);
                return $"$(( {unary.Operator}{operandTemp} ))";
            }

            case IrCallExpression call when
                !call.Callee.StartsWith("__sushi_new_", StringComparison.Ordinal) &&
                (_functions.ContainsKey(call.Callee) ||
                 !call.Callee.StartsWith("__sushi_", StringComparison.Ordinal) ||
                 call.Callee.StartsWith("__sushi_method_", StringComparison.Ordinal)):
            {
                var arguments = new List<string>();
                _functions.TryGetValue(call.Callee, out var function);
                if (function != null && FunctionReturnsValue(function))
                {
                    var result = DeclareUninitializedTemp(inFunction);
                    EmitCallInto(result, call, function, inFunction);
                    if (_integerReturningFunctions.Contains(call.Callee)) _knownIntegerVariables.Add(result);
                    return $"\"${{{result}-}}\"";
                }
                for (var index = 0; index < call.Arguments.Count; index++)
                {
                    var argument = call.Arguments[index].Value;
                    var parameter = function != null && index < function.Parameters.Count
                        ? function.Parameters[index]
                        : null;
                    if (argument is IrObjectLiteralExpression objectLiteral &&
                        parameter?.DeclaredType.Kind == IrTypeKind.Structural)
                    {
                        foreach (var field in parameter.DeclaredType.StructuralFields)
                        {
                            var property = objectLiteral.Properties.FirstOrDefault(item => item.Name == field.Name);
                            arguments.Add(property == null ? "''" : PrepareValue(property.Value, inFunction));
                        }
                    }
                    else if (argument is IrConstructionExpression construction &&
                             parameter != null && IsNativeObjectType(parameter.DeclaredType))
                    {
                        arguments.Add(Escape.BashSingleQuoted(PrepareConstructionReference(construction, inFunction)));
                    }
                    else if (argument is IrIdentifierExpression aggregateIdentifier &&
                             parameter?.DeclaredType.Kind == IrTypeKind.Structural)
                    {
                        var aggregateName = SanitizeVariableName(aggregateIdentifier.Name);
                        foreach (var field in parameter.DeclaredType.StructuralFields)
                        {
                            arguments.Add(_nativeObjectVariables.Contains(aggregateName)
                                ? $"\"${{{aggregateName}[{EmitObjectSubscript(field.Name)}]-}}\""
                                : $"\"${{{aggregateName}_{SanitizeVariableName(field.Name)}-}}\"");
                        }
                    }
                    else if (argument is IrIdentifierExpression aggregateIdentifier2 &&
                             parameter != null && (parameter.DeclaredType.Name == "array" || IsNativeObjectType(parameter.DeclaredType)))
                    {
                        arguments.Add(Escape.BashSingleQuoted(ResolveNativeObjectName(SanitizeVariableName(aggregateIdentifier2.Name))));
                    }
                    else
                    {
                        arguments.Add(PrepareValue(argument, inFunction));
                    }
                }
                var command = arguments.Count > 0
                    ? $"{SanitizeFunctionName(call.Callee)} {string.Join(" ", arguments)}"
                    : SanitizeFunctionName(call.Callee);
                WriteLine(command);
                return "''";
            }

            case IrResolvedMethodCallExpression method:
                return PrepareValue(method.AsFunctionCall(), inFunction);

            case IrAdapterCallExpression adapter:
                return PrepareValue(adapter.AsFunctionCall(), inFunction);

            case IrConstructionExpression:
                _context.Error(AmbiguousShapeCode, "Constructed objects must be assigned to a variable before use on Bash/Zsh targets.");
                return "''";

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id == IntrinsicId.FsGlob:
                return PrepareIntrinsicInto(intrinsic, inFunction, "__sushi_fs_glob_into", 2);

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id == IntrinsicId.ProcessRun:
                return PrepareIntrinsicInto(intrinsic, inFunction, "__sushi_process_run_into", 8);

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id == IntrinsicId.ProcessPipeline:
                return PrepareIntrinsicInto(intrinsic, inFunction, "__sushi_process_pipeline_into", 7);

            case IrIntrinsicCallExpression intrinsic when intrinsic.Id is IntrinsicId.IoWriteText or IntrinsicId.EnvSet or IntrinsicId.ProcessExit or IntrinsicId.OsChdir:
            {
                var command = intrinsic.Id switch
                {
                    IntrinsicId.IoWriteText => EmitIoWriteText(
                        PrepareValue(intrinsic.Arguments[0], inFunction),
                        PrepareValue(intrinsic.Arguments[1], inFunction),
                        PrepareValue(intrinsic.Arguments[2], inFunction)),
                    IntrinsicId.EnvSet => EmitEnvSet(
                        PrepareValue(intrinsic.Arguments[0], inFunction),
                        PrepareValue(intrinsic.Arguments[1], inFunction)),
                    IntrinsicId.ProcessExit => $"exit {PrepareValue(intrinsic.Arguments[0], inFunction)}",
                    _ => $"cd -- {PrepareValue(intrinsic.Arguments[0], inFunction)}"
                };
                WriteLine(command);
                return "''";
            }

            case IrIntrinsicCallExpression intrinsic when IsInlineStringIntrinsic(intrinsic.Id):
                return PrepareStringIntrinsic(intrinsic, inFunction);

            case IrConditionalExpression conditional:
            {
                var result = DeclareTemp("''", inFunction);
                var condition = PrepareCondition(conditional.Condition, inFunction);
                WriteLine($"if {condition}; then");
                _indent++;
                var whenTrue = PrepareValue(conditional.TrueExpression, inFunction);
                WriteLine($"{result}={whenTrue}");
                _indent--;
                WriteLine("else");
                _indent++;
                var whenFalse = PrepareValue(conditional.FalseExpression, inFunction);
                WriteLine($"{result}={whenFalse}");
                _indent--;
                WriteLine("fi");
                return $"\"${{{result}-}}\"";
            }

            default:
                return CaptureValue(EmitValueExpression(expression), inFunction);
        }
    }

    private static bool IsBooleanValueExpression(IrExpression expression) => expression switch
    {
        IrTruthinessExpression => true,
        IrUnaryExpression { Operator: "!" } => true,
        IrBinaryExpression { Operator: "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" } => true,
        _ => false
    };

    private void EmitBooleanAssignment(string name, IrExpression expression, bool inFunction)
    {
        WriteLine($"{(inFunction ? "local " : string.Empty)}{name}='false'");
        var condition = PrepareCondition(expression, inFunction);
        WriteLine($"if {condition}; then {name}='true'; fi");
    }

    private void EmitBooleanOutput(IrExpression expression)
    {
        var condition = PrepareCondition(expression, inFunction: true);
        if (_zshMode)
        {
            WriteLine($"if {condition}; then : ${{(P){_currentOutputName}::='true'}}; else : ${{(P){_currentOutputName}::='false'}}; fi");
        }
        else
        {
            WriteLine($"if {condition}; then {_currentOutputName}='true'; else {_currentOutputName}='false'; fi");
        }
    }

    private string PrepareBooleanValue(IrExpression expression, bool inFunction)
    {
        var result = DeclareTemp("'false'", inFunction);
        var condition = PrepareCondition(expression, inFunction);
        WriteLine($"if {condition}; then {result}='true'; fi");
        return $"\"${{{result}-}}\"";
    }

    private void EmitNativeObject(string name, IrObjectLiteralExpression obj, bool inFunction)
    {
        var properties = MetadataProperties(obj.Properties);
        var entries = (_zshMode
            ? properties.Select(property =>
                $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")
            : properties.Select(property =>
                $"[{Escape.BashSingleQuoted(property.Name)}]={PrepareValue(property.Value, inFunction)}")).ToList();
        EmitAssociativeObject(name, entries, inFunction ? "local " : "declare ", false);
        _nativeObjectVariables.Add(name);
    }

    private IReadOnlyList<IrObjectProperty> MetadataProperties(IReadOnlyList<IrObjectProperty> properties)
    {
        if (_needsDynamicMethodMetadata) return properties;
        return properties.Where(property => !property.Name.StartsWith(NativeObjectMetadata.MethodPrefix, StringComparison.Ordinal)).ToList();
    }

    private static bool ContainsDynamicMethodDispatch(IrStatement statement) => statement switch
    {
        IrBlockStatement block => block.Statements.Any(ContainsDynamicMethodDispatch),
        IrExpressionStatement expression => ContainsDynamicMethodDispatch(expression.Expression),
        IrVariableDeclarationStatement variable => variable.Initializer != null && ContainsDynamicMethodDispatch(variable.Initializer),
        IrIfStatement conditional => ContainsDynamicMethodDispatch(conditional.Condition) ||
                                     ContainsDynamicMethodDispatch(conditional.ThenBlock) ||
                                     (conditional.ElseBlock != null && ContainsDynamicMethodDispatch(conditional.ElseBlock)),
        IrWhileStatement loop => ContainsDynamicMethodDispatch(loop.Condition) || ContainsDynamicMethodDispatch(loop.Body),
        IrDoWhileStatement loop => ContainsDynamicMethodDispatch(loop.Condition) || ContainsDynamicMethodDispatch(loop.Body),
        IrForStatement loop => (loop.Initializer != null && ContainsDynamicMethodDispatch(loop.Initializer)) ||
                              (loop.Condition != null && ContainsDynamicMethodDispatch(loop.Condition)) ||
                              (loop.Increment != null && ContainsDynamicMethodDispatch(loop.Increment)) ||
                              ContainsDynamicMethodDispatch(loop.Body),
        IrFunctionDeclarationStatement function => ContainsDynamicMethodDispatch(function.Body),
        IrReturnStatement result => result.Expression != null && ContainsDynamicMethodDispatch(result.Expression),
        _ => false
    };

    private static bool ContainsDynamicMethodDispatch(IrExpression expression) => expression switch
    {
        IrMethodCallExpression => true,
        IrCallExpression call => call.Arguments.Any(argument => ContainsDynamicMethodDispatch(argument.Value)),
        IrResolvedMethodCallExpression call => ContainsDynamicMethodDispatch(call.Target) ||
                                               call.Arguments.Any(argument => ContainsDynamicMethodDispatch(argument.Value)),
        IrAdapterCallExpression call => ContainsDynamicMethodDispatch(call.Value),
        IrAssignmentExpression assignment => ContainsDynamicMethodDispatch(assignment.Value),
        IrMemberAssignmentExpression assignment => ContainsDynamicMethodDispatch(assignment.Target) || ContainsDynamicMethodDispatch(assignment.Value),
        IrBinaryExpression binary => ContainsDynamicMethodDispatch(binary.Left) || ContainsDynamicMethodDispatch(binary.Right),
        IrUnaryExpression unary => ContainsDynamicMethodDispatch(unary.Operand),
        IrConditionalExpression conditional => ContainsDynamicMethodDispatch(conditional.Condition) ||
                                               ContainsDynamicMethodDispatch(conditional.TrueExpression) ||
                                               ContainsDynamicMethodDispatch(conditional.FalseExpression),
        IrMemberAccessExpression member => ContainsDynamicMethodDispatch(member.Target),
        IrIndexExpression index => ContainsDynamicMethodDispatch(index.Target) || ContainsDynamicMethodDispatch(index.Index),
        IrArrayLiteralExpression array => array.Elements.Any(ContainsDynamicMethodDispatch),
        IrObjectLiteralExpression obj => obj.Properties.Any(property => ContainsDynamicMethodDispatch(property.Value)),
        IrTruthinessExpression truthiness => ContainsDynamicMethodDispatch(truthiness.Operand),
        _ => false
    };

    private void EmitAssociativeObject(string name, IReadOnlyList<string> entries, string declaration, bool assignmentOnly)
    {
        var multiline = entries.Count >= 8 || entries.Any(entry =>
            entry.Contains("['_", StringComparison.Ordinal) || entry.Contains("'_m_", StringComparison.Ordinal));
        var prefix = assignmentOnly ? $"{name}=(" : $"{declaration}-A {name}=(";
        if (!multiline)
        {
            WriteLine($"{prefix}{string.Join(" ", entries)})");
            return;
        }

        WriteLine(prefix);
        _indent++;
        foreach (var entry in entries) WriteLine(entry);
        _indent--;
        WriteLine(")");
    }

    private string PrepareIntrinsicInto(
        IrIntrinsicCallExpression intrinsic,
        bool inFunction,
        string helper,
        int argumentCount)
    {
        var arguments = new List<string>(argumentCount);
        for (var index = 0; index < argumentCount; index++)
        {
            arguments.Add(index < intrinsic.Arguments.Count
                ? PrepareValue(intrinsic.Arguments[index], inFunction)
                : "''");
        }

        WriteLine($"{helper} {string.Join(" ", arguments)}");
        var result = DeclareTemp("\"${__sushi_result-}\"", inFunction);
        return $"\"${{{result}-}}\"";
    }

    private static string? GetKnownJsonKind(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression { Value: null } => "null",
            IrLiteralExpression { Value: string or char } => "string",
            IrLiteralExpression { Value: bool } => "bool",
            IrLiteralExpression { Value: sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal } => "number",
            IrArrayLiteralExpression => "array",
            IrObjectLiteralExpression => "object",
            _ => null
        };
    }

    private string PrepareArithmetic(IrBinaryExpression binary, bool inFunction)
    {
        if (binary.Operator == "+" && ContainsStringLiteral(binary) && TryEmitInlineString(binary, out var inlineString))
        {
            return $"\"{inlineString}\"";
        }

        if (CanEmitInlineInteger(binary))
        {
            return $"$(( {EmitInlineInteger(binary)} ))";
        }

        var leftExpression = PrepareValue(binary.Left, inFunction);
        var rightExpression = PrepareValue(binary.Right, inFunction);
        var left = DeclareTemp(leftExpression, inFunction);
        var right = DeclareTemp(rightExpression, inFunction);
        var result = DeclareTemp("''", inFunction);

        if (binary.Operator == "+" && !IsDefinitelyInteger(binary))
        {
            WriteLine($"{result}=\"${{{left}-}}${{{right}-}}\"");
            return $"\"${{{result}-}}\"";
        }
        WriteLine($"{result}=$(( {left} {binary.Operator} {right} ))");
        _knownIntegerVariables.Add(result);
        return $"\"${{{result}-}}\"";
    }

    private bool TryEmitInlineString(IrExpression expression, out string value)
    {
        switch (expression)
        {
            case IrLiteralExpression { Value: string text }:
                value = EscapeBashDoubleQuotedContent(text);
                return true;
            case IrLiteralExpression { Value: char character }:
                value = EscapeBashDoubleQuotedContent(character.ToString());
                return true;
            case IrIdentifierExpression identifier:
                value = _positionalParameterReferences.TryGetValue(SanitizeVariableName(identifier.Name), out var positional)
                    ? $"${{{positional}:-}}"
                    : $"${{{SanitizeVariableName(identifier.Name)}:-}}";
                return true;
            case IrMemberAccessExpression { Target: IrIdentifierExpression target } member:
            {
                var targetName = ResolveNativeObjectName(SanitizeVariableName(target.Name));
                var sourceName = SanitizeVariableName(target.Name);
                if (_zshMode && _zshReadOnlyObjectParameters.Contains(sourceName) &&
                    _zshObjectParameterNames.TryGetValue(sourceName, out var readOnlyReference))
                {
                    value = $"${{${{(@P){readOnlyReference}}}[{EmitObjectSubscript(member.MemberName)}]-}}";
                    return true;
                }
                if (_nativeObjectVariables.Contains(targetName))
                {
                    value = $"${{{targetName}[{EmitObjectSubscript(member.MemberName)}]-}}";
                    return true;
                }
                if (_recordVariables.Contains(targetName))
                {
                    value = $"${{{targetName}_{SanitizeVariableName(member.MemberName)}-}}";
                    return true;
                }
                break;
            }
            case IrIndexExpression { Target: IrIdentifierExpression target, Index: IrLiteralExpression { Value: int index } }:
            {
                var targetName = SanitizeVariableName(target.Name);
                if (_nativeArrayVariables.ContainsKey(targetName))
                {
                    value = $"${{{targetName}[{index}]-}}";
                    return true;
                }
                break;
            }
            case IrBinaryExpression { Operator: "+" } binary:
                if (TryEmitInlineString(binary.Left, out var left) && TryEmitInlineString(binary.Right, out var right))
                {
                    value = left + right;
                    return true;
                }
                break;
        }

        value = "";
        return false;
    }

    private static bool ContainsStringLiteral(IrExpression expression) => expression switch
    {
        IrLiteralExpression { Value: string or char } => true,
        IrBinaryExpression { Operator: "+" } binary =>
            ContainsStringLiteral(binary.Left) || ContainsStringLiteral(binary.Right),
        _ => false
    };

    private static string EscapeBashDoubleQuotedContent(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("$", "\\$", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal);

    private string EmitObjectSubscript(string memberName) =>
        _zshMode ? memberName : Escape.BashSingleQuoted(memberName);

    private string PrepareCondition(IrExpression expression, bool inFunction)
    {
        if (expression is IrTruthinessExpression truthiness)
            return PrepareTruthinessCondition(truthiness, inFunction);

        if (expression is IrUnaryExpression { Operator: "!" } unary)
        {
            return $"! {PrepareCondition(unary.Operand, inFunction)}";
        }

        if (expression is IrBinaryExpression logical && logical.Operator is "&&" or "||")
        {
            var result = DeclareTemp(logical.Operator == "&&" ? "'false'" : "'true'", inFunction);
            var left = PrepareCondition(logical.Left, inFunction);
            WriteLine(logical.Operator == "&&" ? $"if {left}; then" : $"if ! {left}; then");
            _indent++;
            var right = PrepareCondition(logical.Right, inFunction);
            WriteLine($"if {right}; then {result}='true'; else {result}='false'; fi");
            _indent--;
            WriteLine("fi");
            return $"[[ \"${{{result}-}}\" == 'true' ]]";
        }

        if (expression is IrBinaryExpression comparison && comparison.Operator is "==" or "!=")
        {
            var left = PrepareValue(comparison.Left, inFunction);
            var right = PrepareValue(comparison.Right, inFunction);
            return $"[[ {left} {comparison.Operator} {right} ]]";
        }

        if (expression is IrBinaryExpression relational && relational.Operator is "<" or ">" or "<=" or ">=")
        {
            if (CanEmitInlineInteger(relational.Left) && CanEmitInlineInteger(relational.Right))
            {
                return $"(( {EmitInlineInteger(relational.Left)} {relational.Operator} {EmitInlineInteger(relational.Right)} ))";
            }

            var left = DeclareTemp(PrepareValue(relational.Left, inFunction), inFunction);
            var right = DeclareTemp(PrepareValue(relational.Right, inFunction), inFunction);
            if (!IsDefinitelyInteger(relational.Left))
            {
                WriteLine($"__sushi_validate_integer \"${{{left}:-}}\" 'comparison operand'");
            }
            if (!IsDefinitelyInteger(relational.Right))
            {
                WriteLine($"__sushi_validate_integer \"${{{right}:-}}\" 'comparison operand'");
            }
            return $"(( {left} {relational.Operator} {right} ))";
        }

        if (expression is IrIdentifierExpression identifier)
        {
            var name = SanitizeVariableName(identifier.Name);
            return _knownIntegerVariables.Contains(name)
                ? "true"
                : $"[[ -n \"${{{name}:-}}\" ]]";
        }

        if (expression is IrLiteralExpression literal)
        {
            return literal.Value switch
            {
                null => "false",
                bool boolean => boolean ? "true" : "false",
                sbyte or byte or short or ushort or int or uint or long or ulong =>
                    "true",
                string text => text.Length == 0 ? "false" : "true",
                _ => "true"
            };
        }

        var value = PrepareValue(expression, inFunction);
        return $"[[ -n {value} ]]";
    }

    private string PrepareTruthinessCondition(IrTruthinessExpression expression, bool inFunction)
    {
        var type = expression.OperandType.Name;
        if (type == "null") return "false";
        if (type == "array" || type == "object" || IsNamedObjectType(expression.OperandType))
        {
            if (expression.Operand is IrArrayLiteralExpression or IrObjectLiteralExpression or IrConstructionExpression)
                return "true";
            if (expression.Operand is IrIdentifierExpression identifier)
            {
                var name = SanitizeVariableName(identifier.Name);
                if (_nativeArrayVariables.ContainsKey(name) || _nativeObjectVariables.Contains(name) || _recordVariables.Contains(name))
                    return "true";
            }
            return $"[[ -n {PrepareValue(expression.Operand, inFunction)} ]]";
        }

        if (type == "bool") return PrepareBooleanCondition(expression.Operand, inFunction);
        var value = PrepareValue(expression.Operand, inFunction);
        return type switch
        {
            "int" or "float" => "true",
            "string" => $"[[ -n {value} ]]",
            _ => "false"
        };
    }

    private string PrepareBooleanCondition(IrExpression expression, bool inFunction)
    {
        return expression switch
        {
            IrLiteralExpression { Value: true } => "true",
            IrLiteralExpression { Value: false } => "false",
            IrIdentifierExpression identifier => $"[[ \"${{{SanitizeVariableName(identifier.Name)}:-}}\" == 'true' ]]",
            IrUnaryExpression { Operator: "!" } unary => $"! {PrepareBooleanCondition(unary.Operand, inFunction)}",
            IrBinaryExpression { Operator: "&&" } binary => $"{PrepareBooleanCondition(binary.Left, inFunction)} && {PrepareBooleanCondition(binary.Right, inFunction)}",
            IrBinaryExpression { Operator: "||" } binary => $"{PrepareBooleanCondition(binary.Left, inFunction)} || {PrepareBooleanCondition(binary.Right, inFunction)}",
            IrBinaryExpression binary when binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=" => PrepareCondition(binary, inFunction),
            IrTruthinessExpression truthiness => PrepareTruthinessCondition(truthiness, inFunction),
            _ => $"[[ {PrepareValue(expression, inFunction)} == 'true' ]]"
        };
    }

    private string PrepareStringIntrinsic(IrIntrinsicCallExpression call, bool inFunction)
    {
        var result = $"__sushi_value_{++_valueTempId}";
        EmitStringChain(result, call, inFunction, declareResult: true);
        return $"\"${{{result}-}}\"";
    }

    private void EmitStringChain(string result, IrIntrinsicCallExpression call, bool inFunction, bool declareResult)
    {
        var chain = new List<IrIntrinsicCallExpression> { call };
        var receiverExpression = call.Arguments[0];
        while (receiverExpression is IrIntrinsicCallExpression nested && IsInlineStringIntrinsic(nested.Id))
        {
            chain.Add(nested);
            receiverExpression = nested.Arguments[0];
        }
        chain.Reverse();

        var declaration = declareResult && inFunction ? "local " : "";
        WriteLine($"{declaration}{result}={PrepareValue(receiverExpression, inFunction)}");
        foreach (var operation in chain)
        {
            switch (operation.Id)
            {
                case IntrinsicId.StringTrim:
                    WriteLine($"{result}=\"${{{result}#\"${{{result}%%[![:space:]]*}}\"}}\"");
                    WriteLine($"{result}=\"${{{result}%\"${{{result}##*[![:space:]]}}\"}}\"");
                    break;
                case IntrinsicId.StringLower:
                    WriteLine(_zshMode ? $"{result}=\"${{(L){result}}}\"" : $"{result}=\"${{{result},,}}\"");
                    break;
                case IntrinsicId.StringUpper:
                    WriteLine(_zshMode ? $"{result}=\"${{(U){result}}}\"" : $"{result}=\"${{{result}^^}}\"");
                    break;
                case IntrinsicId.StringReplace:
                {
                    var oldValue = DeclareTemp(PrepareValue(operation.Arguments[1], inFunction), inFunction);
                    var newValue = DeclareTemp(PrepareValue(operation.Arguments[2], inFunction), inFunction);
                    WriteLine($"if [[ -n \"${{{oldValue}-}}\" ]]; then {result}=\"${{{result}//${{{oldValue}}}/${{{newValue}}}}}\"; fi");
                    break;
                }
                case IntrinsicId.StringContains:
                case IntrinsicId.StringStartsWith:
                case IntrinsicId.StringEndsWith:
                {
                    var needle = PrepareValue(operation.Arguments[1], inFunction);
                    var test = operation.Id switch
                    {
                        IntrinsicId.StringContains => $"\"${{{result}-}}\" == *{needle}*",
                        IntrinsicId.StringStartsWith => $"\"${{{result}-}}\" == {needle}*",
                        _ => $"\"${{{result}-}}\" == *{needle}"
                    };
                    WriteLine($"if [[ {test} ]]; then {result}='true'; else {result}='false'; fi");
                    break;
                }
                case IntrinsicId.StringIsMatch:
                {
                    var pattern = PrepareRegex(operation.Arguments[1], inFunction);
                    WriteLine($"if [[ \"${{{result}-}}\" =~ {pattern} ]]; then {result}='true'; else {result}='false'; fi");
                    break;
                }
            }
        }
    }

    private string CaptureValue(string expression, bool inFunction)
    {
        var result = DeclareTemp("''", inFunction);
        WriteLine($"{result}={expression}");
        return $"\"${{{result}-}}\"";
    }

    private string DeclareTemp(string value, bool inFunction)
    {
        var name = _names.Generated(TargetNameKind.Variable, "_tmp" + (++_valueTempId));
        WriteLine($"{(inFunction ? "local " : "")}{name}={value}");
        return name;
    }

    private string DeclareUninitializedTemp(bool inFunction)
    {
        var name = _names.Generated(TargetNameKind.Variable, "_tmp" + (++_valueTempId));
        if (inFunction) WriteLine($"local {name}");
        return name;
    }

    private bool IsDefinitelyInteger(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong,
            IrIdentifierExpression identifier => _knownIntegerVariables.Contains(SanitizeVariableName(identifier.Name)),
            IrIndexExpression { Target: IrIdentifierExpression identifier } =>
                _integerArrayVariables.Contains(SanitizeVariableName(identifier.Name)),
            IrUnaryExpression unary when unary.Operator is "+" or "-" => IsDefinitelyInteger(unary.Operand),
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                IsDefinitelyInteger(binary.Left) && IsDefinitelyInteger(binary.Right),
            IrCallExpression call => _integerReturningFunctions.Contains(call.Callee),
            IrResolvedMethodCallExpression method => _integerReturningFunctions.Contains(method.Callee),
            IrAdapterCallExpression adapter => _integerReturningFunctions.Contains(adapter.Callee),
            IrMemberAccessExpression member => member.ValueType.Name == "int",
            _ => false
        };
    }

    private bool CanEmitInlineInteger(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => literal.Value is sbyte or byte or short or ushort or int or uint or long or ulong,
            IrIdentifierExpression identifier => _knownIntegerVariables.Contains(SanitizeVariableName(identifier.Name)),
            IrUnaryExpression unary when unary.Operator is "+" or "-" => CanEmitInlineInteger(unary.Operand),
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                CanEmitInlineInteger(binary.Left) && CanEmitInlineInteger(binary.Right),
            _ => false
        };
    }

    private string EmitInlineInteger(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
            IrIdentifierExpression identifier => SanitizeVariableName(identifier.Name),
            IrUnaryExpression unary => $"{unary.Operator}{EmitInlineInteger(unary.Operand)}",
            IrBinaryExpression binary => $"({EmitInlineInteger(binary.Left)} {binary.Operator} {EmitInlineInteger(binary.Right)})",
            _ => "0"
        };
    }

    private void SetKnownInteger(string name, bool isInteger)
    {
        if (isInteger)
        {
            _knownIntegerVariables.Add(name);
        }
        else
        {
            _knownIntegerVariables.Remove(name);
        }
    }

    private void SetKnownArray(string name, bool isArray)
    {
        if (isArray)
        {
            _nativeArrayVariables[name] = string.Empty;
        }
        else
        {
            _nativeArrayVariables.Remove(name);
        }
    }

    private static bool IsInlineStringIntrinsic(IntrinsicId id)
    {
        return id is IntrinsicId.StringTrim or IntrinsicId.StringLower or IntrinsicId.StringUpper or
            IntrinsicId.StringContains or IntrinsicId.StringStartsWith or IntrinsicId.StringEndsWith or
            IntrinsicId.StringReplace or IntrinsicId.StringIsMatch;
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
        if (expression is IrTruthinessExpression truthiness)
            return EmitTruthinessCommand(truthiness);

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

        return $"[[ {EmitValueExpression(expression)} == 'true' ]]";
    }

    private string EmitComparableValue(IrExpression expression)
    {
        return expression switch
        {
            IrMemberAccessExpression { Target: IrIdentifierExpression target } member
                when _nativeObjectVariables.Contains(SanitizeVariableName(target.Name)) =>
                $"\"${{{ResolveNativeObjectName(SanitizeVariableName(target.Name))}[{EmitObjectSubscript(member.MemberName)}]-}}\"",
            IrIdentifierExpression identifier => EmitVariableValue(identifier.Name),
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
            IrIdentifierExpression identifier => EmitVariableValue(identifier.Name),
            IrArrayLiteralExpression array => EmitArrayLiteral(array),
            IrObjectLiteralExpression obj => EmitObjectLiteral(obj),
            IrMemberAccessExpression member => EmitMemberValueExpression(member),
            IrIndexExpression index => $"\"$(__sushi_json_index {EmitValueExpression(index.Target)} {EmitValueExpression(index.Index)})\"",
            IrUnaryExpression unary when unary.Operator == "!" =>
                $"\"$(if {EmitConditionCommand(unary.Operand)}; then printf '%s' 'false'; else printf '%s' 'true'; fi)\"",
            IrTruthinessExpression truthiness =>
                $"\"$(if {EmitTruthinessCommand(truthiness)}; then printf '%s' 'true'; else printf '%s' 'false'; fi)\"",
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
            IrResolvedMethodCallExpression method => $"$({EmitCallCommand(method.AsFunctionCall())})",
            IrAdapterCallExpression adapter => $"$({EmitCallCommand(adapter.AsFunctionCall())})",
            IrConstructionExpression => "''",
            IrMethodCallExpression methodCall => $"\"$({EmitMethodCallCommand(methodCall)})\"",
            IrAssignmentExpression assignment => $"$({EmitAssignmentExpression(assignment)}; printf '%s' \"${{{SanitizeVariableName(assignment.Target.Name)}:-}}\")",
            _ => "''"
        };
    }

    private string EmitVariableValue(string name)
    {
        var variable = SanitizeVariableName(name);
        return _positionalParameterReferences.TryGetValue(variable, out var positional)
            ? $"\"${{{positional}:-}}\""
            : $"\"${{{variable}:-}}\"";
    }

    private string EmitMemberValueExpression(IrMemberAccessExpression member)
    {
        if (_zshMode && member.Target is IrIdentifierExpression identifier)
        {
            var sourceName = SanitizeVariableName(identifier.Name);
            if (_zshReadOnlyObjectParameters.Contains(sourceName) &&
                _zshObjectParameterNames.TryGetValue(sourceName, out var referenceName))
            {
                return "\"${${(@P)" + referenceName + "}[" + EmitObjectSubscript(member.MemberName) + "]-}\"";
            }
        }
        return $"\"$(__sushi_json_member {EmitValueExpression(member.Target)} {Escape.BashSingleQuoted(member.MemberName)})\"";
    }

    private string EmitTruthinessCommand(IrTruthinessExpression expression)
    {
        var type = expression.OperandType.Name;
        if (type == "null") return "false";
        if (type == "array" || type == "object" || IsNamedObjectType(expression.OperandType)) return "true";
        if (type == "bool") return EmitBooleanCondition(expression.Operand);
        var value = EmitValueExpression(expression.Operand);
        return type switch
        {
            "int" or "float" => "true",
            "string" => $"[[ -n {value} ]]",
            _ => "false"
        };
    }

    private string EmitBooleanCondition(IrExpression expression) => expression switch
    {
        IrLiteralExpression { Value: true } => "true",
        IrLiteralExpression { Value: false } => "false",
        IrIdentifierExpression identifier => $"[[ \"${{{SanitizeVariableName(identifier.Name)}:-}}\" == 'true' ]]",
        IrUnaryExpression { Operator: "!" } unary => $"! {EmitBooleanCondition(unary.Operand)}",
        IrBinaryExpression { Operator: "&&" } binary => $"{EmitBooleanCondition(binary.Left)} && {EmitBooleanCondition(binary.Right)}",
        IrBinaryExpression { Operator: "||" } binary => $"{EmitBooleanCondition(binary.Left)} || {EmitBooleanCondition(binary.Right)}",
        IrBinaryExpression binary when binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=" => EmitConditionCommand(binary),
        IrTruthinessExpression truthiness => EmitTruthinessCommand(truthiness),
        _ => $"[[ {EmitValueExpression(expression)} == 'true' ]]"
    };

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

    private void EmitVarargsContractCheck(
        IrFunctionParameter parameter,
        string parameterName,
        string functionName,
        string? nativeArrayName = null)
    {
        if (parameter.DeclaredType.IsAnyOrUnknown)
        {
            return;
        }

        WriteLine("local __sushi_vararg_item");
        if (nativeArrayName != null)
        {
            WriteLine($"for __sushi_vararg_item in \"${{{nativeArrayName}[@]}}\"; do");
        }
        else
        {
            WriteLine("while IFS= read -r __sushi_vararg_item; do");
        }
        _indent++;
        EmitContractCheckForValue(
            parameter.DeclaredType,
            "\"${__sushi_vararg_item:-}\"",
            $"varargs parameter '{parameter.Name}' of function '{functionName}'");
        _indent--;
        WriteLine(nativeArrayName != null
            ? "done"
            : $"done < <(__sushi_json_array_each_raw \"${{{parameterName}:-[]}}\")");
    }

    private void EmitContractCheckForValue(IrTypeRef type, string valueExpression, string context)
    {
        // Type errors that can be proven statically are reported by lowering.
        // Bash and Zsh otherwise use their native value model.
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
            IrMemberAccessExpression { Target: IrIdentifierExpression target } member
                when _nativeObjectVariables.Contains(SanitizeVariableName(target.Name)) =>
                member.ValueType.Name == "int"
                    ? $"${{{ResolveNativeObjectName(SanitizeVariableName(target.Name))}[{EmitObjectSubscript(member.MemberName)}]:-0}}"
                    : EmitCheckedInteger(
                        $"\"${{{ResolveNativeObjectName(SanitizeVariableName(target.Name))}[{EmitObjectSubscript(member.MemberName)}]-}}\"",
                        $"member '{member.MemberName}'"),
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

    private static string PrettyPrintBash(string source)
    {
        var output = new StringBuilder(source.Length + 256);
        foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
        {
            if (TryExpandInlineIf(line, output) ||
                TryExpandInlineLoop(line, output) ||
                TryExpandInlineGuard(line, output))
            {
                continue;
            }

            output.AppendLine(line);
        }

        return output.ToString();
    }

    private static bool TryExpandInlineIf(string line, StringBuilder output)
    {
        var match = Regex.Match(line, @"^(?<indent>\s*)if (?<condition>.+?); then (?<true>.+?);(?: else (?<false>.+?);)? fi$");
        if (!match.Success) return false;

        var indent = match.Groups["indent"].Value;
        output.AppendLine($"{indent}if {match.Groups["condition"].Value}; then");
        output.AppendLine($"{indent}    {match.Groups["true"].Value}");
        if (match.Groups["false"].Success)
        {
            output.AppendLine($"{indent}else");
            output.AppendLine($"{indent}    {match.Groups["false"].Value}");
        }
        output.AppendLine($"{indent}fi");
        return true;
    }

    private static bool TryExpandInlineLoop(string line, StringBuilder output)
    {
        var match = Regex.Match(line, @"^(?<indent>\s*)(?<kind>for|while) (?<header>.+?); do (?<body>.+?); done$");
        if (!match.Success) return false;

        var indent = match.Groups["indent"].Value;
        output.AppendLine($"{indent}{match.Groups["kind"].Value} {match.Groups["header"].Value}; do");
        output.AppendLine($"{indent}    {match.Groups["body"].Value}");
        output.AppendLine($"{indent}done");
        return true;
    }

    private static bool TryExpandInlineGuard(string line, StringBuilder output)
    {
        var match = Regex.Match(line, @"^(?<indent>\s*)(?<command>.+?) \|\| \{ (?<status>[^;]+); (?<failure>.+); \}$");
        if (!match.Success || match.Groups["command"].Value.Contains("||", StringComparison.Ordinal)) return false;

        var indent = match.Groups["indent"].Value;
        output.AppendLine($"{indent}{match.Groups["command"].Value} || {{");
        output.AppendLine($"{indent}    {match.Groups["status"].Value};");
        output.AppendLine($"{indent}    {match.Groups["failure"].Value};");
        output.AppendLine($"{indent}}}");
        return true;
    }

    private string SanitizeFunctionName(string name)
    {
        if (!name.StartsWith("__sushi_", StringComparison.Ordinal))
            return _names.Source(TargetNameKind.Function, name);

        if (_generatedFunctionNames.TryGetValue(name, out var existing)) return existing;

        var preferred = name switch
        {
            var value when value.StartsWith("__sushi_new_", StringComparison.Ordinal) =>
                value["__sushi_new_".Length..].ToLowerInvariant() + "_new",
            var value when value.StartsWith("__sushi_method_", StringComparison.Ordinal) =>
                value["__sushi_method_".Length..].ToLowerInvariant(),
            var value when value.StartsWith("__sushi_adapter_", StringComparison.Ordinal) =>
                value["__sushi_adapter_".Length..].ToLowerInvariant(),
            var value when value.StartsWith("__sushi_lambda_", StringComparison.Ordinal) =>
                "_lambda_" + value["__sushi_lambda_".Length..],
            _ => "_s_" + name["__sushi_".Length..]
        };
        var allocated = _names.Generated(TargetNameKind.Function, preferred);
        _generatedFunctionNames[name] = allocated;
        return allocated;
    }

    private string SanitizeVariableName(string name)
    {
        return _names.Source(TargetNameKind.Variable, name);
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
            IntrinsicId.EnvUnset => EmitEnvUnset(call.Arguments),
            IntrinsicId.ProcessExit => EmitProcessExit(call.Arguments),
            IntrinsicId.ProcessSleep => EmitProcessSleep(call.Arguments),
            IntrinsicId.ConsoleError => EmitConsoleError(call.Arguments),
            IntrinsicId.OsChdir => $"cd -- {Arg(call.Arguments, 0)}",
            IntrinsicId.ProcessRun => $"{EmitProcessRunInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessPipeline => $"{EmitProcessPipelineInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessFail => $"{EmitProcessFailInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.ProcessRequireSuccess => $"{EmitProcessRequireSuccessInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.FsGlob => $"{EmitFsGlobInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.FsCreateDirectory => EmitFsCreateDirectory(call.Arguments),
            IntrinsicId.FsRemove => EmitFsRemove(call.Arguments),
            IntrinsicId.FsCopy => EmitFsCopy(call.Arguments),
            IntrinsicId.FsMove => EmitFsMove(call.Arguments),
            IntrinsicId.ArchiveZip => EmitArchiveZip(call.Arguments),
            IntrinsicId.ArchiveUnzip => EmitArchiveUnzip(call.Arguments),
            IntrinsicId.HttpGet => $"{EmitHttpGetInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.HttpPost => $"{EmitHttpPostInvocation(call.Arguments)} >/dev/null",
            IntrinsicId.HttpDownload => EmitHttpDownload(call.Arguments),
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
            IntrinsicId.FsIsFile => $"$([[ -f {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.FsIsDirectory => $"$([[ -d {Arg(call.Arguments, 0)} ]] && printf 'true' || printf 'false')",
            IntrinsicId.PathJoin => EmitPathJoin(call.Arguments),
            IntrinsicId.PathDirname => $"$(dirname -- {Arg(call.Arguments, 0)})",
            IntrinsicId.PathBasename => $"$(basename -- {Arg(call.Arguments, 0)})",
            IntrinsicId.PathExtension => EmitPathExtension(call.Arguments),
            IntrinsicId.PathStem => EmitPathStem(call.Arguments),
            IntrinsicId.EnvGet => EmitEnvGet(call.Arguments),
            IntrinsicId.EnvHas => $"$(__sushi_env_name=$(printf '%s' {Arg(call.Arguments, 0)}); [[ -v $__sushi_env_name ]] && printf 'true' || printf 'false')",
            IntrinsicId.ProcessArgs => "\"$(__sushi_json_array \"$@\")\"",
            IntrinsicId.ProcessWhich => $"$(command -v -- {Arg(call.Arguments, 0)} 2>/dev/null || true)",
            IntrinsicId.ConsoleReadLine => "$(IFS= read -r __sushi_line; printf '%s' \"$__sushi_line\")",
            IntrinsicId.OsCwd => "$(pwd)",
            IntrinsicId.IoWriteText => $"$({EmitIoWriteText(call.Arguments)})",
            IntrinsicId.EnvSet => $"$({EmitEnvSet(call.Arguments)})",
            IntrinsicId.EnvUnset => $"$({EmitEnvUnset(call.Arguments)})",
            IntrinsicId.ProcessExit => $"$({EmitProcessExit(call.Arguments)})",
            IntrinsicId.ProcessSleep => $"$({EmitProcessSleep(call.Arguments)})",
            IntrinsicId.ConsoleError => $"$({EmitConsoleError(call.Arguments)})",
            IntrinsicId.OsChdir => $"$(cd -- {Arg(call.Arguments, 0)})",
            IntrinsicId.ProcessRun => $"\"$({EmitProcessRunInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessPipeline => $"\"$({EmitProcessPipelineInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessFail => $"\"$({EmitProcessFailInvocation(call.Arguments)})\"",
            IntrinsicId.ProcessRequireSuccess => $"\"$({EmitProcessRequireSuccessInvocation(call.Arguments)})\"",
            IntrinsicId.FsGlob => $"\"$({EmitFsGlobInvocation(call.Arguments)})\"",
            IntrinsicId.FsCreateDirectory => $"$({EmitFsCreateDirectory(call.Arguments)})",
            IntrinsicId.FsRemove => $"$({EmitFsRemove(call.Arguments)})",
            IntrinsicId.FsCopy => $"$({EmitFsCopy(call.Arguments)})",
            IntrinsicId.FsMove => $"$({EmitFsMove(call.Arguments)})",
            IntrinsicId.ArchiveZip => $"$({EmitArchiveZip(call.Arguments)})",
            IntrinsicId.ArchiveUnzip => $"$({EmitArchiveUnzip(call.Arguments)})",
            IntrinsicId.HttpGet => $"\"$({EmitHttpGetInvocation(call.Arguments)})\"",
            IntrinsicId.HttpPost => $"\"$({EmitHttpPostInvocation(call.Arguments)})\"",
            IntrinsicId.HttpDownload => $"$({EmitHttpDownload(call.Arguments)})",
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
        return EmitIoWriteText(path, text, append);
    }

    private static string EmitIoWriteText(string path, string text, string append)
    {
        return "__sushi_path=$(printf '%s' " + path + "); " +
               "__sushi_dir=$(dirname -- \"$__sushi_path\"); " +
               "if [[ \"$__sushi_dir\" != \".\" && ! -d \"$__sushi_dir\" ]]; then mkdir -p -- \"$__sushi_dir\"; fi; " +
               "if [[ " + append + " == 'true' ]]; then printf '%s' " + text + " >> \"$__sushi_path\"; else printf '%s' " + text + " > \"$__sushi_path\"; fi";
    }

    private string EmitFsCreateDirectory(IReadOnlyList<IrExpression> arguments) =>
        $"mkdir -p -- {Arg(arguments, 0)}";

    private string EmitFsRemove(IReadOnlyList<IrExpression> arguments) =>
        $"if [[ {Arg(arguments, 1)} == 'true' ]]; then rm -rf -- {Arg(arguments, 0)}; else rm -f -- {Arg(arguments, 0)}; fi";

    private string EmitFsCopy(IReadOnlyList<IrExpression> arguments) =>
        $"if [[ {Arg(arguments, 2)} == 'true' ]]; then cp -R -- {Arg(arguments, 0)} {Arg(arguments, 1)}; else cp -- {Arg(arguments, 0)} {Arg(arguments, 1)}; fi";

    private string EmitFsMove(IReadOnlyList<IrExpression> arguments) =>
        $"mv -f -- {Arg(arguments, 0)} {Arg(arguments, 1)}";

    private string EmitArchiveZip(IReadOnlyList<IrExpression> arguments) =>
        $"zip -r -- {Arg(arguments, 1)} {Arg(arguments, 0)}";

    private string EmitArchiveUnzip(IReadOnlyList<IrExpression> arguments) =>
        $"unzip -o -- {Arg(arguments, 0)} -d {Arg(arguments, 1)}";

    private string EmitEnvSet(IReadOnlyList<IrExpression> arguments)
    {
        var name = Arg(arguments, 0);
        var value = Arg(arguments, 1);
        return EmitEnvSet(name, value);
    }

    private static string EmitEnvSet(string name, string value)
    {
        return $"__sushi_env_name=$(printf '%s' {name}); __sushi_env_value=$(printf '%s' {value}); export \"$__sushi_env_name=$__sushi_env_value\"";
    }

    private string EmitEnvUnset(IReadOnlyList<IrExpression> arguments) =>
        $"__sushi_env_name=$(printf '%s' {Arg(arguments, 0)}); unset \"$__sushi_env_name\"";

    private string EmitProcessSleep(IReadOnlyList<IrExpression> arguments) =>
        $"sleep \"$(({Arg(arguments, 0)} / 1000)).$(({Arg(arguments, 0)} % 1000))\"";

    private string EmitConsoleError(IReadOnlyList<IrExpression> arguments) =>
        $"printf '%s\\n' {Arg(arguments, 0)} >&2";

    private string EmitPathExtension(IReadOnlyList<IrExpression> arguments) =>
        $"$(__sushi_base=$(basename -- {Arg(arguments, 0)}); if [[ \"$__sushi_base\" == *.* && \"$__sushi_base\" != .* ]]; then printf '.%s' \"${{__sushi_base##*.}}\"; fi)";

    private string EmitPathStem(IReadOnlyList<IrExpression> arguments) =>
        $"$(__sushi_base=$(basename -- {Arg(arguments, 0)}); if [[ \"$__sushi_base\" == *.* && \"$__sushi_base\" != .* ]]; then printf '%s' \"${{__sushi_base%.*}}\"; else printf '%s' \"$__sushi_base\"; fi)";

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

    private string EmitHttpDownload(IReadOnlyList<IrExpression> arguments) =>
        $"curl -fsSL -- {Arg(arguments, 0)} -o {Arg(arguments, 1)}";

    private string Arg(IReadOnlyList<IrExpression> arguments, int index)
    {
        return index < arguments.Count ? EmitValueExpression(arguments[index]) : "''";
    }
}
