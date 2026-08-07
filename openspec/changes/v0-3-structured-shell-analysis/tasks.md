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
- [ ] 1.10 Promote each design case into the executable corpus as its production parser slice lands.

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
- [ ] 3.10 Implement Bash `$()` discovery in supported argument words, redirect values, iterables, and expanding heredoc bodies; retain literal/escaped spellings and fail closed on command-name substitutions, legacy backticks, or incomplete interiors.
  - [x] 3.10a Implement the simple-command argument and redirect-target slice, including comment-safe boundaries and fail-closed unsupported interiors.
  - [x] 3.10b Implement the bounded expanding-heredoc slice with quote-removed delimiters, literal quoted/escaped bodies, tab stripping, exact provenance, and fail-closed unsupported header/body forms.
- [ ] 3.11 Implement PowerShell `$()` discovery in supported words, redirect values, foreach expressions, call-operator dynamic identities, standalone expression statements, double-quoted strings, and expandable here-strings; never invent invocation from standalone output, retain literal/escaped spellings, and fail closed on trailing command-style arguments, call-operator script blocks, or unsupported execution-bearing `@()` / `@{}` forms.
  - [x] 3.11a Implement words, redirect values, call-operator dynamic identities, standalone statements, expandable strings/here-strings, and parent-versus-child host payload provenance; fail closed on arbitrary expression values and unsupported execution-bearing `@()` / `@{}` forms.
- [ ] 3.12 Pin substitution parentage, authored sibling indices, innermost-first ordering, Bash-isolated versus PowerShell-current-scope state, unknown-state propagation, nesting/depth limits, and incomplete dynamic identities in direct tests.
  - [x] 3.12a Pin the Bash argument/redirect slice, isolated cwd behavior, wrapper provenance, and the shared structural-depth budget.
  - [x] 3.12b Pin the PowerShell simple-command slice, current-scope exact and unknown cwd propagation, parent/child wrapper provenance, expression boundaries, and the shared structural-depth budget.
  - [x] 3.12c Pin expanding-heredoc sibling/nested ordering, exact spans, isolated state, delimiter modes, escape parity, depth limits, and atomic failure.
- [ ] 3.13 Promote ordinary, multiple, nested, iterator, redirect, quoted, escaped, stateful, malformed, and hidden-execution substitution cases into both executable corpora and the Netclaw approval matrix.
  - [x] 3.13a Promote the Bash ordinary, multiple, nested, redirect, quoted, escaped, stateful, malformed, and hidden-execution cases into its executable corpus.
  - [x] 3.13b Promote the PowerShell ordinary, multiple, nested, redirect, quoted, escaped, stateful, malformed, expression-boundary, and hidden-execution cases into its executable corpus.
  - [x] 3.13c Promote expanding, literal, tab-stripped, multiple, and malformed Bash heredoc cases with full structural expectations into the executable corpus.

## 4. Explicit Redirect Semantics

- [x] 4.1 Add the locked redirect operation and target-analysis types while retaining compatibility redirect members.
- [ ] 4.2 Classify Bash descriptor duplication, close, and move as static only for the complete literal descriptor grammar.
- [ ] 4.3 Keep variable-driven and otherwise computed Bash descriptor targets unknown or incomplete.
- [ ] 4.4 Lex and classify Bash `&>` and `&>>` independently from background-list operators.
- [ ] 4.5 Map existing PowerShell stream redirects into the shared explicit model without losing shell-specific stream identity.
- [ ] 4.6 Add paired direct tests and corpus cases for static, dynamic, malformed, multiple, combined, and file redirects.
- [ ] 4.7 Verify the explicit model removes the need for raw-prefix inference in a Netclaw integration test.

## 5. Consumer Migration Baseline

- [ ] 5.1 Rewrite the production-shaped consumer loop in `docs/CONSUMER_GUIDE.md` to enumerate every command occurrence.
  - [x] 5.1a Document the PowerShell `$()` occurrence ordering, standalone/call-operator distinction, parent-versus-child host payload provenance, and completeness-versus-value-safety contract.
- [ ] 5.2 Document syntax-tree display traversal separately from authorization traversal.
- [ ] 5.3 Document exact, finite, pattern, unknown, joined-state, redirect, and incomplete-result handling.
- [ ] 5.4 Document record equality, hashing, `ToString()`, serialization, and `Clauses` compatibility effects.
- [ ] 5.5 Update the README getting-started and migration examples to direct v0.3 consumers to the command-occurrence API and full consumer guide.
- [ ] 5.6 Publish a 0.3.0 prerelease containing the structural API before enabling control flow.
- [ ] 5.7 Migrate Netclaw's existing-command analysis to the occurrence and redirect APIs behind focused regression tests.

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
    roles and ancestry. Every loop-body occurrence remains incomplete until
    tasks 7.3 and 7.4 prove binding values and runspace state; recognized
    iterator/body state or command-resolution mutation and dynamic invocation
    fail atomically. Current-scope continuations after a loop remain incomplete;
    isolated child-host loops do not taint their outer continuation.
- [x] 7.2a Add the explicit `PwshInitialStateMode` contract and safe default
  before value analysis. Lock the constrained noninteractive no-profile host
  and module baseline, current-runspace sharing, child-host noninheritance,
  mutation invalidation, and ambient typed/read-only binding hazards in the
  canonical specs and case-specific design corpus.
