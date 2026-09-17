## Usage

`std.string.upper(value)` changes text to uppercase. Import it with `use std.string.upper`, or call `text.upper()`.

## Parameters

- `value` is the string to transform.

## Returns

Returns a new uppercase string.

## Examples

```sushi
println("warning".upper())
```

## Errors and portability

Case conversion can vary with the target locale. Avoid it for locale-sensitive identifiers.
