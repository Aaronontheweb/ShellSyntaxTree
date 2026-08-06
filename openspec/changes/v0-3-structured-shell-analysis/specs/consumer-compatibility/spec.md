## ADDED Requirements

### Requirement: Existing Clause projection remains conservative
`ParsedCommand.Clauses` SHALL remain available in v0.3 and SHALL contain every
authored simple command that may execute for a fully parseable result, including
nested condition, iterator, branch, body, and substitution commands.

#### Scenario: Old consumer sees loop body command
- **WHEN** Bash fully parses `for f in a b; do rm "$f"; done`
- **THEN** the compatibility clauses include the authored `rm` command
- **THEN** its authored variable argument remains conservatively dynamic rather than being silently replaced

#### Scenario: Structural boundaries do not invent operators
- **WHEN** clauses are flattened from separate control-flow regions
- **THEN** `Clause.Operator` represents only an actual authored operator relationship
- **THEN** no synthetic `Sequence`, `AndIf`, `OrIf`, or `Pipe` relationship is invented

### Requirement: Unparseable results are never authorization evidence
All syntax, occurrence, clause, value, and redirect facts SHALL be treated as
partial diagnostic evidence when `ParsedCommand.IsUnparseable=true`. The
consumer guide SHALL require prompt or deny before evaluating them for allow.

#### Scenario: Partial body discovered before unsupported syntax
- **WHEN** the parser discovers a command in a body but later encounters an unsupported executable region
- **THEN** the result remains unparseable
- **THEN** a consumer does not authorize from the discovered subset

### Requirement: Security consumers authorize command occurrences
The consumer guide SHALL direct v0.3 security consumers to evaluate every
command occurrence, including conditions and iterators, and SHALL NOT require
recursive syntax traversal to discover executable commands.

#### Scenario: All loop regions are evaluated
- **WHEN** a parsed loop exposes iterator and body occurrences
- **THEN** the example authorization algorithm evaluates both
- **THEN** it does not grant scope to the loop keyword itself

#### Scenario: Syntax remains useful for explanation
- **WHEN** a UI groups approvals by loop or branch
- **THEN** it may use syntax ancestry for display
- **THEN** authorization still uses the complete occurrence collection

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
