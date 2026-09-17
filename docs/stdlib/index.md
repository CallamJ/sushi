# Sushi standard library

The standard library provides file, path, environment, process, operating-system,
console, archive, HTTP, target, and string operations. Import a member directly:

```sushi
use std.fs.glob
```

Or import a module with an alias:

```sushi
use std.fs as fs
string[] files = fs.glob("**/*.sushi")
```

`print`, `println`, and `string(value)` need no import. String operations also
have method sugar: `text.length()` is the same operation as
`std.string.length(text)`.

See the member documents in this directory for the complete API reference.
