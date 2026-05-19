# Implementation Plan — ShellSyntaxTree

Source of truth for active work. Translates `SPEC.md` §16 (bash — shipped)
and `SPEC.POWERSHELL.md` §16 (PowerShell — v0.2.0) into NOW / NEXT / LATER
buckets. Park items aggressively — autonomous loops will otherwise bulldoze
priorities.

> **Hard rule:** every PR ends with this file updated (item moved /
> completed / parked) so the plan reflects reality.

---

## NOW (0.2.0 — PowerShell parser)

> **Spec:** `SPEC.POWERSHELL.md` (v0.2.0), merged 2026-05-19 via PR #38
> (squash commit `c7380b6`). Implementation was gated on that spec — the
> gate is now satisfied. Phases below translate `SPEC.POWERSHELL.md` §16
> (build order) plus §15 / §17 (release + Netclaw); each is a single PR
> unless noted. `SPEC.md` still wins on any conflict — fix the conflict
> before implementing (`AGENTS.md` Discovery Rules).

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
