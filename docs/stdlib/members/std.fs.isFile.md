## Usage

`std.fs.isFile(path)` checks whether a path is a regular file. Import it with `use std.fs.isFile`.

## Parameters

- `path` is the path to inspect.

## Returns

Returns `true` only for regular files.

## Examples

```sushi
use std.fs.isFile

if (isFile("notes.txt")) { println("Readable file path") }
```

## Errors and portability

Returns `false` for missing paths and directories. Symlink treatment follows the target platform's file-system tools.
