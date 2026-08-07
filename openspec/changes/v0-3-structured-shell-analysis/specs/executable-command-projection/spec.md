## ADDED Requirements

### Requirement: Parsed commands expose a complete may-execute set
Every fully parseable result SHALL expose a command-occurrence collection that
contains each authored simple command that may execute exactly once, regardless
of whether the command is top-level or nested.

#### Scenario: Commands in mutually exclusive branches
- **WHEN** Bash parses `if test -f a; then rm a; else echo missing; fi`
- **THEN** the collection contains `test`, `rm`, and `echo` exactly once each
- **THEN** the collection does not predict which branch will run

#### Scenario: Loop body occurrence is not multiplied
- **WHEN** Bash parses `for f in a b c; do echo "$f"; done`
- **THEN** the authored `echo` command appears once
- **THEN** its possible effective values are represented by analysis facts rather than three duplicated occurrences

### Requirement: Occurrences identify structural execution roles
Each command occurrence SHALL identify its immediate structural execution role
and SHALL retain compositional ancestry for analysis, diagnostics, and UI
grouping.

`CommandOccurrenceRole.Unknown` and `CommandAncestryRegion.Unknown` SHALL be
their enum zero values. Ancestry SHALL be ordered outermost to innermost,
exclude the simple-command leaf, and retain child indices and exact-or-null
source ranges for correlation.

Each frame SHALL describe the relationship from its ancestor to the next node
on the path. The root block SHALL use `Root`; non-root blocks and command lists
SHALL use `Statement`; pipelines SHALL use `PipelineStage`; groups SHALL use
`GroupBody`; foreach nodes SHALL use `Iterator` or `LoopBody`; condition loops
SHALL use `Condition` or `LoopBody`; conditionals SHALL use `Branch`;
conditional-branch nodes SHALL use `Condition` or `Branch`; and substitutions
SHALL use `Substitution`. Repeated children SHALL use their zero-based authored
index, with an `else` child indexed after all conditional branches. Frame
source ranges SHALL identify the ancestor. Blocks, command lists, and groups
SHALL retain the incoming immediate role; a nearer pipeline, iterator, body,
condition, branch, or substitution relation SHALL replace it.

#### Scenario: Root and nested block coordinates are deterministic
- **WHEN** a root statement contains a loop-body pipeline
- **THEN** a stage occurrence has outer-to-inner `Root`, `LoopBody`,
  `Statement`, and `PipelineStage` ancestry
- **THEN** each repeated relation carries its authored child index

### Requirement: Projection rejects malformed parser-owned structure and facts
The projector SHALL accept only a tree with one syntax-node and one `Clause`
reference per authored simple-command position. Node and source-fragment spans
SHALL be both unavailable or a non-negative start/length pair. Structural enum
values consumed by projection SHALL be known. Empty blocks MAY be valid, but
empty pipelines, command lists, and conditionals SHALL be rejected.

Value domains, cwd facts, effective-argument coordinates, redirect
coordinates, redirect shapes, and heredoc facts SHALL satisfy their locked
record invariants before projection succeeds. Any repeated identity, malformed
shape, invalid coordinate, cycle, or depth overflow SHALL discard every
partial command and compatibility result.

#### Scenario: Shared leaf identity is not counted twice
- **WHEN** an internal parser bug places one syntax leaf or `Clause` reference
  at two authored positions
- **THEN** projection fails instead of emitting two occurrences
- **THEN** no partial command or compatibility projection is returned

#### Scenario: Complete occurrence cannot contain invalid facts
- **WHEN** parser-owned analysis supplies an invalid value domain, argument
  coordinate, redirect shape, or unknown scope-affecting structural kind
- **THEN** projection fails closed
- **THEN** the occurrence is not published with `IsComplete=true`

#### Scenario: Dynamic Bash command string remains incomplete
- **WHEN** Bash parses a `bash` or `sh` clause with dynamic wrapper-control input, decoded or combined command-string options, or an expanding quoted body that does not match the complete literal exactly-one wrapper production
- **THEN** the existing outer compatibility leaf remains visible with direct source provenance
- **THEN** its command occurrence has `IsComplete=false` because no hidden command-string body was discovered

#### Scenario: While condition and body roles
- **WHEN** Bash parses `while curl URL; do sleep 1; done`
- **THEN** `curl` is identified as a condition occurrence
- **THEN** `sleep` is identified as a loop-body occurrence

#### Scenario: PowerShell iterator pipeline role
- **WHEN** PowerShell parses `foreach ($f in Get-ChildItem C:\input) { Remove-Item $f }`
- **THEN** `Get-ChildItem` is identified as an iterator occurrence
- **THEN** `Remove-Item` is identified as a loop-body occurrence

