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
for enclosing and enclosed regions.

#### Scenario: Iterator precedes body
- **WHEN** Bash parses `for f in $(find .); do rm "$f"; done`
- **THEN** the `find` occurrence precedes the `rm` occurrence

#### Scenario: Branches preserve authored order
- **WHEN** PowerShell parses an `if` statement with then and else commands
- **THEN** condition commands precede then-body commands
- **THEN** then-body commands precede else-body commands in the projection

### Requirement: Command discovery is library-owned
Security consumers SHALL be able to enumerate every potentially executable
command without recursively matching syntax-node types.

#### Scenario: New syntax node in a later version
- **WHEN** a later package adds a syntax-node type whose commands are fully supported
- **THEN** those commands appear through the existing occurrence collection
- **THEN** an occurrence-based consumer does not need a new tree visitor merely to discover them
