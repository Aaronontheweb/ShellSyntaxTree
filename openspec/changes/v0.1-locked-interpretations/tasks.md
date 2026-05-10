# Tasks — v0.1 locked interpretations

The eight interpretations land across PRs 1–6 of the v0.1.0-alpha shipping
plan (`/home/petabridge/.claude/plans/okay-are-you-ready-wise-emerson.md`).
This task list maps each interpretation to its implementing PR(s) and to
the SPEC.md sections that get updated alongside the implementation.

## 1. PR 1 — Public API surface and OpenSpec scaffolding

- [x] 1.1 Bootstrap `src/ShellSyntaxTree/ShellSyntaxTree.csproj`
      (multi-target `netstandard2.0;net8.0`, `IsAotCompatible=true` for
      net8.0)
- [x] 1.2 Implement public types per SPEC §2/§3 verbatim
- [x] 1.3 Add `Arg.IsCwdAttribution: bool` (default `false`) per
      interpretation #1
- [x] 1.4 Bootstrap `tests/ShellSyntaxTree.Tests/ShellSyntaxTree.Tests.csproj`
- [x] 1.5 Author `PublicApiSnapshotTests.cs` — reflection-based snapshot
      of the locked surface (18 facts asserting type kinds, properties,
      enum members, ctor behavior, default values)
- [x] 1.6 Add both projects to `ShellSyntaxTree.slnx`
- [x] 1.7 Bootstrap OpenSpec scaffolding (`openspec/`, change directories,
      this proposal/design/tasks/specs delta)
- [x] 1.8 Update `SPEC.md` §3 to enumerate `Arg.IsCwdAttribution`;
      annotate `VerbChain.Joined` to use `string.Join(" ", Tokens)` for
      cross-tfm compatibility (was `string.Join(' ', …)`, char overload
      missing on netstandard2.0)
- [x] 1.9 Update `IMPLEMENTATION_PLAN.md` — mark PR 1 in-progress;
      reference this OpenSpec change
- [x] 1.10 Update `TOOLING.md` to list installed OpenSpec skills under
       Helper Skills
- [x] 1.11 Run `pwsh ./scripts/Add-FileHeaders.ps1`; verify with `-Verify`
- [x] 1.12 `dotnet build -c Release` clean; `dotnet test -c Release`
       all-green (18/18 tests passing)
- [x] 1.13 Commit (signed) and push `pr1-bootstrap`; opened PR #4 with
       `gh pr merge --auto --squash` against `dev`. Auto-merged at
       2026-05-10T17:55:09Z (Linux 27s, Windows 1m19s).
- [ ] 1.14 Archive this OpenSpec change to
       `openspec/changes/archive/2026-05-10-v0.1-locked-interpretations/`
       (deferred: not all interpretations have landed yet — change spans
       PRs 1–6. Archive when PR 6 merges.)

## 2. PR 2 — Lexer + opaque-region scanner (interpretation #2)

- [x] 2.1 Implement `Internal/Lexing/OpaqueRegionScanner.cs` — finds
      balanced delimiters with quote-aware nesting; `Scan` for `(`/`)`
      style and `ScanSymmetric` for backtick style; honors `\X` escapes,
      single-quote literal preservation, double-quote escape table
- [x] 2.2 Implement `Internal/Bash/Lexing/BashLexer.cs` per SPEC §5;
      single entry point `Tokenize(string) -> IReadOnlyList<BashToken>`
- [x] 2.3 Recognize `$(...)` and backtick `` `...` `` regions via the
      scanner; emit `OpaqueSubstitution` token
- [x] 2.4 Recognize `$((` and `${var//` openers; emit
      `UnparseableSentinel` token with reason naming the construct
- [x] 2.5 Update `SPEC.md` §1 non-goals (command substitution → DynamicSkip),
      §5 (add OPAQUE_SUBSTITUTION + UNPARSEABLE_SENTINEL token kinds, add
      `<<-` operator, simple `${VAR}` absorbed into Word, newlines outside
      heredoc treated as Whitespace), §11 (add arithmetic +
      complex-param-expansion to IsUnparseable conditions list)
- [ ] 2.6 Tests: 16 OpaqueRegionScanner tests + 62 BashLexer tests, all
      green. Combined with PR 1's 18: 96/96 passing.
- [ ] 2.7 Run header script; verify; commit (signed) and push `pr2-lexer`;
      open PR with `gh pr merge --auto --squash`

## 3. PR 3 — Verb tables + parser core

- [x] 3.1 `Internal/Bash/Verbs/BashVerbs.cs` — `BashArity`, `CwdVerbs`,
      `FileVerbs`, `FlagsWithValue`, `ControlFlowKeywords` from SPEC §6
- [x] 3.2 `Internal/Bash/Parsing/BashCommandParser.cs` per SPEC §4
- [x] 3.3 Compound splitting on `&&`, `||`, `;`, `|`; verb chain
      longest-prefix probe; flag/positional walking
- [x] 3.4 Heredoc operator framework (lexer body-skip already in PR 2;
      parser emits placeholder Redirect)
- [x] 3.5 Anomaly safe-fail per SPEC §11 (control-flow keyword,
      function definition, process substitution, unbalanced parens)
- [x] 3.6 Wire `OpaqueSubstitution` tokens into
      `Arg{ Kind=DynamicSkip, IsPath=false }` per interpretation #2
- [x] 3.7 Wire `UnparseableSentinel` tokens into
      `ParsedCommand.IsUnparseable=true` per interpretation #2
- [x] 3.8 `BashParser.Parse` delegates to `BashCommandParser.Parse`;
      placeholder `NotImplementedException` removed
