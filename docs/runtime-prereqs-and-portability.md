# Runtime Prerequisites and Portability (M3)

This document defines what the generated scripts need at runtime, and where
behavior differs between Bash, Zsh, and PowerShell targets.

## 1. Runtime prerequisites

Default target selection:

- Windows: `Powershell7`
- macOS: `Zsh`
- Linux/WSL: `Bash`

### 1.1 For transpiling `.sushi` source

- .NET SDK 9.0+

### 1.2 For running Bash output (`-t Bash`)

- Bash 4.0+ (runtime object storage uses in-memory object handles)
- `curl`
- standard POSIX tools used by helpers (`mktemp`, `awk`, `cat`, `tee`, `find`)

### 1.3 For running Zsh output (`-t Zsh`)

- Zsh 5.0+ (runtime object storage uses in-memory object handles)
- `curl`
- standard POSIX tools used by helpers (`mktemp`, `awk`, `cat`, `tee`, `find`)

### 1.4 For running PowerShell output (`-t Powershell7`)

- PowerShell 7+ (`pwsh`) recommended
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

### 2.3 `std.json.parse(...)` and `std.json.stringify(...)`

- `parse`: parses JSON text into dynamic object/array values.
- invalid JSON returns a non-throwing fallback:
  - Bash/Zsh helpers return raw text
  - PowerShell helper returns raw text
- `stringify`: emits compact JSON by default.

### 2.4 `std.fs.glob(pattern, cwd?)`

- Supports recursive `**` patterns.
- Returns an array.
- Returns empty array on no match.
- Path separators and absolute/relative shape are target-native right now.

Portability guidance:

- treat glob results as opaque paths
- prefer `std.path.*` helpers for composing paths

### 2.5 `std.http.get/post`

Returns object fields:

- `status` (number)
- `ok` (bool)
- `headers` (map/object)
- `body` (string)
- `json` (parsed JSON or null)
- `url` (string)

Network/runtime failure contract:

- helpers return `status = 0` and `ok = false` instead of throwing through Sushi
  script code.

### 2.6 Function type contracts (M4 Phase 3)

Sushi now emits type contract checks for typed user functions:

- primitive parameter types: `string`, `int`, `float`, `bool`, `array`, `object`
- structural parameter types: `object { ... }`
- function return type contracts

Contract mismatch behavior:

- compile-time diagnostics are emitted for statically-provable mismatches
- runtime mismatches print a contract violation message and exit with code `2`

### 2.7 Arithmetic numeric strictness

For Bash/Zsh and PowerShell outputs, numeric arithmetic is strict:

- no implicit fallback coercion to `0` for non-numeric operands
- index/member/call expression operands used in arithmetic are runtime-validated
- non-numeric arithmetic operands fail with contract violation and exit code `2`

### 2.8 `std.string.*` and string method sugar

Available string intrinsics:

- `std.string.trim(value)`
- `std.string.lower(value)`
- `std.string.upper(value)`
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

## 3. Known M3 limitations

- HTTP behavior can vary by host networking/TLS policy.
- Glob output format is not fully normalized cross-target yet.
- Bash/Zsh JSON operations are pure-shell in emitted scripts (no `jq`/`perl` dependency).
- structural field typing currently targets flat field contracts (no deep nested structural field contracts).

## 4. Verification commands

### 4.1 PowerShell target

```powershell
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t Powershell7
pwsh -NoLogo -NoProfile -File examples/m3_verification.ps1
```

### 4.2 Bash target

```bash
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t Bash
bash examples/m3_verification.sh
```

### 4.3 Zsh target

```zsh
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t Zsh
zsh examples/m3_verification.zsh
```

### 4.4 No-network environments

Set `SUSHI_SKIP_HTTP=1` to skip HTTP assertions in verification scripts.
