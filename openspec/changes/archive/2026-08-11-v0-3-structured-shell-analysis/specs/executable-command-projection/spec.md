## ADDED Requirements

### Requirement: Parsed commands expose a complete may-execute set
Every fully parseable result SHALL expose a command-occurrence collection that
contains each authored simple command that may execute exactly once, regardless
of whether the command is top-level or nested.

#### Scenario: Loop body occurrence is not multiplied
- **WHEN** isolated-mode Bash parses `for f in a b c; do echo "$f"; done`
- **THEN** the authored `echo` command appears once
- **THEN** its possible effective values are represented by analysis facts rather than three duplicated occurrences

#### Scenario: Executable corpus pins all public projections
- **WHEN** a selected Bash or PowerShell corpus entry is marked for structural verification
- **THEN** the corpus records the complete syntax tree and command-occurrence collection in authored order
- **THEN** every simple-command node and occurrence references the exact compatibility clause by index and object identity
- **THEN** roles, completeness, joined arguments, ancestor identities, child indices, and exact-or-null node source ranges are compared without weakening legacy corpus entries that omit structural expectations

### Requirement: Occurrences identify structural execution roles
Each command occurrence SHALL identify its immediate structural execution role
and SHALL retain compositional ancestry for analysis, diagnostics, and UI
grouping.

`CommandOccurrenceRole.Unknown` and `CommandAncestryRegion.Unknown` SHALL be
their enum zero values. Ancestry SHALL be ordered outermost to innermost,
exclude the simple-command leaf, and retain the actual ancestor node plus a
child index. A frame SHALL NOT copy a kind or source range that can disagree
with that node.

Each frame SHALL describe the relationship from its ancestor to the next node
on the path. The root block SHALL use `Root`; non-root blocks and command lists
SHALL use `Statement`; pipelines SHALL use `PipelineStage`; groups SHALL use
`GroupBody`; foreach nodes SHALL use `Iterator` or `LoopBody`; substitutions
SHALL use `Substitution`; and execution regions SHALL use `ExecutionRegion`.
Condition loops and branches are not part of stable v0.3. Repeated children
SHALL use their zero-based authored index. Blocks, command lists, and groups
SHALL retain the incoming immediate role; a nearer pipeline, iterator, body, substitution, or
execution-region relation SHALL replace it.

#### Scenario: Root and nested ancestry is deterministic
- **WHEN** a root statement contains a loop-body pipeline
- **THEN** a stage occurrence has outer-to-inner `Root`, `LoopBody`,
  `Statement`, and `PipelineStage` ancestry
- **THEN** each repeated relation carries its authored child index

### Requirement: Projection rejects malformed parser-owned structure and facts
The projector SHALL accept only a tree with one syntax-node and one `Clause`
reference per authored simple-command position. Node and source-fragment spans
SHALL be both unavailable or a non-negative start/length pair. Structural enum
values consumed by projection SHALL be known. Empty blocks MAY be valid, but
empty pipelines and command lists SHALL be rejected.

Value domains, cwd facts, joined argument identities, redirect alternatives,
and heredoc facts SHALL satisfy their locked invariants before projection
succeeds. Any repeated identity, malformed shape, invalid join, cycle, or
depth overflow SHALL discard every partial command and compatibility result.

#### Scenario: Shared leaf identity is not counted twice
- **WHEN** an internal parser bug places one syntax leaf or `Clause` reference
  at two authored positions
- **THEN** projection fails instead of emitting two occurrences
- **THEN** no partial command or compatibility projection is returned

#### Scenario: Complete occurrence cannot contain invalid facts
- **WHEN** parser-owned analysis supplies an invalid value domain, argument
  join, redirect alternative, or unknown scope-affecting structural relation
- **THEN** projection fails closed
- **THEN** the occurrence is not published with `IsComplete=true`

### Requirement: Occurrences expose fully joined authored arguments
Each command occurrence SHALL expose exactly one `AnalyzedArgument` for every
non-cwd-attribution compatibility `Arg`, in authored order. Each entry SHALL
reference that exact `Arg`, its exact argument-role `ClauseElement`, and one
closed shell-value domain. Static authored values SHALL normally be exact;
bounded visits MAY be finite or path-pattern values; unproved values SHALL be
unknown. Consumers SHALL NOT correlate a sparse coordinate back into
`Clause.Elements`.

The join SHALL permit multiple compatibility arguments to reference the same
authored element. Inline equals-form native options and PowerShell colon-bound
parameters can split one token into multiple `Arg` records; all split entries
SHALL remain present, ordered, and linked to that one `ClauseElement`.

