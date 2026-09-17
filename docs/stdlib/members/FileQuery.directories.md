## Usage

`directories()` runs the query and returns matching directories as root-relative `string[]` paths.

```sushi
string[] folders = fs.query("src").recursive().directories()
```
