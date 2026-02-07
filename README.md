# Sushi

Sushi is a shell scripting language that transpiles to Bash or PowerShell.
The goal is to write one script and run it across environments without manually
maintaining two shells.

## Current status

Sushi is in beta development. Core parsing/transpilation is in progress, and
Milestone 3 runtime intrinsics are now available for:

- process execution and pipelines
- JSON parse/stringify
- file globbing
- HTTP GET/POST

## Prerequisites

### Build/transpile Sushi

- .NET SDK 9.0+

### Run transpiled Bash scripts

- Bash
- `jq` (required for JSON/object plumbing in emitted runtime helpers)
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

If your environment blocks outbound HTTP, set `SUSHI_SKIP_HTTP=1` before
running verification.

## Portability and runtime behavior

See `docs/runtime-prereqs-and-portability.md` for:

- runtime dependency details per target
- behavior differences between Bash and PowerShell emitters
- current known limitations for M3 intrinsics

## CI

GitHub Actions now runs:

- unit tests
- M3 verification transpile + execution on Ubuntu (Bash target)
- M3 verification transpile + execution on Windows (PowerShell target)
