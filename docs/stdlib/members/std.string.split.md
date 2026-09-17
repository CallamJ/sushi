## Usage

`std.string.split(value, sep, limit = 0)` separates text into an array. Import it with `use std.string.split`, or call `text.split(sep)`.

## Parameters

- `value` is the text to divide.
- `sep` is the separator text.
- `limit` is the maximum number of pieces. `0` means no limit.

## Returns

Returns an array of strings.

## Examples

```sushi
string[] names = "Ada,Lin,Sam".split(",")
for (string name : names) { println(name) }
```

## Errors and portability

Use a non-empty separator for predictable results. Splitting rules are lowered to native target facilities and can differ for unusual Unicode input.
