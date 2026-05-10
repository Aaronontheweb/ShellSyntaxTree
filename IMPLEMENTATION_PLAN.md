# Implementation Plan — ShellSyntaxTree

Source of truth for active work. Translates `SPEC.md` §16 into NOW / NEXT /
LATER buckets. Park items aggressively — autonomous loops will otherwise
bulldoze priorities.

> **Hard rule:** every PR ends with this file updated (item moved /
> completed / parked) so the plan reflects reality.

---

## NOW (v0.1.0-alpha shipping path)

### 1. Bootstrap projects

- [ ] Create `src/ShellSyntaxTree/ShellSyntaxTree.csproj` (library,
      multi-target `netstandard2.0;net8.0`)
- [ ] Create `tests/ShellSyntaxTree.Tests/ShellSyntaxTree.Tests.csproj`
      (xunit, target `net10.0`)
- [ ] Add both to `ShellSyntaxTree.slnx`
- [ ] `dotnet build` clean, `dotnet test` clean (zero tests OK)

### 2. Public API skeleton (lock surface first)

- [ ] Implement public types from `SPEC.md` §2 verbatim:
      `IShellParser`, `BashParser`, `BashParserOptions`, `ParsedCommand`,
      `Clause`, `VerbChain`, `Arg`, `Redirect`, and the three enums
- [ ] `BashParser.Parse` throws `NotImplementedException` for now
- [ ] `Arg` includes `IsCwdAttribution` per SPEC §9
- [ ] Public API snapshot test (a single test that asserts each public
      member exists with the expected shape — fast feedback when the
      surface drifts)

### 3. BashLexer (SPEC §5)

- [ ] Token kinds: WORD, QUOTED_STRING, OPERATOR, WHITESPACE,
      CONTINUATION
- [ ] Quote handling (single literal, double with `\"`, `\\`, `\$`)
- [ ] Escape handling outside quotes
- [ ] Operator boundaries (no whitespace required)
- [ ] Heavy unit tests on tokenization

### 4. Verb tables (SPEC §6, data only)

- [ ] `BashArity` (multi-token verbs)
- [ ] `CwdVerbs` (`cd`, `chdir`, `popd`, `pushd`, ...)
- [ ] `FileVerbs` (cat, grep, find, ls, ...)
- [ ] `FlagsWithValue` (per-verb flags that consume the next token)
- [ ] Probe order: longest-match-first when joining 1, 2, 3 tokens

### 5. BashParser core (SPEC §4)

- [ ] Compound splitting on `&&`, `||`, `;`, `|`
- [ ] Verb chain extraction using `BashArity`
- [ ] Args + redirects per clause
- [ ] Subshell `( ... )` handling
- [ ] `bash -c "..."` recursion (capped at depth 5)
- [ ] Anomaly safe-fail (SPEC §11): never throw on well-formed input

### 6. Resolver (SPEC §8)

- [ ] Tilde + `$HOME` expansion against `BashParserOptions.HomeDirectory`
- [ ] All other `$VAR` / `${VAR}` → `DynamicSkip`
- [ ] `filesystem::/path` prefix stripping
- [ ] Glob detection (don't expand)
- [ ] Relative path joining against `BashParserOptions.WorkingDirectory`
- [ ] `LooksLikePath` heuristic per §8

### 7. Per-verb path-arg rules (SPEC §7)

- [ ] Default: every non-flag positional after the verb chain is a path
- [ ] Per-verb overrides: `chmod`, `chown`, `chgrp`, `ln`, `find`,
      `grep`, `rg`, `sed`, `awk`, `tar`, `curl`/`wget`, `scp`/`rsync`,
      `cd`-family
- [ ] Flag-with-value handling (`-o file`, `git -C /repo`, `--output=file`)

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
