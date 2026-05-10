# Design — v0.1 locked interpretations

## Context

ShellSyntaxTree's v0.1 implementation has to make a handful of design
choices that ripple through every PR in the shipping plan. This document
captures the cross-cutting decisions so future agents (or this one in
v0.2) don't re-derive them by guesswork.

The constraints we're designing under:

- **Public API is locked** by SPEC §2/§3. Any changes to the surface bump
  the version per SPEC §15. Locked-interpretation #1 (`IsCwdAttribution`)
  is the sole additive surface change in v0.1.0-alpha; everything else
  in this change is internal or specification-text-only.
- **Multi-shell ready.** `IShellParser` is the public seam. v0.2 brings
  `PwshParser : IShellParser`; v0.3 may bring cmd. Internal abstractions
  must scale without forcing a from-scratch rewrite.
- **AOT / trim friendly.** No `Reflection.Emit`, no dynamic loading, no
  source generators in v0.1, no native dependencies. `IsAotCompatible=true`
  set on the library project so the compiler warns on drift.
- **Single maintainer; autonomous PR loop.** Decisions need to be defensible
  without a synchronous review channel.

## Goals / Non-Goals

**Goals:**

- Resolve the eight SPEC ambiguities so the implementation has crisp
  answers every time the question recurs.
- Establish the shared internal abstractions (resolver, opaque-region
  scanner) in a way that v0.2 PowerShell can plug into without redesign.
- Keep the public surface change in v0.1.0-alpha to one additive bool
  field (`Arg.IsCwdAttribution`).

**Non-Goals:**

- Full capability-spec decomposition under `openspec/specs/`. Path C from
  the planning interview defers that work until v0.2 forces it. v0.1 ships
  with one root `shellsyntaxtree-v0.1` spec stub deferring to `SPEC.md`.
- An `IShellGrammar` abstraction that decouples lexer/parser/tables. The
  second implementation (PowerShell) is the right driver for that; abstract
  prematurely and we'll get the seam wrong.
- Source-mapping (line/column on AST nodes). SPEC §18 explicitly defers
  this; not relevant for security gates.
- Performance optimization beyond "fast enough" (~1ms typical). SPEC §1
  defers this.

## Decisions

### Decision 1: `IsCwdAttribution` lives on `Arg`, not `Clause`

The careless-consumer failure mode dictates this. Under "field on `Arg`,"
the consumer naively iterating `clause.Args` looking for `IsPath = true`
sees the synthetic cwd-attribution arg automatically. Worst case: prompts
once extra. Under "side-channel `Clause.AttributedCwd`," the careless
consumer forgets to read the side-channel and a `grep` running in `/etc`
slips past a hard-deny. The asymmetry between "annoying" and "unsafe"
picks the field-on-`Arg` design.

### Decision 2: Tiered handling of "evaluate-something-else-first" constructs

Bash has four such constructs and they don't all deserve the same
treatment.

| Construct | Decision | Why |
|---|---|---|
| `$(cmd)` | Token-level `DynamicSkip` | Common in agent output; surrounding clauses must stay parseable so hard-deny rules can fire on visible parts |
| `` `cmd` `` | Token-level `DynamicSkip` | Same as above; older syntax for the same construct |
| `$((expr))` | `ParsedCommand.IsUnparseable` | SPEC §1 explicit non-goal |
| `${var//pat/repl}` | `ParsedCommand.IsUnparseable` | SPEC §1 explicit non-goal |

The boundary tracking for `$()` and backticks goes in a shared
`Internal/Lexing/OpaqueRegionScanner.cs` so PowerShell's `$( … )` (and
`@( … )` for sub-expression evaluation) can reuse the same machinery in
v0.2.

### Decision 3: `ArgKind` carries directory-extraction strategy

The locked interpretation for Glob vs DynamicSkip turns `ArgKind` from
"what kind of token is this?" into "how does the consumer extract a
directory from this arg?":

| Kind | How consumer gets a directory |
|---|---|
| `Literal` | `Path.GetDirectoryName(arg.Resolved)` |
| `Tilde` | `Path.GetDirectoryName(arg.Resolved)` (after `~` expansion) |
| `EnvVar` | `Path.GetDirectoryName(arg.Resolved)` (only `$HOME` ever lands here) |
| `Glob` | `Path.GetDirectoryName(arg.Raw)` (covering-directory heuristic) |
| `DynamicSkip` | No directory available; consumer prompts |

This is the consumer-facing semantics that explains why Glob and
DynamicSkip can't be collapsed.

### Decision 4: `ParsedCommand.IsUnparseable` is the only "give up" signal

