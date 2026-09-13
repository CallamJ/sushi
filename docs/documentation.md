# Documentation comments

Sushi uses consecutive `///` lines to document the declaration immediately below
them. Keep the comment adjacent to its declaration; a blank line breaks the
connection. Place the comment before `export` when documenting an exported API.

```sushi
/// Formats a friendly greeting for a user.
///
/// See {@link User} for the data model.
/// @param name The name to greet.
/// @returns A ready-to-print greeting.
/// @throws When the supplied name is invalid.
/// @example println(greet("Ada"))
export string greet(string name) { return "Hello " + name }
```

The prose is Markdown. Supported tags are `@param`, `@returns`, `@throws`,
`@deprecated`, and repeatable `@example`. `{@link Symbol}` and
`{@link Symbol display text}` create an explicit reference to a visible symbol.
Unknown tags, invalid parameter names, duplicate tags, missing parameter docs,
and invalid links are warnings reported by `sushi check` and the language server.

Generate an API reference for a root module and all of its imported modules:

```bash
sushi docs app.sushi --output docs/api.md
```

Only exported top-level APIs are emitted. Pass `--include-builtins` to append the
standard-library reference.
