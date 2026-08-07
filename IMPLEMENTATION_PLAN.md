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
- [x] Synchronize the accepted shared and PowerShell v0.3 contracts into
      `SPEC.md` / `SPEC.POWERSHELL.md` in the public-API implementation change.
      The canonical specs now lock the additive types and defaults, closed
      record hierarchy, separate Bash and PowerShell grammars, occurrence and
      compatibility projections, bounded values/state, explicit redirects,
      resolver contexts, fail-closed consumer contract, and persistence caveat.
- [x] Correct the lexer-to-resolver provenance boundary before issue #69.
      Paired Bash and PowerShell shell-oracle cases must distinguish escaped
      literal resolver syntax from expandable syntax even when both decode to
      the same string, including standalone, adjacent-token, all-static
      mixed-quote, within-token escape, and literal-plus-expandable cases.
      Preserve ordered literal / typed-expansion / opaque fragments plus
      operation-specific transform eligibility, expansion identity,
      cardinality, and opaque cause internally; require explicit
      Bash-argument, Bash-redirect, PowerShell-native, cmdlet-Path,
      cmdlet-LiteralPath, and PowerShell-redirect resolver contexts, and
      aggregate adjacent fragments for complete
      argument and redirect targets. Runtime special, positional, numeric, and
      Unicode variables retain typed expansion identity and cardinality while
      remaining unknown without a proved value; incomplete braced
      interpolation is unparseable. Exact composition is required when
      every fragment, binding fact, and resolver fact is exact. Do not change
      the v0.2 public API or infer expansion from decoded text. The first
      implementation was halted before commit after adversarial review proved
      that one universal expandable bit misclassified quoted PowerShell native
      and cmdlet paths, missed valid variable forms, accepted incomplete
      interpolation, and split adjacent redirect targets.
      Bash provider-looking text remains literal; only PowerShell cmdlet path
      and redirect contexts apply provider or PSDrive semantics, overriding
      the obsolete shared resolver rule when the canonical specifications are
      synchronized.
      PowerShell redirects remain a separate Path-like context that applies
      tilde, wildcard, provider, and PSDrive semantics after quote removal;
      unknown wildcard cardinality or drive mappings fail closed without
      enumeration. Bash redirects instead require exactly one proved target.
      The completed correction uses an internal ordered `ShellValue` fragment
      model in both front ends and passes an explicit consumer context into
      each resolver without changing the public API. Direct lexer tests pin
      typed Bash special and multidigit positional parameters, quote-sensitive
      `$*` / `$@` cardinality, and PowerShell special, numeric, scoped, braced,
      and Unicode variable identity. Paired live-shell oracles cover standalone
      and adjacent escapes, static mixed quoting, literal-plus-expandable
      composition, runtime parameter forms, incomplete versus escaped braced
      interpolation, Bash provider-looking literals, native versus cmdlet
      provider and wildcard behavior, `Path` versus `LiteralPath`, adjacent and
      wildcard redirects, and PowerShell tilde/provider/PSDrive redirects.
      Executable corpus cases preserve the corrected v0.2 compatibility
      projection; unknown facts remain `DynamicSkip` or unparseable, while
      completely proved mixed fragments resolve exactly.
