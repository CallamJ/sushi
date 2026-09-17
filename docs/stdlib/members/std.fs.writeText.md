## Usage

`std.fs.writeText(path, text, append = false)` writes text to a file. Import it with `use std.fs.writeText`.

## Parameters

- `path` is the destination file.
- `text` is the content to write.
- `append` adds text to an existing file when `true`; the default `false` replaces it.

## Returns

This function does not return a value.

## Examples

```sushi
use std.fs.writeText

writeText("report.txt", "Done\n")
writeText("report.txt", "More\n", append: true)
```

## Errors and portability

Parent directories must already exist and the process needs write permission. Replacing a file is not an atomic save operation.
