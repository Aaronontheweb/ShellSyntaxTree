# Implementation Plan — ShellSyntaxTree

Source of truth for active work. Translates `SPEC.md` §16 (bash — shipped)
and `SPEC.POWERSHELL.md` §16 (PowerShell — v0.2.0) into NOW / SHIPPED /
NEXT / LATER buckets. Park items aggressively — autonomous loops will
otherwise bulldoze priorities.

> **Hard rule:** every PR ends with this file updated (item moved /
> completed / parked) so the plan reflects reality.

---

## NOW (0.2.0 — PowerShell parser)

> **Spec:** `SPEC.POWERSHELL.md` (v0.2.0), merged 2026-05-19 via PR #38
> (squash commit `c7380b6`). Implementation was gated on that spec — the
> gate is now satisfied. Phases below translate `SPEC.POWERSHELL.md` §16
> (build order) plus §15 / §17 (release + Netclaw); each is a single PR
> unless noted. `SPEC.md` still wins on any conflict — fix the conflict
> before implementing (`AGENTS.md` Discovery Rules). Phase numbering is
> scoped to this section (the v0.2.0 §16 sequence), independent of the
> SHIPPED section's 1–18.

### 1. Public-API surface change (lock first) — SPEC.PWSH §16.1

- [ ] Add `ShellParserOptions` base record; reparent `BashParserOptions`
      onto it (stays source-compatible per SPEC.PWSH §2)
- [ ] Add `PwshParserOptions` (empty record, `: ShellParserOptions`) and a
      `PwshParser` skeleton — `Parse` throws `NotImplementedException`,
      `Parse(null)` throws `ArgumentNullException`
- [ ] Add additive `VerbChain.CanonicalVerb` and `VerbChain.IsDynamic`
      (SPEC.PWSH §3)
- [ ] Rename `Clause.IsBashCWrapped` → `IsCommandStringWrapped`; flip the
      `isBashCWrapped` key in every existing bash corpus JSON entry
- [ ] Update `PublicApiSnapshotTests` and the corpus DTOs; all existing
      bash tests stay green

### 2. PowerShell verb & binding tables (data only) — SPEC.PWSH §16.2

- [ ] `Internal/Pwsh/Verbs/PwshApprovedVerbs` — the `Get-Verb` set (§6.1)
- [ ] `PwshAliases` — the **complete** default alias set, not a subset
      (§6.3)
- [ ] `PwshVerbs` — Cwd / File / control-flow tables (§6.4)
- [ ] `PwshValueParameters` / `PwshSwitchParameters` — the §6.5 binding
      tables
- [ ] `PwshPerVerbRules` — per-verb / per-parameter path rules (§7)

### 3. PwshLexer — SPEC.PWSH §16.3

- [ ] `Internal/Pwsh/Lexing/` — quoting (single, double, here-string),
      backtick escape, `$var` / `$env:` / `${name}`, `-Name` parameters,
      stream redirects, statement separators, `#` / `<# #>` comments
- [ ] Opaque regions via the shared `OpaqueRegionScanner` + a new
      backtick-escape mode (shared file — see the shared-internals note)
- [ ] `--%` stop-parsing token — opaque to end of line (§4)
- [ ] Heavy lexer unit tests

### 4. PwshCommandParser core — SPEC.PWSH §16.4

- [ ] Pipeline / statement splitting (`|`, `;`, `&&`, `||`, newline)
- [ ] Verb-chain extraction — cmdlet (1-token), native (greedy walk,
      case-sensitive predicate per §6.2), alias
- [ ] §6.5 parameter-binding decision (switch vs. value-binding)
- [ ] Args, parameters, redirects per clause
- [ ] `VerbChain.IsDynamic` for `& $exe` / subexpression / script-block
      command names (§3)
- [ ] Grouped sub-pipeline `( ... )` → `IsSubshell` structural marker

### 5. Alias resolution — SPEC.PWSH §16.5

- [ ] Populate `VerbChain.CanonicalVerb` unconditionally; the raw token
      stays verbatim in `Tokens`
- [ ] `[Fact]` diffing `PwshAliases` against live `Get-Alias` output

### 6. PwshResolver — SPEC.PWSH §16.6 / §8

- [ ] `~` / `$HOME` / `$env:USERPROFILE` expansion; every other `$var` /
      `$env:` → `DynamicSkip` (path slot) / `EnvVar` (non-path slot)
