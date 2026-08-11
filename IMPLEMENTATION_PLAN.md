# Implementation Plan — ShellSyntaxTree

Source of truth for active work. Translates `SPEC.md` §16 (bash — shipped)
and `SPEC.POWERSHELL.md` §16 (PowerShell — v0.2.0) into NOW / NEXT / LATER
buckets. Park items aggressively — autonomous loops will otherwise bulldoze
priorities.

> **Hard rule:** every PR ends with this file updated (item moved /
> completed / parked) so the plan reflects reality.

---

## NOW (0.3.1 bounded approval facts)

- [x] **Create the v0.3.1 approval-fact OpenSpec.** Harvest and sanitize the
      Netclaw v0.26.0-beta.3 approval window. Classify all 18 distinct prompts
      and define bounded concatenation, separate effective and authored-source
      word facts, lexical path shape, consumer, corpus, and adversarial
      contracts under `openspec/changes/v0-3-1-approval-facts/`.
- [x] **Approve the v0.3.1 public contract.** Confirm the additive
      `IntegerRange`, `Concatenation`, `AuthoredValue`, `ShellPathShape`,
      `AuthoredPathShape`, and `PublishAuthoredSourceFacts` names. Confirm that
      Netclaw may use the pre-field-splitting word fact for approval matching
      while effective facts retain runtime meaning.
