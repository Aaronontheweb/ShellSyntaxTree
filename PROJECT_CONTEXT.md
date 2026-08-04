# Project Context — ShellSyntaxTree

**Repository:** `Aaronontheweb/ShellSyntaxTree`
**License:** Apache-2.0
**Owner:** Aaron Stannard ([@Aaronontheweb](https://github.com/Aaronontheweb))

## What ShellSyntaxTree Is

ShellSyntaxTree is a focused .NET library that **parses shell command strings
into a structured AST** for downstream policy / security gate evaluation. It
is a *parser*, not an interpreter — it never executes, expands, or evaluates
commands.

The output is a `ParsedCommand` containing:

- one or more `Clause` records (split on `&&`, `||`, `;`, `|`)
- per-clause verb chain (multi-token verbs like `git push`, `docker compose up`)
- per-clause args with `IsPath` classification, `Resolved` absolute path
  when known, and explicit `DynamicSkip` marking for unresolved env vars
  / unexpanded globs
- redirect operators (`>`, `>>`, `<`, `2>`, `2>>`)
- source-ordered clause elements with exact spelling, decoded values, source
  spans when available, and coordinates relative to parser-classified verb
  elements; executable-specific semantics remain consumer-owned
- Bash `cd <dir> && cmd` and PowerShell `Set-Location <dir>; cmd`
  propagation — the target is attributed to subsequent clauses
- recursion into `bash -c`, `pwsh -Command`, and `pwsh -EncodedCommand` so
  wrapped commands surface as clauses
- PowerShell alias canonicalization and explicit dynamic-command identity
- Bash subshell isolation and PowerShell grouping semantics for cwd attribution
- safe-fail flag `IsUnparseable` for unsupported constructs (control flow,
  function definitions, process substitution, deep command-string nesting,
  unbalanced quotes/parens/opaque regions)

The complete contract lives in [`SPEC.md`](./SPEC.md) and
[`SPEC.POWERSHELL.md`](./SPEC.POWERSHELL.md).

## Who It's For

**Primary consumer: [Netclaw](https://github.com/netclaw-dev/netclaw)** — an
open-source autonomous operations agent. Netclaw's POSIX approval gate consumes
ShellSyntaxTree's Bash parser to decompose approval units, identify candidate
verbs and directories, propagate cwd context, inspect redirects, and fail
closed when parsing is uncertain. Its PowerShell integration is the remaining
v0.2.0 downstream acceptance item. Netclaw is expected to use the consumer
guide's general executable-aware matching path for supported commands, with
strict authored-stream matching as the fallback for unrecognized shapes. See
[`docs/CONSUMER_GUIDE.md`](./docs/CONSUMER_GUIDE.md) for the public consumer
algorithm and immutable Netclaw examples.

**Acceptance is tied to Netclaw integration** (see SPEC §17 #7-#8): the
package must be consumable via `<PackageReference>` and exercise at least
one corpus entry through Netclaw's live matcher.

**Secondary consumers:** any tool that needs to reason about the shape of
agent-emitted shell commands without executing them — pre-commit hooks,
audit pipelines, sandbox policy engines, IDE security extensions.

## Why It Exists

LLM-driven agents emit shell commands. Naive substring matching is unsafe
(`rm /tmp/foo` vs `rm -rf $HOME/foo`); shelling out to `bash -n` is unsafe
and doesn't yield AST nodes; tree-sitter-bash is overkill and ships native
binaries. ShellSyntaxTree fills the middle: a hand-rolled, AOT-friendly,
zero-native-deps .NET parser sized to what security gates actually need.

## Scope Discipline

### v0.2 (current prerelease line)

- Bash and PowerShell 7 pipeline parsing ship behind the shared
  `IShellParser` seam. Windows `cmd` remains deferred.
- Public API surface in SPEC §2 is **locked**. Internal changes are free.
- Acceptance is the multi-shell corpus contract: every Bash and PowerShell
  JSON entry parses to its expected AST, and the PowerShell corpus also passes
  the live `pwsh` oracle matrix.

### Explicit non-goals

- Command execution.
- Variable expansion of any kind (we **mark** dynamic tokens, never resolve
  them; `$HOME` is the only exception).
- Heredoc body extraction.
- Process substitution `<(cmd)`, `>(cmd)`.
- Function definitions, `for`/`while`/`case` control flow, arithmetic
  expansion `$((...))`.
- PowerShell script-level control flow, definitions, expression evaluation,
  and `.ps1` file-content parsing.
- Performance optimization beyond "fast enough to invoke per shell call
  without noticeable latency" (~1 ms typical).
- Full IDE-style source mapping. `Clause.Elements` provides security-motivated
  provenance for significant clause leaves, not a lossless concrete syntax
  tree.

### Versioning

- `0.1.0-alpha` — first publishable cut, Bash-only.
- `0.1.x` — additive (more verb table entries, more corpus, bug fixes).
- `0.2.0` — first PowerShell parser implementation; alpha and beta.1 shipped,
  stable promotion pending downstream validation.
- `1.0.0` — at least one external consumer beyond Netclaw ships against it
  without finding API gaps.

## Architectural Constraints

- **Public API in `SPEC.md` §2 is the contract.** Everything else is
  `internal`. During `0.x`, renaming or removing public fields requires a
  deliberate minor version bump and migration notes; after `1.0`, it requires
  a major version bump.
- **`IShellParser` is the multi-shell seam.** Additional parsers such as
  Windows `cmd` must be addable without reshaping consumer code.
- **No native dependencies.** AOT-trim friendly; ship a single managed
  package.
- **Multi-targeting**: `netstandard2.0` for broad consumer reach, `net8.0`
  for modern runtimes, tests on `net10.0`.
- **Security defaults bias**: when in doubt, mark `DynamicSkip` /
  `IsUnparseable`. Consumers can always relax — they cannot retroactively
  un-execute a command we falsely classified as safe.

## Shipped acceptance for 0.1.0-alpha

Per SPEC §17, all of the following must be true:

1. Public API matches SPEC §2 exactly. `dotnet pack` produces a
   `ShellSyntaxTree.0.1.0-alpha.nupkg`.
2. Every corpus entry in
   `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` parses to its expected
   AST. `dotnet test` runs them all and passes.
3. Corpus has ≥ 105 entries spanning the SPEC §13 categories.
4. PII audit scan over `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` finds
   zero hits.
5. PR validation runs on GitHub Actions and passes.
6. Tagging `0.1.0-alpha` triggers `publish_nuget.yml` and the package
   appears on nuget.org.
7. Netclaw consumes the package via `<PackageReference>` and `IShellParser`
   resolves at runtime in Netclaw's DI container.
8. At least one Netclaw integration test exercises a real corpus entry
   through Netclaw's matcher and produces the expected gate decision.

## Key Pain Points and Risks

- **Corpus PII**. Real-world bash corpus entries seed from agent dogfood
  logs (`~/.netclaw/logs/daemon-*.log`) that contain usernames, repo paths,
  channel/thread IDs. Sanitization is mandatory and gated by a CI scan
  (SPEC §14). Shipping unsanitized PII is a release-blocker.
- **Verb/binding table drift**. Bash verb tables and PowerShell alias,
  binding, and per-verb tables are static data. Adding entries is cheap;
  missing entries can misclassify. The corpora and `pwsh` oracle are the
  early-warning system.
- **Command-string recursion depth**. `bash -c`, `pwsh -Command`, and
  `pwsh -EncodedCommand` recursion is capped at 5. Going deeper marks the
  result `IsUnparseable` rather than risking hostile recursion.
- **Resolver assumptions**. `WorkingDirectory` defaults to the daemon's
  cwd. If consumers pass the wrong cwd, relative-path attribution will
  silently disagree with what the selected shell would do at runtime.
- **API surface lock**. The `0.x` line commits to the documented AST shape;
  breaking changes require a deliberate minor bump and migration notes.

## Where Things Live

| Concern | Location |
|---|---|
| Library source | `src/ShellSyntaxTree/` |
| Tests + corpus | `tests/ShellSyntaxTree.Tests/` |
| Corpus entries | `tests/ShellSyntaxTree.Tests/Corpus/{bash,powershell}/*.json` |
| The contracts | `SPEC.md`, `SPEC.POWERSHELL.md` |
| Consumer guide | `docs/CONSUMER_GUIDE.md` |
| Active work plan | `IMPLEMENTATION_PLAN.md` |
| Tooling inventory | `TOOLING.md` |
| Agent constitution | `AGENTS.md` (and `CLAUDE.md`) |
| CI | `.github/workflows/{pr_validation,publish_nuget}.yml` |
| Build settings | `Directory.Build.props`, `Directory.Packages.props`, `global.json` |
| Solution | `ShellSyntaxTree.slnx` |

## Update Discipline

This file is **mutable** but should change only when the project's purpose,
audience, scope, or constraints actually shift. Day-to-day work tracking
belongs in `IMPLEMENTATION_PLAN.md`. Tooling changes belong in `TOOLING.md`.
The agent constitution (`AGENTS.md`) should rarely change.
