## Usage

`std.path.dirname(path)` gets the containing directory portion of a path. Import it with `use std.path.dirname`.

## Parameters

- `path` is a path string.

## Returns

Returns a directory path string.

## Examples

```sushi
use std.path.dirname

println(dirname("output/logs/build.txt"))
```

## Errors and portability

This works on path text and does not require that the path exists. Separator and root-path handling follows the selected target platform.
