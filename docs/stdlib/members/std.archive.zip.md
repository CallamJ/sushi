## Usage

`std.archive.zip(source, destination)` creates a ZIP archive. Import it with `use std.archive.zip`.

## Parameters

- `source` is the file or directory to archive.
- `destination` is the ZIP file to create.

## Returns

This function does not return a value.

## Examples

```sushi
use std.archive.zip

zip("output", "output.zip")
```

## Errors and portability

The target needs its native ZIP tooling. Archive layout, metadata, and overwrite behavior can differ between Bash/Zsh systems and PowerShell.
