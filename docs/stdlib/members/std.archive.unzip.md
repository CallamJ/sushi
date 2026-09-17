## Usage

`std.archive.unzip(source, destination)` extracts a ZIP archive. Import it with `use std.archive.unzip`.

## Parameters

- `source` is the ZIP file to extract.
- `destination` is the directory to receive its contents.

## Returns

This function does not return a value.

## Examples

```sushi
use std.archive.unzip

unzip("release.zip", "release")
```

## Errors and portability

The target needs native archive tooling. Treat archives from untrusted sources carefully because they can contain unexpected paths or large data.
