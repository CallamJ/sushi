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

`files()`, `directories()`, and `entries()` return root-relative `string[]` paths with `/` separators. Ordering is native to the selected target shell. Directory symlinks are not traversed.

## Returns

`query` returns a reusable `FileQuery`; its terminal methods return `string[]` snapshots.

## Errors and portability

The query root must exist and be a directory when a terminal operation runs. The generated code uses native filesystem enumeration for the selected target and does not add a Sushi glob helper.
