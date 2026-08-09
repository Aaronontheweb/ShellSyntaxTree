## 1. Contract Lock

- [x] 1.1 Review Appendix A and lock the public syntax-root, node, occurrence, ancestry, value-domain, and redirect-detail type names and members.
- [x] 1.2 Decide and document whether projections share identical `Clause` instances or guarantee value equality only.
- [x] 1.3 Lock candidate-count, structural-nesting, and existing wrapper-recursion limits with boundary scenarios.
- [x] 1.4 Review Appendix B, lock the v0.3 grammar separately for Bash and PowerShell, and park every deferred form explicitly.
- [x] 1.5 Resolve the initial pattern and divergent-cwd exposure rules, updating the bounded-analysis specification.
- [x] 1.6 Synchronize the accepted public API and shared requirements into `SPEC.md`.
- [x] 1.7 Synchronize PowerShell grammar and analysis deltas into `SPEC.POWERSHELL.md`.
- [x] 1.8 Update `PROJECT_CONTEXT.md` and `IMPLEMENTATION_PLAN.md` with the accepted v0.3 scope and delivery slices.
- [x] 1.9 Add a paired Bash and PowerShell design corpus that records current behavior, desired structure, command occurrences, bounded values, redirect facts, compatibility projections, and security invariants.
- [ ] 1.10 Promote every design case for a stable-v0.3 construct into the
  executable corpus as its production parser slice lands. Retain future-scope
  design cases as non-gating evidence rather than release work.
- [x] 1.11 Correct the PowerShell script-block boundary and lock the additive
  execution-region node, origin/phase/timing/cardinality facts, authored-versus-semantic
  ordering, command projection, and independent shell-state analysis contract
  against local PowerShell 7.6.4 oracles.
- [x] 1.12 Expand the PowerShell design corpus with direct call/dot-source,
  synchronous callback, binder phase, local/remote invocation, child
  process/runspace, initialization, proved data, and unknown receiver cases
  before production implementation. Deferred-action cases remain design-only
  evidence until separately promoted after v0.3.

## 2. Resolver Provenance Correction and Shared Preparation

- [x] 2.1 Implement shell-specific lexical fragment provenance that distinguishes literal, typed recognized-expansion, and opaque resolver input; retains transform eligibility, expansion identity, cardinality, and opaque cause; aggregates complete argument and redirect-target fragment runs; and passes explicit Bash-argument, Bash-redirect, PowerShell-native, cmdlet-Path, cmdlet-LiteralPath, and PowerShell-redirect resolver context without changing the public API.
- [x] 2.2 Add paired Bash and PowerShell shell-oracle regressions for standalone escapes, adjacent escaped values, all-static mixed quoting, within-token escapes, genuine literal-plus-expandable values, adjacent and wildcard redirect targets, runtime special/positional/numeric/Unicode variables, incomplete and escaped-literal braced interpolation, Bash provider-looking literals, and PowerShell native-versus-cmdlet, Path-versus-LiteralPath, and redirect-context divergence; require exact compatibility path results when every fragment, binding fact, and required resolver fact is exact, otherwise fail closed.
- [x] 2.3 Implement issue #69's shell-neutral native argument-fragment classifier with explicit Bash and PowerShell adapters that preserve the new provenance.
- [x] 2.4 Prove raw spelling, decoded logical values, source spans, and unaffected classifications remain unchanged; document each oracle-proved false exact, `Glob`, `Tilde`, provider, path, or avoidable `DynamicSkip` compatibility correction.
- [x] 2.5 Audit duplicated Bash and PowerShell path-normalization helpers and extract only rules with identical shell semantics.
- [x] 2.6 Run Release build, full tests, header verification, and the adversarial security corpus for the completed preparation.

## 3. Structural and Projection Skeleton

