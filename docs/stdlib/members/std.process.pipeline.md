## Usage

`std.process.pipeline(stages, cwd = null, env = null, input = null, timeoutMs = 0, allowFailure = false, stream = false)` runs a sequence of command stages. Import it with `use std.process.pipeline`.

## Parameters

- `stages` is an array of stage descriptors, each with a command and optional arguments.
- The remaining parameters have the same meaning as `std.process.run`.

## Returns

Returns the final stage result object with `code`, `stdout`, `stderr`, and `ok`.

## Examples

```sushi
use std.process.pipeline

var stages = [{ command: "printf", args: ["hello"] }, { command: "wc", args: ["-c"] }]
println(pipeline(stages).stdout)
```

## Errors and portability

The target must provide every stage command. A failed stage follows the `allowFailure` behavior used by `std.process.run`.
