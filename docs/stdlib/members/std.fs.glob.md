## Usage

`std.fs.glob(pattern, cwd = null)` finds paths matching a glob pattern. Import it with `use std.fs.glob`.

## Parameters

- `pattern` uses `*`, `?`, character classes such as `[abc]`, and recursive `**`.
- `cwd` optionally selects the directory to search; omit it to use the process working directory.

## Returns

Returns a sorted `string[]` of relative matching paths. No matches return an empty array.

## Examples

```sushi
use std.fs.glob

string[] sources = glob("src/**/*.sushi")
for (string source : sources) { println(source) }
```

## Errors and portability

`cwd` must exist and be a directory. Dotfiles are included, results use `/` separators, and directory symlinks are not traversed. Brace expansion is not supported.