- [x] 3.1 Add the locked public syntax-node hierarchy and defaults to the public API snapshot.
- [x] 3.2 Add the locked command-occurrence, role, ancestry, completeness, and analysis records to the public API snapshot.
- [x] 3.3 Add `ParsedCommand.Syntax` and `ParsedCommand.Commands` while retaining all v0.2 members.
- [x] 3.4 Build a library-owned traversal that emits each simple command occurrence exactly once in deterministic source order.
- [x] 3.5 Build the conservative `Clauses` compatibility flattener without inventing cross-structure compound operators.
- [x] 3.6 Adapt the existing Bash grammar to emit the structural model with no newly supported syntax.
- [x] 3.7 Adapt the existing PowerShell grammar to emit the structural model with no newly supported syntax.
- [x] 3.8 Add tests proving existing parser inputs retain their v0.2 leaf and compatibility results, except for explicitly promoted v0.3 fail-closed cases.
- [x] 3.9 Add corpus expectations for syntax shape, occurrences, roles, and completeness for existing constructs.
- [x] 3.10 Implement Bash `$()` discovery in supported argument words, redirect values, iterables, and expanding heredoc bodies; retain literal/escaped spellings and fail closed on command-name substitutions, legacy backticks, or incomplete interiors.
  - [x] 3.10a Implement the simple-command argument and redirect-target slice, including comment-safe boundaries and fail-closed unsupported interiors.
  - [x] 3.10b Implement the bounded expanding-heredoc slice with quote-removed delimiters, literal quoted/escaped bodies, tab stripping, exact provenance, and fail-closed unsupported header/body forms.
  - [x] 3.10c Require proved Bash variable-attribute state for simple named-parameter dereferences and fail closed globally on the locked unmodeled execution-bearing builtin catalog, including exact dispatch-wrapper bypasses.
  - [x] 3.10d Fail closed globally on Bash `exec`, mutating or ambiguous `hash`, alias, `shopt`, and `enable` forms, and unmodeled reserved execution prefixes/groups while retaining only exact static query grammar.
- [ ] 3.11 Implement PowerShell `$()` discovery in supported words, redirect values, foreach expressions, call-operator dynamic identities, standalone expression statements, double-quoted strings, and expandable here-strings; never invent invocation from standalone output, retain literal/escaped spellings, and fail closed on trailing command-style arguments, call-operator script blocks, or unsupported execution-bearing `@()` / `@{}` forms.
  - [x] 3.11a Implement words, redirect values, call-operator dynamic identities, standalone statements, expandable strings/here-strings, and parent-versus-child host payload provenance; fail closed on arbitrary expression values and unsupported execution-bearing `@()` / `@{}` forms.
- [x] 3.12 Pin substitution parentage, authored sibling indices, innermost-first ordering, Bash-isolated versus PowerShell-current-scope state, unknown-state propagation, nesting/depth limits, and incomplete dynamic identities in direct tests.
  - [x] 3.12a Pin the Bash argument/redirect slice, isolated cwd behavior, wrapper provenance, and the shared structural-depth budget.
  - [x] 3.12b Pin the PowerShell simple-command slice, current-scope exact and unknown cwd propagation, parent/child wrapper provenance, expression boundaries, and the shared structural-depth budget.
  - [x] 3.12c Pin expanding-heredoc sibling/nested ordering, exact spans, isolated state, delimiter modes, escape parity, depth limits, and atomic failure.
  - [x] 3.12d Pin default-versus-isolated parameter dereferences, nameref and integer hidden execution, reachable mutation invalidation, substitution/subshell scope boundaries, and direct/wrapped execution-bearing builtins with native Bash oracles.
  - [x] 3.12e Pin Bash command-resolution mutation, reserved-prefix/group rejection, and exact-query boundaries with direct tests, recursive wrapper cases, and native Bash oracles.
