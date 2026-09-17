## Usage

`std.string.trim(value)` removes whitespace at both ends of text. Import it with `use std.string.trim`, or use the sugar form `text.trim()`.

## Parameters

- `value` is the string to clean.

## Returns

Returns a new string; the original value is unchanged.

## Examples

```sushi
string name = "  Ada  ".trim()
println(name)
```

## Errors and portability

Whitespace follows the target shell's text facilities. Use it for ordinary command-line input, not for byte-level parsing.
