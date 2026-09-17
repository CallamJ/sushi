## Usage

`std.path.stem(path)` gets a file name without its final extension. Import it with `use std.path.stem`.

## Parameters

- `path` is a path string.

## Returns

Returns the name portion without its final extension.

## Examples

```sushi
use std.path.stem

println(stem("archive.tar.gz"))
```

## Errors and portability

The path need not exist. The final extension is removed; this is not a multi-extension archive parser.
