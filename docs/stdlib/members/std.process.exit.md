## Usage

`std.process.exit(code = 0)` immediately ends the program with an exit status. Import it with `use std.process.exit`.

## Parameters

- `code` is the integer status sent to the calling process; `0` means success.

## Returns

This function does not return a value because execution stops.

## Examples

```sushi
use std.process.exit

if (!ready) { exit(1) }
```

## Errors and portability

Exit codes are most portable in the range `0` through `255`. A non-zero code normally signals failure to automation tools.
