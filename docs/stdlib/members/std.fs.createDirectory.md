## Usage

`std.fs.createDirectory(path)` creates a directory and any missing parent directories. Import it with `use std.fs.createDirectory`.

## Parameters

- `path` is the directory to create.

## Returns

This function does not return a value.

## Examples

```sushi
use std.fs.createDirectory

createDirectory("output/reports")
```

## Errors and portability

It is safe when the directory already exists. The operation fails at runtime if a parent is a file or permissions are insufficient.
