## Usage

`std.http.get(url, headers = null)` makes an HTTP GET request. Import it with `use std.http.get`.

## Parameters

- `url` is the HTTP or HTTPS address to request.
- `headers` is an optional object of request-header names and values.

## Returns

Returns an object with `status`, `body`, `ok`, and `error`. `ok` is `true` for a successful response.

## Examples

```sushi
use std.http.get

var response = get("https://example.com")
if (response.ok) { println(response.body) }
```

## Errors and portability

Bash/Zsh targets require `curl`; PowerShell uses .NET HTTP support. Transport failures produce `ok: false` and an error value rather than a successful response.
