## Usage

`print` writes a value to standard output without ending the line. It is useful when several values should appear on one line.

No import is required.

## Parameters

- `value` is the value to write. It defaults to an empty string.

## Returns

This function does not return a value.

## Examples

```sushi
print("Downloading: ")
print("50%")
println()
```

## Errors and portability

Output goes to the program's normal output stream on Bash, Zsh, and PowerShell. Redirect the generated program's output when you need to save it.
