# Sushi language guide

Sushi is a statically checked shell-scripting language. A `.sushi` file is
transpiled to Bash, Zsh, or PowerShell; the generated file is the program you
run. Simple operations lower to native shell syntax, so generated scripts do
not need a Sushi runtime library.

## First script

```sushi
var name = "Sushi"
println("Hello, " + name)
```

Check and run it with the CLI:

```bash
sushi check hello.sushi
sushi run hello.sushi
```

To inspect generated code, select an explicit target:

```bash
sushi transpile hello.sushi --target bash-linux --output hello.sh
```

Targets are profiles combining shell and platform:
`bash-linux`, `bash-macos`, `zsh-linux`, `zsh-macos`, `powershell-linux`,
`powershell-macos`, and `powershell-windows`. `auto` selects the host default.

## Values and variables

Sushi supports strings, integers, floats, booleans, arrays, objects, and null.
Variables are declared with `var` and reassigned without a declaration:

```sushi
var count = 3
count += 1
var tags = ["build", "test"]
var result = { ok: true, code: 0 }
println(result.code)
```

Object and array shapes are tracked when they can be determined statically.
Use `null` for an absent value. String interpolation uses `$(expression)`:

```sushi
var path = "dist/app.zip"
println("writing $(path)")
```

## Conditions and loops

Conditions require a boolean. The `?` operator converts a value to a
truthiness check; it does not catch errors. Empty strings and null are false.
All numeric values, including `0`, are true. Non-empty strings, arrays, and
objects are also true.

```sushi
if (?result) {
    println("result exists")
} else {
    println("no result")
}

while (count > 0) {
    count -= 1
}
```

Use `!` to negate a boolean expression. `for` supports iteration over arrays
and ranges where the target can lower the operation natively.

## Functions

Functions may have typed parameters and return values. Arguments can be
positional or named, and defaults are allowed:

```sushi
string greet(string name, string punctuation = "!") {
    return "Hello, " + name + punctuation
}

println(greet(name: "Ada"))
```

Common parameter types are `string`, `int`, `float`, `bool`, `array`, and
`object`. Structural object contracts describe required fields:

```sushi
string label(object { string name, int age } person) {
    return person.name + ":" + person.age
}
```

Functions that mutate objects or arrays do so through the native target
representation. Compile-time type mismatches are reported by `sushi check`.

## Standard library

The standard library is a set of compile-time-lowered APIs, not a runtime
package. Frequently used modules include:

- `std.console`: `print`, `println`
- `std.env`: read and test environment variables
- `std.fs`: glob files and perform filesystem operations
- `std.path`: join and inspect paths
- `std.process`: run commands and pipelines
- `std.archive`: create archives such as ZIP files
- `std.http`: HTTP requests and downloads
- `std.string`: string operations and regular-expression matching
- `std.target`: compile-time shell and platform selection

Example:

```sushi
var files = std.fs.glob("src/**/*.cs")
for (var file : files) {
    println(std.path.basename(file))
}
```

`std.archive.zip(source, destination)` lowers to `zip` on Bash/Zsh and
`Compress-Archive` on PowerShell. Target tools such as `curl`, `zip`, `find`,
or `pwsh` must be installed when the generated script uses them.

## Platform-specific code

Use compile-time target values when commands differ by platform or shell:

```sushi
if (std.target.platform() == "windows") {
    println("running on Windows")
} else {
    println("running on Unix")
}
```

The compiler removes branches that are provably unselected for the requested
target profile. This is useful for native commands, but does not make a
platform-specific command available on another platform.

## Modules

Modules are statically linked during transpilation:

```sushi
// helpers.sushi
box Example.Helpers
export greet(name) { return "Hello, " + name }

// app.sushi
use "./helpers.sushi" as helpers
println(helpers.greet("Sushi"))
```

Declarations are private unless marked `export`. Imported modules run once in
dependency order and are not loaded at runtime. See
[`modules-and-objects.md`](modules-and-objects.md) for exports, classes, and
enums.

## Classes and enums

Classes provide fields, constructors, methods, and adapters:

```sushi
class Person {
    string name
    new(string name) { this.name = name }
    string() -> this.name
}

var person = new Person("Ada")
println(string(person))
```

Enums provide named singleton values and built-in `name`, `ordinal`, and
`value` fields:

```sushi
enum Priority(int weight) {
    Low(1), High(2)
}

println(Priority.High.weight)
```

## Errors and portability

Run `check` before execution to catch syntax, type, unsupported-shape, and
platform diagnostics. Generated scripts use strict error handling appropriate
to the target shell. Bash requires version 4.3+ for native associative arrays;
PowerShell output uses a Windows PowerShell 5.1-compatible baseline and also
runs on PowerShell 7. JSON parsing is deliberately not built in and should be
supplied by an explicit external dependency.

More runtime prerequisites and target differences are documented in
[`runtime-prereqs-and-portability.md`](runtime-prereqs-and-portability.md).

# Documentation

See [Documentation comments](documentation.md) for `///` API docs, tags,
cross-references, editor help, and Markdown API generation.