- [x] **Implemented and released v0.3.1.**
      [PR #153](https://github.com/Aaronontheweb/ShellSyntaxTree/pull/153)
      merged as `a414cdda` after Linux and Windows CI passed. The bare `0.3.1`
      tag completed
      [workflow run 31530682715](https://github.com/Aaronontheweb/ShellSyntaxTree/actions/runs/31530682715).
      The workflow published the package and symbols package. The public
      [NuGet index](https://www.nuget.org/packages/ShellSyntaxTree/0.3.1)
      lists version `0.3.1`. The non-draft, non-prerelease
      [GitHub release](https://github.com/Aaronontheweb/ShellSyntaxTree/releases/tag/0.3.1)
      contains both assets. API, unit, corpus, PII, native-oracle, package,
      header, and adversarial checks passed before publication.

## Completed v0.3.0 host integration and release acceptance

> **Spec:** `SPEC.POWERSHELL.md` (v0.2.0). The PowerShell parser is
> implemented — phases 1–14 of `SPEC.POWERSHELL.md` §16 are complete (see
> below). The downstream Netclaw integration and cross-platform acceptance
> gates are complete. The stable package publication remains.

- [x] **v0.3 prerelease consumer-API correction.** Preserve the stable v0.2
      API and the `Syntax` / `Commands` / `Clauses` ingestion lanes, but replace
      the alpha-only sparse coordinate and public property-bag model before
      stable v0.3. Return one parser-owned analyzed argument per authored
      non-cwd argument, use closed value-domain and redirect alternatives,
      reference actual ancestor nodes, and remove public syntax vocabulary
      that stable v0.3 never emits. Update the OpenSpec and source mocks first;
      then update implementation, snapshots, corpus DTOs, consumer guide,
      README, and Netclaw together. No compatibility shim for 0.3 alphas. The
      library, specifications, snapshots, corpus DTOs, generated expectations,
      README, consumer guide, and prerelease migration notes are synchronized;
      Netclaw PRs #1855 and #1857 complete migration and downstream acceptance.

- [x] **v0.3 host-selected grammar and PowerShell dialect — library slice.** The executor
      selects one top-level parser; Bash never cross-parses `pwsh` payloads and
      PowerShell never cross-parses `bash -c` payloads. Add the extend-only
      `PwshDialect` option with PowerShell 7 as the compatibility default and
      Windows PowerShell 5.1 as an explicit native-Windows fallback. Dialect-
      local syntax/catalog behavior and paired direct/corpus coverage are
      implemented. GitHub Actions run 31357084413 proved the hash-pinned
      PowerShell 7.6.4 oracle on Ubuntu and Windows, plus native Windows
      PowerShell 5.1 discovery and its dialect-routed oracle on Windows.

- [x] **v0.3 native-Windows Netclaw integration.** Pass the exact selected
      shell through Netclaw's executor, approval policy, and model context;
      prefer a compatible `pwsh.exe`, fall back to `powershell.exe`, and
      reparse and reauthorize if executable selection changes.

- [x] **v0.3 authored-command approval correction.** Treat PowerShell and Bash
      approval completeness consistently: prove every authored executable
      region, but do not require proof of ambient aliases, functions, modules,
      profiles, executable lookup, or inherited environment state. Preserve the
      existing `PwshInitialStateMode` API. Default-mode PowerShell occurrence
      completeness now follows that authored boundary while loop-dependent
      effective values remain Unknown unless fresh-process state is proved.
      The 2,870-case ShellSyntaxTree suite and Netclaw's 240-row catalog prove
      the executable corpus and approval matrix. Explicit source mutation,
      computed identity, hidden execution, unknown receiver
      semantics, unsupported constructs, and policy-sensitive unknown values,
      paths, cwd, or redirects remain strict.

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
      integration. Added compact input-to-result-to-policy examples for command
      occurrences, attached arguments, bounded and zero-or-more loops, cwd
      propagation, file and descriptor redirects, substitutions, PowerShell
      command-owned execution regions, and safe-fail results. The execution-
      region example pins exact `HostArgument` identity, known metadata,
      nonempty complete body evidence, independent host/body authorization, and
      strict unknown or incomplete fallbacks. Linked it from the README and
      aligned stale PowerShell prerelease/status wording in the public project
      docs.
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

- [x] Netclaw consumes the v0.2.0 package; absorbs the `Clause` rename
      *(separate repository — cannot be done here)*
- [x] ≥1 Netclaw integration test exercises a real PowerShell corpus entry
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
      The PowerShell manifest owns all 491 entries and round-trips them exactly,
      including case-specific isolated-state inputs. Explicit false/null
      assertions remain opt-in and generator-preserved.
- [x] Promote every landed stable Bash design case into the executable corpus
      with complete compatibility, syntax, occurrence, value, ancestry,
      redirect, and completeness assertions. Nine compatibility-only entries
      carry the v0.3 projections, entries 281-292 cover the first inputs that
      had no exact executable-corpus case, and entries 294-308 close the
      remaining exact-input gap for substitutions, cwd joins, bounded loops,
      descriptor duplication, and literal heredoc data. Empty occurrence-level
      redirect projections are explicit in the new cases. The three Bash
      future-scope design cases remain non-gating.
      Promotion also reconciled the unquoted wildcard
      redirect story with the fail-closed completeness contract, publishes
      sparse exact/unknown effective-value overlays, and pins quoted, escaped,
      and continued tilde-prefix behavior against Bash. The later PowerShell
      execution-region and loop slices now complete OpenSpec task 1.10.
- [x] Promote the first 22 stable PowerShell design cases covering value,
      path, and redirect provenance. Exact effective values are published only
      when parser-owned fragments prove the post-lexical value; runtime
      automatic parameters and active wildcards remain Unknown. The sparse
      overlay omits ordinary literals, distinguishes quoted from active tilde
      and wildcard syntax, preserves provider and PSDrive spellings, and keeps
      redirect resolution separate from command arguments. Local PowerShell
      probes and direct tests pin that native tilde expansion preserves the
      configured home string byte-for-byte, including root and trailing
      separators. Adversarial review added 47 executable-corpus cases that
      separate mutable `$HOME`, configured tilde expansion, process-environment
      child initialization, current provider location, and default-versus-
      isolated command binding; re-evaluate redirect targets from occurrence-
      local state; and invalidate home, cwd, environment, and command-binding
      facts after uninspected `.ps1` execution. Decoded wrappers rebuild value
      provenance from preserved inner raw spelling, reset child-process state,
      retain explicit native/script binding semantics from authored path
      spellings without inspecting ambient child state, clear
      profile-mutable automatic HOME and environment facts, and carry a bounded
      invocation-owner depth for current, intermediate, and root-owned
      redirects. Parser-owned binding provenance now distinguishes path-shaped
      native/script candidates, constrained cmdlets and aliases, and ambiguous
      unqualified hyphenated names without reparsing verb spelling. Script
      blocks are excluded from host effective argv, and
      unknown non-pipeline receivers retain visible, incomplete bodies and
      invalidate subsequent state without discarding v0.2 leaves; unproved
      pipelines fail atomically. Alpha.3 authorization completeness requires
      the explicit constrained command-resolution baseline, including after
      decoded-host boundaries. The v0.3 authored-command approval correction
      above supersedes that behavior and is implemented in this slice.
      Execution-region and loop/state design promotions remained for later
      slices; the final promotion below now closes OpenSpec task 1.10.
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
- [x] Design and implement structure-aware Bash abstract-state analysis for the
      complete bounded `for ... in` state slice. The analyzer owns
      success/failure partitions, failure-aware cwd transfer, conservative
      pipeline state, ordered duplicate-preserving loop plans, persistent loop
      bindings, empty iteration, occurrence-fact joins, substitution isolation,
      and explicit decoded-wrapper remapping. Unknown-cardinality loops use
      bounded fixed-point widening, a 4096-transition global budget fails nested
      cross-products atomically, and complete effective argv is re-evaluated for
      every visit. Loop-derived `cd` options, terminators, invalid/multiple
      operands, recursive exact `command` / `builtin` dispatch, physical-path
      compatibility sanitation, and post-loop binding mutation are pinned by
      unit tests, native Bash oracles, the design corpus, and executable corpus.
      The coverage matrix now also pins empty and multiline loops, mixed
      separators, pipelines, nested loops, wrapper scope, static and
      binding-derived redirects, substitutions, option-shaped and unquoted
      values, indirect and parameter-operator rejection, and every candidate
      and transition cap. All unmodeled mutations, dynamic dispatch, control
      transfers, and occurrence-specific redirect values remain fail closed.
      Netclaw PR
      [#1857](https://github.com/netclaw-dev/netclaw/pull/1857) completes the
      consumer gate with 199 Bash approval rows on the alpha.6 package.
- [x] Deliver occurrence-level Bash explicit redirect facts for ordinary file
      input/output/append, static descriptor duplicate/close/move, computed
      descriptor targets, and combined `&>` / `&>>` output. Static descriptor
      operations are complete non-path facts even though the v0.2 compatibility
      redirect remains `IsDynamicSkip`; computed targets remain incomplete and
      cannot be exempted by raw prefix. Arbitrary numeric source descriptors are
      retained only when authored at a token boundary; overflow sources remain
      incomplete rather than being truncated, and word suffixes such as
      `command3>file` keep `3` in the command name. Unquoted LF/CRLF line
      continuations are removed before descriptor recognition, including when
      they join multi-digit sources. Exact file targets now complete their
      containing occurrence, while cwd or value uncertainty still downgrades
      the redirect and occurrence after abstract-state joins. Direct lexer and
      parser tests plus executable corpus cases pin the boundary. Netclaw PR
      #1857 pins the paired allow, prompt, stored-grant, and fail-closed
      redirect dispositions.
- [x] Migrate Netclaw's Bash approval path to `0.3.0-alpha`. Netclaw PR
      [#1835](https://github.com/netclaw-dev/netclaw/pull/1835) enumerates every
      `CommandOccurrence`, consumes complete ancestry, cwd, compatibility
      argument/path, and explicit redirect facts, and removes the temporary raw
      descriptor inference. Focused security tests and the 166-case approval
      matrix pin ordinary commands, pipelines, wrappers, static and dynamic
      redirects, cwd joins, symlink boundaries, POSIX shell payloads, and
      hard-deny precedence. Unknown or incomplete occurrence and redirect facts
      and dynamic or unresolved compatibility arguments remain fail closed.
      Cross-platform test fixtures canonicalize platform temporary roots while
      production symlink checks remain unchanged.
      Netclaw PR #1857 adds the bounded Bash loop and PowerShell consumer gates.
- [x] Deliver occurrence-level PowerShell explicit redirect facts while
      preserving the v0.2 compatibility projection. File output and append
      retain default, numbered, or all-streams sources; the native-only merge
      grammar preserves sources `2`–`6` or `*` and target descriptor `1`.
      File targets remain complete when their value domain is Unknown, and an
      isolated bounded `foreach` can promote redirect targets to exact or
      finite absolute-path domains without publishing them as command
      arguments. Live PowerShell 7.6.4 oracle cases correct two obsolete
      grammar assumptions: `<` is reserved, and `1>&1`, `2>&3`, and `2>&-`
      are syntax errors. `$null` remains explicit but incomplete because the
      locked public operation vocabulary has no discard-sink member. Direct
      tests and the generated executable corpus pin static, dynamic,
      malformed, multiple, all-streams, merge, suffix-boundary, and loop-bound
      cases. Adversarial review additionally forced outer wrapper redirect
      provenance through cwd success/failure joins, native duplicate-source
      rejection, identical `$null` / `${null}` sink handling, and removal of
      stale parse-time cwd targets from unreachable relative redirects while
      retaining cwd-independent absolute targets. A follow-up review also
      separated parent-owned outer wrapper redirect provenance from decoded
      child scope, preserving finite parent-loop targets without relaxing the
      child command's independent completeness. A fourth review extended that
      ownership through nested quoted and encoded child hosts: the outer
      redirect now uses the outermost authoring invocation rather than the
      nearest decoded child. Netclaw PR #1857 pins the paired PowerShell
      redirect matrix.
- [x] Complete PowerShell `foreach` integration and add the
      Netclaw approval-matrix cases. The structural slice now preserves literal
      scalar/array and executable iterator forms, recursively parses bodies,
      projects iterator and loop-body ancestry, survives decoded wrappers, and
      fails closed on dynamic iterables, iterator/body state or
      command-resolution mutation, malformed boundaries, and depth overflow.
      Netclaw PR #1857 adds 36 PowerShell 7 and five Windows PowerShell 5.1
      approval rows. All 21 refreshed GitHub check runs passed; rebased local
      acceptance separately passed the Release build, focused security and
      approval-matrix tests, the full solution suite, headers, and strict
      OpenSpec validation.
      The authored-command correction makes default-mode loop-body and
      current-scope post-loop static occurrences complete without weakening
      explicit mutation or dynamic execution checks. Isolated child-host loops
      do not taint their outer continuation.
      Alpha.3 added an explicit PowerShell initial-runspace contract and
      wrapper-state metadata. The additive `PwshInitialStateMode` API remains
      locked. The v0.3 authored-command correction no longer requires isolated
      mode for static command completeness and removes its old pinned-module
      implication; loop-dependent effective values still require the fresh-
      process assertion. The first value-analysis pass consumes that contract,
      retains parser-owned
      argument provenance, proves quoted scalar and literal-array domains,
      retains ordered duplicate visits separately from public set summaries,
      guards a pinned documented preference inventory plus fresh-host built-ins
      with a live PowerShell oracle, composes case-insensitive distinct nested
      bindings, and leaves pipeline
      objects, null, overflow, wrappers, and redirects conservative. The
      PowerShell-specific state pass now owns ordered persistent bindings,
      same-name overwrites, empty and zero-or-more execution, occurrence joins,
      failure-aware `Set-Location`, current-runspace subexpressions, child-host
      isolation, and the shared 4096-transition budget. Parser-frame location
      attribution is cloned so empty bodies do not leak and possibly reached
      mutations cannot leave a false exact cwd; outcome projection rebases exact
      failure continuations and sanitizes unknown joins. The mutation inventory
      inspects both verbs and parameter binding: common variable writers,
      PowerShell 7 command-specific writers, accepted abbreviations and inline
      values, and opaque splats invalidate later observing proofs, including
      both outcome partitions of specialized `Set-Location` analysis. Alternate
      binder dashes and unsupported module-qualified cmdlets now fail closed
      consistently inside structural regions. Computed `Invoke-Expression`
      invalidates later current-runspace binding, command-resolution, and cwd
      proofs and fails atomically as an unmodeled loop transfer. Expand the design corpus for
      aliases, cmdlets/native commands, pipelines, wrappers, redirects, and the
      remaining adversarial/oracle matrix before tasks 7.5-7.7. The
      simple-command slice is
      delivered for ordinary, adjacent, quoted, here-string, redirect, standalone,
      call-operator, dynamic-identity, and host-wrapper positions, with
      current-scope state propagation and bounded expression rejection pinned
      by the generated executable corpus. Remote/session/SSH/VM/container
      `Invoke-Command` now starts from arbitrary child state, isolates all exit
      effects, publishes synchronous/once only for one proved target, and
      publishes concurrent timing for multiple targets or enabled asynchronous
      switches while keeping dynamic cardinality fail closed.
- [x] Deliver typed PowerShell script-block execution regions before calling
      tasks 7.5-7.7 complete. The corrected contract adds an execution-region
      syntax node with independent origin, phase, timing, and cardinality rather than a
      false shared/isolated scope flag. The inert additive public API skeleton,
      enum/default snapshots, recorded local PowerShell probe evidence, and
      design-corpus categories are delivered. The shared projector,
      compatibility flattener, depth guard, decoded-wrapper cloning, and
      executable-corpus DTOs now preserve direct and command-owned regions in
      the locked substitution-host-region order. The PowerShell structural
      parser now emits command-argument script blocks as conservative unknown,
      incomplete regions, recursively exposes supported body commands, retains
      pure output expressions without inventing command occurrences, and fails
      atomically on unsupported execution-bearing expressions. Until a receiver
      contract proves scope and timing, an unknown region also poisons
      subsequent observing cwd, variable, and command-resolution facts so a
      continuation cannot reuse stale authorization evidence. Receiver-aware
      typed facts and shell-specific state flow remain in tasks 7.5b-7.5e, and
      automated execution-region oracle coverage remains in task 7.7.
      The PowerShell 7.6.4 receiver and parameter-binding catalog is now
      implemented with command-resolution proof as an explicit input. It pins
      aliases, supported module qualification, exact and abbreviated/inline
      parameters, positional slots, parameter sets, `ScriptBlock[]`, authored
      ForEach-Object multi-block coordinates, and semantic Begin/Process/End
      phases. The optional Microsoft.PowerShell.ThreadJob entry remains an
      unknown incomplete receiver and no longer gates stable v0.3. A proved
      local `Invoke-Command -AsJob` combination now fails atomically because
      PowerShell has no compatible in-process parameter set. Ambiguous prefixes,
      malformed value binding, unproved identities, and unknown receivers retain
      unknown/incomplete facts. Supported catalog-owned module qualifications now pass structural
      admission because every possible body remains visible; the occurrence
      analyzer still withholds typed receiver facts after an observed command-
      resolution mutation unless bounded mutation provenance proves the exact
      authored spelling unaffected. PowerShell permits an exact alias whose
      name looks module-qualified, so module qualification is not independent
      identity proof. The first direct-operator sub-slice now handles
      currently supported command interiors in typed synchronous `& {}` and
      `. {}` regions without synthetic host commands. It isolates ordinary
      direct-call binding and command-resolution exit mutation, invalidates
      explicitly escaping scope/provider mutation, carries shared location
      outcomes, and keeps block arguments and leading `param()` declarations
      atomic. `Measure-Command -Expression` and `Trace-Command -Expression`
      now form the first command-owned vertical slice: their Main regions are
      synchronous/once, execute against current-scope state, downgrade across
      conflicting loop visits, and are pinned by live PowerShell probes for
      current-scope, opaque-data, shadowed-command, and module-qualified
      behavior. `ForEach-Object` Begin, Process, RemainingScripts, and End and
      `Where-Object` FilterScript now retain authored region order while the
      analyzer applies semantic phase order. Standalone, first-pipeline-stage,
      upstream-pipeline, and explicit-InputObject cardinalities remain
      distinct; zero-or-more Process/Filter effects use a conservative fixed
      point, and Begin/End state surrounds that join. Live PowerShell probes
      pin current-scope mutation, empty input, explicit input, phase order, and
      the differing no-input behavior of the two cmdlets. Ordinary assignment
      state transfer remains an atomic task 7.5b follow-up; task 7.5b is not
      complete. In-process `Invoke-Command` now distinguishes default child
      variable/command scope from `-NoNewScope` current-scope flow while
      propagating shared location and retaining synchronous/once region facts.
      Pipelines with any supported synchronous execution region plus another
      stateful stage withhold body facts that downstream initialization or
      per-object interleaving can invalidate.
      `New-Module` initialization now runs once synchronously from caller
      state in a child module scope, propagates shared location, restores
      ordinary child bindings, applies common variable-writer initialization
      before the body, invalidates body identity when a writer can alter
      resolution preferences, and retains conservative host command-resolution
      invalidation for exported functions across canonical, alias, and supported
      module-qualified identities.
      `Start-Job` now schedules initialization before main in an isolated child
      process state, inherits or applies the invocation working directory, and
      prevents child exit mutation from contaminating the host continuation.
      Inline working-directory values retain exact value provenance, while an
      explicit relative working directory remains unknown because PowerShell
      resolves it from a platform-specific child startup location rather than
      the caller location. Known but unsupported job variants retain their
      proved child-process isolation while their body analysis stays fail
      closed; an explicit alternate `-PSVersion` remains visible but incomplete
      because it falls outside the pinned PowerShell 7 runtime model.
      `ForEach-Object -Parallel` now publishes a concurrent per-input child-
      runspace region, inherits the captured caller location, starts without
      caller bindings or alias mutations, and isolates runspace-local child
      exit from the host continuation, including `-AsJob`. Pooled-runspace command-
      resolution mutation is joined across later activations; `-UseNewRunspace`
      keeps activations independent. Live PowerShell probes pin both behaviors,
      including the process-wide environment-provider exception: a possibly
      escaping child mutation invalidates later host binding, command-
      resolution, and location facts and also poisons later child activations
      under `-UseNewRunspace`, which resets runspace-local but not process-wide
      state. Runspace-global variables, functions, aliases, and location remain
      isolated. The analyzer retains a bounded, case-insensitive set of exact
      alias/function names whose identity changed and treats only matching later
      invocations as possible process escapes; an ambiguous mutation or set
      overflow fails closed to every unproved command name. This includes a
      mutator such as `Set-Alias` being rebound to `Set-Item` before a later
      authored `Set-Alias Env:...` invocation.
      The generator-owned executable corpus now supports per-entry initial-
      state mode and includes the promoted Parallel and remote/session cases.
      It now contains exact generated expectations for 86 of the 90 PowerShell
      design cases. Four intentionally deferred condition/deferred-action cases
      remain parked. Bounded-loop dynamic invocation is visible and incomplete,
      retains finite loop-variable arguments for the occurrence, and invalidates
      following state. A proved local `Invoke-Command -AsJob` combination fails
      atomically as an invalid parameter set. Generated entries 539-540 pin
      both corrections. Direct generated cases for
      `Measure-Command`, `Trace-Command`, and `ForEach-Object
      -RemainingScripts` fill the remaining stable receiver-catalog evidence
      without renumbering the existing corpus.
      Stable v0.3 stops at the delivered Start-Job, Parallel, and remote/session
      boundaries. Optional-module Start-ThreadJob and exact deferred
      breakpoint/event/completion actions are post-v0.3 catalog work; unknown
      receivers continue to expose supported non-pipeline bodies as incomplete;
      unproved interior pipelines fail atomically. Script blocks consumed
      by a proved canonical, alias, or supported module-qualified `Write-Output`
      receiver now remain opaque data under constrained, bounded command-
      resolution state. Unknown receivers and exact identities changed by
      observed alias mutation expose incomplete executable bodies instead.
      Generated corpus entries 418-422 pin proved data, the fail-closed unknown
      receiver, proved local `Invoke-Command`, and the exact module-qualified-
      looking and canonical-target alias boundaries; unsupported potentially
      executable interiors still fail atomically. Static current-scope
      `Invoke-Expression` cloning preserves cmdlet/native binding provenance;
      paired direct, unit, and executable-corpus cases pin cmdlet tilde and
      wildcard values plus native tilde expansion while decoded child hosts
      remain unknown. Local PowerShell 7.6.4 probes pin variable-versus-location
      independence, semantic phase order, child process/runspace boundaries,
      boundaries and the fact that the in-process `Invoke-Command` parameter
      set does not support `-AsJob`.
- [x] Complete the stable-v0.3 Bash `for ... in` and PowerShell `foreach`
      vertical slices without gating release on a shared-analysis refactor.
- [x] Publish `0.3.0-alpha` for the Netclaw migration gate. The bare SemVer tag
      published the NuGet package, symbol package, and GitHub prerelease from
      the reviewed merge commit after Linux and Windows CI passed.
- [x] Publish `0.3.0-alpha.1` as the next Netclaw validation package. Bare tag
      [`0.3.0-alpha.1`](https://github.com/Aaronontheweb/ShellSyntaxTree/releases/tag/0.3.0-alpha.1)
      points to reviewed merge `a0f95bdb`; the tag workflow published the
      package and symbols to NuGet and created the GitHub prerelease after
      Linux and Windows PR validation passed. It carries the bounded Bash
      heredoc, command-resolution mutation, and here-string slices merged after
      the first alpha without changing the public API.
- [x] Published `0.3.0-alpha.2` for the Netclaw PowerShell policy matrix after
      Linux and Windows validated the reviewed proved-data receiver slice. The
      [NuGet package](https://www.nuget.org/packages/ShellSyntaxTree/0.3.0-alpha.2)
      and [GitHub prerelease](https://github.com/Aaronontheweb/ShellSyntaxTree/releases/tag/0.3.0-alpha.2)
      include exact module-qualified-looking alias and canonical alias-target
      shadowing defenses without changing the public API.
- [x] Published `0.3.0-alpha.3` for the Netclaw PowerShell approval integration
      after Linux and Windows validated the reviewed initial-state, value,
      command-binding, and state-invalidation slice. The
      [NuGet package](https://www.nuget.org/packages/ShellSyntaxTree/0.3.0-alpha.3)
      and [GitHub prerelease](https://github.com/Aaronontheweb/ShellSyntaxTree/releases/tag/0.3.0-alpha.3)
      preserve the v0.2 projection and the existing public v0.3 API.
- [x] Published `0.3.0-alpha.4` with the reviewed authored-command completeness
      correction. The
      [NuGet package](https://www.nuget.org/packages/ShellSyntaxTree/0.3.0-alpha.4)
      and [GitHub prerelease](https://github.com/Aaronontheweb/ShellSyntaxTree/releases/tag/0.3.0-alpha.4)
      preserve strict unknown and source-mutated policy facts while default-mode
      static PowerShell commands no longer require ambient resolution proof.
- [x] Published `0.3.0-alpha.5` with the reviewed host-selected PowerShell
      dialect contract, explicit PowerShell 7.6 and Windows PowerShell 5.1
      behavior, same-language child-host selection, cross-language
      non-delegation, and live dual-dialect Windows oracle proof. The
      [NuGet package](https://www.nuget.org/packages/ShellSyntaxTree/0.3.0-alpha.5)
      and [GitHub prerelease](https://github.com/Aaronontheweb/ShellSyntaxTree/releases/tag/0.3.0-alpha.5)
      were published by successful [tag workflow run 31357832880](https://github.com/Aaronontheweb/ShellSyntaxTree/actions/runs/31357832880)
      after Linux and native Windows validation passed. Use this package for
      Netclaw's native-Windows integration gate.
- [x] Published `0.3.0-alpha.6` with the reviewed corrected consumer API from
      merge `e422aa2a`. The
      [NuGet package](https://www.nuget.org/packages/ShellSyntaxTree/0.3.0-alpha.6)
      and [GitHub prerelease](https://github.com/Aaronontheweb/ShellSyntaxTree/releases/tag/0.3.0-alpha.6)
      were published by successful [tag workflow run 31421653771](https://github.com/Aaronontheweb/ShellSyntaxTree/actions/runs/31421653771)
      after the Release build, 2,870 tests, package creation, header check,
      strict OpenSpec validation, public-API comparison, and two adversarial
      reviews passed. Netclaw PR #1855 consumes this package through the closed
      joined-argument, value-domain, redirect-source, and redirect-analysis
      alternatives. Netclaw PR #1857 closes the downstream acceptance gate with
      240 catalog rows and green cross-platform CI.
- [x] Published `0.3.0` stable from the reviewed release metadata in
      [PR #149](https://github.com/Aaronontheweb/ShellSyntaxTree/pull/149),
      merge `217c007a`, after Linux and native Windows CI passed. The bare
      `0.3.0` tag was derived from the merged MSBuild `Version`; successful
      [tag workflow run 31444933607](https://github.com/Aaronontheweb/ShellSyntaxTree/actions/runs/31444933607)
      published the package and symbols package. The
      [NuGet package](https://www.nuget.org/packages/ShellSyntaxTree/0.3.0)
      resolves as version `0.3.0` for both target frameworks, and the
      [GitHub release](https://github.com/Aaronontheweb/ShellSyntaxTree/releases/tag/0.3.0)
      is a non-draft, non-prerelease release with both `.nupkg` and `.snupkg`
      assets. Netclaw PRs #1855 and #1857 had already completed the downstream
      migration and cross-platform acceptance gates.
- [x] Replace the pre-alpha consumer preview with the v0.3 occurrence-based
      authorization loop and separate syntax-display guidance. Document exact,
      finite, pattern, unknown, joined-cwd, redirect, incomplete-result,
      equality, hashing, `ToString()`, serialization, and `Clauses` migration
      behavior in the guide and release notes; direct the README quick start
      to `Commands` and the full guide.
- [x] Re-closed the v0.3 public-API compatibility gate for the additive
      `PwshDialect` enum and options property. Existing reflection
      snapshots pin the exact exported types, members, enum ordering,
      reference nullability, defaults, parser constructors and entry points,
      and fixed limits against the shared and PowerShell specifications.
      Additional tests pin generated equality and `ToString()` participation
      plus equal-record hash consistency, demonstrate that default JSON is not
      a polymorphic round-trip contract, and make every policy-sensitive
      unknown numeric enum value detectable so consumers can reject it.
      The dialect slice adds explicit default, unknown-value, record equality,
      hash, `ToString()`, propagation, and parser-behavior coverage. A
      `dotnet-inspect` assembly diff against the published 0.3.0-alpha.4 package
      reports exactly two additive changes on both `net8.0` and
      `netstandard2.0`: the enum and one options member, with no breaking change.
- [x] Expose public Bash heredoc body, delimiter, expansion, tab-stripping, and
      completeness facts from the delivered bounded grammar. Direct tests pin
      literal and expanding delimiters, every supported substitution command,
      exact empty/LF/CRLF/tabbed body provenance, the retained v0.2 redirect,
      and nullable source offsets for decoded `bash -c` payloads.
- [x] Close the Bash variable-attribute hidden-execution boundary. Named
      parameter dereferences now require a caller-proved isolated initial
      state, with that proof propagated into substitutions and subshells.
      Direct and recursively dispatch-wrapped `eval`, source/dot, trap,
      variable-attribute and variable-mutating builtins, plus `printf -v`,
      fail atomically until their state effects are modeled. Native Bash
      oracles pin nameref-deferred and integer-assignment execution, and the
      executable corpus carries isolated, unknown-state, and wrapper cases.
- [x] Close Bash command-resolution mutation globally. Reject `exec` and
      mutating or ambiguous `hash`, `alias`, `unalias`, `shopt`, and `enable`
      forms before later occurrences can inherit a false executable identity; retain only
      exact static query grammar and pin native-shell behavior, recursive
      wrappers, and executable corpus cases. Entries 273-280 pin the mutation
      and reserved-form boundaries, including unmodeled `time`, negation,
      coprocess, and current-shell brace-group syntax, which fail closed rather
      than flattening nested execution into apparent ordinary verb chains.
- [x] Promote the Bash command-resolution mutation cases into Netclaw's strict
      allow/prompt/deny matrix before the downstream approval-fatigue gate.
- [x] Add the separately tested Bash `<<<` here-string redirect slice with
      bounded operand analysis and trailing-newline semantics. Default and
      numeric sources publish complete non-path facts; exact and finite data
      include Bash's appended newline, unknown data remains structurally
      complete, and every supported `$()` command stays independently visible.
      Malformed operators fail atomically, while native Bash oracles pin
      newline and no-field-splitting behavior.

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

- After stable v0.3, consider shared Bash/PowerShell analysis extraction only
  where the two delivered implementations prove identical behavior.
- Add Bash and PowerShell condition loops and branches in shell-specific
  vertical slices; keep `case`, `switch`, process substitution, background
  lists, and arithmetic independently gated.
- Add or expand exact optional-module Start-ThreadJob and deferred breakpoint,
  event, and argument-completion receiver semantics only when consumer demand
  justifies a pinned contract; existing conservative recognition may remain.
- Expand the Web sample with curated Bash and PowerShell inputs and
  snapshot-tested deterministic Mermaid views produced from canonical
  projections. This remains part of the broader product follow-up, not the
  stable-v0.3 package or Netclaw migration gate.
