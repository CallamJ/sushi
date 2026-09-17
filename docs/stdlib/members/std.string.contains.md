## Usage

`std.string.contains(value, needle)` checks whether text occurs inside another string. Import it with `use std.string.contains`, or call `text.contains(needle)`.

## Parameters

- `value` is the text to search.
- `needle` is the literal text to find.

## Returns

Returns `true` when `needle` occurs at least once.

## Examples

```sushi
if (filename.contains(".bak")) { println("Backup file") }
```

## Errors and portability

This is a literal substring search, not a regular-expression match. Use `isMatch` when pattern matching is intended.
