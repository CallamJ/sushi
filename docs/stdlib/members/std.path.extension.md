## Usage

`std.path.extension(path)` gets a path's extension, including its leading dot when present. Import it with `use std.path.extension`.

## Parameters

- `path` is a path string.

## Returns

Returns an extension string such as `.sushi`, or an empty string when none exists.

## Examples

```sushi
use std.path.extension

if (extension(file) == ".sushi") { println("Source") }
```

## Errors and portability

The path need not exist. Hidden-file and multi-dot-name behavior follows target path conventions.
