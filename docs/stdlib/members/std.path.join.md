## Usage

`std.path.join(parts...)` combines path pieces using the target platform's path separator. Import it with `use std.path.join`.

## Parameters

- `parts` is one or more path segments.

## Returns

Returns the combined path as a string.

## Examples

```sushi
use std.path.join

string log = join("output", "logs", "build.txt")
```

## Errors and portability

This combines text; it does not create directories or check that the result exists. Prefer it over hard-coded separators for Windows portability.
