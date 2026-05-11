#### 0.1.2-alpha May 11th 2026 ####

Three parser correctness fixes. Public API unchanged.

**Fixed**

- **Single-quoted strings are now literal per SPEC §5 (B2).** Previously
  `echo '$HOME'` produced `Kind=Tilde` because the resolver substituted
  `$HOME` uniformly regardless of quote style. Now the lexer marks
  single-quoted `QuotedString` tokens with the internal `IsSingleQuoted`
  flag, and the resolver bypasses tilde / `$HOME` / `$VAR` / glob /
  `filesystem::` handling for them. `echo '$HOME'` stays `Kind=Literal`,
  `Resolved=null`; `cat '/etc/passwd'` still resolves a path. Matches
  bash semantics.
- **`LooksLikePath` no longer false-positives on a lone trailing
  backslash (B3).** A double-quoted token like `"foo\\"` lexes to
  Value `foo\`; the trailing `\` is an escape-collapse artifact, not a
  meaningful path signal. The heuristic now requires a backslash at a
  non-trailing position. Forward-slash behavior is unchanged — `dir/`
  still classifies as a path (trailing `/` is a meaningful bash
  directory hint).
- **Control-flow keyword detection precedes paren-balance (B4).**
  Previously `case x in a) ;; esac` produced `IsUnparseable=true` with
  reason `unbalanced parens at position N` because the `)` in `a)`
  tripped `SplitIntoSegments` before the per-clause keyword check
  could fire. The anomaly pass now scans the token stream for
  control-flow keywords at verb position (start of input or after
  `&&` / `||` / `;` / `|` / `(`) and short-circuits with the helpful
  `control-flow keyword 'case' is not supported in v0.1` reason
  before downstream checks run. SPEC §11 now pins the full diagnostic
  precedence order.

**Behavior notes**

- Public API surface is unchanged (no `PublicApiSnapshotTests` delta).
- SPEC.md §8: new "Step 0: Single-quoted bypass" preamble; LooksLikePath
  heuristic updated to call out the trailing-backslash carve-out.
- SPEC.md §11: new "Diagnostic precedence" section enumerating the
  order in which unparseable conditions are checked.
- Corpus entries 104 (`echo 'literal $HOME'`) and 109 (`echo "trailing
  backslash\\"`) updated to the corrected outputs. Four new entries
  (119–122) pin the regression guards: single-quoted absolute paths
  still resolve, `cd dir/` still classifies as a path, single-quoted
  `$VAR` stays literal under `rm`, and `case x in a) ;; esac` now
  reports the control-flow keyword reason instead of a paren-balance
  error.

#### 0.1.1-alpha May 11th 2026 ####

Bug fix release for v0.1.0-alpha consumers.

**Fixed**

- **`2>&1` fd-dup redirects no longer produce phantom `<cwd>/&1` file
  targets.** The parser now recognizes POSIX fd-dup / fd-close shorthand
  (`&N`, `&N-`, `&-`) on redirect targets and carries the raw token
  verbatim on `Redirect.Target` with `Redirect.IsDynamicSkip = true`.
  Existing consumers that already skip redirects with
  `IsDynamicSkip = true` get correct behavior with no code changes.
  (B1)

**Behavior notes**

- Public API surface is unchanged. `Redirect.Target` xmldoc and SPEC.md
  §3 / §4 are clarified to document the fd-dup rule.
- The Blazor sample's basename-startswith-`&` workaround has been
  removed; the sample now relies solely on `Redirect.IsDynamicSkip`.

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

