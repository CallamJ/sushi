## Usage

`std.console.readLine()` waits for one line of terminal input. Import it with `use std.console.readLine`.

## Parameters

This function has no parameters.

## Returns

Returns the entered line as a string, without its trailing newline.

## Examples

```sushi
use std.console.readLine

print("Name: ")
string name = readLine()
println("Hello " + name)
```

## Errors and portability

This requires interactive standard input. Piped or closed input can return an empty value or fail according to the target shell.
