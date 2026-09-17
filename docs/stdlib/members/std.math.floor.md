## Usage

`std.math.floor(value)` rounds a number down toward negative infinity. Import it with `use std.math.floor`.

## Parameters

- `value` is the number to round down.

## Returns

Returns an `int`. For example, `floor(1.8)` is `1`, while `floor(-1.2)` is `-2`.

## Examples

```sushi
use std.math.floor

int completePages = floor(bytes / 4096.0)
```

## Errors and portability

The value must be numeric at runtime. The result follows mathematical floor semantics on every target and does not add a Sushi runtime helper.
