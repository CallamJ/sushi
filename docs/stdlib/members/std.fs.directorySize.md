## Usage

`std.fs.directorySize(path, recursive = false)` sums the byte lengths of regular files in a directory. Import it with `use std.fs.directorySize`.

## Parameters

- `path` is the directory to measure.
- `recursive` includes files in nested directories when `true`. It defaults to `false`.

## Returns

Returns an `int` byte count. Directory metadata is not included.

## Examples

```sushi
use std.fs.directorySize

println(directorySize("build"))
println(directorySize("build", recursive: true))
```

## Errors and portability

Missing paths, inaccessible directories, and regular files passed as `path` are runtime errors. The operation uses native directory enumeration and does not add a Sushi runtime helper.
