# Modules and native objects

Sushi resolves modules and object types at transpile time. Generated scripts do
not load Sushi source files or an object runtime.

## Modules and exports

An imported file must declare one unique `box` identity. Imports are relative
`.sushi` paths and may use an explicit alias:

```sushi
// tools.sushi
box Example.Tools
export greet(name, punctuation = "!") { return "Hello " + name + punctuation }
secret() { return "private" }

// app.sushi
use "./tools.sushi" as tools
println(tools.greet(punctuation: "?", name: "Ada"))
```

When `as` is omitted, the filename becomes the alias; invalid identifier
characters become underscores. Declarations are private unless prefixed with
`export`. Exported functions, classes, enums, and variables can be read through
the alias, but imported variables cannot be assigned by an importer.

The compiler canonicalizes paths, rejects cycles and duplicate box identities,
emits dependencies before importers, and emits a shared diamond dependency only
once. Top-level module statements therefore run once in dependency order.
`watch` tracks both loaded dependencies and missing import paths.

## Classes

Classes support declared fields, defaults, constructors, methods, typed
parameters and returns, and compound field mutation:

```sushi
class Counter {
    int value = 0
    new(int start) { this.value = start }
    increment() { this.value += 1 }
    int current() -> this.value
}
```

Without an explicit constructor, fields become constructor parameters and field
initializers become their defaults. Constructors return the new instance
implicitly. Unknown fields and methods are compile-time errors when the
receiver's type is known. Typed class instances preserve their native object
shape across declarations, assignments, parameters, returns, and module
boundaries.

## Type adapters

A zero-argument member named for a primitive type declares an adapter:

```sushi
class Person {
    string name
    string() -> this.name
}

println(string(new Person(name: "Ada")))
```

For longer expressions, assigning the object first often keeps generated code
easier to read:

```sushi
var person = new Person(name: "Ada")
println(string(person))
```

Adapters are resolved statically and lower to ordinary target-shell functions.
A missing adapter on a statically known object is a compile-time error.

## Enums

All enum forms lower to immutable native singleton objects:

```sushi
enum Color { Red, Green, Blue }
enum ExitCode { Ok = 0, Failed = 1 }
enum Priority(int weight) { Low(1), High(2) }
enum Axis { X { label: "horizontal" }, Y { label: "vertical" } }
```

Every value provides `.name`, `.ordinal`, and `.value`, plus record or inline
fields. Enums may define methods and type adapters. An explicit enum constructor
runs once for each declared singleton and may initialize additional fields.
Enum equality compares singleton identity within the enum type; enum fields
cannot be mutated after initialization.

## Deliberate exclusions

The object model does not include inheritance, reflection, dynamic member
names, or runtime replacement of methods. JSON parsing remains an explicit
external dependency rather than a hidden object representation.
