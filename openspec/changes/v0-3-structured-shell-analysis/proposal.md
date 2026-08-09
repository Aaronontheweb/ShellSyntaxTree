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
  that may execute in supported grammar, including iterator, loop-body,
  wrapped, substitution, and PowerShell script-block execution-region commands.
- Correct the PowerShell script-block boundary: represent direct invocation,
  current-runspace callbacks, child-runspace/process jobs, module
  initialization, and unknown receivers as typed execution regions while
  retaining proved non-executing script-block data as opaque values. Origin,
  phase, timing, and cardinality are public structural facts; variable,
  location, command-resolution, runspace, and process propagation remain
  independent shell-specific analysis.
- Add conservative value and shell-state analysis that distinguishes exact,
  finite, bounded-symbolic, and unknown facts without executing commands or
  enumerating the filesystem.
- Require proved Bash variable-attribute state before treating a simple
  parameter dereference as non-executable. Stable v0.3 fails closed on
  unmodeled shell builtins that can evaluate, assign through, or defer
  argument text, because nameref and arithmetic attributes can otherwise turn
  quoted data into hidden execution.
- Treat Bash command resolution as policy-relevant shell state. `exec` and
  mutating or ambiguous `hash`, `alias`, `unalias`, shell-option, and
  builtin-enable forms fail closed globally; only exact documented query forms remain visible.
  Unmodeled `time`, negation, coprocess, and brace-group syntax also fails
  closed rather than hiding a nested mutation in an apparent verb chain.
- Preserve resolver-relevant lexical fragments, typed expansion identity and
  cardinality, operation-specific transform eligibility, opaque cause, and
  consumer/binding context through decoding so escaped or
  quoted literal syntax cannot be mistaken for expandable syntax, while
  PowerShell native arguments, cmdlet paths, and literal paths retain their
  distinct semantics for the same decoded text. Runtime-only variable forms
  retain typed expansion identity and cardinality while their value remains
  unknown without proof; incomplete interpolation fails closed, and adjacent
  redirect fragments retain one target boundary.
- Add explicit redirect operation and target facts so consumers do not infer
  descriptor duplication, close, move, combined output, or dynamic targets
  from raw strings and `DynamicSkip` alone.
- Preserve `Clause`, `Arg`, `Redirect`, `ClauseElement`, `VerbChain`, and
  `ParsedCommand.Clauses` as compatibility projections. Existing consumers
  that check `IsUnparseable` continue to fail closed and do not silently miss
  nested executable commands. Unparseable results expose no command or clause
  authorization projection.
- Expand grammar in vertical slices through Bash `for ... in` and PowerShell
  `foreach`. Preserve the existing Bash
  heredoc grammar while adding body, delimiter, and expansion facts, and add
  Bash `<<<` here-string semantics. Process substitution, background lists,
  condition loops and branches, C-style loops, arithmetic, Bash `case`, and
  PowerShell `switch` remain fail-closed follow-ups rather than stable-v0.3
  release requirements.
- Bound the stable-v0.3 PowerShell receiver catalog to direct invocation,
  synchronous current-runspace callbacks, `Start-Job`, `ForEach-Object
  -Parallel`, and local or remote `Invoke-Command` forms already needed by the
  validating consumer. Optional `Start-ThreadJob` module/version proof and
  exact deferred breakpoint, event, and argument-completion semantics remain
  follow-ups. Existing conservative recognition may remain, but no additional
  catalog expansion gates v0.3. Unproved receivers still expose completely
  delimited bodies as incomplete execution regions, so this scope reduction
  does not hide commands.
- Keep executable-specific option and operand interpretation, authorization
  policy, and durable approval scope consumer-owned.
- Update `docs/CONSUMER_GUIDE.md` and the README usage path so security gates
  authorize the complete command-occurrence projection and use the syntax tree
  only for structure, display, and specialized analysis.

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

This accepted scope supersedes the earlier assumption that every ordinary
PowerShell script-block argument is non-executing. Canonical receivers proved
to treat a block as data remain opaque; known execution-bearing bindings are
typed, and unknown receivers conservatively expose the body with incomplete
facts rather than hiding it.

The implementation also corrects a v0.2 security defect at an internal
boundary: decoded token text currently loses lexical provenance and
consumer/binding context. The correction fixes oracle-proved false exact,
`Glob`, `Tilde`, provider, path, and avoidable `DynamicSkip` classifications
while retaining the shipped public API, raw spelling, decoded logical values,
and source spans.

Netclaw is the validating consumer. Its 0.25.4 redirect workaround remains the
short-term containment; a v0.3 integration must switch authorization traversal
to the command-occurrence projection and retain strict or prompt behavior for
unknown executable shapes. No native dependency or command execution is
introduced.

Stable v0.3 is outcome-gated, not backlog-gated. Shared-analysis refactoring,
additional shell grammar, exact optional/deferred PowerShell receiver semantics,
and the Web/Mermaid showcase remain useful follow-ups, but they do not block the
consumer migration or stable package once the contracted security behavior is
verified. Already-merged conservative behavior may remain; it is not a promise
to expand adjacent catalog or grammar surface during v0.3.
