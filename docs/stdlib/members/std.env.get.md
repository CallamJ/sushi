## Usage

`std.env.get(name, fallback = null)` reads an environment variable. Import it with `use std.env.get`.

## Parameters

- `name` is the environment-variable name.
- `fallback` is returned when it is not set.

## Returns

Returns a string or `null` when missing and no fallback is supplied.

## Examples

```sushi
use std.env.get

string home = get("HOME", ".")
```

## Errors and portability

Environment variable names are case-sensitive on Unix-like targets and usually case-insensitive on Windows.
