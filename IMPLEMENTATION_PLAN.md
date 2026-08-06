# Implementation Plan — ShellSyntaxTree

Source of truth for active work. Translates `SPEC.md` §16 (bash — shipped)
and `SPEC.POWERSHELL.md` §16 (PowerShell — v0.2.0) into NOW / NEXT / LATER
buckets. Park items aggressively — autonomous loops will otherwise bulldoze
priorities.

> **Hard rule:** every PR ends with this file updated (item moved /
> completed / parked) so the plan reflects reality.

---

## NOW (0.2.0 downstream acceptance / 0.3.0 contract design)

> **Spec:** `SPEC.POWERSHELL.md` (v0.2.0). The PowerShell parser is
> implemented — phases 1–14 of `SPEC.POWERSHELL.md` §16 are complete (see
> below). What remains is the downstream Netclaw integration, which needs
> actions outside this repository.

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
- [x] **12. PowerShell corpus** — 273 entries under `Corpus/powershell/`,
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
- [x] **Issue #62 — source-ordered clause elements.** Added the additive
      `Clause.Elements` provenance view for Bash and PowerShell with exact raw
      spelling, decoded values, source spans when available, verb-relative
      argument placement, path facts, redirects, and conservative wrapper-span
      handling. Authored order is authoritative; element roles and
      `PrecedingVerbElementCount` explicitly mirror the greedy parser
      projection rather than executable semantics. Paired Bash/PowerShell
      corpus cases cover Git `-c`/`-C`, multiple occurrences, and a valueless
      option that stops the greedy walk. Existing projection shapes and
      synthetic cwd attribution remain compatible; native options that differ
      only by case receive corrected metadata. The post-implementation option
      audit explicitly covers Wget `-o` / `-O`, curl `-d` / `-D` / `-o` /
      `-O`, Git `-c` / `-C`, and tar `-c` / `-C` / `-f` / `-F`; paired corpus
      cases pin Wget log/document output, curl data/header-output and `@file`
      semantics, and tar helper-command safe-fail behavior in both shells.
      Adversarial review added deterministic coverage for quoted inline native
      fragment runs (including unquoted prefixes and mixed-quote safe-fail),
      PowerShell backtick-decoded colon bindings, native file-verb boundaries,
      and outer redirects on PowerShell command wrappers, including empty
      payloads. The
      corpus runner now verifies direct authored-token coverage even for legacy
      entries without explicit element expectations. Docker `-v`
      remains explicitly context-sensitive: the generic table supports
      `docker run`, while consumers use authored elements for global-option
      interpretation.
      Command-string provenance is integrated with the later
      `Invoke-Expression` recursion work: static expansion clears unmappable
      outer spans, while dynamic payloads retain conservative source-aligned
      elements. Nested and dynamic `bash -c` cases pin the equivalent Bash
      boundary. Consumer guidance separates strict authored-stream matching
      from general executable-aware normalization; Netclaw can use the latter
      for reusable approvals without treating parser verb roles as semantic
      command boundaries.
- [x] **Issue #64 — path-shaped operands after native verb chains.**
      Stop the Bash and PowerShell native greedy passes before a token that
      matches the shared path-shape rules. Preserve that token as a resolved
      argument without a command dictionary or a public API change.
- [x] **Consumer guide.** Added `docs/CONSUMER_GUIDE.md` with the
      shell-neutral security-consumer algorithm, Bash and PowerShell guidance,
      worked public use cases, and immutable permalinks to Netclaw's production
      integration. Linked it from the README and aligned stale PowerShell
      prerelease/status wording in the public project docs.
- [x] **Issue #52 — hyphenated PowerShell parameters/native options.**
      Preserve internal hyphens, apply bash-compatible native
      `--flag=value` splitting and path classification, keep colon binding
      cmdlet-only, and pin the behavior in unit tests plus the PowerShell
      corpus. Review follow-ups shipped with it: the equals-form split moved
      to a shared `NativeFlagSyntax` so the two parsers can't drift, a colon
      value under an `=`-bearing parameter name safe-fails to `DynamicSkip`,
      `-?` lexes as one parameter token, and native option tables now use
      ordinal spelling while PowerShell cmdlet parameters remain
      case-insensitive.

### 15. Release 0.2.0 (alpha → beta → stable) — SPEC.PWSH §15 / §17

- [x] Tag `0.2.0-alpha`; `publish_nuget.yml` produced
      `ShellSyntaxTree.0.2.0-alpha.nupkg` and it is live on nuget.org
      (released 2026-05-20).
- [x] `0.2.0-beta.1` so Netclaw validates the parser + the breaking rename
- [x] Publish the next `0.2.0` prerelease with the additive issue #62
      `Clause.Elements` provenance surface and migration guidance
