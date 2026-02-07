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
    private EmitContext _context = null!;
    private int _indent;

    public string Emit(IrProgram program, EmitContext context)
    {
        _builder.Clear();
        _context = context;
        _indent = 0;

        WriteLine("#!/usr/bin/env bash");
        WriteLine("set -euo pipefail");
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
        _builder.AppendLine(
"""
__sushi_to_json() {
  local value="${1-}"
  if printf '%s' "$value" | jq -e . >/dev/null 2>&1; then
    printf '%s' "$value"
  else
    jq -cn --arg v "$value" '$v'
  fi
}

__sushi_json_array() {
  if [[ "$#" -eq 0 ]]; then
    jq -nc '[]'
    return
  fi
  printf '%s\n' "$@" | jq -Rsc 'split("\n")[:-1] | map(. as $raw | try ($raw | fromjson) catch $raw)'
}

__sushi_json_object() {
  local result='{}'
  while [[ "$#" -gt 1 ]]; do
    local key value
    key="$1"
    value="$2"
    shift 2
    result="$(printf '%s' "$result" | jq -c --arg k "$key" --arg v "$value" '. + {($k): (try ($v | fromjson) catch $v)}')"
  done
  printf '%s' "$result"
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
""");
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
                WriteLine($"{SanitizeName(variable.Name)}={EmitValueExpression(variable.Initializer ?? new IrLiteralExpression(null))}");
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

    private void EmitFunctionDeclaration(IrFunctionDeclarationStatement statement)
    {
        WriteLine($"{SanitizeName(statement.Name)}() {{");
        _indent++;

        for (var i = 0; i < statement.Parameters.Count; i++)
        {
            var param = SanitizeName(statement.Parameters[i]);
            WriteLine($"local {param}=\"${i + 1}\"");
        }

        EmitStatement(statement.Body, inFunction: true);
        _indent--;
        WriteLine("}");
    }

    private void EmitReturn(IrReturnStatement statement, bool inFunction)
    {
        if (statement.Expression != null)
        {
            WriteLine($"printf '%s\\n' {EmitValueExpression(statement.Expression)}");
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
                    var name = SanitizeName(identifier.Name);
                    WriteLine($"{name}=$(( ${{{name}:-0}} {op} 1 ))");
                    return;
                }
                break;
        }

        _context.Error(UnsupportedEmitCode, $"Unsupported expression statement in Bash emitter: {expression.GetType().Name}");
    }

    private string EmitAssignmentExpression(IrAssignmentExpression assignment)
    {
        var name = SanitizeName(assignment.Target.Name);
        if (assignment.Operator == "=")
        {
            return $"{name}={EmitValueExpression(assignment.Value)}";
        }

        var mathOp = assignment.Operator[0];
        return $"{name}=$(( ${{{name}:-0}} {mathOp} {EmitArithmeticExpression(assignment.Value)} ))";
    }

    private string EmitCallCommand(IrCallExpression call)
    {
        var callee = SanitizeName(call.Callee);
        var arguments = call.Arguments.Select(EmitValueExpression).ToList();
        return arguments.Count > 0
            ? $"{callee} {string.Join(" ", arguments)}"
            : callee;
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
            return $"[[ -n \"${{{SanitizeName(identifier.Name)}:-}}\" ]]";
        }

        return $"[[ {EmitValueExpression(expression)} != '' ]]";
    }

    private string EmitComparableValue(IrExpression expression)
    {
        return expression switch
        {
            IrIdentifierExpression identifier => $"\"${{{SanitizeName(identifier.Name)}:-}}\"",
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
            IrIdentifierExpression identifier => $"\"${{{SanitizeName(identifier.Name)}:-}}\"",
            IrArrayLiteralExpression array => EmitArrayLiteral(array),
            IrObjectLiteralExpression obj => EmitObjectLiteral(obj),
            IrMemberAccessExpression member => $"\"$(__sushi_json_member {EmitValueExpression(member.Target)} {Escape.BashSingleQuoted(member.MemberName)})\"",
            IrIndexExpression index => $"\"$(__sushi_json_index {EmitValueExpression(index.Target)} {EmitValueExpression(index.Index)})\"",
            IrUnaryExpression unary when unary.Operator is "-" or "+" =>
                $"$(( {unary.Operator}{EmitArithmeticExpression(unary.Operand)} ))",
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                $"$(( {EmitArithmeticExpression(binary)} ))",
            IrIntrinsicCallExpression intrinsicCall => EmitIntrinsicValue(intrinsicCall),
            IrCallExpression call => $"$({EmitCallCommand(call)})",
            IrAssignmentExpression assignment => $"$({EmitAssignmentExpression(assignment)}; printf '%s' \"${{{SanitizeName(assignment.Target.Name)}:-}}\")",
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

    private string EmitArithmeticExpression(IrExpression expression)
    {
        return expression switch
        {
            IrLiteralExpression literal when literal.Value is int or long or double or float or decimal
                => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
            IrLiteralExpression literal when literal.Value is bool boolean => boolean ? "1" : "0",
            IrLiteralExpression => "0",
            IrIdentifierExpression identifier => $"${{{SanitizeName(identifier.Name)}:-0}}",
            IrUnaryExpression unary when unary.Operator is "+" or "-" =>
                $"{unary.Operator}{EmitArithmeticExpression(unary.Operand)}",
            IrBinaryExpression binary when binary.Operator is "+" or "-" or "*" or "/" or "%" =>
                $"({EmitArithmeticExpression(binary.Left)} {binary.Operator} {EmitArithmeticExpression(binary.Right)})",
            _ => "0"
        };
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

    private static string SanitizeName(string name)
    {
        return name.Replace(".", "_").Replace("-", "_");
    }

    private string EmitIntrinsicCommand(IrIntrinsicCallExpression call)
    {
        return call.Id switch
        {
            IntrinsicId.Print => EmitPrint(call.Arguments, newline: false),
            IntrinsicId.Println => EmitPrint(call.Arguments, newline: true),
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
        return $"__sushi_env_name=$(printf '%s' {name}); __sushi_env_value=$(printf '%s' {value}); printf -v \"$__sushi_env_name\" '%s' \"$__sushi_env_value\"; export \"$__sushi_env_name\"";
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
        return $"$(__sushi_env_name=$(printf '%s' {name}); if [[ -n \"${{!__sushi_env_name+x}}\" ]]; then printf '%s' \"${{!__sushi_env_name}}\"; else printf '%s' {fallback}; fi)";
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
