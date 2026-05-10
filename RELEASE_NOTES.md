#### 0.1.0-alpha May 10th 2026 ####

First publishable cut of ShellSyntaxTree — a focused .NET library that
parses bash command strings into a structured AST for security-gate
evaluators. Hand-rolled, AOT-trim friendly, no native dependencies.

**What's in this release**

- `IShellParser` interface + `BashParser` implementation per locked
  v0.1 contract (SPEC.md §2 / §3)
- Bash lexer: words, quoted strings, operators, opaque substitutions
  (`$()` / backticks → DynamicSkip), arithmetic (`$((...))`) and complex
  parameter expansion (`${var//.../...}`) → IsUnparseable
- Verb tables (BashArity, CwdVerbs, FileVerbs, FlagsWithValue) + per-verb
  path-arg rules + flag-with-value-aware verb-chain probe
- Path resolver: tilde / `$HOME` expansion, `filesystem::` prefix strip,
  glob detection (covering-dir heuristic preserved), cross-platform
  forward-slash normalization
- cd-in-compound attribution: synthetic `Arg.IsCwdAttribution` propagated
  to subsequent clauses; `cd $VAR` produces a DynamicSkip attribution
  signal
- Subshell isolation via attribution stack with monotonic IDs (handles
  sibling subshells `(a) && (b)` cleanly)
- `bash -c` / `sh -c` recursion (cap at depth 5 → outer
  `ParsedCommand.IsUnparseable=true`)
- 115-entry corpus across all 11 SPEC §13 categories, validated by
  `CorpusRunnerTests` with a polished `AstAssert.Equal` helper
- PII audit `[Fact]` enforcing SPEC §14 sanitization patterns

**Public API surface (locked per SPEC §2 / §3)**

`IShellParser`, `BashParser`, `BashParserOptions`, `ParsedCommand`,
`Clause`, `VerbChain`, `Arg`, `Redirect` (records); `ArgKind`,
`RedirectDirection`, `CompoundOperator` (enums). Multi-target
`netstandard2.0;net8.0`; AOT-friendly (`<IsAotCompatible>true</IsAotCompatible>`).

**Verification**

353 tests passing on Linux + Windows. `dotnet pack` produces
`ShellSyntaxTree.0.1.0-alpha.nupkg` with embedded README, icon, and
SourceLink metadata.

**Known limitations (tracked for v0.1.x)**

- `pushd` / `popd` parse as CwdVerbs but don't propagate cwd
  attribution (only `cd` / `chdir` do in v0.1)
- `tar` falls through to the default per-verb rule (no action-flag
  awareness)
- `docker -v "/host:/container"` is a single literal arg with
  `IsPath=false` (no colon-split in v0.1)
- Single-quoted `'$HOME'` is substituted by the resolver (bash
  semantics: doesn't substitute in single quotes)

**Documentation**

- `SPEC.md` — locked v0.1 contract (the source of truth for parser
  behavior)
- `openspec/changes/` — change-proposal history with rationale for the
  eight v0.1 SPEC interpretations resolved during planning
- `README.md` — quick-start usage

