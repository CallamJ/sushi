## Usage

`string(value)` converts a value to text. Use it when a value must be joined with text or passed to an API that expects a string.

No import is required.

## Parameters

- `value` is the value to convert.

## Returns

Returns the text representation of `value`.

## Examples

```sushi
int count = 3
println("Files: " + string(count))
```

## Errors and portability

The target shell performs the final conversion. Primitive values have portable text forms; avoid relying on the display format of complex objects.
