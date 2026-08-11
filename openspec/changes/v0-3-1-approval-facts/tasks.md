## 1. Contract and maintainer decisions

- [ ] 1.1 Approve `AuthoredValue`, `ShellPathShape`, `AuthoredPathShape`,
  `PublishAuthoredSourceFacts`, `IntegerRange`, and `Concatenation`.
- [ ] 1.2 Approve Netclaw's use of a pre-field-splitting authored word. Record
  the ambient attribute, ambient `IFS`, and field-splitting boundary.
- [ ] 1.3 Update `SPEC.md` public API and bounded-analysis contracts before code.
- [ ] 1.4 Approve the public API snapshot diff explicitly.

## 2. Bounded value domains

- [ ] 2.1 Add library-owned `IntegerRange` with inclusive-bound validation and
  exact canonical-decimal documentation.
- [ ] 2.2 Add immutable, flattened `Concatenation` with empty-part removal,
  collapse rules, the 16-part cap, and allowed-part validation.
- [ ] 2.3 Extend internal discriminants, projector validation, equality, and
  public API snapshots without changing existing alternatives.
- [ ] 2.4 Add construction and malformed-projector tests for every invariant.

## 3. Quoted Bash status

- [ ] 3.1 Recognize `$?` only in proved single-field double-quoted value
  positions.
- [ ] 3.2 Publish standalone `IntegerRange(0, 255)` and embedded
  `Concatenation` without enumeration.
- [ ] 3.3 Keep unquoted status, command identity, redirects, positional
  parameters, substitutions, and unsupported mixed contexts strict.
- [ ] 3.4 Add native Bash oracle tests for quoted, unquoted, single-quoted,
  embedded, and multiple-expansion forms.

## 4. Authored-source projection

- [ ] 4.1 Add `AuthoredValue` while preserving the exact released meaning of
  `Value`.
- [ ] 4.2 Add the default-false `PublishAuthoredSourceFacts` option and preserve
  default `IsUnparseable`, `Commands`, `Clauses`, and completeness results.
- [ ] 4.3 Reuse the existing loop evaluator to project opt-in pre-field-splitting
  authored words; do not duplicate fragment composition.
- [ ] 4.4 Keep explicit source attribute mutation, hidden execution, dynamic
  identity, runtime iteration, and unsupported regions strict.
- [ ] 4.5 Add default, opt-in, and isolated tests. Cover nameref, integer, `IFS`,
  field splitting, and option-shaped values.

## 5. Authored lexical path shape

- [ ] 5.1 Add lexical `ShellPathShape` and `AuthoredPathShape`; do not claim
  filesystem operand semantics.
- [ ] 5.2 Cover absolute, separator-bearing relative, `./`, `../`, eligible
  tilde, quoted-literal tilde, and unknown bare-value cases.
- [ ] 5.3 Prove D14's pre-field-splitting words while effective values remain
  unknown under opt-in default-state mode.
- [ ] 5.4 Add URI, repository slug, container image, and slash-bearing data
  counterexamples.
- [ ] 5.5 Prove exact PowerShell compatibility: `AuthoredValue=Value` and
  `AuthoredPathShape=Unknown` for literal, dynamic, path, and provider cases.

## 6. Evidence, corpus, and documentation

- [ ] 6.1 Keep `evidence/approval-matrix.json` byte-identical to the Netclaw
  artifact and add a parity check to the delivery checklist.
- [ ] 6.2 Add exact sanitized D02, D10, and D14 executable cases plus adversarial
  boundaries to the Bash corpus or focused tests.
- [ ] 6.3 Extend the automated PII audit to the OpenSpec evidence JSON and run it
  with zero hits.
- [ ] 6.4 Update `docs/CONSUMER_GUIDE.md` with input/output examples and a
  fail-closed recursive domain switch.
- [ ] 6.5 Update `IMPLEMENTATION_PLAN.md`, `RELEASE_NOTES.md`, and version
  metadata for 0.3.1.

## 7. Validation and release

- [ ] 7.1 Run `openspec validate v0-3-1-approval-facts --type change --strict
  --no-interactive` before implementation and before delivery.
- [ ] 7.2 Run tool restore, Release build, full tests, header verification, and
  public API approval.
- [ ] 7.3 Run Release pack and inspect the 0.3.1 package metadata.
- [ ] 7.4 Obtain adversarial review of API truthfulness, Bash semantics,
  sanitization, and consumer misuse boundaries.
- [ ] 7.5 Push the SemVer tag, verify publish workflow success, and verify the
  package appears on nuget.org before marking release complete.
