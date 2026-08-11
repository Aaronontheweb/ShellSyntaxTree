## ADDED Requirements

### Requirement: Bounded integer and concatenation domains

ShellSyntaxTree SHALL append library-owned `ShellValueDomain.IntegerRange` and
`ShellValueDomain.Concatenation` alternatives.

`IntegerRange` SHALL contain inclusive signed 64-bit bounds. It SHALL reject a
minimum greater than its maximum. Values SHALL use canonical signed ASCII
decimal. A plus sign and leading zeros SHALL NOT be represented. Bash status
SHALL use the nonnegative `0..255` subset.

`Concatenation` SHALL contain two through 16 normalized immutable parts. Parts
SHALL be `Exact`, `FiniteSet`, or `IntegerRange`. The library SHALL reject
unknown, pattern, and nested-concatenation parts. It SHALL remove empty exact
parts and merge adjacent exact parts. It SHALL return `Exact` when all parts
become exact. It SHALL return the sole non-exact domain when one part remains.
More than 16 normalized parts SHALL produce `Unknown`, not truncation.

#### Scenario: Quoted Bash exit status is bounded

- **WHEN** the Bash parser analyzes `status-report "$?"`
- **THEN** the argument value is `IntegerRange(0, 255)`
- **AND** the parser does not enumerate 256 strings

#### Scenario: Embedded status preserves literals

- **WHEN** the Bash parser analyzes `echo "---EXIT $?---"`
- **THEN** the argument value is `Concatenation`
- **AND** its parts are `Exact("---EXIT ")`, `IntegerRange(0, 255)`, and
  `Exact("---")` in that order

#### Scenario: Multiple quoted expansions remain bounded

- **WHEN** the Bash parser analyzes `status-report "left=$?;right=$?"`
- **THEN** both status positions are bounded integer parts
- **AND** no correlation claim between those parts is required

#### Scenario: Unquoted status remains strict

- **WHEN** the default Bash parser analyzes `status-report $?`
- **THEN** the argument value is `Unknown`
- **AND** the parser does not assume an ambient `IFS`

#### Scenario: Status cannot select an executable

- **WHEN** the Bash parser analyzes `"$?" --version`
- **THEN** the command identity remains dynamic
- **AND** the parse is unparseable under the existing identity rule

#### Scenario: Status cannot name a redirect target

- **WHEN** the Bash parser analyzes `status-report ok > "$?"`
- **THEN** the redirect target does not gain bounded authority
- **AND** the affected command remains strict

#### Scenario: Positional parameter is not bounded status

- **WHEN** the Bash parser analyzes `status-report "$1"`
- **THEN** the argument value is not `IntegerRange`
- **AND** it retains the existing positional-parameter result

#### Scenario: Option-shaped finite data is not inert

- **WHEN** an authored finite value contains `-o` or `--execute`
- **THEN** ShellSyntaxTree reports bounded shell data only
- **AND** it does not claim how an external executable treats that data

### Requirement: Effective and authored values stay distinct

`AnalyzedArgument.Value` SHALL retain its effective shell-value meaning.
`AnalyzedArgument.AuthoredValue` SHALL describe the authored shell word before
field splitting, pathname expansion, ambient attributes, and ambient `IFS` can
transform it.

ShellSyntaxTree SHALL append `BashParserOptions.PublishAuthoredSourceFacts`.
Its default SHALL be `false`. The default SHALL retain the released
unparseable static-loop result under `BashInitialStateMode.Unknown`.

#### Scenario: Opt-in exposes a static authored word

- **WHEN** default `Unknown` mode with `PublishAuthoredSourceFacts=true` analyzes
  `for f in a b; do show "$f"; done`
- **THEN** the body occurrence is structurally complete for authored source
- **AND** effective `Value` is `Unknown`
- **AND** `AuthoredValue` is `FiniteSet(["a", "b"])`

#### Scenario: Default options preserve released loop admission

- **WHEN** default `Unknown` mode with `PublishAuthoredSourceFacts=false`
  analyzes the same loop
- **THEN** `IsUnparseable` is true
- **AND** `Commands` and `Clauses` are empty
- **AND** no existing consumer sees a new complete occurrence

#### Scenario: Isolated mode keeps its existing effective proof

- **WHEN** `IsolatedNonInteractive` analyzes the same loop
- **THEN** `Value` is `FiniteSet(["a", "b"])`
- **AND** `AuthoredValue` is the same finite set

#### Scenario: Ambient state is not relabeled as effective fact

