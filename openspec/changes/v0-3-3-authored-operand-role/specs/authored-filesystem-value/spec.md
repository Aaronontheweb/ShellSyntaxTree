## ADDED Requirements

### Requirement: Authored filesystem value is additive and fail-closed

`AnalyzedArgument` SHALL expose an additive `AuthoredFileSystemValue` property
of type `ShellValueDomain` with an assembly-owned setter. Every parser-produced
argument SHALL set the property to a non-null domain. The strict result SHALL
be `ShellValueDomain.Unknown`.

The only positive alternatives in this release SHALL be `Exact` and
`FiniteSet`. A parser path that would produce `IntegerRange`, `Concatenation`,
`PathPattern`, an unrecognized alternative, or no domain SHALL publish
`Unknown` instead. Existing public members and signatures SHALL remain
unchanged. The property SHALL participate in generated record equality,
hashing, `ToString()`, reflection, and default serializer shape.

#### Scenario: Ordinary unknown argument remains strict

- **WHEN** an argument lacks a complete authored local-filesystem proof
- **THEN** `AuthoredFileSystemValue` is `Unknown`
- **AND** no consumer can infer filesystem authority from that default

#### Scenario: Public API extends 0.3.2

- **WHEN** the 0.3.3 public surface is compared with public 0.3.2
- **THEN** `AuthoredFileSystemValue` is the only public API addition for this
  capability
- **AND** no 0.3.2 member or signature changes

#### Scenario: Unsupported value-domain alternative fails closed

- **WHEN** projection encounters a value-domain alternative other than
  `Exact` or `FiniteSet`
- **THEN** `AuthoredFileSystemValue` is `Unknown`

### Requirement: Strong local-path binding is separate from compatibility heuristics

A positive `AuthoredFileSystemValue` SHALL require a parser-owned binding that
proves the accepted argument position is a local filesystem value for every
syntax shape accepted by that binding. The implementation SHALL keep this
audited binding catalog separate from compatibility `Arg.IsPath`,
`ClauseElement.IsPath`, `FileVerbs`, generic lexical path shape, and generic
positional fallback rules. None of those compatibility facts SHALL create a
positive domain by themselves.

The audited catalog MAY use reusable binding categories and data entries. Each
entry SHALL define the complete accepted argument shape needed to distinguish
local path values from flags, flag values, stream sentinels, remote endpoints,
filters, names relative to another operand, and ordinary data. An unaudited or
ambiguous position SHALL remain `Unknown`.

#### Scenario: Audited cat operand supplies a local value

- **GIVEN** the occurrence cwd is exactly `/work`
- **WHEN** Bash parses `cat README.md`
- **THEN** the `README.md` argument has
  `AuthoredFileSystemValue=Exact("/work/README.md")`
- **AND** its lexical `AuthoredPathShape` may remain `Unknown`

#### Scenario: Broad compatibility path result does not supply the strong fact

- **WHEN** Bash parses each of
  `python -c 'print(1)'`, `test 1 -eq 1`, and `head -n 10 README`
- **THEN** `print(1)`, `1`, `10`, and every other unaudited position have
  `AuthoredFileSystemValue=Unknown`
- **AND** any compatibility `IsPath` result is unchanged

#### Scenario: Remote endpoint is not a local path

- **WHEN** Bash parses `scp user@example.invalid:/srv/file .`
- **THEN** the remote endpoint has `AuthoredFileSystemValue=Unknown`
- **AND** no lexical slash or compatibility path bit turns it into a local
  filesystem value

#### Scenario: Path-shaped data remains unknown

- **WHEN** an argument is `/api/v1`, `example/project`, `org/image`, or a URL
  without an audited local-path binding
- **THEN** `AuthoredFileSystemValue` is `Unknown`

#### Scenario: Stream sentinels remain unknown

- **WHEN** Bash parses `curl --output=-` or `tar --file=-`
- **THEN** the inline value has `AuthoredFileSystemValue=Unknown`

#### Scenario: Audited cat stream sentinel remains unknown

- **WHEN** Bash parses `cat -` or `cat -- -`
- **THEN** the `-` operand has `AuthoredFileSystemValue=Unknown`
- **AND** the audited binder does not reinterpret standard input as a local
  filesystem path

### Requirement: Positive authored paths are transform-safe

A positive `AuthoredFileSystemValue` SHALL prove that every represented
authored candidate remains exactly one filesystem argument under the standard
field-splitting and pathname-expansion semantics accepted by
`PublishAuthoredSourceFacts`. The proof SHALL use shell-value provenance, not
only the final characters of `AuthoredValue`.

An active unquoted split, active pathname expansion, zero-or-many field
cardinality, unresolved fragment, opaque source, or explicit shell-state
mutation that invalidates the proof SHALL produce `Unknown`. The parser SHALL
NOT enumerate the filesystem to create this fact. Quoting or escaping MAY
prove that whitespace or metacharacters remain literal.

