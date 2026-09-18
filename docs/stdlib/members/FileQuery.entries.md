## Usage

`entries()` runs the query and returns both matching files and directories as `string[]` paths relative to the query root. The root prefix is removed and results are never absolute.

Entry order is determined by the native filesystem enumeration and is not guaranteed. Symbolic links are excluded.

```sushi
string[] entries = fs.query("src").recursive().entries()
```