- [ ] Provider-qualifier strip; drive-qualified + UNC paths; non-FileSystem
      PSDrive → `IsPath=false`
- [ ] Glob detection; relative-path resolution
- [ ] Comma-array path token → `DynamicSkip`
- [ ] Redirect stream → `RedirectDirection` lossy mapping

### 7. Per-verb / per-parameter path rules — SPEC.PWSH §16.7 / §7

- [ ] Path-typed parameter classification, operating on the §6.5 token
      roles
- [ ] Positional path rules + per-cmdlet overrides
- [ ] Native commands reuse the bash per-verb table (shared)

### 8. Set-Location-in-compound propagation — SPEC.PWSH §16.8 / §9

- [ ] `Set-Location` attributes cwd to subsequent clauses; literal vs.
      `DynamicSkip` (dynamic / non-FS-drive / `-` / no-arg-home cases)
- [ ] Attribution propagates through `( ... )` — no subshell isolation

### 9. pwsh -Command / -EncodedCommand recursion — SPEC.PWSH §16.9 / §10

- [ ] `-Command` recursion: quoted-string, script-block, and
      bare/multi-token forms; inner clauses surface with
      `IsCommandStringWrapped=true`
- [ ] `-EncodedCommand` base64 + UTF-16LE decode, BOM strip, recurse
- [ ] Depth-5 cap; inner-`IsUnparseable` propagates to the outer command
- [ ] `Invoke-Expression` / `iex` is **not** recursed

### 10. Anomaly safe-fail — SPEC.PWSH §16.10 / §11

- [ ] `IsUnparseable` for control-flow / definition / block keywords,
      assignment, type-literal, trailing `&`, recursion overflow
- [ ] 64 KiB input cap (top-level input + each decoded payload)
- [ ] Diagnostic-precedence ordering

### 11. Multi-shell refactor of corpus runner + PII audit — SPEC.PWSH §16.11

- [ ] Corpus runner enumerates every `Corpus/<shell>/` directory;
      directory-routed parser selection (`bash/` → `BashParser`,
      `powershell/` → `PwshParser`)
- [ ] PII audit scans every `Corpus/<shell>/`
- [ ] **Must precede phase 12** — the runner cannot execute a
      `Corpus/powershell/` entry until this lands

### 12. Hand-author the PowerShell corpus (≥170 entries) — SPEC.PWSH §16.12

- [ ] `tests/.../Corpus/powershell/*.json`, §13 schema (`canonicalVerb`,
      `oracleExpectation`)
- [ ] Coverage per the §13 category table — parameter binding ≥25,
      unparseable ≥20, recursion ≥15
- [ ] Every entry parses to its expected AST

### 13. pwsh validation gate + tools/PwshCorpusTool — SPEC.PWSH §16.13

- [ ] CI test feeds every PowerShell corpus `input` to real `pwsh`
      (`[System.Management.Automation.Language.Parser]::ParseInput`);
      enforces the §13 oracle matrix
- [ ] `tools/PwshCorpusTool` authoring aid; register it in `TOOLING.md`
- [ ] CI installs `pwsh` (7.x); an explicit step fails loudly if absent

### 14. SPEC.md edits + wire CI — SPEC.PWSH §16.14

- [ ] Update `SPEC.md` §1 / §2 / §3 / §6.4 / §15 to the shipped v0.2.0
      surface
- [ ] `Directory.Build.props` `VersionPrefix` → `0.2.0`,
      `VersionSuffix` → `alpha`
- [ ] CI test job runs the bash corpus + PowerShell corpus + `pwsh` gate +
      multi-shell PII audit on Linux **and** Windows

### 15. Release 0.2.0 (alpha → beta → stable) — SPEC.PWSH §15 / §17

- [ ] `RELEASE_NOTES.md` v0.2.0 section — the breaking `Clause` rename
      old→new mapping (SPEC.md Appendix A requires it) + the new
      `PwshParser` / `PwshParserOptions` / `ShellParserOptions` /
      `VerbChain.CanonicalVerb` / `VerbChain.IsDynamic` surface
- [ ] Tag `0.2.0-alpha`; `publish_nuget.yml` produces the `.nupkg`
- [ ] `0.2.0-beta` so Netclaw validates the parser + the breaking rename
- [ ] Promote to stable `0.2.0` after Netclaw validation

