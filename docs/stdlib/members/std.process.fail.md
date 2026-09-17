## Usage

`std.process.fail(result)` ends the script when a process result represents failure. Import it with `use std.process.fail`.

## Parameters

- `result` is a result object returned by `run` or `pipeline`.

## Returns

This function does not return a value when it exits; it otherwise leaves a successful result alone.

## Examples

```sushi
use std.process.{run, fail}

fail(run("git", ["status"]))
```

## Errors and portability

Pass only a process result with the documented fields. The exit code is forwarded to the calling environment.
