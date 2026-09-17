## Usage

`std.string.startsWith(value, prefix)` tests whether text begins with literal text. Import it with `use std.string.startsWith`, or call `text.startsWith(prefix)`.

## Parameters

- `value` is the text to test.
- `prefix` is the required opening text.

## Returns

Returns `true` when `value` begins with `prefix`.

## Examples

```sushi
if (path.startsWith("./")) { println("Relative path") }
```

## Errors and portability

The comparison is case-sensitive and literal on every target.
