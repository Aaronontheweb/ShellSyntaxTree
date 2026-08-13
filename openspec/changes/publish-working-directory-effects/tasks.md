## 1. Contract and Design Review

- [x] 1.1 Synchronize the accepted API, relational semantics, Bash rules, and
  consumer boundary into `SPEC.md`.
- [x] 1.2 Synchronize selected-dialect location effects and language separation
  into `SPEC.POWERSHELL.md`.
- [x] 1.3 Correct the v0.3 Bash `chdir` occurrence contract while preserving the
  explicitly documented v0.2 compatibility leaf.
- [x] 1.4 Update `IMPLEMENTATION_PLAN.md` for the completed v0.3.3 release and
  the new v0.3.4 working-directory-effect slice.
- [x] 1.5 Run strict OpenSpec validation and obtain an adversarial review of the
  public API, semantics, threat-model boundary, and acceptance matrix before
  production implementation.

## 2. Public Projection and Internal Facts

- [x] 2.1 Add the closed `ShellWorkingDirectoryEffect` public family with
  library-owned construction and immutable `ChangesOnSuccess.Target`.
- [x] 2.2 Add `CommandOccurrence.WorkingDirectoryEffect` with a non-null
  `Unknown` default and no change to existing signatures.
- [x] 2.3 Add internal relational effect facts and validation that permits only
  `Unknown`, `Unchanged`, and success-only `Unknown`/`Exact`/`FiniteSet`
  targets.
- [x] 2.4 Add correlation-preserving joins for repeated abstract visits and
  fail closed on missing, mixed, invalid, or over-limit facts.
- [x] 2.5 Extend public API snapshots, equality/hash/rendering tests, defensive
  ownership tests, and both target-framework API comparisons against public
  v0.3.3.

## 3. Bash Analysis

- [x] 3.1 Record `Unchanged` for complete ordinary Bash commands only after all
  parser-known current-scope mutation boundaries pass.
- [x] 3.2 Record bounded or unknown `ChangesOnSuccess` for `cd`, `command cd`,
  and `builtin cd` from the existing effective-argument transfer.
- [x] 3.3 Record `Unchanged` for statically invalid `cd` shapes whose only
  reachable normal exit preserves cwd.
- [x] 3.4 Record `Unknown` for pushd/popd and accepted hidden current-scope
  effects; preserve atomic failure for source/dot/eval, unsupported dispatch,
  computed identity, and incomplete execution-bearing syntax.
- [x] 3.5 Stop treating Bash `chdir` as a v0.3 cwd transfer while preserving its
  documented v0.2 compatibility attribution.
- [x] 3.6 Preserve scope-relative effects and ancestry across first/last
  pipeline stages, substitutions, decoded child shells, subshells, and loops;
  pin unproved lastpipe, unreachable body, and zero-iteration boundaries.

## 4. PowerShell Analysis

- [x] 4.1 Record `Unchanged` for complete native PowerShell commands with no
  parser-known current-runspace location mutation.
- [x] 4.2 Record bounded or unknown `ChangesOnSuccess` for `Set-Location` and
  its selected-dialect aliases; record failure-only invalid static shapes as
  `Unchanged`.
- [x] 4.3 Record `Unknown` for Push-Location/Pop-Location, provider ambiguity,
  scripts, unmodeled current-runspace code, and invalidated identity.
- [x] 4.4 Join repeated PowerShell visits without cross-dialect or
  cross-language alias widening.
- [ ] 4.5 Verify PowerShell 7 and Windows PowerShell 5.1 behavior on native
  Windows, including path normalization and aliases.
- [x] 4.6 Pin `$()` and parenthesized current-runspace propagation, PowerShell
  pipeline semantics, modeled and unmodeled attached-region host effects,
  shared Set-Location parameter binding, failure-only shapes, and empty-loop
  bodies.

## 5. Acceptance Corpus and Unit Coverage

- [x] 5.1 Add Bash positives for ordinary unchanged commands, exact and finite
  cd targets, command/builtin wrappers, and subshell-local effects.
- [x] 5.2 Add Bash negatives for pushd/popd, atomic source/dot/eval failure,
  dynamic targets, invalid options, extra operands, incomplete regions, and
  mixed joins.
- [x] 5.3 Pin `chdir` language separation and the
  `cd /tmp && pushd /other; head file` counterexample.
- [x] 5.4 Add PowerShell positives for Set-Location and aliases plus negatives
  for alias prefixes, invalid switch values, location stack, provider, script,
  and execution-region ambiguity.
- [x] 5.5 Extend the corpus DTO and sanitized Bash entries with exact effect
  expectations; run the corpus PII audit.
- [x] 5.6 Add projection corruption tests for null, unsupported target domains,
  malformed finite sets, relative/non-normalized/mixed-style paths, unknown
  subtypes, and missing internal facts.

## 6. Consumer Guidance and Downstream Acceptance

- [x] 6.1 Update `docs/CONSUMER_GUIDE.md` with input/output examples that
  distinguish incoming `WorkingDirectory` from relational effect.
- [x] 6.2 Show a default-deny consumer switch over all effect alternatives,
  target path checks, ancestry, redirects, prerequisites, and fallback cwd.
- [x] 6.3 Explain authored-syntax versus ambient-runtime scope and why the fact
  grants no authority.
- [ ] 6.4 Upgrade the paused Netclaw causal-directory slice to the public
  package and remove its command-name mutation logic.
- [ ] 6.5 Add Netclaw regressions for cd success/failure, original-cwd fallback,
  pushd/popd, command/builtin cd, Bash chdir, PowerShell separation, protected
  paths, parent/subagent, interactive/headless, and recovery.

## 7. Validation and Delivery

- [x] 7.1 Run `openspec validate publish-working-directory-effects --strict`
  after every contract or task-state revision.
- [x] 7.2 Run Release build, full unit and corpus suite, header verification,
  format checks, Slopwatch, package build, API diff, and PII audit.
- [x] 7.3 Obtain adversarial reviews of the implementation, Bash/PowerShell
  semantics, public API, consumer guide, corpus, and final frozen diff.
- [x] 7.4 Open the bounded ShellSyntaxTree pull request and merge only after
  Linux and native Windows checks pass.
- [ ] 7.5 Publish the next additive v0.x package through the release workflow
  only after the implementation PR is merged and release gates pass.
- [ ] 7.6 Run Netclaw local, native Windows, strict OpenSpec, approval-matrix,
  and relevant headless eval gates before its causal-policy PR merges.
