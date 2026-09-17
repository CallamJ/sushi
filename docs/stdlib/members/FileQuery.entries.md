## Usage

`entries()` runs the query and returns both matching files and directories as root-relative `string[]` paths.

```sushi
string[] entries = fs.query("src").recursive().entries()
```
