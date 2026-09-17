## Usage

`std.env.set(name, value)` sets an environment variable for the current script and child processes. Import it with `use std.env.set`.

## Parameters

- `name` is the variable name.
- `value` is its new text value.

## Returns

This function does not return a value.

## Examples

```sushi
use std.env.set

set("MODE", "production")
```

## Errors and portability

The change does not modify the parent terminal's environment after the script exits. Name case behavior differs between Unix and Windows.
