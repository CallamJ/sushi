## Usage

`println` writes a value to standard output and then starts a new line. Use it for normal command-line messages.

No import is required.

## Parameters

- `value` is the value to write. It defaults to an empty string, so `println()` writes a blank line.

## Returns

This function does not return a value.

## Examples

```sushi
println("Build finished")
println()
```

## Errors and portability

Output goes to the program's normal output stream on Bash, Zsh, and PowerShell.
