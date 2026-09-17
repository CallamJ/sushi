## Usage

`matching(pattern)` returns a new query that keeps entries whose filename matches a glob-style pattern.

```sushi
string[] files = fs.query("src").recursive().matching("*.sushi").files()
```

`pattern` is a string expression. Use `excluding()` to remove a matching subset.
