# IDE support

Sushi provides a standard Language Server Protocol endpoint. Any compatible editor can start:

```text
sushi lsp --stdio
```

The server supports diagnostics, completion, hover, signature help, semantic
highlighting, go-to-definition, type definition, document/workspace symbols,
find references, document highlights, folding, selection ranges, inferred-type
inlay hints, formatting, rename, and conservative extract/inline-variable code
actions. It speaks LSP over standard input/output: do not write application
output to stdout when hosting it.

Semantic highlighting is authoritative while the server is running. The VS Code
extension also ships a TextMate fallback for comments, strings/interpolation,
types, declarations, modules, members, literals, and operators.

Refactorings intentionally decline edits when Sushi cannot prove they preserve
evaluation order and shell-side effects. In particular, inline and extract
variable currently accept literals and simple member/name expressions only.

The server analyses the text in open documents and their normal Sushi module
imports. Set the initialization option `targetProfile` to any normal Sushi
target profile (for example `bash-linux`); it defaults to `auto`.

## VS Code

The extension source is in `editors/vscode`. Build it with `npm install` then
`npm run compile`; `npm run package` produces a VSIX. It starts `sushi lsp
--stdio` and uses `sushi.serverPath` (default: `sushi` on `PATH`) and
`sushi.targetProfile` settings.

## JetBrains IDEs

The native LSP plugin source is in `editors/jetbrains`. It targets commercial
JetBrains IDEs based on 2026.2 or newer. Build it with `gradle buildPlugin`.
The server executable and target profile are configured in **Settings | Tools |
Sushi**.
