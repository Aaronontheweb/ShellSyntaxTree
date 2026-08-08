## ADDED Requirements

### Requirement: Existing Clause projection remains conservative
`ParsedCommand.Clauses` SHALL remain available in v0.3 and SHALL contain every
authored simple command that may execute for a fully parseable result, including
nested iterator, loop-body, substitution, and execution-region commands from
the stable-v0.3 grammar.

Existing raw spelling, decoded values, source spans, and unaffected v0.2 leaf
classifications SHALL remain compatible. A paired real-shell oracle MAY
correct a v0.2 `Arg`, `Redirect`, or `ClauseElement` path or expansion
classification that is false because lexical provenance or consumer/binding
context was lost. This includes false exact paths, false `Glob` or `Tilde`
claims, and avoidable `DynamicSkip` results. Every such correction SHALL be
documented and corpus-pinned; compatibility does not require preserving a
security defect. When control-flow analysis cannot prove the cwd used by a
relative compatibility operand, the projection SHALL clear any false exact
resolution rather than publish one parse-order path as authoritative. It SHALL
replace any false exact cwd attribution with the existing dynamic cwd marker;
it SHALL NOT omit the marker and thereby remove a v0.2 consumer's fail-closed
signal.

#### Scenario: Old consumer sees loop body command
- **WHEN** isolated-mode Bash fully parses `for f in a b; do rm "$f"; done`
- **THEN** the compatibility clauses include the authored `rm` command
- **THEN** its authored variable argument remains conservatively dynamic rather than being silently replaced

#### Scenario: Old consumer sees a script-block body command
- **WHEN** PowerShell fully parses `Get-ChildItem | ForEach-Object { Remove-Item $_ }`
- **THEN** compatibility clauses contain the host and the authored `Remove-Item` body command
- **THEN** the host's script-block argument remains conservatively dynamic
- **THEN** no synthetic operator is invented between host and body

#### Scenario: Structural boundaries do not invent operators
- **WHEN** clauses are flattened from separate control-flow regions
- **THEN** `Clause.Operator` represents only an actual authored operator relationship
- **THEN** no synthetic `Sequence`, `AndIf`, `OrIf`, or `Pipe` relationship is invented

#### Scenario: Projections share one Clause instance
- **WHEN** a fully parseable simple command appears in syntax, command, and compatibility projections
- **THEN** all three projections reference the identical in-memory `Clause` instance
- **THEN** serialization is not required to preserve that reference identity

#### Scenario: Oracle-proved false path or expansion claim is corrected
- **WHEN** v0.2 resolves escaped literal syntax as expandable text, treats runtime punctuation as a glob, collapses `Path` and `LiteralPath`, or applies cmdlet path semantics to a native argument
- **THEN** v0.3 preserves its raw spelling, decoded value, and source span
- **THEN** the compatibility leaf reports the oracle-proved literal path or fails closed
- **THEN** release notes identify the classification correction

#### Scenario: Joined cwd does not leak a false compatibility path
- **WHEN** a relative operand may execute under more than one cwd after a loop, pipeline, or conditional list
- **THEN** its authored spelling, path relevance, and source provenance remain available
- **THEN** `Arg.Resolved` and the corresponding `ClauseElement.Resolved` are null
- **THEN** the clause contains a synthetic `<dynamic-cwd>` `DynamicSkip` attribution argument with `Resolved=null`
- **THEN** no synthetic cwd-attribution argument selects one possible exact path

#### Scenario: Cwd rebasing preserves resolver-owned operand semantics
- **WHEN** an executable-specific rule transforms an authored path operand such as curl `@../request.json`
- **THEN** outcome-sensitive cwd rebasing uses the retained logical resolver operand rather than reconstructing it from `Arg.Raw` or `ClauseElement.Value`
- **THEN** split and equals-form options update their exact corresponding Arg and ClauseElement coordinates
- **THEN** decoded wrappers retain the same resolver provenance while inheriting their invocation cwd

#### Scenario: Exact outcome partition recovers a cwd-blocked path
- **WHEN** parse-order attribution initially makes a relative argument or redirect dynamic, but outcome analysis later proves its execution cwd exact
- **THEN** retained path-slot provenance permits an exact compatibility path without rescanning decoded text
- **THEN** a sibling partition whose cwd remains unknown keeps the operand dynamic

#### Scenario: Dynamic redirect preserves authored target spelling
- **WHEN** a quoted relative redirect target becomes dynamic after cwd outcomes join
- **THEN** its compatibility `Redirect.Target` retains the target-only authored spelling including quotes
- **THEN** the redirect is marked `IsDynamicSkip=true` and its ClauseElement exact resolution is cleared

### Requirement: Unparseable results are never authorization evidence
When `ParsedCommand.IsUnparseable=true`, `Commands` and `Clauses` SHALL be
empty. `Syntax` MAY contain partial diagnostic evidence, and the consumer guide
SHALL require prompt or deny without using that tree for allow.

#### Scenario: Partial body discovered before unsupported syntax
- **WHEN** the parser discovers a command in a body but later encounters an unsupported executable region
- **THEN** the result remains unparseable
- **THEN** command and compatibility projections are empty
- **THEN** a consumer does not authorize from the partial syntax tree