#### Scenario: D14 candidates are transform-safe

- **GIVEN** `PublishAuthoredSourceFacts=true`
- **AND** the occurrence cwd is exact
- **WHEN** Bash parses
  `for f in src/A.cs src/B.cs; do cat /work/$f; done`
- **THEN** the `cat` argument has effective `Value=Unknown`
- **AND** its compatibility `Argument.IsPath` is false
- **AND** its `AuthoredValue` is the finite set
  `[/work/src/A.cs, /work/src/B.cs]`
- **AND** its `AuthoredFileSystemValue` is the normalized finite set
  `[/work/src/A.cs, /work/src/B.cs]`

#### Scenario: Unquoted field splitting blocks the projection

- **GIVEN** `PublishAuthoredSourceFacts=true`
- **WHEN** Bash parses
  `for f in 'src/A.cs /etc/passwd'; do cat /work/$f; done`
- **THEN** `AuthoredValue` may contain the bounded authored word
  `/work/src/A.cs /etc/passwd`
- **BUT** `AuthoredFileSystemValue` is `Unknown`
- **AND** the parser does not claim one runtime path argument

#### Scenario: Active glob remains strict

- **WHEN** an audited path position contains an active unquoted glob
- **THEN** `AuthoredFileSystemValue` is `Unknown`
- **AND** this release does not enumerate matches or publish a `PathPattern`

#### Scenario: Substituted text is not recursively expanded

- **GIVEN** an exact occurrence cwd of `/work`
- **WHEN** Bash analyzes
  `for f in '$HOME/literal'; do cat "$f"; done`
- **THEN** `AuthoredFileSystemValue` is
  `Exact("/work/$HOME/literal")`
- **AND** the parser does not expand the substituted `$HOME` text again

#### Scenario: Quoting controls literal glob provenance

- **GIVEN** an exact occurrence cwd of `/work`
- **WHEN** Bash analyzes
  `for f in '*.txt'; do cat "$f"; done`
- **THEN** `AuthoredFileSystemValue` is `Exact("/work/*.txt")`
- **BUT WHEN** the loop body uses unquoted `cat $f`
- **THEN** `AuthoredFileSystemValue` is `Unknown`

#### Scenario: Quoted whitespace can remain one field

- **WHEN** an audited path argument has an exact quoted value
  `"/work/file name.txt"`
- **AND** the local resolver accepts it
- **THEN** the whitespace does not by itself force the domain to `Unknown`

### Requirement: Positive values are normalized local filesystem paths

Every `Exact` or `FiniteSet` value in `AuthoredFileSystemValue` SHALL be an
absolute local filesystem path normalized by the existing shell-specific
resolver against the occurrence's exact cwd. An unknown cwd, invalid path,
remote endpoint, non-filesystem provider, unresolved provider, or path-style
conflict SHALL produce `Unknown`. `AuthoredPathShape` MAY reject a candidate
but SHALL NOT create a positive domain.

#### Scenario: Relative audited path uses the exact occurrence cwd

- **GIVEN** an exact occurrence cwd of `/work/project`
- **WHEN** Bash parses `cat src/App.cs`
- **THEN** `AuthoredFileSystemValue` is
  `Exact("/work/project/src/App.cs")`

#### Scenario: Unknown cwd remains strict

- **WHEN** an audited relative path has no exact occurrence cwd
- **THEN** `AuthoredFileSystemValue` is `Unknown`

#### Scenario: Unknown executable does not gain authority from path shape

- **WHEN** an unknown executable receives `/work/value`
- **THEN** `AuthoredFileSystemValue` is `Unknown`
- **AND** the absolute spelling alone does not supply binding proof

### Requirement: Inline projection and abstract joins preserve exact argument semantics

When one authored element projects a flag and a bound value, the parser SHALL
assign `AuthoredFileSystemValue` to each `AnalyzedArgument` independently.
Only an audited local-path value MAY receive a positive domain. Abstract-state
analysis SHALL replay the complete argument vector for every reachable visit.
The joined result SHALL be positive only when every visit binds the same
analyzed argument to an audited local-path slot and every represented value is
transform-safe and locally resolved. A binding conflict, missing visit fact,
over-limit union, or ambiguous option binding SHALL produce `Unknown`.

#### Scenario: Unmodeled inline output remains strict

- **WHEN** Bash parses `curl --output=/work/out`
- **THEN** the projected inline value has
  `AuthoredFileSystemValue=Unknown` until a complete audited binding models
  that option shape

#### Scenario: Inline data remains strict

- **WHEN** Bash parses `curl --data=/api/v1`
- **THEN** the projected inline value has
  `AuthoredFileSystemValue=Unknown`

