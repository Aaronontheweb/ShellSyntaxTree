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

- [x] 4.1 `Internal/Resolving/BashResolver.cs` per SPEC §8: full pipeline
      (filesystem:: strip → tilde expansion → $HOME substitution → other
      env-var DynamicSkip → glob detection → path resolution against
      WorkingDirectory). Cross-platform-safe (Linux + Windows).
- [x] 4.2 Tilde + `$HOME` (the only env var we expand); other env vars
      in path slot → `DynamicSkip, IsPath=false` per interpretation #3
- [x] 4.3 `filesystem::/path` strip; glob detection per interpretation #3
      (`Kind=Glob, IsPath=true (in path slot), Resolved=null`)
- [x] 4.4 Relative-path joining against `WorkingDirectory` (lazy fallback
      to `Environment.CurrentDirectory`); `LooksLikePath` heuristic with
      curated extension list
- [x] 4.5 `Internal/Bash/Verbs/BashPerVerbRules.cs` per SPEC §7 + flag-value
      classification table:
       - Per-positional rules for `chmod`, `chown`, `chgrp`, `ln`, `find`,
         `grep`, `rg`, `sed`, `awk`, `tar`, `curl`, `wget`, `scp`/`rsync`,
         `cd`/`chdir`/`pushd`/`popd`/`push-location`/`set-location`
       - Default rule (all non-flag positionals → paths) for other FileVerbs
       - `LooksLikePath` fallback for non-FileVerbs
       - Flag-with-value path classification: `git -C` is path; `curl -d`
         is not; `docker -v` value is single literal IsPath=false
         (interpretation #8)
- [x] 4.6 `BashCommandParser` updates:
       - Flag-with-value-aware verb-chain probe so `git -C /repo log`
         produces `Verb.Tokens=["git", "log"]` per SPEC §12
       - Resolver wired into Arg + Redirect building
       - `Redirect.Target = Resolved` when resolvable; `IsDynamicSkip=true`
         when not
- [x] 4.7 SPEC.md §7 already updated in PR 3 with FlagsWithValue compat
       note + PR 4 follow-up. PR 4 SPEC.md updates: §8 step 4/6 overlap
       resolved (Glob ≠ DynamicSkip distinction). To be applied during
       commit.
- [ ] 4.8 File 2 GitHub issues: tar action-flag awareness; docker -v
      colon-split + Windows drive-letter handling. (Tracked locally;
      filing in PR 5/6 when the issues are easier to reference real
      corpus repros.)
- [x] 4.9 BashResolverTests (34) + BashPerVerbRulesTests (34) +
       BashCommandParserTests refresh (9 new) + 12 corpus entries
       refreshed + 20 new corpus entries (10 dynamic-skip + 10 per-verb)
- [x] 4.10 **296/296 tests passing**; clean build; PublicApiSnapshotTests
       still green (no API surface change)

### PR 4 follow-ups (tracked for PR 5)

- `Segment.FromSubshell` flag plumbed in PR 4 but unused — PR 5 hooks it
  into IsSubshell + attribution-stack push/pop.
- cd-attribution propagation: cleanest approach is constructing new
  `BashParserOptions{ WorkingDirectory = /target }` for clauses
  following `cd /target`. PR 5 wires this.
- Locked interpretation #6 (cd $VAR propagation): when cd target is
  DynamicSkip, subsequent clauses' relative-path args need to be
  flagged as such. Mechanism (without adding to public BashParserOptions
  surface): internal context state piggy-backed on the parsing pipeline.

## 5. PR 5 — cd attribution + subshells + bash -c (interpretations #4, #5, #6)

- [x] 5.1 `Internal/Bash/Parsing/CdAttributionContext.cs` — parser-internal
      mutable; SubshellStack of monotonic IDs (handles sibling subshells
      cleanly); SetLiteralAttribution / SetDynamicAttribution; HasAttribution
- [x] 5.2 Only `cd`/`chdir` propagate (interpretation #5); pushd/popd
      parse as CwdVerbs but don't propagate. Test
      `Pushd_does_not_propagate_attribution` pins this.
- [x] 5.3 Synthetic `Arg{ IsCwdAttribution=true, … }` appended to
      subsequent clauses. `Kind=Literal, IsPath=true, Resolved=<cwd>`
      when cd target resolved; `Kind=DynamicSkip, IsPath=false,
      Resolved=null, Raw="<dynamic-cwd>"` when cd target was DynamicSkip
      per interpretation #6.
- [x] 5.4 Subshell `(...)` parsing with attribution stack push/pop.
      `cd /a && (cd /b && cmd1) && cmd2` — cmd1 sees /b attribution;
      cmd2 sees /a (subshell's /b doesn't leak out). `IsSubshell=true`
      set on every clause inside a subshell.
- [x] 5.5 `bash -c "..."` / `sh -c "..."` recursion replaces PR 3's
      single-clause framework. Outer `bash -c` consumed; inner clauses
      surfaced inline with `IsBashCWrapped=true`. Outer cd attribution
      does NOT leak into inner shell (v0.1 decision).
- [x] 5.6 Recursion depth cap at 5 → outer
      `ParsedCommand.IsUnparseable=true` with reason `"bash -c recursion
      depth exceeded (>5)"` per interpretation #4.
- [x] 5.7 SPEC §9 updated (rule 3 explicit on which verbs propagate;
      dynamic-cd attribution subsection added per interp #6); §10
      updated (subshell flattening rephrased; bash -c recursion-limit
      replaced with "set ParsedCommand.IsUnparseable" per interp #4).
- [x] 5.8 BashResolver internal overload `Resolve(raw, treatAsPath,
      options, workingDirectoryUnknown)` lets the propagator force
      DynamicSkip on relative-path args under dynamic-cd (interp #6)
      without polluting the public `BashParserOptions` surface.
- [x] 5.9 30 new corpus entries (71-100): 10 cd-in-compound + 10
      subshell + 10 bash -c. 5 PR 3-4 corpus entries refreshed
      (25, 28, 34, 35, 52).
- [x] 5.10 8 new parser tests covering attribution propagation, subshell
       isolation, sequential cd, pushd non-propagation, dynamic cd,
       sibling subshells, bash -c depth-2 success, depth-6 overflow.
- [x] 5.11 **337/337 tests passing** (was 296 at PR 4 baseline). Public
       API surface unchanged.
- [ ] 5.12 File GitHub issue: "Model pushd/popd directory stack semantics"
       (interp #5 option-C upgrade)

### PR 5 follow-ups (tracked for PR 6)

- Possible SPEC clarification: pin the sentinel `Raw` value for
  dynamic-cd synthetic attribution args (current implementation uses
  `"<dynamic-cwd>"` but SPEC is silent).
- Outer cd attribution into bash -c inner clauses (v0.1: doesn't
  propagate; v0.1.x or v0.2 may revisit).
- `cd -` (jump-to-previous-dir): currently treated as "no attribution
  change" because `-` is detected as a flag (no non-flag positional).
  v0.1.x may want explicit handling.
- Existing PR 3 corpus entries 21-24, 26-27, 29-33 (the compound
  entries that had no `cd` prefix) likely don't need updates; but PR 6
  should audit all 100 corpus entries for AST drift now that PRs 1-5
  have all landed.

## 6. PR 6 — Corpus completeness + PII audit (interpretation #7)

- [x] 6.1 Audited corpus categories from SPEC §13; filled to **115
      entries** (target was ≥105). Added 10 quote-handling (101-110)
      and 5 more unparseable (111-115).
- [x] 6.2 `tests/.../Corpus/AstAssert.cs` — polished structural-equality
      helper with path-prefixed messages (e.g.
      `clauses[1].args[2].kind: expected DynamicSkip, actual Literal`).
      CorpusRunnerTests refactored to delegate.
- [x] 6.3 `tests/.../Corpus/PiiAuditTests.cs` — `[Fact]` walks every
      `bash/*.json`, applies SPEC §14 regex patterns; allowlists
      generic placeholders; reports all hits in one failure.
- [x] 6.4 PR validation runs corpus runner + PII audit via standard
      `dotnet test` (no separate job).
- [x] 6.5 Cross-platform: BashResolver (PR 4 fix) keeps forward-slash
      paths regardless of host OS; corpus uses pinned
      WorkingDirectory=/work, HomeDirectory=/home/test for stability.
- [x] 6.6 SPEC §13 updated to canonical
      `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json`; also fixed
      stale refs in §14, §15, §17 acceptance criteria.
- [x] 6.7 Light Path-C adoption: §13 delta lives in this bootstrap
      change; no separate `corpus-location` change needed.
- [x] 6.8 **353/353 tests passing**; clean build; PublicApiSnapshotTests
       still 18/18 green; public API surface unchanged.

### PR 6 follow-ups (tracked for v0.1.x or post-v0.1.0-alpha)

- Single-quote `'literal $HOME'` resolver behavior: v0.1 substitutes
  uniformly (corpus 104 pins). Bash semantics: doesn't substitute
  inside single quotes. v0.1.x may carry quote-style through tokens.
- LooksLikePath heuristic: trailing backslash in quoted literal
  triggers IsPath=true (corpus 109 pins). v0.1.x may tighten heuristic.
- Sentinel `Raw="<dynamic-cwd>"` for dynamic-cd attribution: pinned
  by corpus 52 but SPEC §9 dynamic-cd subsection doesn't formalize
  the value. v0.1.x can pin in spec text.
- `case x in a) ;; esac` form: hits "unbalanced parens" diagnostic
  before keyword check. Acceptable v0.1 behavior; v0.1.x may pin
  diagnostic precedence in SPEC §11.

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