- [x] 7.3 Derive exact and finite string domains without treating pipeline objects as literal strings.
  - The PowerShell-specific value pass consumes lexer provenance, composes
    case-insensitive distinct active bindings, publishes bounded literal
    scalar/array domains only under the explicit isolated-runspace contract,
    and collapses object, null, unsupported, and over-cap values to Unknown.
    The internal plan retains ordered duplicate visits and an exact authored
    count separately from its public set summary; unknown object iterables are
    zero-or-more. Reserved or stateful built-in binding collisions fail
    atomically; a pinned documented preference inventory covers lazy names and a
    live PowerShell 7.x oracle guards the fresh-host inventory. Decoded
    child hosts, current-runspace wrappers, redirect values, same-name nested
    overwrites, and post-loop state remain conservative for tasks 7.4-7.6.
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
- [ ] 7.6 Add adversarial cases for object-valued iterables, mutation, dynamic invocation, splatting, and cap overflow.
- [ ] 7.7 Add PowerShell corpus entries, live `pwsh` oracle coverage, and Netclaw integration cases.
  - Add case-specific `PwshInitialStateMode` support to `PwshCorpusTool` before
    folding isolated-state entries 362+ into its generated manifest.

## 8. Proven Shared Analysis Extraction

- [ ] 8.1 Compare the two working loop implementations and inventory only behaviorally identical analysis steps.
- [ ] 8.2 Extract shared command-occurrence traversal without coupling shell token consumption.
- [ ] 8.3 Extract the value-domain lattice, combination cap, and unknown fallback.
- [ ] 8.4 Extract conservative sequential and branch-state join primitives used identically by both shells.
- [ ] 8.5 Keep shell-specific iterable, quoting, scoping, expression, and parser code behind explicit adapters.
- [ ] 8.6 Re-run both complete corpora to prove the extraction is behavior-preserving.

## 9. Condition Loops and Branches

- [ ] 9.1 Add Bash `while` and `until` with condition and body command occurrences.
- [ ] 9.2 Add Bash `if` / `elif` / `else` with conservative branch-state joins.
- [ ] 9.3 Defer Bash `case` until after stable v0.3 and add it only after pattern and branch-selection uncertainty is specified.
- [ ] 9.4 Add PowerShell `while` with the locked condition-pipeline boundary; defer `do` forms until after stable v0.3.
- [ ] 9.5 Add PowerShell `if` / `elseif` / `else` with conservative branch-state joins.
- [ ] 9.6 Defer PowerShell `switch` until after stable v0.3 and add it only after string, regex, wildcard, and script-block modes are bounded explicitly.
- [ ] 9.7 Add paired security scenarios proving every condition and branch command remains visible.

## 10. Heredoc / Here-String Slice and Separately Gated Follow-ups

- [x] 10.1 Specify heredoc delimiter adjacency and quoting, expansion mode, body provenance, substitutions, tab stripping, completeness, and Bash here-string semantics.
- [x] 10.2 Preserve existing `<<` / `<<-` behavior and fix quoted-delimiter adjacency without regressing the v0.2 compatibility redirect.
- [ ] 10.3 Add explicit heredoc delimiter/body/expansion/completeness facts and surface every supported substitution command.
- [ ] 10.4 Add Bash `<<<` here-string tokenization, explicit redirect facts, bounded operand analysis, and trailing-newline semantics.
- [ ] 10.5 Add direct, malformed, quoted/unquoted, tab-stripped, dynamic, and substitution-bearing corpus cases plus real-Bash parse-only validation.
  - [x] 10.5a Add direct, executable-corpus, real-Bash output, and real-Bash parse-only coverage for the bounded substitution-discovery slice; explicit redirect facts and the full heredoc matrix remain pending.
- [ ] 10.6 After stable v0.3, specify process-substitution command discovery and the unknown produced descriptor/path value before enabling it.
- [ ] 10.7 After stable v0.3, specify background-list concurrency, ordering, and shell-state boundaries before enabling single `&`.
- [ ] 10.8 Specify C-style loop and arithmetic hidden-execution behavior before enabling either construct.
- [ ] 10.9 Keep URL-versus-glob and environment-assignment approval behavior in executable-aware consumer issues unless a shell lexical fact is missing.
- [ ] 10.10 Reproduce multiline quoted-argument reports against exact parser input before assigning a parser change.

## 11. Verification and Release

- [ ] 11.1 Add public API default-value, equality, serialization, and unknown-enum compatibility tests.
- [ ] 11.2 Assert every supported executable region appears exactly once and every unsupported executable region makes the result unparseable.
- [ ] 11.3 Run the complete Bash and PowerShell corpus suites plus the PII audit.
- [ ] 11.4 Run `dotnet build -c Release`, `dotnet test -c Release`, `dotnet pack -c Release`, and header verification.
- [ ] 11.5 Validate the public API field-for-field against the synchronized shared and PowerShell specifications.
- [ ] 11.6 Validate Netclaw's ordinary-command, redirect, bounded-loop, and unknown-value approval matrices against the prerelease package.
- [ ] 11.7 Update release notes and remove Netclaw's temporary descriptor workaround only after explicit redirect integration is live.
- [ ] 11.8 Expand the Web sample with curated complex Bash and PowerShell inputs and snapshot-tested deterministic Mermaid diagrams produced only from canonical syntax, occurrence, and compatibility projections; cover ancestry, redirects, and fail-closed results, escape arbitrary shell labels safely, and emit no raw HTML.
- [ ] 11.9 Promote stable 0.3.0 only after Linux and Windows CI, package publication, and downstream acceptance succeed.
