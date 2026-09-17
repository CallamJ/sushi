## Usage

`std.process.requireSuccess(result)` verifies a process result before using it. Import it with `use std.process.requireSuccess`.

## Parameters

- `result` is a result object returned by `run` or `pipeline`.

## Returns

Returns the successful result, allowing chained member access.

## Examples

```sushi
use std.process.{run, requireSuccess}

println(requireSuccess(run("git", ["rev-parse", "HEAD"])).stdout)
```

## Errors and portability

A failed result ends the script using its process status. Use `allowFailure: true` with `run` only when you intend to inspect failure yourself.
