## Usage

`std.math.round(value, precision = 0)` rounds a number to the requested number of decimal places. Import it with `use std.math.round`.

## Parameters

- `value` is the number to round.
- `precision` is the number of decimal places to retain. It defaults to `0`; negative values round to tens, hundreds, and so on.

## Returns

Returns a `float`. Exact half values round away from zero: `round(1.5)` is `2`, and `round(-1.5)` is `-2`.

## Examples

```sushi
use std.math.round

float megabytes = round(bytes / 1_000_000.0, precision: 2)
```

## Errors and portability

The value must be numeric at runtime. Sushi uses the same half-away-from-zero rule on every target and lowers directly to target-native numeric operations.