#### Scenario: Static and loop-derived arguments share one ingestion path
- **WHEN** an occurrence contains a static option and a bounded loop variable
- **THEN** both appear once in `Arguments` in authored order
- **THEN** each entry directly exposes its compatibility argument, authored element, and effective value

#### Scenario: Synthetic cwd attribution is not an authored argument
- **WHEN** the v0.2 clause carries a synthetic cwd-attribution `Arg`
- **THEN** it remains available through `Clause.Args`
- **THEN** it is absent from `Arguments` because `WorkingDirectory` is the canonical v0.3 cwd fact

#### Scenario: Inline option creates a many-to-one authored join
- **WHEN** a supported inline option produces an option `Arg` and an operand `Arg` from one authored token
- **THEN** two analyzed arguments reference their two exact compatibility arguments
- **THEN** both analyzed arguments reference the same exact authored `ClauseElement`
- **THEN** each analyzed value describes its corresponding split `Arg`, not the unsplit element text

### Requirement: Published collections are not consumer-mutable
Every v0.3 `IReadOnlyList<T>` SHALL use an immutable backing collection or a
defensive copy. The library SHALL NOT expose an array or mutable list that a
consumer can cast and mutate after a successful projection.

#### Scenario: Caller retains a parser input collection
- **WHEN** internal lowering receives a mutable collection and publishes a v0.3 fact
- **THEN** later mutation of the original collection cannot change the parsed result

#### Scenario: Dynamic Bash command string remains incomplete
- **WHEN** Bash parses a `bash` or `sh` clause with dynamic wrapper-control input, decoded or combined command-string options, or an expanding quoted body that does not match the complete literal exactly-one wrapper production
- **THEN** the existing outer compatibility leaf remains visible with direct source provenance
- **THEN** its command occurrence has `IsComplete=false` because no hidden command-string body was discovered

#### Scenario: Unproved PowerShell command string remains incomplete
- **WHEN** PowerShell parses a `pwsh` or `powershell` host whose command-string control is dynamic, quoted, hidden behind `--%`, stdin-driven by `-Command -`, or uses an unsupported command-string-capable form such as `-CommandWithArgs` / `-cwa`
- **THEN** the existing outer compatibility leaf remains visible with direct source provenance
- **THEN** its command occurrence has `IsComplete=false` because no complete executable body was discovered

#### Scenario: Computed Invoke-Expression remains incomplete
- **WHEN** PowerShell retains an outer `Invoke-Expression` clause because its payload is computed rather than one exact static scalar
- **THEN** the payload remains an authored `DynamicSkip` value
- **THEN** the command occurrence has `IsComplete=false` and cannot authorize hidden code

#### Scenario: PowerShell iterator pipeline role
- **WHEN** PowerShell parses `foreach ($f in Get-ChildItem C:\input) { Remove-Item $f }`
- **THEN** `Get-ChildItem` is identified as an iterator occurrence
- **THEN** `Remove-Item` is identified as a loop-body occurrence

#### Scenario: Pipeline stage nested in a loop body
- **WHEN** isolated-mode Bash parses `for f in a b; do printf '%s\n' "$f" | sort; done`
- **THEN** `printf` and `sort` have the immediate role pipeline stage
- **THEN** their ancestry also identifies the enclosing loop body

### Requirement: Iterator and substitution commands remain visible
The parser SHALL include every inner command from a supported executable
iterator, substitution, or nested command in the occurrence collection even
when the produced value is unknown.

#### Scenario: Bash command substitution iterable
- **WHEN** isolated-mode Bash supports and parses `for f in $(find /tmp -type f); do rm "$f"; done`
- **THEN** both `find` and `rm` appear in the occurrence collection
- **THEN** the value produced by `find` is not presented as exact or finite

#### Scenario: Process substitution remains gated
- **WHEN** Bash encounters `diff <(git show HEAD) <(git show HEAD~1)` before process substitution discovery is supported
- **THEN** the result is unparseable rather than omitting either `git` command

### Requirement: Script-block execution regions remain visible
Every completely delimited execution-bearing PowerShell script block SHALL
contribute each authored body command exactly once. A direct call or dot-source
region SHALL contribute only its body commands. A command-owned region SHALL
retain the host command occurrence as well as its body commands. An ambiguous
receiver MAY produce incomplete occurrences with unknown region facts, but it
SHALL NOT omit the body or authorize it as inert data.

