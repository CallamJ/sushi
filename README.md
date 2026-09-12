# Sushi

[![CI](https://github.com/callamj/sushi/actions/workflows/ci.yml/badge.svg)](https://github.com/callamj/sushi/actions/workflows/ci.yml)

Sushi is a shell scripting language that transpiles to Bash, Zsh, or PowerShell.
The goal is to write one script and run it across environments without manually
maintaining per-shell script variants.

## Current status

Sushi is in beta development. Core parsing/transpilation is in progress, and
Milestone 3 runtime intrinsics are now available for:

- process execution and pipelines
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

The native-lowered standard library includes `std.fs.*`, `std.path.*`,
`std.env.*`, `std.process.*`, `std.console.*`, `std.archive.*`, and
`std.http.download`. These APIs emit the corresponding target command or
cmdlet; they do not bring a Sushi runtime library into simple scripts.

Milestone 4 Phase 3 function contracts are now available:

- typed function parameters (e.g. `int`, `string`, `bool`, `array`, `object`)
- structural object parameters (e.g. `object { string name, int age } user`)
- compile-time mismatch diagnostics for provable literal mismatches
- target-native parameter typing without an embedded runtime library

Modules and native object constructs are also available:

- relative `use "./module.sushi" as alias` imports with private-by-default exports
- classes with fields, constructors, methods, mutation, and type adapters
- simple, direct-value, record, inline-property, and explicitly constructed enums
- native associative arrays on Bash/Zsh and `PSCustomObject` on PowerShell—no JSON object runtime

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

- Windows: `powershell-windows`
- macOS: `zsh-macos`
- Linux/WSL: `bash-linux`

Targets are profiles, not shell names: use `-t auto` (the default), or one of
`bash-linux`, `bash-macos`, `zsh-linux`, `zsh-macos`, `powershell-linux`,
`powershell-macos`, or `powershell-windows`. An explicit profile makes the
default output name unambiguous (for example, `script.bash-linux.sh`); pass
`-o` to choose the output path yourself. `std.target.shell()` and
`std.target.platform()` are compile-time values, so unselected conditional
branches are omitted from generated code.

### Conditions and truthiness

Conditions require a `bool`. Use the prefix `?` operator to explicitly convert
a known value to a boolean; it does not catch evaluation errors:

```sushi
var token = std.env.get("SUSHI_TOKEN")
if (?token) { println("token configured") }
if (!?token) { println("token missing") }
if (std.env.has("SUSHI_TOKEN")) { println("variable exists") }
```

`?` treats `null` and empty strings as false. Numeric values—including `0`—are
true. Non-empty strings (including `"false"`) and non-null arrays or objects
are also true. Add type annotations to values whose type cannot be inferred.

### Run transpiled Bash scripts

- Bash 4.3+ (associative arrays and namerefs lower objects directly to native storage)
- `curl` (required for `std.http.get/post/download` and native archive downloads)

### Run transpiled Zsh scripts

- Zsh 5.0+ (arrays and associative arrays lower directly to native storage)
- `curl` (required for `std.http.get/post/download` and native archive downloads)

### Run transpiled PowerShell scripts

- Windows PowerShell 5.1+ or PowerShell 7+ (`pwsh`)

PowerShell output uses a 5.1-compatible baseline. The same output runs on
PowerShell 7 across Windows, macOS, and Linux.

## Quickstart (M3 verification)

### PowerShell target

```powershell
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t powershell-windows
powershell -NoProfile -ExecutionPolicy Bypass -File examples/m3_verification.powershell-windows.ps1
```

### Bash target

```bash
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t bash-linux
bash examples/m3_verification.bash-linux.sh
```

### Zsh target

```zsh
dotnet run --project src/Sushi -- transpile examples/m3_verification.sushi -t zsh-macos
zsh examples/m3_verification.zsh-macos.zsh
```

If your environment blocks outbound HTTP, set `SUSHI_SKIP_HTTP=1` before
running verification.

## Language guide

See [`docs/language-guide.md`](docs/language-guide.md) for a practical guide
to Sushi syntax, functions, conditions, standard-library APIs, modules,
classes, enums, and target selection.

## IDE support

Sushi includes `sushi lsp --stdio` for editor integration. The initial server
provides diagnostics, completion, hover, semantic highlighting, definitions,
symbols, references, and rename. See [`docs/ide-support.md`](docs/ide-support.md)
for generic LSP use plus the VS Code and JetBrains extensions.

## Quickstart (M4 workflow)

```powershell
dotnet run --project src/Sushi -- check examples/m3_verification.sushi
dotnet run --project src/Sushi -- run examples/m3_verification.sushi
dotnet run --project src/Sushi -- watch examples/m3_verification.sushi --run
```

## ZIP example

`examples/zip_directory.sushi` uses `std.archive.zip`, which lowers directly
to native `zip` on Bash/Zsh and `Compress-Archive` on PowerShell. The required
native archive tool must be present on the target machine:

```bash
just run examples/zip_directory.sushi
```

## Modules

Modules use a declared identity, explicit exports, and relative imports:

```sushi
// helpers.sushi
box Example.Helpers
export greet(name) { return "Hello, $(name)!" }

// app.sushi
use "./helpers.sushi" as helpers
println(helpers.greet("Sushi"))
```

Run the complete example with `just run examples/modules/app.sushi`. Imported
modules are statically linked once and do not require source files at runtime.
Only declarations prefixed with `export` are visible to importers, and exported
variables are read-only outside their defining module. See
[`docs/modules-and-objects.md`](docs/modules-and-objects.md) for the complete
module, class, enum, and adapter model.

## Classes and enums

```sushi
class Person {
    string name
    new(string name) { this.name = name }
    string() -> this.name
}

enum Priority(int weight) {
    Low(1), Normal(2), High(3)
}

var person = new Person("Ada")
println(string(person))
println(Priority.High.weight)
```

Run `just run examples/object_model.sushi` for constructors, methods, adapters,
enum forms, typed object parameters/returns, and reference aliases.

## Benchmark examples

```bash
scripts/benchmark-examples.sh
scripts/benchmark-examples.sh --iterations 3 --targets bash,zsh,powershell
scripts/benchmark-native-vs-transpiled.sh --iterations 3 --targets bash,zsh,powershell
dotnet run --project src/Sushi -- benchmark --targets bash,zsh,powershell
```

`benchmark-native-vs-transpiled.sh` compares transpiled outputs against scripts
in:

- `benchmarks/native/bash/<example>.sh`
- `benchmarks/native/zsh/<example>.zsh`
- `benchmarks/native/powershell/<example>.ps1`

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
dotnet run --project src/Sushi -- check examples/m4_structural_valid.sushi -t bash-linux
dotnet run --project src/Sushi -- check examples/m4_typed_return.sushi -t powershell-windows
dotnet run --project src/Sushi -- check examples/m4_structural_invalid_static.sushi -t zsh-macos
```

## Portability and runtime behavior

See `docs/runtime-prereqs-and-portability.md` for:

- runtime dependency details per target
- behavior differences between Bash, Zsh, and PowerShell emitters
- current known limitations for M3 intrinsics

Current tooling limitations:

- `fmt` is not exposed until deterministic formatting is implemented.
- Relative modules and explicit exports are statically linked. Imported files
  must declare a unique `box`; package-name imports are not supported yet.
- `check` rejects references to undefined variables in supported code paths.
- Generated scripts contain no embedded Sushi helper library. Bash and Zsh
  scalar functions use a result slot, while arrays, records, intrinsics, and
  control flow lower directly to target-native constructs.
- Operations whose value shape cannot be determined statically fail checking
  with `SUSHI1030` instead of adding runtime type dispatch.
- JSON is not part of the built-in standard library; it is reserved for a
  future optional dependency.
- Filesystem APIs are under `std.fs.*`; `std.io.*` remains temporarily as a
  deprecated compatibility alias and reports `SUSHI2001`.

## CI

GitHub Actions now runs:

- unit tests
- M3 verification transpile + execution on Ubuntu (Bash target)
- M3 verification transpile + execution on macOS (Zsh target)
- M3 verification transpile + execution on Windows (PowerShell target)
- NuGet package caching for faster runs

HTTP checks are skipped by default in CI to reduce flakiness. To allow HTTP
checks, run the workflow manually and set `allow_http=true`.
