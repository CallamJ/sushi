## Usage

`std.math.ceil(value)` rounds a number up toward positive infinity. Import it with `use std.math.ceil`.

## Parameters

- `value` is the number to round up.

## Returns

Returns an `int`. For example, `ceil(1.2)` is `2`, while `ceil(-1.8)` is `-1`.

## Examples

```sushi
use std.math.ceil

int requiredPages = ceil(bytes / 4096.0)
```

## Errors and portability

The value must be numeric at runtime. The result follows mathematical ceiling semantics on every target and does not add a Sushi runtime helper.
