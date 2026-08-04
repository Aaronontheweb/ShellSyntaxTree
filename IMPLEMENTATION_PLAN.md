# Implementation Plan — ShellSyntaxTree

Source of truth for active work. Translates `SPEC.md` §16 (bash — shipped)
and `SPEC.POWERSHELL.md` §16 (PowerShell — v0.2.0) into NOW / NEXT / LATER
buckets. Park items aggressively — autonomous loops will otherwise bulldoze
priorities.

> **Hard rule:** every PR ends with this file updated (item moved /
> completed / parked) so the plan reflects reality.

---

## NOW (0.2.0 — PowerShell parser)

> **Spec:** `SPEC.POWERSHELL.md` (v0.2.0). The PowerShell parser is
> implemented — phases 1–14 of `SPEC.POWERSHELL.md` §16 are complete (see
> below). What remains is the release flow and the downstream Netclaw
> integration, both of which need actions outside this repository.

### Implemented (SPEC.POWERSHELL.md §16 phases 1–14) — done

- [x] **1. Public-API surface** — `ShellParserOptions` base; `BashParserOptions`
      reparented; `PwshParserOptions` + `PwshParser`; additive
      `VerbChain.CanonicalVerb` / `IsDynamic`; `Clause.IsBashCWrapped` →
      `IsCommandStringWrapped`. `PublicApiSnapshotTests`, corpus DTOs, and the
      bash corpus JSON key all updated.
- [x] **2. Verb & binding tables** — `PwshApprovedVerbs`, `PwshAliases`
      (complete default set), `PwshVerbs`, `PwshBindingTables`,
      `PwshPerVerbRules`.
- [x] **3. `PwshLexer`** — quoting, backtick escape, `$var` / `$env:` /
      `${name}`, parameters, stream redirects, statement separators,
      comments, opaque regions, `--%`; `OpaqueRegionScanner` backtick mode.
- [x] **4–10. `PwshCommandParser`** — pipeline / statement splitting,
      verb-chain extraction, the §6.5 binding model, `PwshResolver` (§8),
      per-verb path rules (§7), `Set-Location` propagation (§9), `pwsh
      -Command` / `-EncodedCommand` recursion (§10), anomaly safe-fail and
      the 64 KiB cap (§11).
- [x] **11. Multi-shell corpus runner + PII audit** — directory-routed by
      `Corpus/<shell>/`.
- [x] **12. PowerShell corpus** — 211 entries under `Corpus/powershell/`,
      every §13 category minimum exceeded.
- [x] **13. `pwsh` validation gate + `tools/PwshCorpusTool`** —
      `PwshOracleTests` enforces the §13 oracle matrix + the `PwshAliases`
      completeness `[Fact]`; the tool is registered in `TOOLING.md`.
- [x] **14. `SPEC.md` edits + CI + version bump** — `SPEC.md` §1 / §2 / §3 /
      §6.4 / §15 updated; `VersionPrefix` → `0.2.0`, `VersionSuffix` →
      `alpha`; CI verifies `pwsh` and runs both corpora on Linux + Windows;
      `RELEASE_NOTES.md` v0.2.0 section; CLI + Web samples gain a shell
      selector; `README.md` updated.

### Completed maintenance

- [x] **Issue #63 — static Invoke-Expression command-string recursion.**
      Recurse into provably static `Invoke-Expression` / `iex` payloads,
      safe-fail computed and pipeline-fed code, share the existing recursion
      limits, and preserve current-scope PowerShell location attribution.
- [x] **Issue #64 — path-shaped operands after native verb chains.**
      Stop the Bash and PowerShell native greedy passes before a token that
      matches the shared path-shape rules. Preserve that token as a resolved
      argument without a command dictionary or a public API change.
- [x] **Issue #52 — hyphenated PowerShell parameters/native options.**
      Preserve internal hyphens, apply bash-compatible native
      `--flag=value` splitting and path classification, keep colon binding
      cmdlet-only, and pin the behavior in unit tests plus the PowerShell
      corpus. Review follow-ups shipped with it: the equals-form split moved
      to a shared `NativeFlagSyntax` so the two parsers can't drift, a colon
      value under an `=`-bearing parameter name safe-fails to `DynamicSkip`,
      and `-?` lexes as one parameter token.

### 15. Release 0.2.0 (alpha → beta → stable) — SPEC.PWSH §15 / §17

- [x] Tag `0.2.0-alpha`; `publish_nuget.yml` produced
      `ShellSyntaxTree.0.2.0-alpha.nupkg` and it is live on nuget.org
      (released 2026-05-20).
- [x] `0.2.0-beta.1` so Netclaw validates the parser + the breaking rename
- [ ] Promote to stable `0.2.0` after Netclaw validation

### 16. Netclaw v0.2.0 integration — SPEC.PWSH §17 #9

- [ ] Netclaw consumes the v0.2.0 package; absorbs the `Clause` rename
      *(separate repository — cannot be done here)*
- [ ] ≥1 Netclaw integration test exercises a real PowerShell corpus entry
      through the live matcher and gets the expected gate decision

---

## NEXT (0.1.x / 0.2.x — additive)

- Seed corpus entries from sanitized real-world dogfood logs (SPEC §14
  workflow) — both shells.
- Expand verb / cmdlet / alias tables as the corpus surfaces real commands.
- Document the "consumer's algorithm" — given a `ParsedCommand`, how a
  security gate walks it (a `docs/CONSUMER_GUIDE.md` or `SPEC.md` appendix).
- Performance sanity check (~1 ms typical) with a tiny BenchmarkDotNet
  harness — only if anything in the daemon hot path complains.

## LATER (post-0.2.0)

- v0.2.x candidates from `SPEC.POWERSHELL.md` §18 — lossless redirect-stream
  identity (grow the `RedirectDirection` enum), per-element comma-array
  path extraction (`-Path a,b,c`).
- PowerShell script-level constructs — control flow, `function` / `class` /
  `enum` definitions, `param()` / `begin` / `process` / `end` blocks,
  `.ps1` file parsing (`SPEC.POWERSHELL.md` §18).
- Extract a shared lexer/parser core now that two parsers exist — the seam
  can be designed from real duplication (`SPEC.POWERSHELL.md` §18); the
  path-normalization helpers duplicated between `BashResolver` and
  `PwshResolver` are the first candidate.
- Windows `cmd` parser.
- Source-mapping (line/column on AST nodes) — only if an IDE consumer asks.
- Heredoc body extraction, process substitution, bash function definitions
  — only if a real consumer need surfaces.

## Parked

*(empty; move items here when scope changes rather than deleting them)*
