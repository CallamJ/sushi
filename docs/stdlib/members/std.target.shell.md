## Usage

`std.target.shell()` tells a script which shell target Sushi selected during transpilation. Import it before use with `use std.target.shell`.

## Parameters

This function has no parameters.

## Returns

Returns a string such as `bash`, `zsh`, or `powershell`.

## Examples

```sushi
use std.target.shell

println("Generated for " + shell())
```

## Errors and portability

The value describes the selected Sushi target, not necessarily the interactive shell that later launches the script.
