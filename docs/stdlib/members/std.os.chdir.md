## Usage

`std.os.chdir(path)` changes the current working directory. Import it with `use std.os.chdir`.

## Parameters

- `path` is an existing directory.

## Returns

This function does not return a value.

## Examples

```sushi
use std.os.chdir

chdir("project")
```

## Errors and portability

The directory must exist and be accessible. The change affects later file and process operations in this script, not the terminal that launched it.
