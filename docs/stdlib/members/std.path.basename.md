## Usage

`std.path.basename(path)` gets the final name portion of a path. Import it with `use std.path.basename`.

## Parameters

- `path` is a path string.

## Returns

Returns the final file or directory name as a string.

## Examples

```sushi
use std.path.basename

println(basename("output/logs/build.txt"))
```

## Errors and portability

The path need not exist. Trailing separators and platform path rules are handled by the target's native facilities.