`Clause.IsUnparseable` is not added in v0.1. Failure cases that SPEC §10
or §11 describe at "deepest clause" granularity get translated to the
outer `ParsedCommand.IsUnparseable = true` with a precise reason. Clauses
parsed up to the failure point may still appear in `Clauses` for diagnostic
UI value, but consumers safe-fail on the outer flag.

This keeps the surface minimal. If future use cases (e.g., partially-bad
heredoc body, malformed redirect on one clause) want clause-level signals,
add `Clause.IsUnparseable` then with concrete drivers.

### Decision 5: Internal layout (pluggable grammar without full abstraction)

```
src/ShellSyntaxTree/
├── *.cs                           ← public types (locked by SPEC §2/§3)
├── Internal/
│   ├── Lexing/                    ← shared (grammar-agnostic)
│   │   └── OpaqueRegionScanner.cs ← used by Bash for $(...); pwsh later
│   ├── Resolving/                 ← shared (grammar-agnostic)
│   │   └── BashResolver.cs        ← (named "Bash" in v0.1; rename + share at v0.2)
│   └── Bash/
│       ├── Lexing/
│       │   ├── BashLexer.cs
│       │   ├── BashToken.cs
│       │   └── BashTokenKind.cs
│       ├── Parsing/
│       │   ├── BashCommandParser.cs
│       │   └── CdAttributionContext.cs
│       └── Verbs/
│           ├── BashVerbs.cs       ← BashArity, CwdVerbs, FileVerbs, FlagsWithValue
│           └── BashPerVerbRules.cs
└── Properties/
    └── IsExternalInit.cs          ← internal polyfill for netstandard2.0
```

Rules:

- Public types exported from `ShellSyntaxTree` namespace; everything else
  is `internal`.
- `Internal/Bash/` owns bash-specific code. `Internal/Pwsh/` will own
  pwsh-specific code symmetrically in v0.2.
- `Internal/Lexing/` and `Internal/Resolving/` host the shared
  abstractions that v0.2 will lean on. In v0.1 they have one user (bash);
  v0.2 adds a second user without renaming.
- The public `BashParser : IShellParser` is a thin façade that wires
  `BashLexer` + `BashCommandParser` + `BashResolver` together. v0.2's
  `PwshParser` will be the symmetric thin façade.

Resisted in v0.1: extracting an `IShellGrammar` interface that defines
"what a grammar provides" (lexer, verb tables, per-verb rules). Premature
without a second implementation. The folder discipline + locked AST is
enough governance.

### Decision 6: `pushd`/`popd` parse but don't propagate

The directory-stack semantics needed to model `pushd`/`popd` correctly
(option C in the planning interview) are real work — `~50–80 LOC of state
tracking + subshell-aware push/pop + ~10 corpus entries`. For a construct
that's *rare* in single-command LLM emissions, we accept the documented
limitation in v0.1: `pushd /target` parses (its first non-flag arg is
path-classified, hard-deny rules fire), but no synthetic IsCwdAttribution
arg gets appended to subsequent clauses. The GitHub issue for the option-C
upgrade tracks this as v0.1.x or v0.2.0 work.

### Decision 7: `cd $VAR` propagates a DynamicSkip attribution arg

Even when the cd target is dynamic, subsequent clauses get a synthetic
`Arg{ IsCwdAttribution = true, Kind = DynamicSkip, IsPath = false }`. This
preserves the "this clause's cwd context is unknown" signal that
sophisticated consumers can elevate to user-prompt, while naive
iterate-paths consumers see nothing different (since `IsPath = false`).

The alternative — appending no arg at all — would make `cd $VAR && cmd`
indistinguishable from `cmd` (no preceding cd) at the consumer level.
Silent signal loss is exactly what the locked-interpretations table avoids.

### Decision 8: `tar` and `docker -v` are documented v0.1 limitations

Both fall through to the default rule (all non-flag positionals → paths
for tar; single literal arg for `docker -v`). Real consumer pain probably
forces an upgrade path within v0.1.x, but speculative work belongs in
issues, not in the alpha.

## Failure modes and recovery

The locked interpretations all converge on the same recovery posture:

- Anything we can't model cleanly → `DynamicSkip` arg or
  `ParsedCommand.IsUnparseable = true`.
- Consumers route safe-fail per SPEC §11 (prompt user; offer Once / Deny
  only; no persistent grants on shapes the parser can't model).
- The PII audit (SPEC §14, implemented in PR 6) is the safety net for the
  corpus; the locked interpretations don't change its scope.

No interpretation chooses a "best-effort guess" path. The constitution's
discipline is preserved: when in doubt, mark and let the consumer decide.
