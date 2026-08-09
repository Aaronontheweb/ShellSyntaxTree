## ADDED Requirements

### Requirement: Parsed commands expose authored nested structure
Every fully parsed command SHALL expose one library-owned syntax root that
preserves the authored nesting and source order of supported command lists,
pipelines, groups, simple commands, substitutions, execution regions, and
foreach loops.

The public syntax family SHALL be a closed hierarchy of records derived from
`ShellSyntaxNode`. Every node SHALL expose a `ShellSyntaxKind` discriminant and
zero SHALL mean `Unknown`. The locked family SHALL include block, simple
command, pipeline, command list, group, foreach, condition loop, conditional,
conditional branch, command substitution, and execution-region nodes.
Condition-loop and conditional node kinds are reserved structural vocabulary;
stable v0.3 does not emit them because their grammar remains fail closed.

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
redirects, including expanding heredoc bodies, and an authored-order
collection of execution-bearing regions bound to its arguments. Nested
substitutions and execution regions SHALL
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

#### Scenario: Cmdlet-owned execution region preserves its host
- **WHEN** PowerShell parses `Get-ChildItem | ForEach-Object { Remove-Item $_ }`
- **THEN** the `ForEach-Object` simple-command node owns one execution region
- **THEN** the region body contains `Remove-Item`
- **THEN** the authored script-block argument remains on the host `Clause`

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

#### Scenario: PowerShell call operator script block is a direct execution region
- **WHEN** PowerShell encounters `& { Remove-Item target.txt }`
- **THEN** the root contains a synchronous, once-per-invocation execution region
- **THEN** the body contains `Remove-Item`
- **THEN** no synthetic outer command occurrence is invented for `&`

### Requirement: Execution-bearing regions are typed independently from scope
An `ExecutionRegionSyntax` SHALL represent a completely delimited authored
body that may execute because of a direct shell invocation operator or a
recognized command argument binding. It SHALL expose an execution origin,
phase, timing, cardinality, exact-or-null source range, optional host
`ClauseElement` coordinate, and body. Origin, phase, timing, and cardinality
SHALL be independent enum facts whose zero values are `Unknown`.

Origin SHALL have `Unknown`, `DirectCall`, `DotSource`, and `CommandArgument`
values. Direct call and dot-source regions SHALL retain their distinct origins
even when source text is unavailable to a consumer. A command-owned region
SHALL use `CommandArgument`.

The region SHALL NOT expose one shared/isolated scope flag. PowerShell
variable, working-directory, command-resolution, runspace, and process state
do not share one boundary: for example, `& {}` isolates ordinary variable
assignment while sharing location. Those effects SHALL remain shell-specific
analysis and SHALL be reflected in occurrence facts and following state.

The containing command's `ExecutionRegions` collection SHALL preserve authored
script-block order. Semantic phase order MAY differ and SHALL be consumed by
the shell-specific analyzer rather than by reordering authored syntax. A direct
`& {}` or `. {}` region SHALL appear as a statement, SHALL use `DirectCall` or
`DotSource` respectively, and SHALL have no host element coordinate. A region
bound to a command argument SHALL be attached to
that `SimpleCommandSyntax` and SHALL identify the exact script-block
`ClauseElement` when the binding is proved.

#### Scenario: Reordered pipeline phases retain both orders
- **WHEN** PowerShell parses `1 | ForEach-Object -End { Write-Output end } -Begin { Write-Output begin } -Process { Write-Output $_ }`
- **THEN** the execution-region collection retains the authored `End`, `Begin`, `Process` order
- **THEN** each region carries its semantic phase
- **THEN** state analysis applies Begin, Process, End semantics without rewriting the authored tree

#### Scenario: Dot-sourced block has current-scope effects
- **WHEN** PowerShell parses `. { $x = 'changed'; Set-Location /tmp }`
- **THEN** the body is a synchronous once-per-invocation execution region
- **THEN** supported variable and location changes propagate according to dot-source semantics
- **THEN** the `.` operator does not become a synthetic command occurrence

#### Scenario: Direct block arguments remain atomic until binding is modeled
- **WHEN** PowerShell encounters `& { Write-Output $args } alpha` or `. { Write-Output $args } alpha`
- **THEN** the whole parse is unparseable
- **THEN** no body command is exposed as authorization evidence from a partial binding model

#### Scenario: Leading region parameter declaration remains atomic
- **WHEN** an execution region begins with `param(...)`
- **THEN** the whole parse is unparseable until bounded declaration grammar is implemented
- **THEN** a realistic argument completer is not partially authorized from only its post-declaration body

#### Scenario: Known non-executing script-block data stays data
- **WHEN** PowerShell parses the static authored command `Write-Output { Remove-Item target.txt }`
- **THEN** the script block remains one opaque compatibility argument
- **THEN** no execution region or `Remove-Item` occurrence is invented

#### Scenario: Unknown script-block receiver over-approximates execution
- **WHEN** command resolution or script-block parameter binding cannot prove whether a receiver executes its block
- **THEN** the completely parsed block is retained as an execution region with unknown facts
- **THEN** affected occurrences are incomplete
- **THEN** an unsupported block interior makes the whole result unparseable rather than hiding commands

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

#### Scenario: Foreach remains contextual in a pipeline command slot
- **WHEN** PowerShell parses `Write-Output x | foreach ($_)`
- **THEN** `foreach` remains an ordinary command or alias pipeline stage
- **THEN** no loop node is invented
- **THEN** the parenthesized argument remains opaque

#### Scenario: Foreach requires a statement boundary
- **WHEN** PowerShell parses a `foreach` statement adjacent to another command
- **THEN** `;` or newline may terminate the statement
- **THEN** `|`, `&&`, and `||` do not admit the `foreach` statement as a pipeline element

### Requirement: Shared loop structure does not erase shell grammar
`ForEachSyntax` SHALL preserve the normalized binding name, the authored
binding source, the raw iterable source fragment, commands discovered in the
iterator, and the body. It SHALL NOT normalize Bash words and PowerShell
expressions into a false shared expression grammar.

#### Scenario: Iterator with no exact outer span
- **WHEN** a loop is lifted from decoded wrapper content without an exact mapping to the outer source
- **THEN** the iterable raw text remains available
- **THEN** its outer source start and length are null

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

#### Scenario: Unmodeled execution-bearing Bash builtin
- **WHEN** Bash encounters a direct or statically dispatch-wrapped builtin whose arguments can execute, defer, or recursively evaluate authored text
- **THEN** the whole result is unparseable until that builtin's state and execution semantics are modeled
- **THEN** no complete outer occurrence hides the unmodeled executable region

#### Scenario: Ordinary PowerShell script-block argument
- **WHEN** PowerShell parses a script block for a canonical receiver proved not to execute that argument
- **THEN** it remains an opaque dynamic argument
- **THEN** the parser does not invent the block contents as commands
