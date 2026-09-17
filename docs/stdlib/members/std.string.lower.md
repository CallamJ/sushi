## Usage

`std.string.lower(value)` changes text to lowercase. Import it with `use std.string.lower`, or call `text.lower()`.

## Parameters

- `value` is the string to transform.

## Returns

Returns a new lowercase string.

## Examples

```sushi
if (answer.trim().lower() == "yes") {
    println("Confirmed")
}
```

## Errors and portability

Case conversion can vary with the target locale. Use ASCII protocol keywords when exact cross-platform results matter.
