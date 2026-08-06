## 1. Contract Lock

- [ ] 1.1 Review Appendix A and lock the public syntax-root, node, occurrence, ancestry, value-domain, and redirect-detail type names and members.
- [ ] 1.2 Decide and document whether projections share identical `Clause` instances or guarantee value equality only.
- [ ] 1.3 Lock candidate-count, structural-nesting, and existing wrapper-recursion limits with boundary scenarios.
- [ ] 1.4 Review Appendix B, lock the v0.3 grammar separately for Bash and PowerShell, and park every deferred form explicitly.
- [ ] 1.5 Resolve the initial pattern and divergent-cwd exposure rules, updating the bounded-analysis specification.
- [ ] 1.6 Synchronize the accepted public API and shared requirements into `SPEC.md`.
- [ ] 1.7 Synchronize PowerShell grammar and analysis deltas into `SPEC.POWERSHELL.md`.
- [ ] 1.8 Update `PROJECT_CONTEXT.md` and `IMPLEMENTATION_PLAN.md` with the accepted v0.3 scope and delivery slices.
- [x] 1.9 Add a paired Bash and PowerShell design corpus that records current behavior, desired structure, command occurrences, bounded values, redirect facts, compatibility projections, and security invariants.
- [ ] 1.10 Promote each design case into the executable corpus as its production parser slice lands.

## 2. Behavior-Preserving Shared Preparation

- [ ] 2.1 Implement issue #69's shell-neutral native argument-fragment classifier with explicit Bash and PowerShell adapters.
- [ ] 2.2 Prove all existing raw, decoded, span, path, and `DynamicSkip` results remain unchanged in both corpora.
- [ ] 2.3 Audit duplicated Bash and PowerShell path-normalization helpers and extract only rules with identical shell semantics.
- [ ] 2.4 Run Release build, full tests, and header verification for the behavior-preserving refactor.

## 3. Structural and Projection Skeleton

- [ ] 3.1 Add the locked public syntax-node hierarchy and defaults to the public API snapshot.
- [ ] 3.2 Add the locked command-occurrence, role, ancestry, completeness, and analysis records to the public API snapshot.
- [ ] 3.3 Add `ParsedCommand.Syntax` and `ParsedCommand.Commands` while retaining all v0.2 members.
- [ ] 3.4 Build a library-owned traversal that emits each simple command occurrence exactly once in deterministic source order.
- [ ] 3.5 Build the conservative `Clauses` compatibility flattener without inventing cross-structure compound operators.
- [ ] 3.6 Adapt the existing Bash grammar to emit the structural model with no newly supported syntax.
- [ ] 3.7 Adapt the existing PowerShell grammar to emit the structural model with no newly supported syntax.
- [ ] 3.8 Add tests proving existing parser inputs retain their v0.2 leaf and compatibility results.
- [ ] 3.9 Add corpus expectations for syntax shape, occurrences, roles, and completeness for existing constructs.

## 4. Explicit Redirect Semantics

- [ ] 4.1 Add the locked redirect operation and target-analysis types while retaining compatibility redirect members.
- [ ] 4.2 Classify Bash descriptor duplication, close, and move as static only for the complete literal descriptor grammar.
- [ ] 4.3 Keep variable-driven and otherwise computed Bash descriptor targets unknown or incomplete.
- [ ] 4.4 Lex and classify Bash `&>` and `&>>` independently from background-list operators.
- [ ] 4.5 Map existing PowerShell stream redirects into the shared explicit model without losing shell-specific stream identity.
- [ ] 4.6 Add paired direct tests and corpus cases for static, dynamic, malformed, multiple, combined, and file redirects.
- [ ] 4.7 Verify the explicit model removes the need for raw-prefix inference in a Netclaw integration test.

## 5. Consumer Migration Baseline

- [ ] 5.1 Rewrite the production-shaped consumer loop in `docs/CONSUMER_GUIDE.md` to enumerate every command occurrence.
- [ ] 5.2 Document syntax-tree display traversal separately from authorization traversal.
- [ ] 5.3 Document exact, finite, pattern, unknown, joined-state, redirect, and incomplete-result handling.
- [ ] 5.4 Document record equality, hashing, `ToString()`, serialization, and `Clauses` compatibility effects.
- [ ] 5.5 Update the README getting-started and migration examples to direct v0.3 consumers to the command-occurrence API and full consumer guide.
- [ ] 5.6 Publish a 0.3.0 prerelease containing the structural API before enabling control flow.
- [ ] 5.7 Migrate Netclaw's existing-command analysis to the occurrence and redirect APIs behind focused regression tests.

