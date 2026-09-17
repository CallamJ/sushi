## Usage

`std.http.download(url, destination)` downloads a URL to a file. Import it with `use std.http.download`.

## Parameters

- `url` is the HTTP or HTTPS address to download.
- `destination` is the output file path.

## Returns

This function does not return a value.

## Examples

```sushi
use std.http.download

download("https://example.com/file.txt", "file.txt")
```

## Errors and portability

Bash/Zsh targets require `curl`; PowerShell uses .NET HTTP support. The destination directory must exist and network or write failures are runtime errors.