#### Scenario: Pipeline stage nested in a loop body
- **WHEN** Bash parses `for f in a b; do printf '%s\n' "$f" | sort; done`
- **THEN** `printf` and `sort` have the immediate role pipeline stage
- **THEN** their ancestry also identifies the enclosing loop body

### Requirement: Iterator and substitution commands remain visible
The parser SHALL include every inner command from a supported executable
iterator, substitution, or nested command in the occurrence collection even
when the produced value is unknown.

#### Scenario: Bash command substitution iterable
- **WHEN** Bash supports and parses `for f in $(find /tmp -type f); do rm "$f"; done`
- **THEN** both `find` and `rm` appear in the occurrence collection
- **THEN** the value produced by `find` is not presented as exact or finite

#### Scenario: Process substitution remains gated
- **WHEN** Bash encounters `diff <(git show HEAD) <(git show HEAD~1)` before process substitution discovery is supported
- **THEN** the result is unparseable rather than omitting either `git` command

### Requirement: Occurrence completeness is explicit
Each occurrence SHALL state whether its command identity, structural ancestry,
and parser-owned shell analysis are complete. No incomplete occurrence SHALL
be sufficient authorization evidence.

#### Scenario: Dynamic command identity
- **WHEN** PowerShell parses a supported structure whose body invokes `& $exe arg`
- **THEN** the invocation occurrence is incomplete or has dynamic command identity
- **THEN** a security consumer is directed to prompt or deny

#### Scenario: Complete literal command
- **WHEN** Bash parses `echo ready` in a supported loop body
- **THEN** the occurrence is structurally complete
- **THEN** completeness does not imply that `echo` is authorized

#### Scenario: Complete occurrence with unknown value
- **WHEN** PowerShell parses `foreach ($item in Get-ChildItem) { Write-Output $item }`
- **THEN** the `Write-Output` occurrence may be structurally complete
- **THEN** its effective `$item` value remains unknown because pipeline objects are not evaluated

### Requirement: Source order is deterministic
The occurrence collection SHALL be ordered by authored command occurrence,
including commands nested in headers and bodies, with a documented tie-breaker
for enclosing and enclosed regions. Disjoint executable regions SHALL follow
authored source order. An enclosed substitution SHALL precede its containing
simple command. Nested substitutions SHALL be emitted innermost first. When
decoded wrapper content has no comparable outer spans, the containing
structural collection order SHALL be used. `ParsedCommand.Clauses` SHALL use
the same ordering. A substitution ancestry frame SHALL use
`Region=Substitution` and the authored zero-based child index in its containing
structural collection. For an embedded simple-command value this is
`SimpleCommandSyntax.Substitutions`; for a direct iterator substitution this is
the iterator-command collection.

#### Scenario: Iterator precedes body
- **WHEN** Bash parses `for f in $(find .); do rm "$f"; done`
- **THEN** the `find` occurrence precedes the `rm` occurrence

#### Scenario: Branches preserve authored order
- **WHEN** PowerShell parses an `if` statement with then and else commands
- **THEN** condition commands precede then-body commands
- **THEN** then-body commands precede else-body commands in the projection

#### Scenario: Ordinary command substitution precedes its consumer
- **WHEN** Bash parses `rm "$(find /tmp)"`
- **THEN** `find` precedes `rm` in both command and compatibility projections
- **THEN** each command appears exactly once

#### Scenario: Multiple substitutions preserve authored order
- **WHEN** one command contains two sibling substitutions
- **THEN** commands from the first substitution precede commands from the second
- **THEN** the containing command follows both substitutions
- **THEN** the sibling substitution frames use child indices zero and one

#### Scenario: Nested substitutions are innermost first
- **WHEN** a substitution contains a command with another substitution
- **THEN** the innermost command precedes its containing substitution command
- **THEN** both precede the outermost containing command

#### Scenario: PowerShell expression output is not a command
- **WHEN** standalone PowerShell `$()` produces text shaped like a command name
- **THEN** the occurrence collection contains its inner commands only
- **THEN** an outer command occurrence exists only when the call operator invokes that value

### Requirement: Command discovery is library-owned
Security consumers SHALL be able to enumerate every potentially executable
command without recursively matching syntax-node types.

#### Scenario: New syntax node in a later version
- **WHEN** a later package adds a syntax-node type whose commands are fully supported
- **THEN** those commands appear through the existing occurrence collection
- **THEN** an occurrence-based consumer does not need a new tree visitor merely to discover them