- [ ] 3.13 Promote ordinary, multiple, nested, iterator, redirect, quoted, escaped, stateful, malformed, and hidden-execution substitution cases into both executable corpora and the Netclaw approval matrix.
  - [x] 3.13a Promote the Bash ordinary, multiple, nested, redirect, quoted, escaped, stateful, malformed, and hidden-execution cases into its executable corpus.
  - [x] 3.13b Promote the PowerShell ordinary, multiple, nested, redirect, quoted, escaped, stateful, malformed, expression-boundary, and hidden-execution cases into its executable corpus.
  - [x] 3.13c Promote expanding, literal, tab-stripped, multiple, and malformed Bash heredoc cases with full structural expectations into the executable corpus.
  - [ ] 3.13d Promote sanitized nameref, unknown-state dereference, and execution-bearing builtin failures into the Bash executable corpus and Netclaw strict matrix.
  - [ ] 3.13e Promote sanitized Bash hash, alias/option, exec, builtin-enable, and reserved execution syntax failures into the executable corpus and Netclaw strict matrix.
- [x] 3.14 Add `ExecutionRegionSyntax`, its four discriminant enums,
  `SimpleCommandSyntax.ExecutionRegions`, and appended occurrence/ancestry enum
  members to the public API and snapshot without changing existing enum values.
- [x] 3.15 Extend the structural projector, compatibility flattener, depth
  validation, cloning, and corpus DTOs so direct and command-owned execution
  regions emit every body command exactly once in the locked order.

## 4. Explicit Redirect Semantics

- [x] 4.1 Add the locked redirect operation and target-analysis types while retaining compatibility redirect members.
- [x] 4.2 Classify Bash descriptor duplication, close, and move as static only for the complete literal descriptor grammar.
- [x] 4.3 Keep variable-driven and otherwise computed Bash descriptor targets unknown or incomplete.
- [x] 4.4 Lex and classify Bash `&>` and `&>>` independently from background-list operators.
- [x] 4.5 Map existing PowerShell stream redirects into the shared explicit model without losing shell-specific stream identity.
- [x] 4.6 Add paired direct tests and corpus cases for static, dynamic, malformed, multiple, combined, and file redirects.
  - [x] 4.6a Add Bash direct and executable-corpus cases for static duplicate,
    close, and move; computed descriptor targets; combined output overwrite and
    append; ordinary file redirects; arbitrary numeric source descriptors; and
    overflow/malformed fail-closed boundaries. LF/CRLF continuations at the
    descriptor boundary and inside multi-digit sources follow Bash's pre-token
    removal semantics. Executable-corpus cases pin malformed atomic failure and
    independent occurrence facts for multiple redirects.
  - [x] 4.6b Add the paired PowerShell direct and executable-corpus cases when
    task 4.5 maps its stream model.