- **GIVEN** ambient Bash can change attributes or `IFS`
- **WHEN** opt-in authored-source mode analyzes the static loop
- **THEN** effective `Value` remains `Unknown`
- **AND** only `AuthoredValue` contains authored word candidates

#### Scenario: Unquoted word is pre-field-splitting evidence

- **WHEN** opt-in authored-source mode analyzes
  `for f in src/A.cs src/B.cs; do show /work/$f; done`
- **THEN** `AuthoredValue` contains the two complete `/work/` words
- **AND** effective `Value` remains `Unknown`
- **AND** the parser does not claim an argv count or ambient `IFS`

#### Scenario: Explicit source attribute mutation remains strict

- **WHEN** source declares a relevant nameref or integer binding before use
- **THEN** the affected authored value is `Unknown`
- **AND** hidden execution keeps the region incomplete or unparseable

#### Scenario: Unassigned ambient variable remains unknown

- **WHEN** the parser analyzes `show "$external_value"`
- **THEN** it does not inspect the process environment
- **AND** both value projections are `Unknown` if a result can be published

### Requirement: Authored lexical path shape

ShellSyntaxTree SHALL append `ShellPathShape` with `Unknown = 0`, `Posix = 1`,
and `Windows = 2`. `AnalyzedArgument.AuthoredPathShape` SHALL expose this fact.

The fact SHALL describe lexical form only. It SHALL NOT claim that an external
executable treats the word as a filesystem operand. It SHALL NOT create
filesystem authority. Classification SHALL apply this precedence to each
represented word: URI shape matching `^[A-Za-z][A-Za-z0-9+.-]*://` produces
`Unknown`; otherwise drive, UNC, or backslash-bearing form produces `Windows`;
otherwise absolute, `./`, `../`, eligible tilde, or slash-bearing form produces
`Posix`; otherwise the result is `Unknown`.

A bounded domain SHALL publish a known shape only when every represented word
proves the same known shape. Mixed known shapes, any member with unknown shape,
or a symbolic concatenation without one provable shape SHALL produce `Unknown`.

#### Scenario: Static loop composes a source path shape

- **WHEN** default mode with `PublishAuthoredSourceFacts=true` analyzes
  `for f in src/A.cs src/B.cs; do cat "/work/$f"; done`
- **THEN** effective `Value` is `Unknown`
- **AND** `AuthoredValue` is
  `FiniteSet(["/work/src/A.cs", "/work/src/B.cs"])`
- **AND** `AuthoredPathShape` is `Posix`

#### Scenario: Eligible tilde prefix uses configured home

- **GIVEN** configured home `/home/user`
- **WHEN** `Unknown` mode with `PublishAuthoredSourceFacts=true` analyzes
  `for f in A.cs B.cs; do cat ~/"repo/$f"; done`
- **THEN** effective `Value` is `Unknown`
- **AND** `AuthoredValue` contains the two `/home/user/repo/` words
- **AND** `AuthoredPathShape` is `Posix`

#### Scenario: Quoted tilde remains literal

- **GIVEN** configured home `/home/user`
- **WHEN** `Unknown` mode with `PublishAuthoredSourceFacts=true` analyzes
  `for f in A.cs B.cs; do cat "~/repo/$f"; done`
- **THEN** effective `Value` is `Unknown`
- **AND** `AuthoredValue` contains the two literal `~/repo/` words
- **AND** `AuthoredPathShape` is `Posix`
- **AND** the parser does not claim home expansion

#### Scenario: Windows drive takes precedence over slash shape

- **WHEN** an authored word is `C:/work/file.txt`
- **THEN** `AuthoredPathShape` is `Windows`

#### Scenario: Mixed domains have unknown shape

- **WHEN** `AuthoredValue` is `FiniteSet(["/tmp/a", "C:\\work\\b"])`
- **THEN** `AuthoredPathShape` is `Unknown`

#### Scenario: Partially unknown domains have unknown shape

- **WHEN** `AuthoredValue` is `FiniteSet(["/tmp/a", "bare-name"])`
- **THEN** `AuthoredPathShape` is `Unknown`

#### Scenario: Concatenation can prove one shape

- **WHEN** `AuthoredValue` concatenates `Exact("/tmp/status-")` with
  `IntegerRange(0, 255)`
- **THEN** `AuthoredPathShape` is `Posix`

#### Scenario: URI schemes take precedence

- **WHEN** authored words are `https://example.invalid/a`,
  `ssh://example.invalid/a`, and `custom+v1://example.invalid/a`
