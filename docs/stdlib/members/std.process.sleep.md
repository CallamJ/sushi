## Usage

`std.process.sleep(milliseconds)` pauses the current script. Import it with `use std.process.sleep`.

## Parameters

- `milliseconds` is the requested delay in thousandths of a second.

## Returns

This function does not return a value.

## Examples

```sushi
use std.process.sleep

println("Waiting")
sleep(500)
```

## Errors and portability

Actual timing is approximate and depends on operating-system scheduling and target shell timer resolution.
