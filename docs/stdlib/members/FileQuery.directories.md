## Usage

`directories()` runs the query and returns matching directories as `string[]` paths relative to the query root. The root prefix is removed and results are never absolute.

```sushi
string[] folders = fs.query("src").recursive().directories()
```

Result order is determined by the native filesystem enumeration and is not guaranteed. Symbolic links are excluded and never traversed.
