# Sushi — task runner
# https://github.com/casey/just

set shell := ["bash", "-uc"]

sln        := "Sushi.sln"
cli        := "src/Sushi/Sushi.csproj"
tests      := "src/Sushi.Tests/Sushi.Tests.csproj"
config     := "Release"
tfm        := "net9.0"

# List available recipes
default:
    @just --list

# Restore NuGet dependencies
restore:
    dotnet restore {{sln}}

# Build the whole solution
build: restore
    dotnet build {{sln}} -c {{config}} --no-restore

# Run the test suite (a console app, not `dotnet test`)
test: restore
    dotnet run --project {{tests}} --framework {{tfm}} --no-restore

# Run the CLI with arbitrary args, e.g. `just cli --help`
cli *args:
    dotnet run --project {{cli}} --no-restore -- {{args}}

# Transpile a .sushi file to a target profile (for example, bash-linux or powershell-windows)
transpile file target="Bash":
    dotnet run --project {{cli}} --no-restore -- transpile {{file}} -t {{target}}

# Static-check a .sushi file
check file target="Bash" format="plain":
    dotnet run --project {{cli}} --no-restore -- check {{file}} --target {{target}} --format {{format}}

# Execute a .sushi file directly
run file *args:
    dotnet run --project {{cli}} --no-restore -- run {{file}} {{args}}

# Run the benchmark suite
bench *args:
    dotnet run --project {{cli}} -c {{config}} --no-restore -- benchmark {{args}}

# Transpile and execute the M3 verification example for a shell
verify target="Bash" ext="sh":
    dotnet run --project {{cli}} --no-restore -- transpile examples/m3_verification.sushi -t {{target}}
    {{ if target == "Bash" { "bash" } else if target == "Zsh" { "zsh" } else { "pwsh" } }} examples/m3_verification.{{ext}}

# Build self-contained single-file executables (e.g. `just package linux-x64 osx-arm64`)
package *platforms:
    bash scripts/build.sh {{platforms}}

# Pack the dotnet tool nupkg
pack: restore
    dotnet pack {{cli}} -c {{config}} --no-restore -o publish/nupkg

# Compile and package the VS Code extension as a VSIX
vscode:
    npm --prefix editors/vscode run compile
    cd editors/vscode && ./node_modules/.bin/vsce package --allow-missing-repository --no-dependencies
    tmp_dir=$(mktemp -d); mkdir -p "$tmp_dir/extension/node_modules"; cp -R editors/vscode/node_modules/vscode-languageclient editors/vscode/node_modules/vscode-jsonrpc editors/vscode/node_modules/vscode-languageserver-protocol editors/vscode/node_modules/vscode-languageserver-types editors/vscode/node_modules/semver editors/vscode/node_modules/minimatch editors/vscode/node_modules/brace-expansion editors/vscode/node_modules/balanced-match "$tmp_dir/extension/node_modules/"; (cd "$tmp_dir" && zip -q -r "$OLDPWD/editors/vscode/sushi-language-0.1.1.vsix" extension/node_modules); rm -rf "$tmp_dir"

# Build the IntelliJ-based plugin ZIP
jetbrains:
    gradle -p editors/jetbrains buildPlugin

# Format check / apply
fmt:
    dotnet format {{sln}}

fmt-check:
    dotnet format {{sln}} --verify-no-changes

# Remove build output
clean:
    dotnet clean {{sln}}
    rm -rf publish

# CI-equivalent gate: tests + linux package smoke
ci: test
    bash scripts/build.sh linux-x64
    test -f publish/Sushi-linux_x64-64
    publish/Sushi-linux_x64-64 --help >/dev/null
    publish/Sushi-linux_x64-64 check examples/m3_verification.sushi --target bash-linux --format json >/dev/null
