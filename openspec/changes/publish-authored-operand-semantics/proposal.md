## Why

ShellSyntaxTree exposes positive local-filesystem facts but cannot prove that
path-shaped text is non-filesystem data. Netclaw therefore prompts for reviewed
diagnostics such as `tr -d '\n'`, even though the operand is transform data.

## What Changes

- Add one additive authored non-filesystem value domain with an unknown
  default.
- Distinguish audited local-filesystem operands from audited non-filesystem
  data without changing compatibility `IsPath` behavior.
- Extend the parser-owned binding catalog with bounded, full-shape data
  entries, beginning with Bash `tr` translation operands.
- Keep unknown, dynamic, executable, remote, relational, and unaudited
  operands strict.
- Add direct, corpus, consumer-guide, and downstream Netclaw regressions for
  the observed `tr -d '\n'` approval case.

## Capabilities

### New Capabilities

- `authored-operand-semantics`: Publish audited filesystem-versus-data
  semantics independently from lexical shape and bounded value domains.

### Modified Capabilities

None.

## Impact

- Public API: one additive `AnalyzedArgument` property using the existing
  closed `ShellValueDomain` family.
  Generated record equality, hashing, `ToString()`, reflection, and default
  serialization include the property.
- Parser internals: the audited binder catalog gains non-filesystem data
  bindings without changing broad compatibility tables.
- Package: intended as additive ShellSyntaxTree 0.3.5 behavior.
- Consumer: Netclaw may skip compatibility and lexical local-path checks only
  for the exact argument carrying an audited non-filesystem value. Command
  effects, redirects, completeness, and every unknown fact retain their checks.