### Requirement: Security consumers authorize command occurrences
The consumer guide SHALL direct v0.3 security consumers to evaluate every
command occurrence, including conditions and iterators, and SHALL NOT require
recursive syntax traversal to discover executable commands.

#### Scenario: All loop regions are evaluated
- **WHEN** a parsed loop exposes iterator and body occurrences
- **THEN** the example authorization algorithm evaluates both
- **THEN** it does not grant scope to the loop keyword itself

#### Scenario: Syntax remains useful for explanation
- **WHEN** a UI groups approvals by loop or execution region
- **THEN** it may use syntax ancestry for display
- **THEN** authorization still uses the complete occurrence collection

#### Scenario: Execution metadata does not grant approval
- **WHEN** an execution region has known or unknown timing and cardinality
- **THEN** timing and cardinality remain explanatory shell facts
- **THEN** the consumer still interprets every authored command occurrence and policy-sensitive value

### Requirement: Unknown facts fail closed when policy-sensitive
The consumer guide SHALL require strict matching, prompt, or deny whenever an
unknown fact can affect command identity, option interpretation, path scope,
redirect behavior, effective cwd, or another consumer-defined sensitive
position.

#### Scenario: Unknown executable operand
- **WHEN** an executable-aware interpreter cannot completely consume effective operands
- **THEN** the consumer falls back to strict matching or prompts
- **THEN** it does not discard the unknown operand to reuse a broader approval

#### Scenario: Unknown node or enum member
- **WHEN** a consumer encounters a syntax, role, domain, or redirect kind it does not recognize
- **THEN** it fails closed for authorization

#### Scenario: Unknown execution-region facts
- **WHEN** a script-block origin, receiver, phase, timing, cardinality, or trigger-time state is unknown
- **THEN** a consumer prompts or denies whenever the uncertainty affects its policy
- **THEN** it does not treat the visible body as proof that following state is unchanged

### Requirement: Non-path redirect data does not create implicit approval scope
The consumer guide SHALL distinguish complete heredoc and here-string data from
command occurrences and filesystem redirect targets. A consumer SHALL NOT
prompt solely because complete non-path data uses heredoc or here-string shell
syntax. It SHALL still apply executable-specific policy to determine whether
stdin data affects authorization, and unknown data in such a sensitive position
SHALL prompt or deny.

#### Scenario: Literal data sent to a non-interpreting command
- **WHEN** a complete literal heredoc or here string feeds a command whose stdin is not policy-sensitive
- **THEN** the consumer evaluates the receiving command and any independent path redirects
- **THEN** it need not create a separate command or path approval for the data body

#### Scenario: Receiver interprets stdin as code
- **WHEN** a command such as a shell interpreter receives unknown here-string data
- **THEN** an executable-aware consumer treats that stdin position as policy-sensitive
- **THEN** the unknown value prompts or denies rather than reusing a broader approval

#### Scenario: Expanding heredoc executes a substitution
- **WHEN** an expanding heredoc contains a supported command substitution
- **THEN** the substitution is authorized as its own command occurrence
- **THEN** the remaining body is still data rather than an invented child command

### Requirement: Proven candidates are interpreted individually
For every exact or finite effective value, the consumer guide SHALL require
the consumer to reapply executable-specific option and operand semantics. A
finite shell proof SHALL NOT itself grant authorization.

#### Scenario: Loop candidate changes option parsing
- **WHEN** one effective candidate begins with `-`
- **THEN** the consumer evaluates that candidate at its actual authored position
- **THEN** it does not rely solely on the original argument's `IsFlag` value

### Requirement: v0.2 consumers have a documented migration path
Release notes and the consumer guide SHALL document the new canonical
projections, retained compatibility fields, record equality and serialization
effects, and the period during which `Clauses` remains supported.

#### Scenario: Consumer remains on Clauses during alpha
- **WHEN** a consumer upgrades to a v0.3 prerelease without adopting `Commands`
- **THEN** existing simple-command behavior remains available
- **THEN** supported nested constructs expose conservative authored clauses rather than omitting commands

#### Scenario: Netclaw adopts v0.3 analysis
- **WHEN** Netclaw migrates to the occurrence and explicit redirect APIs
- **THEN** integration tests cover ordinary commands, static fd operations, bounded loops, and unknown-value fallback
- **THEN** its temporary raw-prefix redirect workaround can be removed

### Requirement: Persistence is consumer-versioned
ShellSyntaxTree SHALL define an in-memory typed API but SHALL NOT claim a stable
serialized wire format for polymorphic syntax nodes. The consumer guide SHALL
document generated record equality, hashing, `ToString()`, and default
serialization changes, and SHALL direct persistence consumers to versioned DTOs
or explicit serializer configuration.

#### Scenario: Consumer persists parser results
- **WHEN** a consumer needs to store or transmit a v0.3 parser result
- **THEN** it does not assume the closed record hierarchy is an implicit stable JSON union
- **THEN** it owns an explicit versioned representation or serializer mapping
