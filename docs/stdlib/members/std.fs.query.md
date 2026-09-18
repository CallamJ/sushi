## Usage

`std.fs.query(root = ".")` creates a reusable `FileQuery`. With no argument, it searches from the process working directory. Import the filesystem module with `use std.fs as fs`.

```sushi
var query = fs.query("src").recursive().matching("*.sushi")
string[] files = query.files()
```

## Parameters

- `root` is the directory from which each query starts. It defaults to `"."`, the process working directory.

## Examples

```sushi
var sourceFiles = fs.query("src").recursive().matching("*.cs")
for (string file : sourceFiles.files()) {
    println(file)
}

string[] cwdSources = fs.query().recursive().matching("*.sushi").files()
```

The root and patterns may be normal expressions. Calling `files()`, `directories()`, or `entries()` performs a fresh search, so the same query can be used again after the filesystem changes.

## Query filters

- `recursive()` includes descendants; queries otherwise inspect only direct children.
- `matching(pattern)` keeps entry names matching a glob-style pattern.
- `excluding(pattern)` removes matching entry names.
- Hidden entries are excluded by default. Use `includingHidden()` for both visible and hidden entries, or `hidden()` for only hidden entries.

Filters return a new query and leave the original query unchanged.

## Results

`files()`, `directories()`, and `entries()` return paths relative to the query root as `string[]`: the root prefix is removed, results are never absolute, and `/` is always the separator. For example, `fs.query("src").files()` returns `main.sushi`, not `src/main.sushi` or an absolute path.

Result order is determined by the native filesystem enumeration and is not guaranteed. Symbolic links (including directory links) are excluded from all query results and are never traversed. This avoids target-specific link-following behavior and cycles.

## Returns

`query` returns a reusable `FileQuery`; its terminal methods return `string[]` snapshots.

## Errors and portability

The query root must exist and be a directory when a terminal operation runs. The generated code uses native filesystem enumeration for the selected target and does not add a Sushi glob helper.
