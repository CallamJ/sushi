## Usage

`std.http.post(url, body, headers = null, contentType = "application/json")` sends an HTTP POST request. Import it with `use std.http.post`.

## Parameters

- `url` is the HTTP or HTTPS address to request.
- `body` is the request content.
- `headers` optionally supplies additional request headers.
- `contentType` describes the body format and defaults to JSON.

## Returns

Returns an object with `status`, `body`, `ok`, and `error`.

## Examples

```sushi
use std.http.post

var response = post("https://example.com/events", "{\"kind\":\"build\"}")
println(response.status)
```

## Errors and portability

Bash/Zsh targets require `curl`; PowerShell uses .NET HTTP support. Check `ok` before relying on the response body.
