## Usage

`std.string.endsWith(value, suffix)` tests whether text ends with literal text. Import it with `use std.string.endsWith`, or call `text.endsWith(suffix)`.

## Parameters

- `value` is the text to test.
- `suffix` is the required ending text.

## Returns

Returns `true` when `value` ends with `suffix`.

## Examples

```sushi
if (file.endsWith(".sushi")) { println("Sushi source") }
```

## Errors and portability

The comparison is case-sensitive and literal on every target.