- [x] Implement [issue #69](https://github.com/Aaronontheweb/ShellSyntaxTree/issues/69)
      against the corrected fragment contract. One shell-neutral classifier
      now aggregates the complete raw span, decoded value, next-token index,
      and ordered `ShellValue` provenance supplied by explicit Bash and
      PowerShell adapters. It distinguishes literal-only, typed expansion,
      and opaque/computed runs without rescanning decoded text; missing lexer
      provenance fails closed as `Opaque` in both adapters. Direct adapter
      tests pin spans, maximal consumption, expansion identity, opaque cause,
      and fallback behavior. The full Bash and PowerShell corpora prove the
      extraction leaves raw, decoded, span, path, and `DynamicSkip` results
      unchanged. It introduces no new compatibility correction; the
      shell-oracle-proved corrections remain the ones documented in the
      preceding provenance item.
- [x] Audit duplicated Bash and PowerShell path-normalization helpers. Share
      only the identical string-level join and separator-normalization rules;
      keep root detection, drive-relative handling, full segment
      normalization, provider/PSDrive behavior, and resolver failure policy in
      their shell-specific implementations. Direct boundary tests pin the
      extracted helpers, while the complete resolver and corpus suites prove
      the refactor leaves both compatibility projections unchanged.
- [x] Add the inert v0.3 public API skeleton: the closed syntax-node family,
      command occurrences and ancestry, value domains and fixed limits,
      explicit redirect records, plus additive `ParsedCommand.Syntax` and
      `Commands`. Public snapshot tests pin every member, enum order, default,
      and assembly-only closure mechanism. Until the projection passes land,
      `Syntax` is an empty block and `Commands` is empty, so early use remains
      fail-closed while v0.2 `Clauses` behavior is unchanged.
- [x] Add the parser-owned structural projector and conservative compatibility
      flattener. It walks every syntax shape in deterministic authored order,
      assigns immediate roles and compositional ancestry, preserves the exact
      `Clause` instance and its authored operator, joins parser-owned analysis
      facts without mutating compatibility leaves, and discards every partial
      projection on malformed, aliased, cyclic, or over-depth structure or
      invalid joined facts. Direct tests pin ordering, branch and pipeline
      precedence, ancestry coordinates, reference identity, span and enum
      validity, value/redirect invariants, safe defaults, copied collections,
      and the 16-container bound.
- [x] Adapt the existing Bash grammar to emit structural and command-occurrence
      projections before enabling control flow. The recursive coordinator
      preserves pipeline/list precedence, isolated nested groups, decoded
      wrapper ownership and nullable spans, compatibility operators, and exact
      `Clause` identity; unsupported wrapper tails and depth overflow fail
      closed. Redirect-bearing leaves remain incomplete until the explicit
      redirect-analysis slice lands.
- [x] Adapt the existing PowerShell grammar to emit the structural and
      command-occurrence projections before enabling any control-flow
      construct. The PowerShell-specific recursive coordinator preserves
      statement/pipeline precedence, current-scope parenthesized groups,
      isolated child-host wrappers, current-scope `Invoke-Expression`, exact
      direct spans, nullable decoded spans, compatibility operators, and
      shared leaf identity. It rejects hostile structural depth before descent
      and leaves redirects, dynamic identities, unproved host command strings,
      and undiscovered executable expressions incomplete.
- [x] Promote representative existing constructs into the executable corpus
      with exact v0.3 syntax and command-occurrence expectations. Bash and
      PowerShell cases pin simple commands, list/pipeline precedence, group
      scope, static and dynamic wrappers, redirects, hidden substitutions,
      roles, ancestry, completeness, nullable decoded spans, compatibility
      operators, and exact shared `Clause` identity. The strict DTO rejects
      unknown fields and always requires unparseable projections to be empty.
      The PowerShell manifest now owns all 341 entries and round-trips exactly;
      explicit false/null assertions remain opt-in and generator-preserved.
- [x] Deliver the first Bash `$()` substitution slice for supported
      simple-command arguments and redirect targets. Direct tests and corpus
      entries pin multiple and nested ordering, exact ancestry/spans, isolated
      cwd, decoded wrappers, literal boundaries, dynamic compatibility values,
      depth limits, comment-safe delimiter scanning, and fail-closed command
      identities, background lists, assignment prefixes, backticks, heredocs,
      and malformed interiors.
- [x] Extend Bash substitution discovery to expanding heredoc bodies. The
      bounded slice recognizes quoted, escaped, mixed, and tab-stripping
      delimiters; preserves exact body/terminator provenance; surfaces nested
      substitutions in authored order with isolated state; and rejects header
      tails, queued heredocs, backticks, arithmetic, continuations, incomplete
      interiors, and depth overflow atomically. Direct and executable-corpus
      cases pin exact syntax, command ancestry, spans, completeness, and
      literal-versus-expanding behavior; real-Bash output and parse-only
      oracles independently pin the bounded semantic boundary.
- [x] Deliver the static-value Bash `for ... in` slice: locked structural
      nodes and spans, iterator `$()` discovery, condition-free body
      occurrences, exact/finite/pattern/unknown value domains, quote-proved
      effective arguments, nested distinct-name correlation, fixed
      candidate/depth limits,
      strict executable-corpus facts, and real-Bash oracles. Compatibility
      leaves preserve authored dynamic operands. Loop binding and cwd
      mutation fail closed, loops reached after recognized prior shell-state
      mutation fail closed, and occurrence cwd remains Unknown.
- [ ] Design and implement structure-aware Bash abstract-state analysis before
      enabling cwd-changing loop bodies or claiming the complete `for ... in`
      vertical slice. The parse-order attribution model cannot soundly publish
      occurrence cwd across pipelines, conditional lists, substitutions, and
      repeated iterations. Keep OpenSpec task 6.5 open, then add the remaining
      loop cases and Netclaw approval matrix after that design is reviewed.
- [ ] Complete PowerShell `$()` discovery in `foreach` expressions and add the
      Netclaw approval-matrix cases. The simple-command slice is delivered for
      ordinary, adjacent, quoted, here-string, redirect, standalone,
      call-operator, dynamic-identity, and host-wrapper positions, with
      current-scope state propagation and bounded expression rejection pinned
      by the 341-entry executable corpus.
- [ ] Deliver Bash `for ... in` and PowerShell `foreach` as the first two
      language-specific vertical slices, then extract only the shared analysis
      proven by both implementations.
- [ ] Build on the delivered bounded Bash heredoc grammar and quoted-delimiter
      adjacency by exposing public body/delimiter/expansion/completeness facts,
      then add a separately tested Bash `<<<` here-string redirect slice.
- [ ] Near the end of v0.3 delivery, expand the Web sample with curated complex
      Bash and PowerShell inputs and deterministic Mermaid views of syntax,
      occurrences, compatibility clauses, ancestry, redirects, and fail-closed
      outcomes. Keep visualization downstream of the canonical projection so
      it cannot become a second command-discovery implementation; snapshot the
      rendering, escape arbitrary shell labels, and emit no raw HTML.

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
