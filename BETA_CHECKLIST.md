# Sushi Beta Checklist and Delivery Plan

This document defines the minimum bar for a usable Sushi beta and provides a concrete execution checklist.

## 1. Beta Definition

### 1.1 Beta goals
- [ ] Users can build real automation scripts in Sushi and run them on Bash and PowerShell.
- [ ] Behavior is predictably portable for core language and stdlib APIs.
- [ ] Failures are diagnosable with clear source-level errors.
- [ ] The CLI workflow is complete enough for daily use (`check`, `fmt`, `transpile`, `run`).

### 1.2 Explicit non-goals for beta
- [ ] Full language parity with mature general-purpose languages.
- [ ] Perfect optimization of generated shell scripts.
- [ ] Large package ecosystem.
- [ ] Zero breaking changes after beta.

## 2. Beta Release Gates (Must Pass)

All gates must be true before tagging a beta release.

- [ ] Gate A: Core transpilation works for supported language constructs on both targets.
- [ ] Gate B: Portable stdlib v1 implemented and tested end-to-end on Bash + PowerShell.
- [ ] Gate C: Shell interop (commands, exit codes, capture, piping) is stable.
- [ ] Gate D: Developer tooling commands are functional and documented.
- [ ] Gate E: Cross-platform conformance suite is green on CI.
- [ ] Gate F: At least 4 production-style example projects are included.
- [ ] Gate G: Beta docs include quickstart, limitations, and migration notes.

## 3. Core Language Checklist

### 3.1 Parsing and AST completeness
- [ ] Top-level statements and declarations are supported.
- [ ] Functions, loops, conditionals, switch, returns, break/continue are supported.
- [ ] Arrays, indexing, slicing, destructuring are supported.
- [ ] Object literals and member access are supported.
- [ ] Named args, default params, varargs, lambdas parse correctly.

### 3.2 Semantic analysis (minimum required)
- [ ] Name resolution for locals, parameters, functions, and imports.
- [ ] Duplicate symbol detection in relevant scopes.
- [ ] Arity and named-argument validation for calls.
- [ ] Intrinsic stdlib call validation with useful diagnostics.
- [ ] Assignment validity checks (non-assignable targets rejected).
- [x] Control-flow diagnostics (invalid break/continue/return contexts).

### 3.3 Runtime semantics consistency
- [ ] Truthiness semantics are documented and enforced consistently.
- [ ] Equality/comparison semantics are documented and enforced consistently.
- [ ] Null handling is consistent across targets.
- [ ] Numeric/string conversion behavior is deterministic.

## 4. Transpiler Architecture Checklist

### 4.1 Pipeline
- [ ] `Source -> Tokens -> AST -> Semantic Model -> Lowered IR -> Backend Emitter`.
- [ ] Diagnostics carry source ranges through each stage.
- [ ] Transpile result includes emitted source + diagnostics + metadata.

### 4.2 Backends
- [ ] Dedicated Bash emitter.
- [ ] Dedicated PowerShell emitter.
- [ ] Shared helper layer for escaping, quoting, and literal formatting.
- [ ] Backend conformance tests for equivalent outputs on key scenarios.

### 4.3 Code generation quality
- [ ] Generated script is readable enough for debugging.
- [ ] Deterministic output (stable ordering, stable formatting).
- [x] Hot-path expressions avoid subshells and unnecessary runtime conversion.
- [ ] Safe identifier mangling to avoid target collisions.

## 5. Shell Interop Checklist (Critical)

### 5.1 External command execution
- [ ] Execute command with argument-array semantics (no unsafe string concat).
- [ ] Read exit code explicitly.
- [ ] Capture stdout and stderr separately.
- [ ] Stream output mode for long-running commands.
- [ ] Timeout/cancellation support for command execution.

### 5.2 Error behavior
- [ ] Portable fail-fast mode (default) for command failures.
- [ ] Opt-out mode to handle non-zero exits manually.
- [ ] Standard error object shape (code, stdout, stderr, command).

### 5.3 Pipelines
- [ ] Pipe external command output into Sushi expressions.
- [ ] Pipe Sushi values into external command stdin (text mode minimum).
- [ ] Ensure pipeline behavior is tested for both Bash and PowerShell.

## 6. Portable Stdlib v1 Checklist

### 6.1 Console
- [ ] `print(value)`
- [ ] `println(value)`

### 6.2 File and IO
- [ ] `std.io.readText(path)`
- [ ] `std.io.writeText(path, text, append: bool = false)`
- [ ] `std.io.exists(path)`

### 6.3 Paths
- [ ] `std.path.join(...parts)`
- [ ] `std.path.dirname(path)`
- [ ] `std.path.basename(path)`

### 6.4 Environment and process
- [ ] `std.env.get(name, fallback = null)`
- [ ] `std.env.set(name, value)`
- [ ] `std.process.args()`
- [ ] `std.process.exit(code = 0)`

### 6.5 OS context
- [ ] `std.os.cwd()`
- [ ] `std.os.chdir(path)`

### 6.6 Additional beta-level practical APIs
- [x] `std.json.parse(text)` (maps to Sushi dynamic object/array values)
- [x] `std.json.stringify(value)`
- [x] `std.fs.glob(pattern)` (portable behavior contract documented)
- [x] `std.http.get(url, headers?)`
- [x] `std.http.post(url, body, headers?)`

## 7. CLI and UX Checklist