- [x] 3.9 `CorpusRunnerTests` skeleton (`[Theory] [MemberData]` over
      `tests/.../Corpus/bash/*.json` with field-by-field comparison;
      polished AstAssert lands in PR 6)
- [x] 3.10 Test project copies `Corpus/bash/*.json` to bin output via
       `<None Update CopyToOutputDirectory="PreserveNewest" />`
- [x] 3.11 Authored 50 corpus entries: 10 simple-verb + 10 multi-token-verb
       + 15 compound + 10 redirect + 5 unparseable. PR 4-5 will refine
       expectations once path classification + cd-attribution land.
- [x] 3.12 SPEC §7 updated: `FlagsWithValue` value type is `HashSet<string>`
       (not `IReadOnlySet<string>`) for netstandard2.0 compat; PR 4
       follow-up note on flag-with-value-aware verb-chain probing
- [x] 3.13 53 parser unit tests + 50 corpus runner cases. With PR 1+2:
       **199/199 passing**.

### PR 3 follow-ups (tracked for PR 4)

- Flag-with-value-aware verb-chain probe (`git -C /repo log` →
  `Verb.Tokens = ["git", "log"]`); currently probe stops at the leading
  flag and uses `["git"]`. SPEC §12 example expects the post-probe
  shape, so PR 4 must move the probe to run after flag-with-value
  consumption.
- Per-verb path-arg rules + path-shape classification to populate
  `IsPath`. Corpus entries currently have all literal args at
  `IsPath=false`; PR 4 will update the corpus to reflect the new
  classification.

## 4. PR 4 — Resolver + per-verb rules (interpretations #3 + #8)

- [ ] 4.1 `Internal/Resolving/BashResolver.cs` per SPEC §8
- [ ] 4.2 Tilde + `$HOME`; other env vars → `DynamicSkip, IsPath=false`
- [ ] 4.3 `filesystem::/path` strip; glob detection per interpretation #3
      (`Kind=Glob, IsPath=true (in path slot), Resolved=null`)
- [ ] 4.4 Relative-path joining against `WorkingDirectory`; `LooksLikePath`
      heuristic
- [ ] 4.5 `Internal/Bash/Verbs/BashPerVerbRules.cs` per SPEC §7 with
      interpretation #8 fallback for tar (default rule) and docker -v
      (single literal arg, IsPath=false)
- [ ] 4.6 Update `SPEC.md` §7 (note v0.1 limitations + reference issues),
      §8 (rewrite steps 4 & 6 to remove the overlap; explicit IsPath
      asymmetry per interpretation #3)
- [ ] 4.7 File 2 GitHub issues: tar action-flag awareness; docker -v
      colon-split + Windows drive-letter handling
- [ ] 4.8 Open OpenSpec change `path-resolver-rules` for the §7/§8 deltas

## 5. PR 5 — cd attribution + subshells + bash -c (interpretations #4, #5, #6)

- [ ] 5.1 `Internal/Bash/Parsing/CdAttributionContext.cs` (parser-internal
      mutable; output AST stays immutable)
- [ ] 5.2 Only `cd`/`chdir` propagate (interpretation #5); pushd/popd
      parse but don't propagate
- [ ] 5.3 Synthetic `Arg{ IsCwdAttribution=true, … }` appended to
      subsequent clauses; `Kind=DynamicSkip` when cd target is dynamic
      (interpretation #6)
- [ ] 5.4 Subshell `(...)` parsing with attribution stack push/pop
- [ ] 5.5 `bash -c "..."` / `sh -c "..."` recursion (cap at 5)
- [ ] 5.6 Recursion overflow → outer `ParsedCommand.IsUnparseable=true`
      (interpretation #4)
- [ ] 5.7 Update `SPEC.md` §9 (clarify which CwdVerbs propagate; add
      dynamic-cd attribution subsection); §10 (replace "mark the deepest
      clause" wording with "set ParsedCommand.IsUnparseable=true")
- [ ] 5.8 File GitHub issue: "Model pushd/popd directory stack semantics"
- [ ] 5.9 Open OpenSpec change `cd-attribution-clarifications` for the
      §9/§10 deltas

## 6. PR 6 — Corpus completeness + PII audit (interpretation #7)

- [ ] 6.1 Audit corpus categories from SPEC §13; fill to ≥105 entries
- [ ] 6.2 `tests/.../Corpus/AstAssert.cs` — structural equality with
      diffable failures
- [ ] 6.3 `tests/.../Corpus/PiiAuditTests.cs` — `[Fact]` regex scan over
      corpus JSON for SPEC §14 forbidden patterns
- [ ] 6.4 Verify pr_validation runs both via `dotnet test`
- [ ] 6.5 Confirm green on Linux + Windows (path separators)
- [ ] 6.6 Update `SPEC.md` §13 (replace abbreviated path with canonical
      `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json`)
- [ ] 6.7 Open OpenSpec change `corpus-location` for the §13 delta

## 7. Verify

- [ ] 7.1 After all PRs land: `dotnet build -c Release` and `dotnet test
      -c Release` clean on Linux + Windows
- [ ] 7.2 `PublicApiSnapshotTests` still green — no public surface drift
      beyond the locked `IsCwdAttribution` addition
- [ ] 7.3 PII audit passes
- [ ] 7.4 SPEC.md edits consistent with implementations across all PRs

## Status conventions

- `- [ ]` pending
- `- [x]` done
- Subtasks left as `[ ]` while the parent PR is in flight; flipped to
  `[x]` only when the PR merges.
