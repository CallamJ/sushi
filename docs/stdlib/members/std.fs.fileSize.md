## Usage

`std.fs.fileSize(path)` gets the byte length of one regular file. Import it with `use std.fs.fileSize`.

## Parameters

- `path` is the regular file to measure.

## Returns

Returns an `int` byte count.

## Examples

```sushi
use std.fs.fileSize

println("Bytes: " + string(fileSize("archive.zip")))
```

## Errors and portability

Missing paths, inaccessible files, and directories are runtime errors. The result is bytes, not character count.