- [x] Promote to stable `0.2.0` after Netclaw validation

### 16. Netclaw v0.2.0 integration — SPEC.PWSH §17 #9

- [ ] Netclaw consumes the v0.2.0 package; absorbs the `Clause` rename
      *(separate repository — cannot be done here)*
- [ ] ≥1 Netclaw integration test exercises a real PowerShell corpus entry
      through the live matcher and gets the expected gate decision

### 17. v0.3 structured shell analysis contract — issue #72

- [x] Create the release-level OpenSpec proposal, design, capability deltas,
      and ordered task list under
      `openspec/changes/v0-3-structured-shell-analysis/`.
- [x] Create [issue #72](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/72)
      as the v0.3 roadmap and cross-link issue #71 control flow and issue #69
      shared native argument-fragment classification without merging their
      scopes.
- [x] Add a versioned pre-implementation design corpus with paired Bash and
      PowerShell representative, boundary, and adversarial cases. The validator
      rejects schema drift, checks references and command ordering, confirms
      every `current` expectation against the v0.2 parsers, and includes the
      files in the PII audit. Corpus review established that command role must
      be immediate while ancestry remains compositional, occurrence
      completeness is independent of value precision, and PowerShell authored
      parameter classification must remain distinct from effective values. The
      paired 32/33-candidate boundary cases lock finite-versus-unknown behavior.
- [x] Lock OpenSpec task-group decisions 1.1–1.5 and 1.8: exact public type
      candidates and safe defaults, in-memory `Clause` identity, fixed 32/16/5
      analysis limits, separate Bash and PowerShell grammar matrices, static
      pattern-cover rules, divergent-cwd fallback, deferred forms, project
      context, and the preimplementation consumer-guide migration contract.
- [ ] Complete OpenSpec tasks 1.6–1.7 in the public-API implementation PR:
      synchronize the accepted shared and PowerShell contracts into
      `SPEC.md` / `SPEC.POWERSHELL.md` together with source and snapshot tests
      so the repository authority never intentionally drifts from the assembly.
- [ ] Correct the lexer-to-resolver provenance boundary before issue #69.
      Paired Bash and PowerShell shell-oracle cases must distinguish escaped
      literal resolver syntax from expandable syntax even when both decode to
      the same string, including standalone, adjacent-token, all-static
      mixed-quote, within-token escape, and literal-plus-expandable cases.
      Preserve ordered literal / expandable / opaque fragments internally;
      require exact composition when every fragment and resolver fact is
      exact, do not change the v0.2 public API, and never infer expansion from
      decoded text.
- [ ] Implement [issue #69](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/69)
      against the corrected fragment contract. Preserve raw, decoded, and span
      facts plus unaffected classifications; explicitly document only
      shell-oracle-proved compatibility corrections to false path claims and
      avoidable `DynamicSkip` results.
- [ ] Add the structural and command-occurrence projections for the existing
      grammar before enabling any control-flow construct.
- [ ] Deliver Bash `for ... in` and PowerShell `foreach` as the first two
      language-specific vertical slices, then extract only the shared analysis
      proven by both implementations.
- [ ] Preserve the existing Bash heredoc grammar, fix quoted-delimiter
      adjacency, expose body/delimiter/expansion/completeness facts, and add a
      separately tested Bash `<<<` here-string redirect slice.

---

## NEXT (0.1.x / 0.2.x — additive)

- Seed corpus entries from sanitized real-world dogfood logs (SPEC §14
  workflow) — both shells.
- Expand verb / cmdlet / alias tables as the corpus surfaces real commands.
- Performance sanity check (~1 ms typical) with a tiny BenchmarkDotNet
  harness — only if anything in the daemon hot path complains.

## LATER (post-0.2.0)

- The remaining v0.2.x candidate from `SPEC.POWERSHELL.md` §18 is per-element
  comma-array path extraction (`-Path a,b,c`). Lossless cross-shell redirect
  identity is now part of issue #72's explicit v0.3 redirect model.
- PowerShell definitions, `param()` / `begin` / `process` / `end` blocks, and
  `.ps1` file parsing remain outside issue #72 (`SPEC.POWERSHELL.md` §18).
- Any broader shared parser/analysis extraction beyond issue #69 follows the
  two language-specific tracer bullets in issue #72; lexers and structural
  parsers remain shell-specific unless proven duplication justifies a narrower
  composed helper.
- Windows `cmd` parser.
- Source-mapping (line/column on AST nodes) — only if an IDE consumer asks.
- Process substitution remains a separately gated issue #72 task; Bash
  function definitions remain deferred until a consumer need surfaces.

## Parked

*(empty; move items here when scope changes rather than deleting them)*
