## Usage

`std.process.args()` returns command-line arguments passed to the Sushi program. Import it with `use std.process.args`.

## Parameters

This function has no parameters.

## Returns

Returns a `string[]` in argument order. It does not include the script name.

## Examples

```sushi
use std.process.args

for (string argument : args()) { println(argument) }
```

## Errors and portability

Arguments are supplied by the shell that launches the generated script. Shell quoting determines how words become arguments.
