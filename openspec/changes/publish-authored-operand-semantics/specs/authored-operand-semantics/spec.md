## ADDED Requirements

### Requirement: Authored non-filesystem value projection

`AnalyzedArgument` SHALL expose an additive
`AuthoredNonFileSystemValue` property using `ShellValueDomain`. The default
SHALL be `Unknown`.

#### Scenario: Default remains fail closed

- **WHEN** no audited non-filesystem binding applies
- **THEN** `AuthoredNonFileSystemValue` is `Unknown`

#### Scenario: Positive bounded data value

- **WHEN** an audited non-filesystem binding and complete provenance prove one
  authored value
- **THEN** `AuthoredNonFileSystemValue` is `Exact`

#### Scenario: Positive finite data values

- **WHEN** every reachable visit proves the same audited non-filesystem slot
  with two through 32 distinct bounded values
- **THEN** `AuthoredNonFileSystemValue` is their `FiniteSet`

#### Scenario: Unsupported domain stays unknown

- **WHEN** the value is unbounded, actively glob-expanded, incomplete, or over
  the fixed candidate limit
- **THEN** `AuthoredNonFileSystemValue` is `Unknown`

### Requirement: Independent authored facts

The parser SHALL keep authored value, lexical path shape, local-filesystem
value, and non-filesystem value as independent facts. One argument SHALL NOT
publish positive filesystem and non-filesystem domains together.

#### Scenario: Path-shaped data retains lexical evidence

- **WHEN** Bash parses the exact single-quoted translation set `\n`
- **THEN** `AuthoredPathShape` remains `Windows`
- **AND** an audited `tr` binding publishes
  `AuthoredNonFileSystemValue=Exact("\\n")`

#### Scenario: Filesystem operand remains distinct

- **WHEN** Bash parses `cat README.md` with an exact cwd
- **THEN** `AuthoredFileSystemValue` is the normalized exact path
- **AND** `AuthoredNonFileSystemValue` is `Unknown`

#### Scenario: Impossible positive pairing fails closed

- **WHEN** internal projection supplies positive filesystem and
  non-filesystem domains for one argument
- **THEN** the authorization projection is rejected atomically

### Requirement: Audited non-filesystem binding catalog

A positive non-filesystem domain SHALL require a parser-owned audited binding
covering every accepted argument shape. Compatibility tables, lexical shape,
and command spelling alone SHALL NOT supply the fact.

#### Scenario: Bash tr translation set is data

- **WHEN** Bash parses `tr -d '\n'`
- **THEN** the translation set has
  `AuthoredNonFileSystemValue=Exact("\\n")`

#### Scenario: Ordinary tr operands remain arguments

- **WHEN** Bash parses `tr abc def`
- **THEN** the verb chain is exactly `tr`
- **AND** `abc` and `def` are analyzed non-filesystem arguments

#### Scenario: Path-looking tr translation set remains data

- **WHEN** Bash parses `tr -d '/etc/passwd'`
- **THEN** the translation set has
  `AuthoredNonFileSystemValue=Exact("/etc/passwd")`
- **AND** it has no positive `AuthoredFileSystemValue`

#### Scenario: Active tr glob stays unknown

- **WHEN** Bash parses `tr -d *.txt`
- **THEN** the active glob has `AuthoredNonFileSystemValue=Unknown`

#### Scenario: Tr redirect remains independent

- **WHEN** Bash parses `tr -d '\n' > /outside/result`
- **THEN** the translation set retains its non-filesystem value
- **AND** the output redirect remains a path-relevant file redirect

#### Scenario: Unknown executable does not inherit tr semantics

- **WHEN** Bash parses `tool '\n'`
- **THEN** the argument has `AuthoredNonFileSystemValue=Unknown`

#### Scenario: Hidden option path remains unknown data

- **WHEN** Bash parses `grep -f /outside/patterns input.txt`
- **THEN** the option value has `AuthoredNonFileSystemValue=Unknown`

#### Scenario: PowerShell default remains unknown

- **WHEN** PowerShell parses path-shaped native or cmdlet data without a
  positive binder from this slice
- **THEN** `AuthoredNonFileSystemValue` is `Unknown`

### Requirement: Tr compatibility path correction

The Bash compatibility projection SHALL keep `tr` as one verb token and
classify every positional translation operand as non-path data. Generic path
heuristics SHALL remain unchanged for unknown commands.

#### Scenario: Backslash translation set is not a compatibility path

- **WHEN** Bash parses `tr -d '\n'` with cwd `/work`
- **THEN** the translation argument has `IsPath=false`
- **AND** its compatibility `Resolved` value is null

#### Scenario: Plain translation sets do not extend the verb chain

- **WHEN** Bash parses `tr abc def`
- **THEN** `Clause.Verb.Tokens` contains only `tr`
- **AND** both remaining tokens are compatibility arguments

#### Scenario: Unknown command keeps conservative compatibility

- **WHEN** Bash parses `tool '\n'` with cwd `/work`
- **THEN** the generic path heuristic retains its current path classification

### Requirement: Consumer use remains path-specific

A consumer SHALL NOT omit compatibility or lexical local-path evaluation
unless the same argument's `AuthoredNonFileSystemValue` is `Exact` or
`FiniteSet`. The consumer SHALL retain every command, completeness, effect,
cwd, redirect, raw protected-path, and occurrence check.

#### Scenario: Reviewed tr diagnostic no longer creates n scope

- **WHEN** Netclaw evaluates the sanitized live diagnostic containing
  `gh run view ... | tr -d '\n'` under the project cwd
- **THEN** the `tr` argument creates no child `n` directory scope
- **AND** reviewed-safe coverage may apply only after all independent facts
  pass

#### Scenario: File-writing redirect remains strict

- **WHEN** Netclaw evaluates `tr -d '\n' > /outside/result`
- **THEN** the non-filesystem argument does not suppress redirect policy
- **AND** the outside output remains strict

#### Scenario: Unknown semantics remain strict

- **WHEN** a path-shaped argument has
  `AuthoredNonFileSystemValue=Unknown`
- **THEN** the consumer retains its ordinary lexical-path policy

### Requirement: Compatibility and package boundary

The change SHALL be additive to public 0.3.4. Existing members, constructors,
and parser entry points SHALL remain unchanged.

#### Scenario: Public API comparison

- **WHEN** both target frameworks are compared with public 0.3.4
- **THEN** the only public API addition is
  `AnalyzedArgument.AuthoredNonFileSystemValue`
- **AND** no existing signature is removed or changed

#### Scenario: Generated record behavior

- **WHEN** consumers use generated equality, hashing, `ToString()`, reflection,
  or default serialization
- **THEN** the additive property participates in those generated behaviors