#### Scenario: Audited PowerShell inline value retains argument coordinates

- **WHEN** the selected dialect parses
  `Get-Content -LiteralPath:C:\work\a.txt`
- **THEN** the projected parameter argument has
  `AuthoredFileSystemValue=Unknown`
- **AND** the projected value argument has
  `AuthoredFileSystemValue=Exact("C:/work/a.txt")`

#### Scenario: Audited loop visits disagree about option and operand meaning

- **WHEN** Bash analyzes
  `for f in -n README; do cat "$f"; done`
- **THEN** the expansion is an option in one visit and an audited filesystem
  operand in the other
- **AND** its joined `AuthoredFileSystemValue` is `Unknown`

#### Scenario: Every audited loop visit is a filesystem operand

- **GIVEN** an exact occurrence cwd of `/work`
- **WHEN** Bash analyzes
  `for f in README LICENSE; do cat "$f"; done`
- **THEN** the joined `AuthoredFileSystemValue` is the finite set
  `[/work/README, /work/LICENSE]`

#### Scenario: Over-limit finite union remains strict

- **WHEN** reachable audited path values exceed the existing finite-domain cap
- **THEN** `AuthoredFileSystemValue` is `Unknown`

### Requirement: Bash and PowerShell share one filesystem-value contract

Bash and PowerShell SHALL publish the same `ShellValueDomain` vocabulary for
`AuthoredFileSystemValue`. PowerShell SHALL require an audited binding from the
selected dialect and a target that its resolver proves is a local filesystem
path. A filter, rename fragment, non-filesystem provider, unresolved provider,
remote native endpoint, or dialect-ambiguous binding SHALL remain `Unknown`.

#### Scenario: PowerShell filesystem parameter supplies a local value

- **GIVEN** the selected PowerShell dialect binds `-LiteralPath`
- **AND** its resolver proves `C:\work\a.txt` is a local Windows path
- **WHEN** PowerShell parses
  `Get-Content -LiteralPath C:\work\a.txt`
- **THEN** the path argument has
  `AuthoredFileSystemValue=Exact("C:/work/a.txt")`

#### Scenario: PowerShell filter is not a path

- **WHEN** PowerShell parses `Get-ChildItem C:\work *.cs`
- **THEN** the audited local path, if proved, is independent from the filter
- **AND** the `*.cs` filter has `AuthoredFileSystemValue=Unknown`

#### Scenario: Rename fragment is not an independently resolved path

- **WHEN** PowerShell parses `Rename-Item C:\old new`
- **THEN** the `new` argument has `AuthoredFileSystemValue=Unknown`

#### Scenario: Non-filesystem provider remains strict

- **WHEN** an audited PowerShell parameter targets `Registry::HKEY_LOCAL_MACHINE`
  or another non-filesystem provider
- **THEN** `AuthoredFileSystemValue` is `Unknown`

#### Scenario: Native remote endpoint remains strict

- **WHEN** native PowerShell parses `scp user@example.invalid:/srv/file .`
- **THEN** the remote endpoint has `AuthoredFileSystemValue=Unknown`

#### Scenario: Selected dialect controls binding

- **WHEN** Windows PowerShell 5.1 is the selected dialect
- **THEN** no PowerShell 7-only parameter binding creates a positive domain

### Requirement: Consumers apply their own policy after the parser proof

A positive `AuthoredFileSystemValue` SHALL NOT mean that a path exists, is
safe, is authorized, or lies inside a trust zone. A security consumer that
accepts this source-oriented fact SHALL require a complete occurrence, accept
the documented authored-source environment assumption, accept only `Exact`
or `FiniteSet`, and evaluate every represented path through its own path and
trust-zone policy. It SHALL independently evaluate command identity,
redirects, substitutions, other arguments, and every other occurrence.

`AuthoredValue`, `AuthoredPathShape`, compatibility `IsPath`, and executable
names SHALL NOT substitute for `AuthoredFileSystemValue`.

#### Scenario: D14 enters path policy without becoming an allow decision

- **GIVEN** D14 publishes a finite `AuthoredFileSystemValue`
- **WHEN** a consumer accepts the authored-source contract
- **THEN** it checks each normalized path through its path policy
- **AND** the parser fact alone does not allow the invocation

#### Scenario: One unsafe finite path blocks reuse

- **GIVEN** a finite domain contains one path outside consumer authority
- **WHEN** the consumer evaluates the domain
- **THEN** it does not reuse approval for the invocation

#### Scenario: All other policy-relevant facts remain required

- **GIVEN** an argument has a positive `AuthoredFileSystemValue`
- **WHEN** identity, redirect, substitution, ancestry, or completeness facts
  remain unresolved
- **THEN** the filesystem value does not relax those facts
