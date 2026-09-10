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

# Run the CLI with arbitrary args, e.g. `just cli -- --help`
cli *args:
    dotnet run --project {{cli}} --no-restore -- {{args}}

# Transpile a .sushi file to a target shell (Bash|Zsh|Powershell7)
transpile file target="Bash":
    dotnet run --project {{cli}} --no-restore -- transpile {{file}} -t {{target}}

# Static-check a .sushi file
check file target="Bash" format="text":
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
    publish/Sushi-linux_x64-64 check examples/m3_verification.sushi --target Bash --format json >/dev/null
