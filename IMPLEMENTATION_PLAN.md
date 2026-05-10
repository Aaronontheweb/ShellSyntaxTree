# Implementation Plan — ShellSyntaxTree

Source of truth for active work. Translates `SPEC.md` §16 into NOW / NEXT /
LATER buckets. Park items aggressively — autonomous loops will otherwise
bulldoze priorities.

> **Hard rule:** every PR ends with this file updated (item moved /
> completed / parked) so the plan reflects reality.

---

## NOW (v0.1.0-alpha shipping path)

> **Active OpenSpec change:** `v0.1-locked-interpretations` — captures
> the eight planning-interview decisions (see
> `openspec/changes/v0.1-locked-interpretations/`). Archived on PR 1
> merge.

### 1. Bootstrap projects (PR 1, in progress)

- [x] Create `src/ShellSyntaxTree/ShellSyntaxTree.csproj` (library,
      multi-target `netstandard2.0;net8.0`, `IsAotCompatible=true`)
- [x] Create `tests/ShellSyntaxTree.Tests/ShellSyntaxTree.Tests.csproj`
      (xunit, target `net10.0`)
- [x] Add both to `ShellSyntaxTree.slnx`
- [x] `dotnet build -c Release` clean, `dotnet test -c Release` clean
      (18 PublicApiSnapshotTests passing)

### 2. Public API skeleton (lock surface first) — PR 1, in progress

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

### 3. BashLexer + opaque-region scanner — PR 2, in progress

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

### 8. cd-in-compound propagation (SPEC §9)

- [ ] First-clause `cd` sets attributed cwd for the compound
- [ ] Subsequent clauses receive synthetic `Arg` with
      `IsCwdAttribution=true`
- [ ] Subsequent `cd` in the same compound replaces attribution
- [ ] Subshell boundaries reset attribution

### 9. Subshell + bash -c surfacing (SPEC §10)

- [ ] Flatten subshell clauses into parent's `Clauses` with
      `IsSubshell=true`
- [ ] Surface `bash -c` inner clauses inline with `IsBashCWrapped=true`
- [ ] Recursion-depth cap

### 10. Hand-authored corpus (SPEC §13 — minimum 105 entries)

- [ ] 10 simple-verb cases
- [ ] 10 multi-token-verb cases
- [ ] 15 compound cases
- [ ] 10 cd-in-compound cases
- [ ] 10 quote-handling cases
- [ ] 10 redirect cases
- [ ] 10 subshell cases
- [ ] 10 `bash -c` cases
- [ ] 10 dynamic-skip cases
- [ ] 10 per-verb path-rule cases
- [ ] 10 unparseable cases

### 11. Corpus runner test

- [ ] Single `[Theory] [MemberData]` enumerating
      `tests/.../Corpus/bash/*.json`
- [ ] Per-entry test name so failures point at the specific case
- [ ] Structural equality helper `AstAssert.Equal`

### 12. PII audit (SPEC §14)

- [ ] Single `[Fact]` that scans `tests/.../Corpus/bash/*.json` for
      SPEC §14 forbidden patterns
- [ ] Wired into `pr_validation.yml` via `dotnet test` (no separate job)

### 13. Release v0.1.0-alpha

- [ ] `RELEASE_NOTES.md` updated with v0.1.0-alpha section
- [ ] Tag `v0.1.0-alpha`; verify `publish_nuget.yml` produces and pushes
      the package

### 14. Netclaw integration smoke (SPEC §17 #7-#8)

- [ ] Add `<PackageReference Include="ShellSyntaxTree">` to a Netclaw
      project; verify `IShellParser` resolves in DI
- [ ] One Netclaw integration test exercises a real corpus entry through
      the live matcher and gets the expected gate decision

### 15. NuGet package icon

- [ ] Generate a fitting icon via `/generate-image` (HCTI)
- [ ] Place at `assets/icon.png`; wire into `Directory.Build.props`
      (`PackageIcon` + a packed `<None>` item)
- [ ] Re-pack to validate icon embeds

---

## NEXT (v0.1.x — additive, post-alpha)

- Seed 50–100 corpus entries from sanitized real-world dogfood logs
  (SPEC §14 workflow)
- Expand verb tables as corpus surfaces real commands
- Document the "consumer's algorithm" — given a `ParsedCommand`, here is
  how a security gate walks it (likely a section in `SPEC.md` Appendix
  or a separate `docs/CONSUMER_GUIDE.md`)
- Performance sanity check (~1 ms typical) with a tiny BenchmarkDotNet
  harness — only if anything in the daemon hot path complains

## LATER (v0.2+ — out of v0.1 scope)

- PowerShell parser (`PwshParser : IShellParser`) — first time we exercise
  the multi-shell seam
- Windows `cmd` parser
- Source-mapping (line/column on AST nodes) — only if an IDE consumer
  asks
- Heredoc body extraction, process substitution, function definitions —
  only if a real consumer need surfaces

## Parked

*(empty; move items here when scope changes rather than deleting them)*
