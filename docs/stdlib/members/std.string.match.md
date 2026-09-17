## Usage

`std.string.match(value, pattern)` finds a regular-expression match and returns details about it. Import it with `use std.string.match`.

## Parameters

- `value` is the text to search.
- `pattern` is a regular-expression pattern.

## Returns

Returns an object with `ok` (`bool`), `value` (`string`), `index` (`int`, `-1` when absent), and `groups` (`string[]`).

## Examples

```sushi
var result = std.string.match("release-42", "[0-9]+")
if (result.ok) { println(result.value) }
```

## Errors and portability

Regex support follows the same target limitations as `isMatch`; avoid engine-specific patterns for portable scripts.