### 7.1 Command behavior
- [ ] `sushi check <file>` validates syntax + semantics without emit.
- [ ] `sushi fmt <files>` formats deterministically.
- [ ] `sushi transpile <file> -t <bash|powershell>` writes target script.
- [ ] `sushi run <file> -t <bash|powershell>` transpiles + executes.
- [ ] `sushi watch <file>` re-checks/transpiles on changes.

### 7.2 Diagnostics and errors
- [ ] Consistent error format: file, line, column, code, message.
- [ ] Actionable suggestions for common mistakes.
- [ ] Colored output with non-color fallback.
- [ ] Non-zero exit codes for failing commands.

## 8. Testing and Quality Checklist

### 8.1 Unit tests
- [ ] Tokenizer/Lexer/Parser coverage for language constructs.
- [ ] Semantic analysis tests for diagnostics.
- [ ] Intrinsic resolution and argument binding tests.
- [ ] Backend expression/statement emission unit tests.

### 8.2 Integration tests
- [ ] End-to-end transpile tests for representative scripts.
- [ ] End-to-end run tests for command execution, files, env, and JSON.
- [ ] Golden snapshot tests for emitted Bash and PowerShell output.

### 8.3 Conformance matrix
- [ ] Windows + PowerShell 7
- [ ] Windows + Git Bash (or WSL Bash)
- [ ] Linux + Bash
- [ ] macOS + Bash

### 8.4 Reliability thresholds
- [ ] CI green on all supported environments.
- [ ] No known P0/P1 bugs open.
- [ ] Test flake rate below agreed threshold (target: <1%).

## 9. Documentation Checklist

### 9.1 User docs
- [ ] Quickstart from install to first script.
- [ ] Language basics with portable patterns.
- [ ] Stdlib reference for every beta API.
- [ ] Shell interop guide with safety rules.
- [ ] Known limitations and unsupported features.

### 9.2 Contributor docs
- [ ] Compiler architecture overview.
- [ ] How to add a new intrinsic stdlib API.
- [ ] Backend emitter conventions and escaping rules.
- [ ] Testing strategy and snapshot update workflow.

## 10. Example Projects Checklist

Include real examples proving practical utility.

- [ ] CI helper script (lint/test/build orchestration).
- [ ] Deployment helper (env handling, file operations, command execution).
- [ ] API automation script (HTTP + JSON + conditional logic).
- [ ] File sync/report script (globbing + iteration + summary output).

Each example must include:
- [ ] Source `.sushi` file
- [ ] Expected behavior description
- [ ] How to run on Bash and PowerShell
- [ ] Notes about portability assumptions

## 11. Beta Stabilization Checklist

### 11.1 Backward compatibility policy
- [ ] Define what can still change in beta.
- [ ] Publish deprecation policy for renamed APIs.
- [ ] Emit warnings for deprecated constructs before removal.

### 11.2 Performance sanity
- [x] Manifest benchmark artifacts include transpile time and generated size.
- [x] Native-vs-transpiled startup/runtime overhead is measured per target.
- [x] CPU/runtime benchmark scenarios enforce a 5x median-ratio ceiling.

### 11.3 Security and safety
- [ ] Prevent obvious command injection via unsafe interpolation paths.
- [ ] Safe temp file handling.
- [ ] Clear documentation of unsafe escape hatches, if any.

## 12. Suggested Milestone Plan

## Milestone 1: Compiler Foundation (Week 1-2)
- [ ] Implement transpiler pipeline skeleton and diagnostic plumbing.
- [ ] Add Bash/PowerShell backend emitters with minimal expression support.
- [ ] Wire `transpile` command end-to-end.

Acceptance criteria:
- [ ] Simple scripts transpile successfully to both targets.
- [ ] Failure diagnostics include source location.

## Milestone 2: Portable Stdlib Core (Week 2-4)
- [ ] Implement intrinsic registry/resolution/lowering.
- [ ] Implement console/io/path/env/process/os core APIs.
- [ ] Add tests for argument validation and backend mappings.

Acceptance criteria:
- [ ] Core stdlib APIs pass integration tests on Bash + PowerShell.
- [ ] Portable behavior matches documented contract.

## Milestone 3: Shell Interop + Practical APIs (Week 4-6)
- [x] Implement robust external command execution APIs.
- [x] Implement JSON helpers and basic HTTP/glob support.
- [x] Add example scripts that rely on these features.

Acceptance criteria:
- [ ] Real automation tasks can be completed in Sushi.
- [ ] Command execution behavior is stable under failure cases.

## Milestone 4: Tooling + Hardening (Week 6-8)
- [ ] Implement `check`, `fmt`, `run`, `watch` behavior.
- [ ] Improve diagnostics UX and docs.
- [ ] Complete conformance matrix on CI and fix portability gaps.

Acceptance criteria:
- [ ] All release gates in section 2 are checked.
- [ ] Beta release candidate passes full test matrix.

## 13. Beta Exit Criteria

Before announcing beta publicly:
- [ ] All release gates (section 2) checked.
- [ ] No open critical bugs.
- [ ] Docs and examples complete.
- [ ] Tagged beta release with changelog and known limitations.

---

## 14. Notes for Post-Beta (Not Required for Beta)
- Typed shell command contracts and richer process control.
- Package manager and dependency lockfile workflow.
- Language server (LSP) support.
- Extended stdlib (streams, archives, crypto, advanced networking).
