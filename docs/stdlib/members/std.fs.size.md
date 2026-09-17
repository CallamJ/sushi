## Usage

`std.fs.size(path)` gets the byte length of a regular file. Import it with `use std.fs.size`.

## Parameters

- `path` is the file to measure.

## Returns

Returns an `int` byte count.

## Examples

```sushi
use std.fs.size

println("Bytes: " + string(size("archive.zip")))
```

## Errors and portability

Directories, missing paths, and inaccessible files are runtime errors. The result is bytes, not character count.
