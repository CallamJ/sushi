## Usage

`std.fs.remove(path, recursive = false)` deletes a file or directory. Import it with `use std.fs.remove`.

## Parameters

- `path` is the item to delete.
- `recursive` permits deleting directory contents when `true`.

## Returns

This function does not return a value.

## Examples

```sushi
use std.fs.remove

remove("old-report.txt")
remove("scratch", recursive: true)
```

## Errors and portability

Deletion is destructive and cannot be undone. Use `recursive: true` only for a deliberately controlled path; missing paths and permission failures are runtime errors.
