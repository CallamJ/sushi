# Sushi

Sushi is a cross-platform shell scripting language. Write one script and
transpile it to Bash, Zsh, or PowerShell for a specific operating-system and
shell target.

## Install

Download a platform binary or the `Sushi.Tool` package from the latest GitHub
release. The standalone binaries are named by platform and architecture.

```bash
dotnet tool install --global Sushi.Tool --add-source ./release-assets --version X.Y.Z
sushi --help
```

## Quick start

```sushi
println("Hello from Sushi")
```

```bash
sushi transpile hello.sushi --target bash-linux
bash hello.bash-linux.sh
```

Use `--target auto` to select the host default, or choose one of:
`bash-linux`, `bash-macos`, `zsh-linux`, `zsh-macos`, `powershell-linux`,
`powershell-macos`, and `powershell-windows`.

Sushi also supports `check`, `run`, and `watch` commands. See the
[language guide](docs/language-guide.md), [module and object guide](docs/modules-and-objects.md),
and [IDE support guide](docs/ide-support.md) for details.

## Benchmarks

The benchmark suite compares native shell examples with equivalent transpiled
scripts. Ratios below are transpiled median time divided by native median time;
lower is better. P95 shows the high-end result across scenarios. Run
`just bench` to reproduce the full suite.

<!-- benchmark:start -->
Run: 2026-09-13 · 10 scenarios passed for every target.

| Target | Scenarios | Median ratio | P95 ratio |
| --- | ---: | ---: | ---: |
| bash | 10 | 1.044× | 1.531× |
| powershell | 10 | 1.046× | 3.463× |
| zsh | 10 | 1.004× | 2.227× |
<!-- benchmark:end -->

## Development

```bash
just ready
just bench
just package-all
just package-editors 1.0.0
just release patch
```

`just release patch` runs the local checks, creates the next `vX.Y.Z` tag, and
pushes it. GitHub Actions then builds and publishes the binaries, .NET tool,
VS Code extension, and JetBrains plugin in one release.
