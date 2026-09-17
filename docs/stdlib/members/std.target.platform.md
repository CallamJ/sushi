## Usage

`std.target.platform()` tells a script which operating-system target Sushi selected. Import it with `use std.target.platform`.

## Parameters

This function has no parameters.

## Returns

Returns the selected platform name as a string.

## Examples

```sushi
use std.target.platform

println("Generated for " + platform())
```

## Errors and portability

This is compile-time target information. It does not probe the operating system at runtime.
