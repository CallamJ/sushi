## Usage

`std.env.unset(name)` removes an environment variable from the current script environment. Import it with `use std.env.unset`.

## Parameters

- `name` is the variable name.

## Returns

This function does not return a value.

## Examples

```sushi
use std.env.unset

unset("DEBUG")
```

## Errors and portability

The removal affects only the script and processes it starts, not its parent terminal.
