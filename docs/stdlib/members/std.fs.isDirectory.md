## Usage

`std.fs.isDirectory(path)` checks whether a path is a directory. Import it with `use std.fs.isDirectory`.

## Parameters

- `path` is the path to inspect.

## Returns

Returns `true` only for directories.

## Examples

```sushi
use std.fs.isDirectory

if (isDirectory("build")) { println("Build directory exists") }
```

## Errors and portability

Returns `false` for missing paths and regular files. Symlink treatment follows the target platform's file-system tools.
