## Usage

`std.env.has(name)` checks whether an environment variable is set. Import it with `use std.env.has`.

## Parameters

- `name` is the variable name.

## Returns

Returns `true` when the variable exists, even if its value is empty.

## Examples

```sushi
use std.env.has

if (has("CI")) { println("Running in CI") }
```

## Errors and portability

Variable-name case behavior follows the selected platform.
