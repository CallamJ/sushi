## Usage

`std.fs.copy(source, destination, recursive = false)` copies a file or directory. Import it with `use std.fs.copy`.

## Parameters

- `source` is the existing item to copy.
- `destination` is the new path.
- `recursive` is required for directories.

## Returns

This function does not return a value.

## Examples

```sushi
use std.fs.copy

copy("settings.txt", "backup/settings.txt")
```

## Errors and portability

The destination parent must exist unless native target behavior creates it. Copying directories requires `recursive: true`; permission and overwrite behavior comes from the target tool.