### 16. Netclaw v0.2.0 integration — SPEC.PWSH §17 #9

- [ ] Netclaw consumes the v0.2.0 package; absorbs the `Clause` rename
- [ ] ≥1 Netclaw integration test exercises a real PowerShell corpus entry
      through the live matcher and gets the expected gate decision

**Shared-internals note (SPEC.PWSH §16):** phases 3 and 7 touch shared
surface — `OpaqueRegionScanner` gains a backtick-escape mode, and the
native greedy walk + per-verb rules are reused from the bash
implementation. Promote the reused pieces into `Internal/Shared/` and cover
every shared path in the PowerShell corpus, so a bash-side PR cannot
silently regress PowerShell without a red test.

---

## SHIPPED (0.1.0-alpha → 0.1.5 — complete)

> **OpenSpec change `v0.1-locked-interpretations`:** archived
> 2026-05-11 (post-alpha housekeeping PR) under
> `openspec/changes/archive/2026-05-11-v0.1-locked-interpretations/`.
> Captured the eight planning-interview decisions; superseded by
> shipped 0.1.0-alpha behavior.

### 1. Bootstrap projects (PR 1, complete)

- [x] Create `src/ShellSyntaxTree/ShellSyntaxTree.csproj` (library,
      multi-target `netstandard2.0;net8.0`, `IsAotCompatible=true`)
- [x] Create `tests/ShellSyntaxTree.Tests/ShellSyntaxTree.Tests.csproj`
      (xunit, target `net10.0`)
- [x] Add both to `ShellSyntaxTree.slnx`
- [x] `dotnet build -c Release` clean, `dotnet test -c Release` clean
      (18 PublicApiSnapshotTests passing)

### 2. Public API skeleton (lock surface first) — PR 1, complete

- [x] Implement public types from `SPEC.md` §2/§3 verbatim:
      `IShellParser`, `BashParser`, `BashParserOptions`, `ParsedCommand`,
      `Clause`, `VerbChain`, `Arg`, `Redirect`, and the three enums
- [x] `BashParser.Parse` throws `NotImplementedException`; `Parse(null)`
      throws `ArgumentNullException`
