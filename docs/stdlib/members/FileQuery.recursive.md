## Usage

`recursive()` returns a new query that searches the root directory and all of its descendants.

```sushi
var sources = fs.query("src").recursive()
```

The original query is unchanged.
