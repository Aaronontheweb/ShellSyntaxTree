## Why

ShellSyntaxTree 0.3.2 can publish a finite authored word while clearing the
effective `Arg.IsPath` bit when Bash field splitting prevents an exact runtime
path claim. A policy consumer then cannot distinguish a parser-owned
filesystem operand from data that merely looks path-shaped, so it must prompt
for safe bounded loops such as D14.

## What Changes

- Add a shell-neutral `AnalyzedArgument.AuthoredFileSystemValue` domain.
- Publish a bounded local path domain only when an audited parser-owned binder
  and transform-safe authored candidates both hold.
- Keep the broad compatibility `IsPath` tables separate; they cannot supply
  this stronger fact without an explicit audit.
- Keep effective value, authored value, lexical path shape, and the authored
  filesystem projection as independent facts.
- Cover Bash and PowerShell boundaries without adding executable-private
  grammar for the motivating command.
- Update the consumer guide and D14 corpus evidence.

## Capabilities

### New Capabilities

- `authored-filesystem-value`: Expose bounded parser-owned local filesystem values
  for authored arguments, with strict unknown defaults and cross-shell
  consumer rules.

### Modified Capabilities

None.

## Impact

- Public API: one additive `AnalyzedArgument` property that reuses the closed
  `ShellValueDomain` family. Existing members and signatures remain unchanged.
  Generated record equality, hashing, `ToString()`, reflection, and default
  serializer shape will include the new property.
- Parser internals: Bash and PowerShell must retain audited local-path binding
  and transform provenance even when effective resolution clears `Arg.IsPath`.
- Tests and corpus: public API snapshots, direct parser cases, executable
  corpus DTOs, D14, and adversarial path-shaped data cases change.
- Consumer guidance: policy code may send each represented value through path
  policy only when `AuthoredFileSystemValue` is `Exact` or `FiniteSet`.
- Version: the intended package is an additive 0.3.3 release.
