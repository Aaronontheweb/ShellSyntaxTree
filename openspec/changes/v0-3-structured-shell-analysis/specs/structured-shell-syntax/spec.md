## ADDED Requirements

### Requirement: Parsed commands expose authored nested structure
Every fully parsed command SHALL expose one library-owned syntax root that
preserves the authored nesting and source order of supported command lists,
pipelines, groups, simple commands, loops, and branches.

The public syntax family SHALL be a closed hierarchy of records derived from
`ShellSyntaxNode`. Every node SHALL expose a `ShellSyntaxKind` discriminant and
zero SHALL mean `Unknown`. The locked family SHALL include block, simple
command, pipeline, command list, group, foreach, condition loop, conditional,
conditional branch, and command substitution nodes.

#### Scenario: Existing flat command receives a structural root
- **WHEN** either parser parses `git status && dotnet test`
- **THEN** the syntax root contains the two simple commands in authored order
- **THEN** the `&&` relationship is preserved structurally

#### Scenario: External code cannot invent syntax nodes
- **WHEN** a consumer references the public syntax-node base type
- **THEN** it cannot derive and inject an external node implementation

#### Scenario: Later node kind is not silently authorized
- **WHEN** a later package returns a derived node or kind an older consumer does not recognize
- **THEN** a display visitor may show an unknown node
- **THEN** an authorization visitor fails closed

### Requirement: Simple-command nodes preserve existing leaves
A simple-command syntax node SHALL expose the existing `Clause` facts rather
than replacing `VerbChain`, `Arg`, `Redirect`, or `ClauseElement` with a second
incompatible leaf model. It SHALL also own an authored-order collection of
completely delimited command substitutions evaluated for its words and
redirects, including expanding heredoc bodies. Nested substitutions SHALL
remain attached to the nearest containing simple command; they SHALL NOT be
promoted to unrelated siblings or stored only in a side table.

#### Scenario: Clause provenance survives structural wrapping
- **WHEN** Bash parses `git -C /repo status > status.txt`
- **THEN** the simple-command node exposes the existing verb, arguments, redirect, and ordered elements
- **THEN** their raw values and source spans retain the v0.2 meanings

#### Scenario: Substitution remains attached to its containing command
- **WHEN** Bash parses `rm "$(find /tmp)"`
- **THEN** the `rm` simple-command node retains its unchanged dynamic compatibility argument
- **THEN** its substitutions contain a command-substitution node whose body contains `find`

#### Scenario: Nested substitutions preserve parentage
- **WHEN** a supported shell parses a command substitution inside another substitution
- **THEN** the inner substitution belongs to the simple command inside the outer substitution
- **THEN** the inner substitution is not flattened into the outer command's substitution collection

### Requirement: Executable substitution boundaries are accounted for
Stable v0.3 SHALL recursively parse every completely delimited Bash `$()` or
PowerShell `$()` that can execute while forming a supported simple-command
argument word, redirect value, iterator, expanding heredoc or here-string, or
PowerShell call-operator dynamic identity. The produced value SHALL remain
unknown unless separately proved. If an executable substitution interior
cannot be completely parsed, the whole result SHALL be unparseable.

Bash legacy backtick substitution and execution-bearing PowerShell `@()` or
`@{}` forms outside the locked literal-foreach subset SHALL remain unparseable
until their distinct semantics have complete command discovery. Literal or
escaped substitution-looking text SHALL NOT create syntax or occurrences.

#### Scenario: Bash literal substitution spelling
- **WHEN** Bash parses single-quoted or backslash-escaped `$()` text
- **THEN** no substitution node or command occurrence is created for that text

#### Scenario: PowerShell literal substitution spelling
- **WHEN** PowerShell parses single-quoted, literal-here-string, or backtick-escaped `$()` text
- **THEN** no substitution node or command occurrence is created for that text

#### Scenario: Expandable value executes a substitution
- **WHEN** either shell parses a supported `$()` inside an expandable quoted value or redirect target
- **THEN** every inner command is exposed before the containing command
- **THEN** an unsupported inner executable region makes the whole result unparseable

#### Scenario: Dynamic identity contains a substitution
- **WHEN** PowerShell `&` dynamic invocation contains supported executable `$()` syntax
- **THEN** every inner command remains visible
- **THEN** the containing dynamic command occurrence remains incomplete or the whole result is unparseable

#### Scenario: Bash command-name substitution remains gated
- **WHEN** Bash command-name formation contains executable `$()` syntax
- **THEN** the whole result is unparseable because the outer command identity is runtime-dependent
- **THEN** diagnostic syntax may retain the substitution but command and compatibility projections are empty

#### Scenario: Standalone PowerShell subexpression is not invocation
- **WHEN** PowerShell parses `$(Write-Output Get-Date)` as an expression statement
- **THEN** `Write-Output` is exposed as a substitution command
- **THEN** no outer `Get-Date` command is invented from the produced string

#### Scenario: PowerShell call operator invokes subexpression output
- **WHEN** PowerShell parses `& $(Write-Output Get-Date)`
- **THEN** `Write-Output` is exposed before one incomplete dynamic outer invocation
- **THEN** the produced string is not assumed to equal a static command identity

#### Scenario: PowerShell call operator script block remains gated
- **WHEN** PowerShell encounters `& { Remove-Item target.txt }`
- **THEN** the whole result is unparseable until script-block execution semantics are modeled
- **THEN** the body is not treated as an ordinary opaque argument

### Requirement: Bash for-in loops preserve header and body structure
The Bash parser SHALL represent a supported `for name in words; do body; done`
loop as a typed loop node with its binding name, authored iterable expression,
and nested body block.

#### Scenario: Literal Bash loop
- **WHEN** isolated-mode Bash parses `for f in a.txt b.txt; do rm -- "$f"; done`
- **THEN** the root contains one loop node binding `f`
- **THEN** the iterable preserves `a.txt` and `b.txt` in source order
- **THEN** the body contains one simple command for `rm -- "$f"`

#### Scenario: Nested Bash loop
- **WHEN** isolated-mode Bash parses `for d in a b; do for f in x y; do echo "$d/$f"; done; done`
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

### Requirement: Shared loop structure does not erase shell grammar
`ForEachSyntax` SHALL preserve the normalized binding name, the authored
binding source, the raw iterable source fragment, commands discovered in the
iterator, and the body. It SHALL NOT normalize Bash words and PowerShell
expressions into a false shared expression grammar.

#### Scenario: Iterator with no exact outer span
- **WHEN** a loop is lifted from decoded wrapper content without an exact mapping to the outer source
- **THEN** the iterable raw text remains available
- **THEN** its outer source start and length are null

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
- **WHEN** isolated-mode Bash parses a direct supported `for` loop
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

#### Scenario: Deferred Bash process substitution
- **WHEN** Bash encounters `diff <(git show HEAD) <(git show HEAD~1)` in v0.3
- **THEN** the whole result is unparseable until both commands and the produced descriptors are modeled

#### Scenario: Deferred background execution
- **WHEN** Bash encounters a single-`&` background list in v0.3
- **THEN** the whole result is unparseable until concurrency and state boundaries are specified

#### Scenario: Ordinary PowerShell script-block argument
- **WHEN** PowerShell parses a script block as an ordinary command argument rather than a recognized statement body
- **THEN** it remains an opaque dynamic argument
- **THEN** the parser does not invent the block contents as commands that necessarily execute
