## Usage

`excluding(pattern)` returns a new query that removes entries whose filename matches a glob-style pattern.

```sushi
string[] files = fs.query("src").recursive().excluding("*Tests.cs").files()
```

The exclusion is applied after other query filters.
