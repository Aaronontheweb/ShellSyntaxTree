## ADDED Requirements

### Requirement: Parsed commands expose authored nested structure
Every fully parsed command SHALL expose one library-owned syntax root that
preserves the authored nesting and source order of supported command lists,
pipelines, groups, simple commands, loops, and branches.

#### Scenario: Existing flat command receives a structural root
- **WHEN** either parser parses `git status && dotnet test`
- **THEN** the syntax root contains the two simple commands in authored order
- **THEN** the `&&` relationship is preserved structurally

#### Scenario: External code cannot invent syntax nodes
- **WHEN** a consumer references the public syntax-node base type
- **THEN** it cannot derive and inject an external node implementation

### Requirement: Simple-command nodes preserve existing leaves
A simple-command syntax node SHALL expose the existing `Clause` facts rather
than replacing `VerbChain`, `Arg`, `Redirect`, or `ClauseElement` with a second
incompatible leaf model.

#### Scenario: Clause provenance survives structural wrapping
- **WHEN** Bash parses `git -C /repo status > status.txt`
- **THEN** the simple-command node exposes the existing verb, arguments, redirect, and ordered elements
- **THEN** their raw values and source spans retain the v0.2 meanings

### Requirement: Bash for-in loops preserve header and body structure
The Bash parser SHALL represent a supported `for name in words; do body; done`
loop as a typed loop node with its binding name, authored iterable expression,
and nested body block.

#### Scenario: Literal Bash loop
- **WHEN** Bash parses `for f in a.txt b.txt; do rm -- "$f"; done`
- **THEN** the root contains one loop node binding `f`
- **THEN** the iterable preserves `a.txt` and `b.txt` in source order
- **THEN** the body contains one simple command for `rm -- "$f"`

#### Scenario: Nested Bash loop
- **WHEN** Bash parses `for d in a b; do for f in x y; do echo "$d/$f"; done; done`
- **THEN** the outer loop body contains the inner loop node
- **THEN** the `echo` command remains nested beneath both loops

### Requirement: PowerShell foreach loops preserve header and body structure
The PowerShell parser SHALL represent a supported `foreach` statement as a
typed loop node with its binding, iterable expression, and nested body block
without treating the script block as one opaque argument.

#### Scenario: Literal PowerShell foreach
- **WHEN** PowerShell parses `foreach ($f in @('a.txt', 'b.txt')) { Remove-Item -LiteralPath $f }`
- **THEN** the root contains one loop node binding `f`
- **THEN** the iterable preserves the two literal array elements
- **THEN** the body contains one simple command for `Remove-Item`

### Requirement: Condition loops and branches preserve executable regions
The parser SHALL preserve the condition, every branch body, and the
continuation after each supported Bash or PowerShell condition loop or branch
as distinct structural regions.

#### Scenario: Bash while condition contains a command
- **WHEN** Bash parses `while curl https://example.invalid/ready; do echo waiting; done`
- **THEN** the condition block contains the `curl` command
- **THEN** the body block contains the `echo` command

#### Scenario: PowerShell branch preserves both alternatives
- **WHEN** PowerShell parses `if (Test-Path a.txt) { Remove-Item a.txt } else { Write-Output missing }`
- **THEN** the condition, then body, and else body remain distinct regions
- **THEN** no branch is discarded based on predicted runtime behavior

### Requirement: Source ranges are exact or explicitly unavailable
Each direct-source structural node SHALL carry a source range into
`ParsedCommand.Source`. A node lifted from decoded, escaped, or encoded wrapper
content SHALL report an unavailable outer range unless an exact mapping exists.

#### Scenario: Direct loop span
- **WHEN** Bash parses a direct `for` loop
- **THEN** the loop range starts at `for` and ends after `done`

#### Scenario: Decoded wrapper span
- **WHEN** PowerShell lifts a command from a decoded `-EncodedCommand` payload
- **THEN** the inner node does not claim an approximate range in the encoded outer source

### Requirement: Unsupported executable structure fails closed
The parser SHALL set `ParsedCommand.IsUnparseable=true` whenever it cannot
account for every potentially executable region in an input. Preserved partial
syntax SHALL be diagnostic evidence only.

#### Scenario: Arithmetic contains command substitution
- **WHEN** Bash encounters `for ((i = $(next); i < 10; i++)); do run "$i"; done` before that form is supported
- **THEN** the result is unparseable
- **THEN** the parser does not treat the arithmetic header as harmless opaque text

#### Scenario: Unknown PowerShell expression boundary
- **WHEN** a PowerShell control-flow expression contains execution-bearing syntax the parser cannot delimit completely
- **THEN** the result is unparseable even if the body commands were discovered
