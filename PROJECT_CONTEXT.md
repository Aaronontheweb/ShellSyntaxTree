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
- `cd <dir> && cmd` propagation — the cd target is attributed to subsequent
  clauses in the same compound
- recursion into `bash -c "<inner>"` so wrapped commands surface as clauses
- subshell `( ... )` isolation for `cd` attribution
- safe-fail flag `IsUnparseable` for unsupported constructs (control flow,
  function definitions, process substitution, deep `bash -c` nesting,
  unbalanced quotes/parens)

The complete contract lives in [`SPEC.md`](./SPEC.md).

## Who It's For

**Primary consumer: [Netclaw](https://github.com/netclaw-dev/netclaw)** — an
open-source autonomous operations agent. Netclaw's approval gate currently
hand-rolls equivalent functionality in
`src/Netclaw.Security/ShellApprovalSemantics.cs` and `ShellTokenizer.cs`.
ShellSyntaxTree v0.1 is intended to **replace that hand-rolled approximation**
with a richer, structured AST that Netclaw's gate can walk directly.

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

### v0.1 (current)

- Bash only. PowerShell and Windows `cmd` are deferred — but the
  `IShellParser` seam is in place so consumers don't have to refactor.
- Public API surface in SPEC §2 is **locked**. Internal changes are free.
- Acceptance is the corpus contract (SPEC §13): every JSON entry parses to
  its expected AST.

### Explicit non-goals (v0.1)

- Command execution.
- Variable expansion of any kind (we **mark** dynamic tokens, never resolve
  them; `$HOME` is the only exception).
- Heredoc body extraction.
- Process substitution `<(cmd)`, `>(cmd)`.
- Function definitions, `for`/`while`/`case` control flow, arithmetic
  expansion `$((...))`.
- Performance optimization beyond "fast enough to invoke per shell call
  without noticeable latency" (~1 ms typical).
- Source-mapping (line/column for AST nodes — useful for IDEs, irrelevant
  for security gates).

### Versioning

- `v0.1.0-alpha` — first publishable cut, bash-only.
- `v0.1.x` — additive (more verb table entries, more corpus, bug fixes).
- `v0.2.0` — first PowerShell parser implementation.
- `v1.0.0` — at least one external consumer beyond Netclaw ships against it
  without finding API gaps.

## Architectural Constraints

- **Public API in `SPEC.md` §2 is the contract.** Everything else is
  `internal`. Renaming/removing public fields requires a major version bump.
- **`IShellParser` is the multi-shell seam.** PowerShell and `cmd` parsers
  must be addable without touching consumer code.
- **No native dependencies.** AOT-trim friendly; ship a single managed
  package.
- **Multi-targeting**: `netstandard2.0` for broad consumer reach, `net8.0`
  for modern runtimes, tests on `net10.0`.
- **Security defaults bias**: when in doubt, mark `DynamicSkip` /
  `IsUnparseable`. Consumers can always relax — they cannot retroactively
  un-execute a command we falsely classified as safe.

## Acceptance for v0.1.0-alpha

Per SPEC §17, all of the following must be true:

1. Public API matches SPEC §2 exactly. `dotnet pack` produces a
   `ShellSyntaxTree.0.1.0-alpha.nupkg`.
2. Every corpus entry in `tests/Corpus/bash/*.json` parses to its expected
   AST. `dotnet test` runs them all and passes.
3. Corpus has ≥ 105 entries spanning the SPEC §13 categories.
4. PII audit scan over `tests/Corpus/bash/*.json` finds zero hits.
5. PR validation runs on GitHub Actions and passes.
6. Tagging `v0.1.0-alpha` triggers `publish_nuget.yml` and the package
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
- **Verb table drift**. `BashArity`, `FileVerbs`, `CwdVerbs`, and
  `FlagsWithValue` are static data. Adding entries is cheap; *missing*
  entries means the parser silently mis-classifies. The corpus is the
  early-warning system.
- **`bash -c` recursion depth**. Capped at 5 (SPEC §10). Going deeper
  marks the deepest clause `IsUnparseable` so we don't blow the stack on
  hostile inputs.
- **Resolver assumptions**. `WorkingDirectory` defaults to the daemon's
  cwd. If consumers pass the wrong cwd, relative-path attribution will
  silently disagree with what bash would do at runtime. Document loudly.
- **API surface lock**. v0.1 commits to the AST shape. Mistakes here cost
  a major-version bump to fix.

## Where Things Live

| Concern | Location |
|---|---|
| Library source | `src/ShellSyntaxTree/` *(to be created)* |
| Tests + corpus | `tests/ShellSyntaxTree.Tests/` *(to be created)* |
| Corpus entries | `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` |
| The contract | `SPEC.md` |
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
