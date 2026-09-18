## Usage

`files()` runs the query and returns matching regular files as `string[]` paths relative to the query root. The root prefix is removed and results are never absolute.

```sushi
string[] files = fs.query("src").recursive().files()
```

Paths use `/` separators on every target.

Result order is determined by the native filesystem enumeration and is not guaranteed. Symbolic links are excluded.
