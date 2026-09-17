## Usage

`std.fs.exists(path)` checks whether a path exists. Import it with `use std.fs.exists`.

## Parameters

- `path` is a file-system path.

## Returns

Returns `true` for existing files or directories.

## Examples

```sushi
use std.fs.exists

if (!exists("settings.txt")) { println("Using defaults") }
```

## Errors and portability

An inaccessible path can be reported as absent by the target shell. Use a later read/write operation when you need a definitive permission failure.
