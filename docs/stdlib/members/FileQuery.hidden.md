## Usage

`hidden()` returns a new query that keeps only dot-prefixed entries.

```sushi
string[] hiddenFiles = fs.query(".").hidden().files()
```
