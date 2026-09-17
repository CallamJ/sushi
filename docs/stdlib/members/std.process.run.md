## Usage

`std.process.run(command, args = null, cwd = null, env = null, input = null, timeoutMs = 0, allowFailure = false, stream = false)` runs one external command. Import it with `use std.process.run`.

## Parameters

- `command` is the executable to start.
- `args` is an optional array of arguments; use an array instead of manually quoting one command string.
- `cwd`, `env`, and `input` optionally set its working directory, environment, and standard input.
- `timeoutMs` is a timeout in milliseconds; `0` disables the timeout.
- `allowFailure` keeps a non-zero command status in the result instead of failing the script.
- `stream` forwards command output while it runs when supported.

## Returns

Returns an object with `code`, `stdout`, `stderr`, and `ok` fields.

## Examples

```sushi
use std.process.run

var result = run("git", ["status", "--short"])
if (result.ok) { println(result.stdout) }
```

## Errors and portability

When `allowFailure` is false, a non-zero exit status fails the script. The command must exist on the target machine; quoting and executable lookup are handled by the target shell.
