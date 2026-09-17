## Usage

`std.string.length(value)` counts characters in a string. Import it with `use std.string.length`, or call `text.length()` without an import.

## Parameters

- `value` is the string to measure.

## Returns

Returns an `int` character count.

## Examples

```sushi
if (password.length() < 12) {
    println("Choose a longer password")
}
```

## Errors and portability

The count follows each target's native string representation. Treat it as a user-facing character count, not a byte count for files.
