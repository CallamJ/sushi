## Usage

`std.console.error(value = "")` writes a message to standard error. Import it with `use std.console.error`.

## Parameters

- `value` is the message to report. It defaults to an empty string.

## Returns

This function does not return a value.

## Examples

```sushi
use std.console.error

error("Configuration file is missing")
```

## Errors and portability

Standard error is separate from normal output, so callers can redirect messages independently on Bash, Zsh, and PowerShell.
