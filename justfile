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

package runtime:
    scripts/build.sh -c Release --self-contained --single-file --ready-to-run --runtime {{runtime}}

package-all:
    scripts/build.sh -c Release --self-contained --single-file --ready-to-run --runtime all

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

# Pack the dotnet tool nupkg
pack: restore
    dotnet pack {{cli}} -c {{config}} --no-restore -o publish/nupkg

# Compile and package the VS Code extension as a VSIX
vscode version="0.0.0":
    (cd editors/vscode && npm ci)
    bash scripts/package-vscode.sh {{version}} publish/editors

# Build the IntelliJ-based plugin ZIP
jetbrains version="0.0.0":
    (cd editors/jetbrains && ./gradlew -PsushiVersion={{version}} buildPlugin)
    mkdir -p publish/editors
    cp editors/jetbrains/build/distributions/sushi-jetbrains-{{version}}.zip publish/editors/
    @echo "JetBrains plugin: publish/editors/sushi-jetbrains-{{version}}.zip"

# Build both editor packages with a shared version
package-editors version="0.0.0":
    (cd editors/vscode && npm ci)
    bash scripts/package-vscode.sh {{version}} publish/editors
    (cd editors/jetbrains && ./gradlew -PsushiVersion={{version}} buildPlugin)
    cp editors/jetbrains/build/distributions/sushi-jetbrains-{{version}}.zip publish/editors/
    @echo "JetBrains plugin: publish/editors/sushi-jetbrains-{{version}}.zip"

# Format check / apply
fmt:
    dotnet format {{sln}}

fmt-check:
    dotnet format {{sln}} --verify-no-changes

# Run the checks used before publishing a release
ready: ci
    (cd editors/vscode && npm ci && npm run compile)
    (cd editors/jetbrains && ./gradlew buildPlugin)

# Validate a release version without creating a tag or pushing
release-check kind="patch":
    bash scripts/release.sh --check {{kind}}

# Create and push a release tag; GitHub Actions publishes the artifacts
release kind="patch":
    bash scripts/release.sh {{kind}}

# Alias matching the publishing workflow vocabulary
publish kind="patch":
    bash scripts/release.sh {{kind}}

# Regenerate the compact benchmark section in README.md
benchmark-readme results:
    python3 scripts/update-benchmark-readme.py {{results}}

# Remove build output
clean:
    dotnet clean {{sln}}
    rm -rf publish

# CI-equivalent gate: tests + linux package smoke
ci: test
    bash scripts/build.sh --runtime linux-x64 --self-contained --single-file --ready-to-run
    test -f dist/Sushi/Release/linux-x64/Sushi
    dist/Sushi/Release/linux-x64/Sushi --help >/dev/null
    mkdir -p tmp
    printf 'println("Sushi package smoke")\n' > tmp/package-smoke.sushi
    dist/Sushi/Release/linux-x64/Sushi check tmp/package-smoke.sushi --target bash-linux --format json >/dev/null
