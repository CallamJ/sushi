# Runtime Prerequisites and Portability (M3)

This document defines what the generated scripts need at runtime, and where
behavior differs between Bash, Zsh, and PowerShell targets.

## 1. Runtime prerequisites

Default target selection:

- Windows: `powershell-windows` (PowerShell 5.1-compatible output)
- macOS: `Zsh`
- Linux/WSL: `Bash`

### 1.1 For transpiling `.sushi` source

- .NET SDK 9.0+

### 1.2 For running Bash output (`-t Bash`)

- Bash 4.3+ (native indexed/associative arrays and object namerefs)
- `curl`
- standard POSIX tools emitted for requested features (`mktemp`, `cat`, `find`, `timeout`)

### 1.3 For running Zsh output (`-t Zsh`)

- Zsh 5.0+ (native indexed and associative arrays)
- `curl`
- standard POSIX tools emitted for requested features (`mktemp`, `cat`, `find`, `timeout`)

### 1.4 For running PowerShell output (`-t powershell-windows`)

- Windows PowerShell 5.1+ or PowerShell 7+ (`pwsh`)
- .NET runtime available to PowerShell (for `System.Net.Http.HttpClient`)

## 2. Intrinsic behavior contract (M3)

### 2.1 `std.process.run(...)`

Returns an object with fields:

- `code` (number)
- `stdout` (string)
- `stderr` (string)
- `ok` (bool)
- `command` (string)
- `timedOut` (bool)

Notes:

- Arguments are passed as arrays to avoid unsafe string concat/splitting.
- If `allowFailure` is false and exit code is non-zero, script exits with that
  code.

### 2.2 `std.process.pipeline(...)`

Runs stage descriptors in order. Stage `stdout` becomes the next stage `stdin`.
Returns the same object shape as `std.process.run`, from the final stage.

### 2.3 JSON

JSON parsing and serialization are not built into Sushi. Calls under
`std.json.*` are rejected as unknown intrinsics (`SUSHI1301`). JSON support is
planned as an optional program dependency.

### 2.4 `std.fs.glob(pattern, cwd?)`

Import the API explicitly with `use std.fs.{glob}`. The Bash and Zsh emitters
lower the result directly into a native shell array; no array runtime or JSON
runtime is required.

- Supports recursive `**` patterns.
- Supports `*`, `?`, character classes such as `[abc]`, and `**`; `**/`
  matches zero or more directory segments.
- Returns an array of files and directories, including dotfiles.
- Results are relative to `cwd`, or to the process working directory when
  `cwd` is omitted, and always use `/` separators.
- Results use deterministic ordinal lexical ordering.
- Returns an empty array on no match.
- A missing or non-directory `cwd` is a runtime error.
- Does not traverse directory symlinks and does not support brace expansion.

### 2.5 `std.fs.size(path)`

Returns the file size in bytes as an integer. The path must identify a regular
file; missing paths, inaccessible paths, and directories are runtime errors.
The operation lowers directly to `stat` on Bash/Zsh and `Get-Item`.Length on
PowerShell, so it does not add a Sushi runtime helper.

### 2.6 `std.http.get/post`

Returns object fields:

- `status` (number)
- `ok` (bool)
- `headers` (map/object)
- `body` (string)
- `url` (string)

Network/runtime failure contract:

- transport failures return `status = 0` and `ok = false`
  script code.

### 2.6 Function type contracts (M4 Phase 3)

Sushi statically checks typed user functions and lowers parameter types to each
target's native facilities:

- primitive parameter types: `string`, `int`, `float`, `bool`, `object`; use
  `T[]` (for example `string[]`) for arrays of any element type
- structural parameter types: `object { ... }`
- function return type contracts

Contract mismatch behavior:

- compile-time diagnostics are emitted for statically-provable mismatches
- values not rejected statically follow the target shell's native coercion

### 2.6.1 Bash/Zsh function and collection ABI

- user functions execute in the current shell and place their return value in
  an internal result slot; return values are not transported through stdout
- this preserves caller-visible mutations and prevents command substitution
  from swallowing a failing status
- arrays and objects lower directly to native shell collections or statically
  known field bundles; generated scripts contain no embedded helper library
- generated Zsh enables zero-based array indexing for Bash parity
- class and enum instances remain native objects: Bash uses associative arrays
  and namerefs, Zsh uses associative arrays with compiler-managed references,
  and PowerShell uses `PSCustomObject`
- typed class values can cross function and method boundaries without JSON
  serialization or stdout transport

### 2.7 Arithmetic numeric strictness

For Bash/Zsh and PowerShell outputs, numeric arithmetic is strict:

- no implicit fallback coercion to `0` for non-numeric operands
- arithmetic lowers directly when its value shape is known
- ambiguous arithmetic, indexing, and member access fail with `SUSHI1030`
- invalid top-level `break`, `continue`, and `return` are rejected during
  checking (`SUSHI1027`, `SUSHI1028`, and `SUSHI1029`)

### 2.8 `std.string.*` and string method sugar

Available string intrinsics:

- `std.string.trim(value)`
- `std.string.lower(value)`
- `std.string.upper(value)`
- `std.string.length(value)` (returns the string length as an `int`)
- `std.string.split(value, sep, limit=0)`
- `std.string.contains(value, needle)`
- `std.string.startsWith(value, prefix)`
- `std.string.endsWith(value, suffix)`
- `std.string.replace(value, old, new)` (literal/global replace)
- `std.string.isMatch(value, pattern)`
- `std.string.match(value, pattern)`

Method sugar lowers to these intrinsics:

- `text.trim().lower()` -> `std.string.trim(text)` then `std.string.lower(...)`

`match` return shape:

- `ok` (bool)
- `value` (string)
- `index` (number, `-1` when no match)
- `groups` (array)

Regex contract:

- PowerShell target uses .NET regex directly.
- Bash/Zsh targets approximate .NET regex behavior using shell tooling.

## 3. Known limitations

- Glob output format is not fully normalized cross-target yet.
- structural field typing currently targets flat field contracts (no deep nested structural field contracts).
- inheritance, reflection, dynamic member names, and mutation of enum singletons
  are intentionally unsupported

## 4. Verification commands

Create a small script first:

```sushi
// hello.sushi
println("Sushi is working")
```

### 4.1 PowerShell target

```powershell
dotnet run --project src/Sushi -- transpile hello.sushi --target powershell-windows
powershell -NoProfile -ExecutionPolicy Bypass -File hello.powershell-windows.ps1
```

### 4.2 Bash target

```bash
dotnet run --project src/Sushi -- transpile hello.sushi --target bash-linux
bash hello.bash-linux.sh
```

### 4.3 Zsh target

```zsh
dotnet run --project src/Sushi -- transpile hello.sushi --target zsh-macos
zsh hello.zsh-macos.zsh
```