- [x] `Arg` includes `IsCwdAttribution` per SPEC §9 (locked interpretation #1)
- [x] PublicApiSnapshotTests reflection-based `[Fact]`s assert the surface
      shape with strict namespace closure
- [x] Bootstrap OpenSpec scaffolding (config, root spec stub, change
      proposal/design/tasks/specs delta for `v0.1-locked-interpretations`)
- [x] Copy OpenSpec authoring skills into `.claude/skills/`
- [x] Update SPEC.md §3 to enumerate `Arg.IsCwdAttribution`; cross-tfm
      note on `VerbChain.Joined`

### 3. BashLexer + opaque-region scanner — PR 2, complete

- [x] `Internal/Lexing/OpaqueRegionScanner.cs` — shared, grammar-agnostic;
      `Scan` for `(`/`)` style + `ScanSymmetric` for backtick; quote-aware,
      escape-aware, nesting-aware
- [x] `Internal/Bash/Lexing/{BashLexer,BashToken,BashTokenKind}.cs`
- [x] Token kinds: Word, QuotedString, Operator, Whitespace, Continuation,
      OpaqueSubstitution, UnparseableSentinel
- [x] Quote handling (single literal, double with `\"`, `\\`, `\$`,
      `\` + newline)
- [x] Escape handling outside quotes
- [x] Operator boundaries (no whitespace required); `<<-` heredoc variant
- [x] `$(…)` and backticks → `OpaqueSubstitution` (locked interpretation #2)
- [x] `$((expr))` and `${var//pat/repl}` → `UnparseableSentinel`
- [x] Heredoc body skip per SPEC §4
- [x] 78 lexer + scanner unit tests (combined with PR 1's 18 → 96/96
      passing)
- [x] SPEC §1 / §5 / §11 updated for token kinds + non-goal additions
- [x] OpenSpec change `v0.1-locked-interpretations` tasks.md updated
      (Phase 2 marked [x])

### 4. Verb tables (SPEC §6, data only) — PR 3, complete

- [x] `BashArity` (multi-token verbs) per SPEC §6.1
- [x] `CwdVerbs` (`cd`, `chdir`, `popd`, `pushd`, `push-location`,
      `set-location`)
- [x] `FileVerbs` (full SPEC §6.3 list)
- [x] `FlagsWithValue` (`git`, `curl`, `wget`, `docker`, `tar`)
- [x] `ControlFlowKeywords` (for IsUnparseable detection)
- [x] Probe order: longest-match-first (3-token, then 2-token, then
      1-token); SPEC note added that flag-with-value-aware probing
      arrives in PR 4

### 5. BashParser core (SPEC §4) — PR 3, complete

- [x] Compound splitting on `&&`, `||`, `;`, `|`
- [x] Verb chain extraction using `BashArity` (PR 4 will refine for
      flag-with-value pairs)
- [x] Args + redirects per clause (literal-only mode; path classification
      arrives in PR 4)
- [x] Subshell `( ... )` framework (inner clauses parse inline;
      `IsSubshell` flag stays false until PR 5)
- [x] `bash -c "..."` framework (single-clause mode in PR 3; PR 5 wires
      recursion + `IsBashCWrapped`)
- [x] Anomaly safe-fail (SPEC §11): never throw on well-formed input;
      strict empty-Clauses on anomaly (PR 6 may relax to partial recovery)
- [x] CorpusRunnerTests skeleton + 50 corpus entries
- [x] `BashParser.Parse` delegates to `BashCommandParser.Parse`

### 6. Resolver (SPEC §8) — PR 4, complete

- [x] Tilde + `$HOME` expansion against `BashParserOptions.HomeDirectory`
- [x] All other `$VAR` / `${VAR}` → `DynamicSkip` (in path slots) /
      `EnvVar` (in non-path slots)
- [x] `filesystem::/path` prefix stripping
- [x] Glob detection (don't expand); locked interp #3 distinguishes
      Glob (IsPath=true in path slot) vs DynamicSkip (IsPath=false)
- [x] Relative path joining against `BashParserOptions.WorkingDirectory`
- [x] `LooksLikePath` heuristic per §8 with curated extension list
- [x] SPEC §8 step 4/6 overlap resolved

### 7. Per-verb path-arg rules (SPEC §7) — PR 4, complete

- [x] Default: every non-flag positional after the verb chain is a path
- [x] Per-verb overrides: `chmod`, `chown`, `chgrp`, `ln`, `find`,
      `grep`, `rg`, `sed`, `awk`, `tar` (default fallback per #8),
      `curl`/`wget`, `scp`/`rsync`, `cd`-family
- [x] Flag-with-value handling (`-o file`, `git -C /repo`,
      `--output=file`); `git -C /repo log` → Verb=["git", "log"]
- [x] Flag-value path classification table (`git -C` is path; `curl -d`
      is body data; `docker -v` is single literal IsPath=false per #8)

### 8. cd-in-compound propagation (SPEC §9) — PR 5, complete

- [x] First-clause `cd`/`chdir` sets attributed cwd; only those two verbs
      propagate (interp #5)
- [x] Subsequent clauses receive synthetic `Arg` with
      `IsCwdAttribution=true`; `Kind=Literal, IsPath=true` when cd
      target resolved, `Kind=DynamicSkip, IsPath=false` when dynamic
      (interp #6)
- [x] Subsequent `cd` in the same compound replaces attribution
- [x] Subshell boundaries isolate attribution via push/pop stack

### 9. Subshell + bash -c surfacing (SPEC §10) — PR 5, complete

- [x] Flatten subshell clauses into parent's `Clauses` with
      `IsSubshell=true`; sibling subshells handled via SubshellStack IDs
- [x] Surface `bash -c "..."` / `sh -c "..."` inner clauses inline with
      `IsBashCWrapped=true`; outer cd attribution doesn't leak into
      inner shell (v0.1 decision)
- [x] Recursion depth cap at 5 → outer
      `ParsedCommand.IsUnparseable=true` (interp #4)

### 10. Hand-authored corpus (SPEC §13 — minimum 105 entries) — PR 6, complete

- [x] 10 simple-verb cases (01-10) — PR 3
- [x] 10 multi-token-verb cases (11-20) — PR 3
- [x] 15 compound cases (21-35) — PR 3
- [x] 10 cd-in-compound cases (71-80) — PR 5
- [x] 10 quote-handling cases (101-110) — PR 6
- [x] 10 redirect cases (36-45) — PR 3
- [x] 10 subshell cases (81-90) — PR 5
- [x] 10 `bash -c` cases (91-100) — PR 5
- [x] 10 dynamic-skip cases (51-60) — PR 4
- [x] 10 per-verb path-rule cases (61-70) — PR 4
- [x] 10 unparseable cases (46-50, 111-115) — PRs 3 + 6
- [x] **115 total corpus entries** (target was ≥105)

### 11. Corpus runner test — PR 6, complete

- [x] Single `[Theory] [MemberData]` enumerating
      `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` (skeleton in PR 3)
- [x] Per-entry test name (file name) so failures point at the specific
      case
- [x] Polished structural-equality helper `AstAssert.Equal` with
      diff-friendly messages (e.g.
      `clauses[1].args[2].kind: expected DynamicSkip, actual Literal`)

### 12. PII audit (SPEC §14) — PR 6, complete

- [x] Single `[Fact]` that scans
      `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` for SPEC §14
      forbidden patterns; allowlists generic placeholders; reports all
      hits in one failure
- [x] Wired into `pr_validation.yml` via standard `dotnet test`
      (no separate job)

### 13. Release 0.1.0-alpha — shipped 2026-05-11

- [x] `RELEASE_NOTES.md` updated with 0.1.0-alpha section
- [x] `dotnet pack -c Release -o ./bin/nuget` produces clean
      `ShellSyntaxTree.0.1.0-alpha.nupkg` + `.snupkg` with embedded
      README, icon, SourceLink metadata
- [x] User go-ahead received; tag pushed: `0.1.0-alpha` → commit
      `41e9433` (PR #19) on 2026-05-11
- [x] `publish_nuget.yml` workflow run completed `success` at
      2026-05-11T13:58:20Z; GitHub Release "ShellSyntaxTree 0.1.0-alpha"
      cut at 14:08:47Z (Pre-release); package live on nuget.org
- **Post-alpha CI follow-ups** (improve future releases, not retroactive):
  - PR #19: `chore(ci): assert tags are bare version numbers (no v prefix)`
  - PR #20: `chore(ci): strip section heading from extracted release notes`

### 14. Netclaw integration smoke (SPEC §17 #7-#8) — complete

- [x] In netclaw repo: `dotnet add package ShellSyntaxTree --version 0.1.0-alpha`
- [x] Wire `IShellParser` into Netclaw DI; replace minimal call site in
      `src/Netclaw.Security/ShellApprovalSemantics.cs`
- [x] One Netclaw integration test exercises a real corpus entry through
      the live matcher and gets the expected gate decision
- [x] SPEC §17 #7–#8 satisfied (confirmed by Aaron 2026-05-15: all shipped
      alpha versions are working in Netclaw production)

### 15. NuGet package icon — PR 7, complete

- [x] Icon already generated as `assets/icon.png` (ShellSyntaxTree-themed
      AST tree on dark green background with `>_` shell prompt motif —
      512x512 PNG, ~98 KB; created during template bootstrap)
- [x] `Directory.Build.props` wires `<PackageIcon>icon.png</PackageIcon>`
      + a packed `<None>` item (already present from bootstrap)
- [x] `dotnet pack` validation: icon embedded in `.nupkg` confirmed
- [x] Re-pack to validate icon embeds

### 16. Bash line comments (#25) — 0.1.3-alpha

- [x] `BashTokenKind.Comment` enum member (internal)
- [x] `BashLexer.ConsumeLineComment` helper; `#` dispatch in main scan loop
- [x] `BashCommandParser.FilterSignificant` drops Comment tokens
- [x] SPEC.md §4 BNF note + §5 "Comment handling" subsection
- [x] 10 new lexer unit tests + 8 new parser unit tests
- [x] 9 new corpus entries (123–131) including both Netclaw repros
      (sanitized paths per SPEC §14)
- [x] `Directory.Build.props` `VersionPrefix` 0.1.2 → 0.1.3
- [x] `RELEASE_NOTES.md` 0.1.3-alpha section
- [x] Cut 0.1.3-alpha tag once branch is merged

### 17. Greedy verb-chain extraction (#27) — 0.1.4-alpha

- [x] Remove `BashArity` static table and `ProbeArity()` method from
      `BashVerbs.cs`
- [x] Add `BashVerbs.IsVerbLikeToken` predicate (strict allow-list:
      Word kind, length 1–64, leading `[a-z]`, body `[a-z0-9._-]`)
- [x] Rewrite verb-extraction loop in
      `BashCommandParser.ParseClauseSegment` (greedy walk + FileVerb
      1-token carveout + flag-with-value consumption)
- [x] 7 new corpus entries (132–138) for the issue #27 headline cases:
      `freshdesk ticket list`, `git -C /repo worktree list --porcelain`,
      `kubectl get pods`, `kubectl get pods my-pod`, `aws s3 cp src dst`,
      `dotnet ef migrations add InitialCreate`, `cat README`
      (FileVerb-carveout proof)
- [x] 11 existing corpus entries flipped to new shape: `04_echo_hello`,
      `11_git_push_origin_main`, `13_git_checkout_dev`,
      `17_docker_run_nginx`, `27_make_install`, `45_echo_append_log`,
      `84_subshell_nested`, `91_bash_c_simple`,
      `96_bash_c_nested_depth_2`, `100_bash_c_nested_depth_3`,
      `130_netclaw_repro_leading_comment_pipeline`
- [x] 8 unit-test cases updated in `BashCommandParserTests.cs` to match
      the new expected verb chains
- [x] SPEC.md updates: §3 `VerbChain`, §4 grammar, §6.1 rewritten end-to-end,
      new §6.1.1 consumer pattern-matching guidance, §7 flag-with-value
      note, §12 worked examples, §15 versioning, §16 sequencing
- [x] `Directory.Build.props` `VersionPrefix` 0.1.3 → 0.1.4
- [x] `RELEASE_NOTES.md` 0.1.4-alpha section
- [x] Cut 0.1.4-alpha tag once branch is merged

### 18. Newline-as-statement-separator — 0.1.5-beta

- [x] `BashToken.IsStatementSeparator` init-property (internal)
- [x] `BashLexer` flags the newline-branch Whitespace token and the
      heredoc-terminator Whitespace token
- [x] `BashCommandParser`: `FilterSignificant` retains flagged
      Whitespace; `SplitIntoSegments` splits on it as a `Sequence`
      boundary (an empty pending segment collapses — no empty clause);
      `TryDetectAnomaly` treats it as a verb-slot boundary
- [x] SPEC.md §4 grammar + notes, §5 `WHITESPACE` bullet, §15
      versioning, §16 sequencing note
- [x] 8 new `BashLexerTests` + 14 new `BashCommandParserTests`; stale
      comment in `Comment_between_two_statements_preserves_both_clauses`
      corrected
- [x] 11 new corpus entries (139–149); entry 126 note corrected
- [x] `Directory.Build.props` `VersionPrefix` 0.1.4 → 0.1.5,
      `VersionSuffix` → `beta`
- [x] `RELEASE_NOTES.md` 0.1.5-beta section
- [x] Cut 0.1.5-beta tag once branch is merged; promote to stable
      0.1.5 after Netclaw validates the behavior change

---

## NEXT (0.1.x — additive, post-alpha)

- Seed 50–100 corpus entries from sanitized real-world dogfood logs
  (SPEC §14 workflow)
- Expand verb tables as corpus surfaces real commands
- Document the "consumer's algorithm" — given a `ParsedCommand`, here is
  how a security gate walks it (likely a section in `SPEC.md` Appendix
  or a separate `docs/CONSUMER_GUIDE.md`)
- Performance sanity check (~1 ms typical) with a tiny BenchmarkDotNet
  harness — only if anything in the daemon hot path complains

## LATER (post-0.2.0)

- v0.2.x candidates from `SPEC.POWERSHELL.md` §18 — lossless redirect-stream
  identity (grow the `RedirectDirection` enum), per-element comma-array
  path extraction (`-Path a,b,c`)
- PowerShell script-level constructs — control flow, `function` / `class` /
  `enum` definitions, `param()` / `begin` / `process` / `end` blocks,
  `.ps1` file parsing (`SPEC.POWERSHELL.md` §18)
- Extract a shared lexer/parser core once two parsers exist — deferred
  until the seam is designed from real duplication (`SPEC.POWERSHELL.md`
  §18)
- Windows `cmd` parser
- Source-mapping (line/column on AST nodes) — only if an IDE consumer asks
- Heredoc body extraction, process substitution, bash function definitions
  — only if a real consumer need surfaces

## Parked

*(empty; move items here when scope changes rather than deleting them)*