#### Scenario: Direct call operator has no synthetic host
- **WHEN** PowerShell parses `& { Remove-Item target.txt }`
- **THEN** `Remove-Item` appears exactly once with immediate role `ExecutionRegion`
- **THEN** no occurrence is created for `&` or the script block itself

#### Scenario: Pipeline callback retains host and body
- **WHEN** PowerShell parses `Get-ChildItem | ForEach-Object { Remove-Item $_ }`
- **THEN** occurrences contain `Get-ChildItem`, `ForEach-Object`, and `Remove-Item` exactly once
- **THEN** the body occurrence has execution-region ancestry nested beneath the pipeline stage

#### Scenario: Unknown receiver preserves a supported non-pipeline body
- **WHEN** a script-block argument's receiver or binding is not statically proved
- **WHEN** its body contains only supported non-pipeline commands
- **THEN** the host and every body command remain visible
- **THEN** the affected occurrence facts are incomplete or unknown

#### Scenario: Unknown receiver pipeline fails atomically
- **WHEN** an unknown receiver's body contains a pipeline whose stage identity is unproved
- **THEN** the whole result is unparseable
- **THEN** `Commands` and `Clauses` are empty

### Requirement: Occurrence completeness is explicit
Each occurrence SHALL state whether its authored command identity, structural
ancestry, executable-region discovery, and parser-owned authored shell analysis
are complete. Completeness SHALL NOT claim that ambient aliases, functions,
modules, profiles, executable lookup, or inherited environment select a
particular runtime implementation. No incomplete occurrence SHALL be sufficient
authorization evidence.

#### Scenario: Dynamic command identity
- **WHEN** PowerShell parses a supported structure whose body invokes `& $exe arg`
- **THEN** the invocation occurrence is incomplete or has dynamic command identity
- **THEN** a security consumer is directed to prompt or deny

#### Scenario: Complete literal command
- **WHEN** Bash parses `echo ready` in a supported loop body
- **THEN** the occurrence is structurally complete
- **THEN** completeness does not imply that `echo` is authorized

#### Scenario: Complete occurrence with unknown value
- **WHEN** isolated-mode PowerShell parses `foreach ($item in Get-ChildItem) { Write-Output $item }`
- **THEN** the `Write-Output` occurrence may be structurally complete
- **THEN** its effective `$item` value remains unknown because pipeline objects are not evaluated

#### Scenario: Ambient PowerShell resolution does not erase authored completeness
- **WHEN** default-mode PowerShell parses `Write-Output victim.txt`
- **THEN** one v0.2 compatibility `Clause` remains visible
- **THEN** the v0.3 occurrence is complete for the authored `Write-Output` identity
- **THEN** runtime shadowing remains outside the approval-grammar proof

### Requirement: Source order is deterministic
The occurrence collection SHALL be ordered by authored command occurrence,
including commands nested in headers and bodies, with a documented tie-breaker
for enclosing and enclosed regions. Disjoint executable regions SHALL follow
authored source order. An enclosed substitution SHALL precede its containing
simple command. Nested substitutions SHALL be emitted innermost first. When
decoded wrapper content has no comparable outer spans, the containing
structural collection order SHALL be used. `ParsedCommand.Clauses` SHALL use
the same ordering. A command-owned execution region SHALL follow its host
command and sibling regions SHALL follow authored script-block order;
shell-specific state analysis MAY schedule semantic phases independently.
A substitution ancestry frame SHALL use
`Region=Substitution` and the authored zero-based child index in its containing
structural collection. For an embedded simple-command value this is
`SimpleCommandSyntax.Substitutions`; for a direct iterator substitution this is
the iterator-command collection.

#### Scenario: Iterator precedes body
- **WHEN** isolated-mode Bash parses `for f in $(find .); do rm "$f"; done`
- **THEN** the `find` occurrence precedes the `rm` occurrence

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

#### Scenario: Host precedes its script-block regions
- **WHEN** PowerShell parses `ForEach-Object -End { Write-Output end } -Begin { Write-Output begin }`
- **THEN** `ForEach-Object` precedes both body commands
- **THEN** the body commands retain authored End-then-Begin projection order
- **THEN** semantic execution order is an analyzer fact rather than a projection reorder

### Requirement: Command discovery is library-owned
Security consumers SHALL be able to enumerate every potentially executable
command without recursively matching syntax-node types.

#### Scenario: New syntax node in a later version
- **WHEN** a later package adds a syntax-node type whose commands are fully supported
- **THEN** those commands appear through the existing occurrence collection
- **THEN** an occurrence-based consumer does not need a new tree visitor merely to discover them
