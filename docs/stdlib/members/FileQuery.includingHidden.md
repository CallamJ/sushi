## Usage

`includingHidden()` returns a new query that includes both ordinary and dot-prefixed entries.

```sushi
string[] entries = fs.query(".").includingHidden().entries()
```

Queries exclude hidden entries by default.
