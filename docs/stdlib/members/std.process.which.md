## Usage

`std.process.which(command)` locates an executable available on `PATH`. Import it with `use std.process.which`.

## Parameters

- `command` is the executable name, such as `git`.

## Returns

Returns the resolved executable path as a string, or an empty string when it cannot be found.

## Examples

```sushi
use std.process.which

if (which("git") == "") { println("Git is required") }
```

## Errors and portability

Search behavior follows the target shell and its `PATH`. Do not treat a found path as proof that the command can run successfully.
