## Usage

`std.fs.move(source, destination)` renames or moves a file-system item. Import it with `use std.fs.move`.

## Parameters

- `source` is the existing file or directory.
- `destination` is its new path.

## Returns

This function does not return a value.

## Examples

```sushi
use std.fs.move

move("report.tmp", "report.txt")
```

## Errors and portability

Moving across file systems may be implemented as copy-and-delete by the target. Existing destination and permission behavior is target-dependent.
