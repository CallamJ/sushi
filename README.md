# Sushi

[![CI](https://github.com/callamj/sushi/actions/workflows/ci.yml/badge.svg)](https://github.com/callamj/sushi/actions/workflows/ci.yml)

Sushi is a shell scripting language that transpiles to Bash, Zsh, or PowerShell.
The goal is to write one script and run it across environments without manually
maintaining per-shell script variants.

## Current status

Sushi is in beta development. Core parsing/transpilation is in progress, and
Milestone 3 runtime intrinsics are now available for:

- process execution and pipelines
- JSON parse/stringify
- file globbing
- HTTP GET/POST

Milestone 4 CLI workflow commands are available:

- `sushi check` to validate/transpile without writing output
- `sushi run` to transpile and execute in one step
- `sushi watch` to continuously transpile (and optionally run)

String runtime intrinsics and instance-method sugar are available:

- `std.string.trim/lower/upper/split/contains/startsWith/endsWith/replace`
- `std.string.isMatch` and `std.string.match`
- method sugar lowering (for example: `text.trim().lower()`)

Milestone 4 Phase 3 function contracts are now available:

- typed function parameters (e.g. `int`, `string`, `bool`, `array`, `object`)
- structural object parameters (e.g. `object { string name, int age } user`)
- compile-time mismatch diagnostics for provable literal mismatches
- runtime contract checks in emitted Bash/Zsh/PowerShell scripts (exit code `2`)

## Prerequisites

### Build/transpile Sushi

- .NET SDK 9.0+

## Installation

Tagged prereleases publish standalone executables for Linux x64, macOS x64,
and Windows x64, plus a `Sushi.Tool` NuGet package on the GitHub release.

To install a downloaded tool package locally:

```bash
dotnet tool install --global Sushi.Tool --add-source ./download-directory --prerelease
sushi --help
```

To build a standalone executable from source:

```bash
scripts/build.sh linux-x64
./publish/Sushi-linux_x64-64 --help
```

Default transpile target selection:

- Windows: `Powershell7`
- macOS: `Zsh`
- Linux/WSL: `Bash`

### Run transpiled Bash scripts

- Bash 4.3+ (native array storage uses namerefs)
- `curl` (required for `std.http.get/post`)

### Run transpiled Zsh scripts

- Zsh 5.0+ (runtime object storage uses in-memory object handles)
- `curl` (required for `std.http.get/post`)

### Run transpiled PowerShell scripts

- PowerShell 7+ (`pwsh`) recommended

Windows PowerShell 5.1 may work for some scripts, but `Powershell7` is the
supported target.

## Quickstart (M3 verification)

### PowerShell target

```powershell
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t Powershell7
pwsh -NoLogo -NoProfile -File examples/m3_verification.ps1
```

### Bash target

```bash
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t Bash
bash examples/m3_verification.sh
```

### Zsh target

```zsh
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t Zsh
zsh examples/m3_verification.zsh
```

If your environment blocks outbound HTTP, set `SUSHI_SKIP_HTTP=1` before
running verification.

## Quickstart (M4 workflow)

```powershell
dotnet run --project src/Sushi -- check examples/m3_verification.sushi
dotnet run --project src/Sushi -- run examples/m3_verification.sushi
dotnet run --project src/Sushi -- watch examples/m3_verification.sushi --run
```

## Benchmark examples

```bash
scripts/benchmark-examples.sh
scripts/benchmark-examples.sh --iterations 3 --targets bash,zsh,powershell
scripts/benchmark-native-vs-transpiled.sh --iterations 3 --targets bash,zsh,powershell
dotnet run --project src/Sushi -- benchmark --targets bash,zsh,powershell
```

`benchmark-native-vs-transpiled.sh` compares transpiled outputs against scripts
in:

- `native/bash/<example>.sh`
- `native/zsh/<example>.zsh`
- `native/powershell/<example>.ps1`

## Benchmark system (manifest-driven)

The `benchmark` command compares native scripts with equivalent transpiled Sushi
scripts using `benchmarks/manifest.json`.

Default command:

```bash
dotnet run --project src/Sushi -- benchmark
```

Useful options:

```bash
dotnet run --project src/Sushi -- benchmark --targets bash,zsh --iterations 20 --warmup 3
dotnet run --project src/Sushi -- benchmark --filter process --output-dir tmp/benchmarks/local
dotnet run --project src/Sushi -- benchmark --baseline tmp/baseline/results.json
dotnet run --project src/Sushi -- benchmark --targets bash,zsh --allow-skipped
```

Artifacts written to `tmp/benchmarks/<timestamp>`:

- `results.json`
- `results.tsv`
- `summary.md`

Benchmark manifests can define exact `exitCode`, `stdout`, and `stderr`
expectations plus per-target `maxRuntimeRatioMedian` gates. Output comparison
preserves whitespace (apart from CRLF normalization), missing or malformed
explicit baselines fail fast, and baselines are reused only when their suite
fingerprint matches. Requested skips fail by default; `--allow-skipped` is an
explicit local-development escape hatch.

## Quickstart (M4 Phase 3 contracts)

```powershell
dotnet run --project src/Sushi -- check examples/m4_structural_valid.sushi -t Bash
dotnet run --project src/Sushi -- check examples/m4_typed_return.sushi -t Powershell7
dotnet run --project src/Sushi -- check examples/m4_structural_invalid_static.sushi -t Zsh
```

## Portability and runtime behavior

See `docs/runtime-prereqs-and-portability.md` for:

- runtime dependency details per target
- behavior differences between Bash, Zsh, and PowerShell emitters
- current known limitations for M3 intrinsics

Current tooling limitations:

- `fmt` is not exposed until deterministic formatting is implemented.
- `box` and `use` syntax is parser-only and produces an explicit transpilation
  error instead of being silently ignored.
- `check` rejects references to undefined variables in supported code paths.
- Bash and Zsh functions return values through an internal result slot. Calls
  execute in the current shell, so variable mutations are retained and runtime
  contract failures propagate with exit code `2`.

## CI

GitHub Actions now runs:

- unit tests
- M3 verification transpile + execution on Ubuntu (Bash target)
- M3 verification transpile + execution on macOS (Zsh target)
- M3 verification transpile + execution on Windows (PowerShell target)
- NuGet package caching for faster runs

HTTP checks are skipped by default in CI to reduce flakiness. To allow HTTP
checks, run the workflow manually and set `allow_http=true`.
