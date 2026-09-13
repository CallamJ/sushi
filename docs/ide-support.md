# IDE support

Sushi provides a standard Language Server Protocol endpoint. Any compatible editor can start:

```text
sushi lsp --stdio
```

The server supports diagnostics, completion, hover, signature help, semantic
highlighting, go-to-definition, type definition, document/workspace symbols,
find references, document highlights, folding, selection ranges, inferred-type
inlay hints, formatting, rename, and conservative extract/inline-variable code
actions, code lenses, and incoming/outgoing call hierarchy. It speaks LSP over standard input/output: do not write application
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

Sushi deliberately has no inheritance feature, so a type hierarchy would be a
misleading empty tree. Class and enum structure is available through document
symbols; call hierarchy is provided for functions, methods, and constructors.

The server also exposes `sushi/transpileDocument`, an editor-facing JSON-RPC
request that receives a document URI, optional current buffer text, and optional
target profile. It returns generated text and diagnostics without writing a
generated file. Editor integrations use it for generated-output previews and
unsaved-buffer workflow actions.

## VS Code

The extension source is in `editors/vscode`. Build it with `npm install` then
`npm run compile`; `npm run package` produces a VSIX. It starts `sushi lsp
--stdio` and uses `sushi.serverPath` (default: `sushi` on `PATH`) and
`sushi.targetProfile` settings.

The command palette and code lenses provide **Sushi: Run Current Buffer**,
**Sushi: Check Current Buffer**, and **Sushi: Preview Generated Output**.
Run and preview use the current unsaved document text. Run emits a private
temporary script and removes it after the process exits; preview opens a
read-only-style virtual `sushi-generated:` document. Completion includes common
control-flow, declaration, and module-import snippets.

## JetBrains IDEs

The native LSP plugin source is in `editors/jetbrains`. It targets commercial
JetBrains IDEs based on 2026.2 or newer. Build it with `gradle buildPlugin`.
The server executable and target profile are configured in **Settings | Tools |
Sushi**. Its **Tools | Sushi** menu provides matching Check, Run, and Preview
Generated Output actions. They use the current unsaved editor buffer through a
private temporary source file, never by overwriting the project source.
