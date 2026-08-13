## 1. Public Contract

- [x] 1.1 Add `AnalyzedArgument.AuthoredFileSystemValue` to `SPEC.md`, source,
  XML docs, and public API snapshots without changing any 0.3.2 signature.
- [x] 1.2 Pin the non-null `Unknown` default, permitted `Exact` and `FiniteSet`
  alternatives, unsupported-domain fallback, equality, hashing, `ToString()`,
  reflection, and default JSON participation.
- [x] 1.3 Compare both target-framework API surfaces against public 0.3.2 and
  prove that this capability adds only the property.

## 2. Audited Local-Path Binding

- [x] 2.1 Add a separate internal local-filesystem binder catalog whose
  results cannot be sourced from compatibility `IsPath`, `FileVerbs`, lexical
  path shape, or generic positional fallback.
- [x] 2.2 Define reusable binding categories plus full-shape invariants for
  flags, bound values, positionals, stream sentinels, remote endpoints, and
  ambiguous positions.
- [x] 2.3 Add the smallest Bash entry that covers ordinary `cat` operands and
  the smallest dialect-aware PowerShell entry that proves one local
  filesystem parameter.
- [x] 2.4 Keep unaudited entries and positions unknown; do not add
  consumer-specific or executable-private branching.
- [x] 2.5 Preserve per-argument binding through PowerShell inline parameter
  projection and join audited Bash binding across every reachable
  abstract-state visit.

## 3. Transform and Resolution Proof

- [x] 3.1 Extend internal authored provenance with an exact one-field proof
  under the declared standard field-splitting and pathname-expansion model.
- [x] 3.2 Reject active unquoted splitting, active globs, zero-or-many fields,
  opaque fragments, invalidated shell state, unresolved cardinality, and
  over-limit unions without filesystem enumeration.
- [x] 3.3 Resolve positive candidates to normalized absolute local paths using
  the occurrence cwd and existing shell-specific resolver; reject unknown cwd,
  invalid paths, remote endpoints, providers, and path-style conflicts.
- [x] 3.4 Emit only `Exact`, deduplicated bounded `FiniteSet`, or `Unknown`.

## 4. Bash Acceptance

- [x] 4.1 Add exact D14 coverage for effective `Value=Unknown`, compatibility
  `Argument.IsPath=false`, finite `AuthoredValue`, and finite normalized
  `AuthoredFileSystemValue`.
- [x] 4.2 Add direct positive cases for `cat README.md`, an absolute `cat`
  operand, exact cwd resolution, quoted whitespace, non-recursive `$HOME`
  text, literal quoted glob text, and loop finite-set joins.
- [x] 4.3 Add transform negatives for an unquoted candidate containing
  `/etc/passwd` after whitespace, active glob, zero-or-many expansion, explicit
  field-splitting state mutation, runtime iterator, and over-limit union.
- [x] 4.4 Add audited-binding negatives for `python -c 'print(1)'`,
  `test 1 -eq 1`, `head -n 10 README`, remote `scp`, path-shaped data, unknown
  executables, `cat -`, `cat -- -`, `curl --output=-`,
  `curl --data=/api/v1`, `tar --file=-`,
  an unaudited `--output`/`--data` shape, and the audited `cat -n` versus
  operand loop.
- [x] 4.5 Preserve dynamic identity, substitutions, redirects, unsupported
  control flow, and incomplete occurrence behavior in paired negatives.
- [x] 4.6 Extend the executable corpus DTO and exact Bash corpus entries with
  authored-filesystem expectations. Run the corpus PII audit.

## 5. PowerShell Acceptance

- [ ] 5.1 Add selected-dialect positives for spaced and inline exact local
  filesystem `-LiteralPath` values; prove that only the inline value argument
  receives the domain and verify normalization on native Windows.
- [x] 5.2 Add negatives for `Get-ChildItem C:\work *.cs`,
  `Rename-Item C:\old new`, non-filesystem and unresolved providers, remote
  native `scp`, path-shaped data, stream sentinels, and dialect ambiguity.
- [x] 5.3 Prove PowerShell 7 and Windows PowerShell 5.1 use their selected
  metadata without cross-dialect widening.
- [x] 5.4 Regenerate PowerShell corpus expectations through the owned corpus
  tool, inspect the exact temporary-directory diff, and run the PII audit.

## 6. Consumer Contract

- [x] 6.1 Update `docs/CONSUMER_GUIDE.md` with input, parser output, and policy
  examples for D14, direct Bash, direct PowerShell, transform-sensitive words,
  path-shaped data, remote endpoints, filters, and rename fragments.
- [x] 6.2 Show a consumer accepting only `Exact` and `FiniteSet`, checking each
  normalized path through its own policy, and independently enforcing complete
  occurrences, identity, redirects, substitutions, and ancestry.
- [x] 6.3 State the exact authored-source environment assumption and that no
  parser fact grants authority or proves existence.
- [x] 6.4 Update paired sanitized approval evidence without source identity or
  private repository data.
- [ ] 6.5 Validate the public 0.3.3 package through Netclaw's exact D14 policy
  fixture before the downstream policy PR merges.

## 7. Validation and Release

- [x] 7.1 Run `openspec validate v0-3-3-authored-operand-role --strict` and
  synchronize the accepted contract into `SPEC.md` before implementation is
  declared complete.
- [x] 7.2 Run the Release build, full unit and corpus suite, header check,
  Slopwatch, package build, public API diff, and PII audit sequentially.
- [ ] 7.3 Obtain adversarial reviews of the API contract, audited binders,
  transform proof, Bash and PowerShell behavior, consumer guidance, and final
  frozen diff.
- [x] 7.4 Update `IMPLEMENTATION_PLAN.md` and release notes with exact evidence.
- [ ] 7.5 Merge only after Linux and native Windows CI pass; tag and publish
  0.3.3 through the release workflow.