- **THEN** each `AuthoredPathShape` is `Unknown`

#### Scenario: Repository slug is shape, not authority

- **WHEN** the parser analyzes `gh repo view example/project`
- **THEN** the slash-bearing argument MAY have `Posix` shape
- **AND** ShellSyntaxTree does not claim filesystem operand semantics

#### Scenario: Container image is shape, not authority

- **WHEN** the parser analyzes `docker pull example/project`
- **THEN** the slash-bearing argument MAY have `Posix` shape
- **AND** the shape does not create filesystem authority

#### Scenario: URI stays separate from path shape

- **WHEN** the parser analyzes `fetch https://example.invalid/api/v1`
- **THEN** URI recognition takes precedence and `AuthoredPathShape` is `Unknown`
- **AND** the parser does not claim a filesystem operand

#### Scenario: Slash-bearing data stays semantically unknown

- **WHEN** the parser analyzes `printf '%s' /api/v1`
- **THEN** `/api/v1` MAY have `Posix` shape
- **AND** ShellSyntaxTree does not claim how `printf` interprets it

#### Scenario: Bare dynamic filename has unknown shape

- **WHEN** the parser analyzes `show "$external_value"`
- **THEN** `AuthoredPathShape` is `Unknown`
- **AND** a consumer does not infer path safety

#### Scenario: Runtime iterator remains unknown

- **WHEN** `IsolatedNonInteractive` analyzes
  `for f in $(find /work -name '*.cs'); do cat "$f"; done`
- **THEN** the parser discovers the substitution command
- **AND** it does not execute `find` or enumerate its output
- **AND** the loop-dependent authored value and path shape are `Unknown`

### Requirement: Consumer boundary stays shell-general

ShellSyntaxTree SHALL report shell structure, value domains, proof provenance,
lexical path shape, redirects, and completeness. It SHALL NOT label a fact safe
or grant authority.

A consumer SHALL fail closed on unknown policy facts. Use of `AuthoredValue`
SHALL be an explicit threat-model choice. Path shape SHALL only add conservative
path review and SHALL NOT grant authority.

#### Scenario: Existing consumer remains compatible

- **WHEN** a consumer upgrades and keeps default parser options
- **THEN** `IsUnparseable`, `Commands`, `Clauses`, and `IsComplete` retain
  v0.3.0 loop behavior
- **AND** effective `Value` retains v0.3.0 semantics

#### Scenario: Authored-source consumer declares its boundary

- **WHEN** a consumer enables and uses `AuthoredValue` for approval matching
- **THEN** it documents that ambient attributes, ambient `IFS`, and field
  splitting are outside the approval claim
- **AND** it retains effective facts for execution and deny checks

#### Scenario: Path shape cannot authorize slash-bearing value

- **WHEN** a consumer receives `AuthoredPathShape=Posix`
- **THEN** it MAY apply conservative path checks
- **AND** it does not infer filesystem operand semantics or authority

#### Scenario: Unknown future domain fails closed

- **WHEN** a consumer receives an unrecognized `ShellValueDomain` subtype
- **THEN** it prompts or denies when the value affects policy

### Requirement: Sanitized v0.3.1 evidence and corpus

The exact D01-D18 evidence SHALL live at `evidence/approval-matrix.json` and
SHALL match the paired Netclaw artifact byte-for-byte. D02, D10, and D14 SHALL
be executable Bash corpus or focused regression cases with complete facts.

The corpus SHALL include dynamic identity, unquoted field splitting, redirect
target, command substitution, explicit attribute mutation, multiple expansion,
option-shaped finite data, slash-bearing non-path data, and resource-cap cases.
Sanitization SHALL follow `SPEC.md` section 14. The automated PII gate SHALL
scan the OpenSpec evidence file and corpus JSON.

#### Scenario: PII audit passes

- **WHEN** the PII audit scans the change and executable corpus
- **THEN** it finds no local username, private repository, channel, thread,
  host, email, token, or secret

#### Scenario: Exact harvested cases stay linked

- **WHEN** either repository changes a D01-D18 classification or command
- **THEN** both evidence files change together
- **AND** byte equality is checked during review

#### Scenario: PowerShell projection is exact and compatible

- **WHEN** shared argument projection gains the additive properties
- **THEN** each PowerShell literal, dynamic, path, and provider argument sets
  `AuthoredValue` equal to its existing effective `Value`
- **AND** `AuthoredPathShape` is `Unknown`
- **AND** every v0.3.0 PowerShell result otherwise remains unchanged
