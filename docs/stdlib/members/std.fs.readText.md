## Usage

`std.fs.readText(path)` reads an entire text file. Import it with `use std.fs.readText`.

## Parameters

- `path` is the file path, relative to the process working directory or absolute.

## Returns

Returns the file contents as a string.

## Examples

```sushi
use std.fs.readText

string config = readText("settings.txt")
```

## Errors and portability

Missing, unreadable, or directory paths are runtime errors. Files are read with native shell/PowerShell facilities; use it for text rather than binary data.