## 6. Bash For-In Vertical Slice

- [ ] 6.1 Parse Bash `for name in literal...; do ...; done` into the locked structural nodes.
- [ ] 6.2 Emit condition-free loop-body occurrences and conservative compatibility clauses.
- [ ] 6.3 Derive exact and finite literal binding domains within the locked candidate cap.
- [ ] 6.4 Substitute a bounded binding only where Bash quoting proves argument boundaries.
- [ ] 6.5 Propagate and conservatively join cwd and supported binding state across zero-or-more loop execution.
- [ ] 6.6 Cover empty iterables, separators, multiline bodies, redirects, pipelines, nested loops, and wrapper boundaries.
- [ ] 6.7 Add adversarial cases for option injection, mutation, unquoted expansion, indirect expansion, substitutions, and cap overflow.
- [ ] 6.8 Add sanitized Bash corpus entries and Netclaw allow/prompt/deny integration cases.

## 7. PowerShell Foreach Vertical Slice

- [ ] 7.1 Parse PowerShell `foreach` with literal scalar and array iterables into the locked structural nodes.
- [ ] 7.2 Emit iterator and loop-body occurrences plus conservative compatibility clauses.
- [ ] 7.3 Derive exact and finite string domains without treating pipeline objects as literal strings.
- [ ] 7.4 Propagate PowerShell scope and location state according to the locked statement semantics.
- [ ] 7.5 Cover aliases, cmdlets, native commands, nested loops, pipelines, script blocks, and wrapper boundaries.
- [ ] 7.6 Add adversarial cases for object-valued iterables, mutation, dynamic invocation, splatting, and cap overflow.
- [ ] 7.7 Add PowerShell corpus entries, live `pwsh` oracle coverage, and Netclaw integration cases.

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
- [ ] 9.3 Add Bash `case` only after pattern and branch-selection uncertainty is specified.
- [ ] 9.4 Add PowerShell `while` and `do` forms with separately specified expression boundaries.
- [ ] 9.5 Add PowerShell `if` / `elseif` / `else` with conservative branch-state joins.
- [ ] 9.6 Add PowerShell `switch` only after string, regex, wildcard, and script-block modes are bounded explicitly.
- [ ] 9.7 Add paired security scenarios proving every condition and branch command remains visible.

## 10. Separately Gated Syntax Concerns

- [ ] 10.1 Specify heredoc delimiter adjacency and quoting, expansion mode, body provenance, substitutions, and completeness before enabling heredoc bodies.
- [ ] 10.2 Specify process-substitution command discovery and the unknown produced descriptor/path value before enabling it.
- [ ] 10.3 Specify background-list concurrency, ordering, and shell-state boundaries before enabling single `&`.
- [ ] 10.4 Specify C-style loop and arithmetic hidden-execution behavior before enabling either construct.
- [ ] 10.5 Keep URL-versus-glob and environment-assignment approval behavior in executable-aware consumer issues unless a shell lexical fact is missing.
- [ ] 10.6 Reproduce multiline quoted-argument reports against exact parser input before assigning a parser change.

## 11. Verification and Release

- [ ] 11.1 Add public API default-value, equality, serialization, and unknown-enum compatibility tests.
- [ ] 11.2 Assert every supported executable region appears exactly once and every unsupported executable region makes the result unparseable.
- [ ] 11.3 Run the complete Bash and PowerShell corpus suites plus the PII audit.
- [ ] 11.4 Run `dotnet build -c Release`, `dotnet test -c Release`, `dotnet pack -c Release`, and header verification.
- [ ] 11.5 Validate the public API field-for-field against the synchronized shared and PowerShell specifications.
- [ ] 11.6 Validate Netclaw's ordinary-command, redirect, bounded-loop, and unknown-value approval matrices against the prerelease package.
- [ ] 11.7 Update release notes and remove Netclaw's temporary descriptor workaround only after explicit redirect integration is live.
- [ ] 11.8 Promote stable 0.3.0 only after Linux and Windows CI, package publication, and downstream acceptance succeed.
