#### 0.1.4-alpha May 12th 2026 ####

Greedy verb-chain extraction. Public API surface (`VerbChain`, `Clause`)
unchanged; the *content* of `Clause.Verb.Tokens` changes for many inputs.

**BEHAVIOR CHANGE: verb-chain length is no longer table-driven (#27)**

- The `BashArity` static lookup table and `ProbeArity()` method have been
  **removed**. The parser walks consecutive verb-like Word tokens from
  the start of each clause, transparently consuming flag-with-value
  pairs (e.g. `git -C /repo`), and stops at the first non-verb-like
  token, the first plain flag, or the first non-Word token.
- A token is "verb-like" when its kind is `Word`, length 1–64, first
  character is an ASCII lowercase letter, and remaining characters are
  in `[a-z0-9._-]`. The strict allow-list naturally excludes flags,
  paths (`/`, `\`, `~`), env-var refs (`$VAR`), URLs (`://`), globs,
  numeric tokens, and uppercase user-named identifiers like migration
  names — without requiring per-case predicate logic. See SPEC §6.1.
- For known FILE verbs (`cat`, `ls`, `bash`, `cd`, `chmod`, `grep`,
  `find`, …) the verb chain stops at exactly one token to preserve
  per-verb positional-arg classification. The flag-with-value
  consumption still runs so `tar -C /path` and `curl -o file` style
  values still pick up `IsPath=true` via `FlagValueIsPath`.

Examples that change:

- `git push origin main` → verb `[git, push, origin, main]` (was
  `[git, push]`).
- `git worktree list` (and arbitrary CLI subcommand chains) → fully
  extracted as `[git, worktree, list]` (was `[git, worktree]`).
- `freshdesk ticket list --status open` → `[freshdesk, ticket, list]`
  (was `[freshdesk]` because freshdesk wasn't in the BashArity table).
- `kubectl get pods my-pod` → `[kubectl, get, pods, my-pod]` (was
  `[kubectl, get]`).
- `aws s3 cp src dst` → `[aws, s3, cp, src, dst]` (was `[aws, s3]`).
- `dotnet ef migrations add InitialCreate` → `[dotnet, ef, migrations,
  add]` (was `[dotnet, ef]`). `InitialCreate` stays in args because the
  predicate rejects uppercase first character.
- `cat README` → still `[cat]` (FileVerb carveout preserves `IsPath` on
  bare-name targets).
- `echo hello` → `[echo, hello]` (echo is not a FILE verb).

`Clause.Verb` is now documented as a **convenience hint, not a security
contract** (SPEC §6.1.1). Consumers needing security-grade verb
identification should pattern-prefix match against the raw token
stream: a command matches an approval pattern *P* iff the first
`len(P.verb_prefix)` command tokens equal `P.verb_prefix`. This punts
depth choice to the consumer and accommodates the parser's deliberate
over-extraction on bare-word args. Auto-proposed patterns should default
to the full extracted verb chain (greedy match): a subsequent variation
re-prompts rather than silently auto-grants.

**Behavior notes**

- Public API surface is unchanged (no `PublicApiSnapshotTests` delta).
- SPEC.md updates: §3 `VerbChain`, §4 grammar, §6.1 verb-chain
  extraction (rewritten end-to-end), new §6.1.1 consumer
  pattern-matching guidance, §7 flag-with-value note, §12 worked
  examples, §15 versioning, §16 implementation sequencing.
- Corpus: 7 new entries (132–138) pin the issue #27 headline cases;
  10 existing entries flipped to the new shape (`04_echo_hello`,
  `11_git_push_origin_main`, `13_git_checkout_dev`, `17_docker_run_nginx`,
  `27_make_install`, `45_echo_append_log`, `84_subshell_nested`,
  `91_bash_c_simple`, `96_bash_c_nested_depth_2`,
  `100_bash_c_nested_depth_3`, `130_netclaw_repro_leading_comment_pipeline`).
- Unit tests: 8 pinned `BashCommandParserTests` cases updated to the new
  expected verb chains.

#### 0.1.3-alpha May 12th 2026 ####

Bash line comment handling. Public API unchanged.

**Fixed**

- **Bash line comments are now recognized and skipped (#25).** `BashLexer`
  treats `#` at a word boundary (start of input, or preceded by
  whitespace, a newline, or any operator) as the start of a comment
  that runs to the next newline. The comment text is emitted as a new
  internal `BashTokenKind.Comment` token for source fidelity and is
  filtered by the parser alongside `Whitespace` / `Continuation`, so
  it contributes no verb, args, redirects, or flags to any clause.
  Comment-only input parses to `Clauses = []`, `IsUnparseable = false`,
  matching the existing empty-/whitespace-only path. Quoting and
  escape rules are honored: `#` inside single or double quotes is
  literal, `#` in the interior of an unquoted word (e.g. `abc#def`)
  is literal, and `\#` outside quotes is literal.

  Before this fix, `# Extract worktree branches\ngit worktree list`
  parsed to a single clause with verb chain `[#, Extract]` — the
  comment text leaked into downstream approval prompts and broke
  approval-state caching in consumers that did asymmetric verb-chain
  extraction (persistence-time vs. retry-authorization saw different
  verb sets, causing tool calls to fail after the user had already
  clicked Approve).

**Behavior notes**

- Public API surface is unchanged (no `PublicApiSnapshotTests` delta).
- SPEC.md §4 / §5: new "Comment handling" subsection in §5 documents
  the boundary rules; §4 BNF notes that comments are
  whitespace-equivalent at the lexer level.
- Corpus: 9 new entries (123–131) pin every case from the issue
  report, plus the two Netclaw repros (sanitized paths per §14).
- v0.1 still does not treat top-level newlines as statement separators
  (SPEC §4 gap, tracked separately in IMPLEMENTATION_PLAN NEXT) — a
  comment between two commands on separate lines requires an explicit
  `;` separator to split into two clauses.

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

