## Usage

`std.string.isMatch(value, pattern)` checks a string against a regular expression. Import it with `use std.string.isMatch`, or call `text.isMatch(pattern)`.

## Parameters

- `value` is the text to test.
- `pattern` is a regular-expression pattern.

## Returns

Returns `true` when the pattern matches.

## Examples

```sushi
if (version.isMatch("^[0-9]+\\.[0-9]+$")) { println("Valid") }
```

## Errors and portability

PowerShell uses .NET regular expressions. Bash and Zsh use their native tooling, so advanced .NET-only constructs are not portable.