- [x] 4.7 Verify the explicit model removes the need for raw-prefix inference in a Netclaw integration test.
  - Netclaw PR
    [#1835](https://github.com/netclaw-dev/netclaw/pull/1835) consumes typed
    descriptor duplicate, move, close, combined-output, and file-target facts.
    It removes the temporary raw descriptor-prefix workaround and pins static,
    computed, malformed, and future-enum forms in focused security tests. Its
    cross-platform fixtures preserve production symlink rejection rather than
    weakening that fail-closed check for macOS temporary-path aliases.

## 5. Consumer Migration Baseline

- [x] 5.1 Rewrite the production-shaped consumer loop in `docs/CONSUMER_GUIDE.md` to enumerate every command occurrence.
  - [x] 5.1a Document the PowerShell `$()` occurrence ordering, standalone/call-operator distinction, parent-versus-child host payload provenance, and completeness-versus-value-safety contract.
- [x] 5.2 Document syntax-tree display traversal separately from authorization traversal.
- [x] 5.3 Document exact, finite, pattern, unknown, joined-state, redirect, and incomplete-result handling.
- [x] 5.4 Document record equality, hashing, `ToString()`, serialization, and `Clauses` compatibility effects.
- [x] 5.5 Update the README getting-started and migration examples to direct v0.3 consumers to the command-occurrence API and full consumer guide.
- [x] 5.6 Publish a 0.3.0 prerelease containing the contracted structural,
  substitution, redirect, Bash `for`, and PowerShell `foreach` behavior before
  the downstream migration gate.
- [x] 5.7 Migrate Netclaw's existing-command analysis to the occurrence and redirect APIs behind focused regression tests.
  - Netclaw PR
    [#1835](https://github.com/netclaw-dev/netclaw/pull/1835) migrates the Bash
    approval path to `ParsedCommand.Commands`, complete ancestry and cwd facts,
    compatibility argument/path facts, and explicit redirects. Unknown or
    incomplete identity, ancestry, cwd, or redirect facts and dynamic or
    unresolved compatibility arguments still fail closed. The 166-case
    approval matrix and focused security tests cover
    ordinary commands, wrappers, pipelines, cwd attribution, redirects,
    symlinks, wrapper-prefix executables, and hard-deny precedence.

## 6. Bash For-In Vertical Slice

- [x] 6.1 Parse Bash `for name in literal...; do ...; done` into the locked structural nodes.
- [x] 6.2 Emit condition-free loop-body occurrences and conservative compatibility clauses.
- [x] 6.3 Derive exact and finite literal binding domains within the locked candidate cap.
- [x] 6.4 Substitute a bounded binding only where Bash quoting proves argument boundaries.
- [ ] 6.5 Propagate and conservatively join cwd and supported binding state across zero-or-more loop execution.
  - [x] 6.5a Lock outcome-partitioned Bash flow, failure-aware `cd`,
    conservative `lastpipe` / `pipefail`, ordered iteration plans,
    decoded-wrapper inheritance, and dynamic fail-closed compatibility
    sanitization before implementing the state pass.
  - [x] 6.5b Apply outcome-sensitive cwd analysis to existing Bash lists,
    pipelines, substitutions, subshells, and decoded wrappers; rebase exact
    compatibility paths and retain `<dynamic-cwd>` after conservative joins.
  - [ ] 6.5c Carry ordered loop binding and cwd state through zero-or-more
    iterations, then remove the temporary loop-mutation rejection.
    - [x] 6.5c.1 Correct the contract after adversarial review: require an
      explicit isolated initial-state mode and supported scalar-name boundary;
      make the analyzer own persistent bindings, parameterized ordered plans,
      full argument provenance/effective-argv transfer, occurrence-fact joins,
      and unreachable exit partitions.
    - [x] 6.5c.2 Make the analyzer own persistent bindings, ordered and empty
      iteration, visit-joined effective arguments, unreachable exit partitions,
      substitution inheritance, and explicit decoded-wrapper remapping. Pin
      special-name rejection, 32/33 visit boundaries, duplicate order,
      zero-iteration state, nested correlation, substitutions, pipelines, and
      wrapper provenance in unit tests and the Bash corpus. Use bounded
      fixed-point widening for unknown cardinality and fail atomically after
      4096 total loop-body transitions.
    - [x] 6.5c.3 Re-parse each visit's complete effective argv for state
      transfers, including loop-derived `cd` options and wrapped dispatch;
      carry those transfers through the bounded fixed point, then remove only
      the temporary mutation rejections whose transfers are fully modeled.
  - The analyzer now publishes exact incoming cwd for reached loop occurrences
    when no modeled transfer can disagree. Complete effective `cd` argv,
    recursive exact `command` / `builtin` dispatch, failure-only invalid forms,
    physical-path sanitation, and persistent post-loop binding protection are
    implemented. It still rejects every unmodeled shell-state mutation,
    control transfer, nested active-binding reuse, and dynamic dispatch.
- [x] 6.6 Cover empty iterables, separators, multiline bodies, redirects, pipelines, nested loops, and wrapper boundaries.
  - Direct tests, executable corpus, and native Bash oracles cover each
    interaction. Redirect-bearing occurrences remain incomplete until the
    separately tracked explicit redirect-analysis slice lands.
- [x] 6.7 Add adversarial cases for option injection, mutation, unquoted expansion, indirect expansion, substitutions, and cap overflow.
  - The executable corpus includes indirect and parameter-operator rejection,
    loop-body substitution, and atomic transition-budget overflow; native
    oracles pin the shell semantics behind the conservative boundaries.
- [ ] 6.8 Add sanitized Bash corpus entries and Netclaw allow/prompt/deny integration cases.

## 7. PowerShell Foreach Vertical Slice

- [x] 7.1 Parse PowerShell `foreach` with literal scalar and array iterables into the locked structural nodes.
  - Direct tests pin exact spans, nested structure, decoded-wrapper nullable
    spans, contextual alias collisions, statement boundaries, malformed forms,
    and the shared structural-depth cap.
- [x] 7.2 Emit iterator and loop-body occurrences plus conservative compatibility clauses.
  - Iterator pipelines and direct `$()` are recursively visible with authored
    roles and ancestry. The initial structural slice emitted incomplete body
    occurrences until tasks 7.3 and 7.4 added binding and runspace analysis.
    Alpha.3 still leaves default-mode occurrences incomplete for ambient
    resolution; task 7.2c corrects that behavior. Explicit iterator/body state
    mutation and dynamic invocation remain strict, and isolated child-host
    loops do not taint their outer continuation.
- [x] 7.2a Add the explicit `PwshInitialStateMode` contract and safe default
  before value analysis. Lock the constrained noninteractive no-profile host
  and module baseline, current-runspace sharing, child-host noninheritance,
  mutation invalidation, and ambient typed/read-only binding hazards in the
  canonical specs and case-specific design corpus. The ambient-resolution
  portion of this completed alpha.3 design is superseded by 7.2b; the public API
  remains compatible.
- [x] 7.2b Correct the approval boundary so PowerShell matches Bash's
  authored-command model: ambient runtime resolution is outside occurrence
  completeness, while explicit source mutations, computed identities, hidden
  execution, and unsupported syntax remain strict. Preserve the existing
  `PwshInitialStateMode` API shape.
- [ ] 7.2c Implement authored-command completeness for default-mode static
  PowerShell commands, pipelines, decoded children, and known script-block
  receivers without weakening explicit mutation or dynamic-execution checks.
- [x] 7.3 Derive exact and finite string domains without treating pipeline objects as literal strings.
  - The PowerShell-specific value pass consumes lexer provenance, composes
    case-insensitive distinct active bindings, publishes bounded literal
    scalar/array domains under the alpha.3 isolated-runspace contract,
    and collapses object, null, unsupported, and over-cap values to Unknown.
    The internal plan retains ordered duplicate visits and an exact authored
    count separately from its public set summary; unknown object iterables are
    zero-or-more. Reserved or stateful built-in binding collisions fail
    atomically; a pinned documented preference inventory covers lazy names and a
    live PowerShell 7.x oracle guards the fresh-host inventory. Decoded
    child hosts, current-runspace wrappers, redirect values, same-name nested
    overwrites, and post-loop state remain conservative for tasks 7.4-7.6. The
    authored-command correction leaves this effective-value contract intact;
    isolated mode no longer implies a pinned command-resolution baseline.
- [x] 7.4 Propagate PowerShell scope and location state according to the locked statement semantics.
  - The PowerShell-specific abstract-state pass now owns case-insensitive
    persistent bindings, ordered/empty/zero-or-more execution, occurrence joins,
    failure-aware `Set-Location`, current-runspace `$()` propagation, child-host
    isolation, target-aware fail-closed provider mutation, and the shared
    4096-transition budget. Common and PowerShell 7 command-specific
    variable-writing parameters, accepted abbreviations and inline values, and
    opaque splats invalidate later observing proofs; pipeline writers fail
    atomically until pipeline state propagation is modeled, and specialized
    `Set-Location` success/failure transfers compose rather than bypass those
    writer effects. Alternate
    PowerShell parameter dashes and unsupported module-qualified cmdlets fail
    structured parses atomically. Computed `Invoke-Expression` payloads poison
    later current-runspace binding, command-resolution, and cwd facts, and fail
    atomically when their loop transfer cannot be modeled. Loop parser attribution is
    cloned so unreachable bodies do not leak and possibly reached location
    mutation cannot retain a false exact compatibility cwd. Outcome projection
    rebases exact failure continuations, including decoded child hosts, and
    sanitizes unknown joins. Broader wrapper,
    pipeline, alias/cmdlet/native, redirect, and adversarial matrices remain in
    tasks 7.5-7.7.
- [ ] 7.5 Cover aliases, cmdlets, native commands, nested loops, pipelines, script blocks, and wrapper boundaries.
  - [x] 7.5a Implement the version-pinned PowerShell 7 script-block receiver and
    parameter-binding catalog, including aliases, supported module-qualified
    identities, parameter abbreviations/inline values, positional binding,
    parameter sets, `ScriptBlock[]`, and ForEach-Object Begin/Process/End
    assignment.
  - [ ] 7.5b Implement direct `& {}` and `. {}` plus synchronous current-runspace
    regions for ForEach-Object, Where-Object, Measure-Command, Trace-Command,
    in-process Invoke-Command, and New-Module with shell-specific state flow.
  - [x] 7.5c Implement Start-Job and initialization, ForEach-Object -Parallel,
    remote/session Invoke-Command, and `-AsJob` child process/runspace
    boundaries conservatively. Additional optional-module `Start-ThreadJob`
    proof is not a stable-v0.3 requirement; unproved forms follow the unknown-
    receiver rule.
  - [x] 7.5e Keep proved non-executing script-block data opaque; over-approximate
    unknown receivers/bindings as unknown incomplete regions; fail atomically
    on unsupported interiors or state transfers.
    - Supported non-pipeline bodies remain visible and incomplete. An interior
      pipeline whose stage identity is unproved fails the whole parse atomically
      with empty authorization projections.
    - Alpha.3 kept canonical, alias, and supported module-qualified
      `Write-Output` receivers opaque only under constrained state. Task 7.2c
      supersedes the ambient-state part: static authored receivers stay data in
      default mode, while a matching observed source-level mutation still
      downgrades to an unknown incomplete region. Executable-corpus entries pin
      proved data, an unknown receiver, proved local `Invoke-Command`, the exact
      module-qualified-looking mutation boundary, and canonical-target
      invalidation through `echo`.
  - [ ] 7.5f Pin authored projection order separately from semantic phase order,
    exact host element coordinates, nested regions, wrappers, pipelines, loops,
    and the 16-container depth boundary.
  - [x] 7.5g Retain explicit atomic-failure behavior for direct-block arguments
    and leading `param(...)` declarations. Declaration and argument-binding
    grammar is not required for stable v0.3.
- [ ] 7.6 Add adversarial cases for object-valued iterables, mutation, dynamic invocation, splatting, and cap overflow.
- [ ] 7.7 Add PowerShell corpus entries, live `pwsh` oracle coverage, and Netclaw integration cases.
  - `PwshCorpusTool` now supports case-specific `PwshInitialStateMode`; keep
    promoting the remaining stable execution-region and adversarial cases into
    its generated manifest, then add the Netclaw PowerShell policy matrix.

## 10. Heredoc / Here-String Slice

- [x] 10.1 Specify heredoc delimiter adjacency and quoting, expansion mode, body provenance, substitutions, tab stripping, completeness, and Bash here-string semantics.
- [x] 10.2 Preserve existing `<<` / `<<-` behavior and fix quoted-delimiter adjacency without regressing the v0.2 compatibility redirect.
- [x] 10.3 Add explicit heredoc delimiter/body/expansion/completeness facts and surface every supported substitution command.
- [x] 10.4 Add Bash `<<<` here-string tokenization, explicit redirect facts,
  bounded operand analysis, and trailing-newline semantics.
  - Longest-match lexer and occurrence-level tests cover default and numeric
    sources, exact empty and literal data, unknown values, visible command
    substitutions, malformed forms, and finite loop-bound operands. Native
    Bash oracles pin the appended newline and suppression of field splitting.
- [x] 10.5 Add direct, malformed, quoted/unquoted, tab-stripped, dynamic, and substitution-bearing corpus cases plus real-Bash parse-only validation.
  - [x] 10.5a Add direct, executable-corpus, real-Bash output, and real-Bash parse-only coverage for the bounded substitution-discovery slice, explicit redirect facts, and the full heredoc matrix.

## 11. Verification and Release

- [x] 11.1 Add public API default-value, equality, serialization, and unknown-enum compatibility tests.
  - `V03PublicApiSnapshotTests` pins every additive record default and enum
    zero value, proves `Syntax` and `Commands` participate in generated record
    equality and `ToString()` plus equal-record hash consistency, demonstrates
    that default JSON is not a polymorphic round-trip contract, and makes every
    policy-sensitive unknown numeric enum value detectable for consumer rejection.
- [ ] 11.2 Assert every supported executable region appears exactly once and every unsupported executable region makes the result unparseable.
- [x] 11.3 Run the complete Bash and PowerShell corpus suites plus the PII audit.
- [x] 11.4 Run `dotnet build -c Release`, `dotnet test -c Release`, `dotnet pack -c Release`, and header verification.
- [x] 11.5 Validate the public API field-for-field against the synchronized shared and PowerShell specifications.
  - `PublicApiSnapshotTests` and `V03PublicApiSnapshotTests` enumerate the exact
    exported namespace, type family, exact property sets, parser constructors
    and entry points, enum ordering, reference nullability, defaults, and fixed
    limits synchronized into `SPEC.md` and `SPEC.POWERSHELL.md`.
- [ ] 11.6 Validate Netclaw's ordinary-command, redirect, bounded-loop, and unknown-value approval matrices against the prerelease package.
- [x] 11.7 Update release notes and remove Netclaw's temporary descriptor workaround only after explicit redirect integration is live.
  - The `0.3.0-alpha` release notes document the explicit redirect model. The
    workaround was removed only in the reviewed Netclaw migration after the
    prerelease package was published.
- [ ] 11.9 Promote stable 0.3.0 only after Linux and Windows CI, package publication, and downstream acceptance succeed.

## Post-v0.3 Backlog (Non-Gating)

These are worthwhile follow-ups, not unfinished tasks in this change:

- Compare the working Bash and PowerShell analyzers and extract only behavior
  proven identical; keep shell token consumption, quoting, scoping, and
  expression semantics behind separate adapters.
- Specify and implement Bash `while`, `until`, `if`, and `elif` plus PowerShell
  `while`, `if`, and `elseif` in shell-specific vertical slices with paired
  execution-accounting scenarios.
- Specify Bash `case` and PowerShell `switch` only after their pattern and
  branch-selection uncertainty is bounded.
- Specify process-substitution values and command discovery, background-list
  concurrency/state, and C-style/arithmetic hidden execution before enabling
  those constructs.
- Add exact optional-module `Start-ThreadJob` and deferred breakpoint, event,
  and argument-completion receiver semantics only when consumer demand
  justifies a pinned runtime/module contract. Existing conservative recognition
  may remain; unproved forms keep supported non-pipeline bodies unknown and
  incomplete, while unproved interior pipelines fail atomically.
- Keep URL-versus-glob, environment-assignment, and multiline reproduction
  work in executable-aware consumer issues unless an exact missing lexical
  fact is demonstrated.
- Expand the Web sample with curated Bash and PowerShell inputs and
  snapshot-tested deterministic Mermaid diagrams from canonical projections.
  Escape arbitrary labels and emit no raw HTML; this showcase does not gate
  package or Netclaw delivery.
