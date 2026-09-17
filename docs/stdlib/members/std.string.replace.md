## Usage

`std.string.replace(value, old, new)` replaces every literal occurrence of one string with another. Import it with `use std.string.replace`, or call `text.replace(old, new)`.

## Parameters

- `value` is the original text.
- `old` is the literal text to replace.
- `new` is replacement text.

## Returns

Returns a new string with all matches replaced.

## Examples

```sushi
println("2026-09-16".replace("-", "/"))
```

## Errors and portability

`old` is literal text, not a regular expression. An empty search string has target-specific behavior; avoid it.
