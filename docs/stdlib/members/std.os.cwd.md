## Usage

`std.os.cwd()` gets the current working directory. Import it with `use std.os.cwd`.

## Parameters

This function has no parameters.

## Returns

Returns the current directory as a string.

## Examples

```sushi
use std.os.cwd

println(cwd())
```

## Errors and portability

The working directory belongs to the running script and can be changed by `std.os.chdir`.
