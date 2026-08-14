## 1. Public Contract

- [ ] 1.1 Add `AuthoredNonFileSystemValue` to `SPEC.md`, source, defaults, and
  exact public API snapshots.
- [ ] 1.2 Document the independent authored-value, lexical-shape,
  filesystem-value, and non-filesystem-value invariants.
- [ ] 1.3 Add equality, hashing, `ToString()`, reflection, default serializer,
  and corruption-boundary tests.

## 2. Audited Operand Projection

- [ ] 2.1 Generalize the internal audited operand catalog and projector without
  duplicating filesystem and non-filesystem binding logic.
- [ ] 2.2 Preserve the existing Bash `cat` and PowerShell `Get-Content
  -LiteralPath` filesystem projections exactly.
- [ ] 2.3 Add the Bash `tr` all-argument non-filesystem binding with bounded
  provenance and value-domain checks.
- [ ] 2.4 Reject positive filesystem and non-filesystem domains on the same
  argument before publishing an occurrence.
- [ ] 2.5 Route the `tr` audited entry into single-token verb extraction and
  compatibility non-path classification while retaining generic heuristics.

## 3. Parser and Corpus Coverage

- [ ] 3.1 Add direct cases for `tr abc def`, `tr -d '\n'`, path-looking
  translation data, active globs, dynamic values, substitutions, and redirects.
- [ ] 3.2 Add adversarial cases for unknown commands, `grep -f`, cat paths,
  option-like translation values, and over-limit joins.
- [ ] 3.3 Add the sanitized Bash executable-corpus entry with exact clauses,
  elements, occurrences, authored facts, and no PII.
- [ ] 3.4 Add PowerShell default-unknown and cross-shell API consistency cases.

## 4. Consumer Integration

- [ ] 4.1 Update the consumer guide with the exact path-specific use rule and
  independent redirect/effect checks.
- [ ] 4.2 Upgrade Netclaw's typed path-fact projection to omit compatibility
  and lexical path facts only for positive non-filesystem values.
- [ ] 4.3 Add the sanitized live `gh run view | tr -d '\n'` coordinator fixture
  and assert no child `n` scope.
- [ ] 4.4 Retain strict Netclaw outcomes for unknown semantics, hidden option
  paths, output redirects, dynamic commands, and incomplete occurrences.
- [ ] 4.5 Record shell-authored file writes as agent-alignment corpus cases;
  do not widen `cat`, heredoc, `tee`, or redirect authority.

## 5. Release and Live Validation

- [ ] 5.1 Update `IMPLEMENTATION_PLAN.md` and release notes with the bounded
  0.3.5 behavior and unchanged public signatures.
- [ ] 5.2 Run Release build, full unit and corpus suite, PII audit, headers,
  Slopwatch, package validation, and 0.3.4 API comparison.
- [ ] 5.3 Run Linux and native Windows CI plus an independent adversarial
  security review before publication.
- [ ] 5.4 Publish the package, consume the exact public version in Netclaw, and
  run its complete policy and cross-platform gates.
- [ ] 5.5 Binary-swap the merged Netclaw build and classify another live
  traffic window before claiming approval-fatigue improvement.
