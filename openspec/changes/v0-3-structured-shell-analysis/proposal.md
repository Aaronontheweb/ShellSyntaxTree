## Why

ShellSyntaxTree's flat `ParsedCommand.Clauses` projection cannot represent
control flow, nested executable regions, bounded loop variables, or divergent
shell state without either omitting commands or marking useful static syntax
unparseable. Production approval data also shows that overloaded
`DynamicSkip` and redirect facts force consumers to reconstruct shell grammar,
causing avoidable prompts and unsafe opportunities for inconsistent inference.

Version 0.3 should add a structured, security-oriented model that identifies
every command that may execute while preserving the v0.2 leaf records and
fail-closed behavior for incomplete analysis.

## What Changes

- Add a strongly typed syntax-node hierarchy above the existing `Clause` leaf
  model, with shell-specific front ends producing one shared structural
  contract where their semantics actually coincide.
- Add a library-owned command-occurrence projection containing every command
  that may execute, including condition, iterator, branch, loop-body, wrapped,
  and substitution commands.
- Add conservative value and shell-state analysis that distinguishes exact,
  finite, bounded-symbolic, and unknown facts without executing commands or
  enumerating the filesystem.
- Add explicit redirect operation and target facts so consumers do not infer
  descriptor duplication, close, move, combined output, or dynamic targets
  from raw strings and `DynamicSkip` alone.
- Preserve `Clause`, `Arg`, `Redirect`, `ClauseElement`, `VerbChain`, and
  `ParsedCommand.Clauses` as compatibility projections. Existing consumers
  that check `IsUnparseable` continue to fail closed and do not silently miss
  nested executable commands.
- Expand grammar in vertical slices: Bash `for ... in`, PowerShell `foreach`,
  then condition loops and branches. Heredocs, process substitution,
  background lists, C-style loops, and arithmetic remain independently gated
  by explicit executable-region and value-semantics requirements.
- Keep executable-specific option and operand interpretation, authorization
  policy, and durable approval scope consumer-owned.
- Update the consumer contract so security gates authorize the complete
  command-occurrence projection and use the syntax tree only for structure,
  display, and specialized analysis.

## Capabilities

### New Capabilities

- `structured-shell-syntax`: Represent nested Bash and PowerShell command
  structure with typed nodes while retaining existing simple-command leaves.
- `executable-command-projection`: Expose every authored command occurrence
  that may execute, with structural role, ancestry, and completeness facts.
- `bounded-shell-analysis`: Derive exact, finite, bounded-symbolic, or unknown
  values and conservatively join working-directory and variable state.
- `explicit-redirect-semantics`: Distinguish file redirects, descriptor
  duplication, close, move, combined output, and computed targets directly.
- `consumer-compatibility`: Define the conservative `Clauses` projection,
  migration rules, unknown-case behavior, and authorization-consumer contract.

### Modified Capabilities

None. This repository has not yet synchronized its shipped v0.2 contracts
into `openspec/specs/`; accepted deltas from this change will be reconciled
into `SPEC.md` and `SPEC.POWERSHELL.md` before implementation.

## Impact

This is an additive but release-shaped public API change affecting
`ParsedCommand`, new syntax and analysis records, redirect modeling, both
shell parsers, corpus schemas, public API snapshots, the shared and
PowerShell specifications, and `docs/CONSUMER_GUIDE.md`. Adding properties to
public records also changes generated equality, hashing, `ToString()`, and
default serialization and therefore requires explicit migration notes.

Netclaw is the validating consumer. Its 0.25.4 redirect workaround remains the
short-term containment; a v0.3 integration must switch authorization traversal
to the command-occurrence projection and retain strict or prompt behavior for
unknown executable shapes. No native dependency or command execution is
introduced.
