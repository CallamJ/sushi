## Usage

`files()` runs the query and returns matching regular files as root-relative `string[]` paths.

```sushi
string[] files = fs.query("src").recursive().files()
```

Paths use `/` separators on every target.
