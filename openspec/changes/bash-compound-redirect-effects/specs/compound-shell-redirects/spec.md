## Purpose

Expose file and descriptor effects that Bash applies to a supported compound command. Keep those effects separate from inner command occurrences so consumers can apply their own authority rules.

## ADDED Requirements

### Requirement: Opt in to supported Bash compound redirects

The parser SHALL keep `ParseCompoundRedirects` false by default. When that option is false, a compound redirect SHALL keep the existing unparseable result. When the option is true, the parser SHALL accept supported redirects after `for`, `while`, `until`, `if`, and subshell commands. It SHALL expose each redirect on a compound owner that identifies the wrapped command and keeps redirects in source order. Each redirect fact SHALL expose the existing redirect analysis, the exact redirect source span, its target domain, and its completeness. The owner SHALL expose the working directory at the compound invocation, before its body runs.

The analyzer SHALL evaluate outer redirects at the compound command entry. It SHALL include the input state as a possible failure result because Bash skips the body when a redirect fails to open. It SHALL NOT infer compound redirect targets from assignments in the compound body or condition. Compound redirects inside decoded `bash -c` source, command-substitution targets, here-documents, here-strings, brace groups, and unsupported redirect syntax SHALL remain unparseable.

#### Scenario: Default mode keeps the prior fail-closed result
- **WHEN** the parser receives a supported compound redirect and `ParseCompoundRedirects` is false
- **THEN** the result SHALL remain unparseable
- **AND** the result SHALL expose no authorization command occurrences

#### Scenario: Opted-in literal input redirect on a loop
- **WHEN** the parser receives `for item in one two; do cat "$item"; done < input.txt`
- **AND** `ParseCompoundRedirects` is true
- **THEN** the result SHALL be parseable and SHALL expose one exact input redirect on the loop owner
- **AND** the owner SHALL expose the working directory from the loop invocation
- **AND** the redirect SHALL NOT appear as an inner `cat` occurrence redirect
- **AND** the loop and redirect source spans SHALL identify their exact source slices

#### Scenario: Opted-in redirects keep source order
- **WHEN** a supported compound command has more than one redirect
- **AND** `ParseCompoundRedirects` is true
- **THEN** the owner SHALL expose redirect facts in authored source order
- **AND** every fact SHALL preserve its own operator-to-target source span

#### Scenario: Opted-in input redirects after each supported compound form
- **WHEN** the parser receives a valid input redirect after a `while`, `until`, `if`, or subshell command
- **AND** `ParseCompoundRedirects` is true
- **THEN** the result SHALL expose the redirect on that compound command owner

### Requirement: Keep compound effects separate from command occurrences

The parser SHALL expose a compound redirect separately from the redirects on its child command occurrences. It SHALL keep `CommandOccurrence.IsComplete` scoped to the occurrence's own syntax. A consumer that opts in SHALL read the compound owner and each child occurrence.

#### Scenario: Opted-in consumer reads both syntax and occurrences
- **WHEN** a consumer reads the compound redirect owner and the command occurrences
- **AND** `ParseCompoundRedirects` is true
- **THEN** it SHALL be able to associate each outer redirect with the compound command without copying it onto child occurrences
- **AND** it SHALL retain each occurrence's own redirect facts separately

### Requirement: Keep unresolved redirect effects fail closed

The parser SHALL preserve the existing redirect target rules on compound redirects. An unknown target or incomplete redirect SHALL remain unknown or incomplete. The parser SHALL NOT resolve a target by reading the parser process filesystem. Unsupported compound forms SHALL remain unparseable.

#### Scenario: Unknown input variable
- **WHEN** the parser receives a compound redirect whose variable has no proved launch value
- **AND** `ParseCompoundRedirects` is true
- **THEN** the redirect target SHALL remain unknown or incomplete
- **AND** `CommandOccurrence.IsComplete` SHALL describe only the child's own syntax

#### Scenario: Command substitution in an outer redirect
- **WHEN** the parser receives a command substitution in a compound redirect target
- **AND** `ParseCompoundRedirects` is true
- **THEN** the whole result SHALL remain unparseable
- **AND** the result SHALL expose no partial command occurrences that hide the substitution

#### Scenario: Launch-proved HOME input
- **WHEN** the caller supplies a launch environment that proves `HOME` and the redirect target uses `$HOME`
- **AND** `ParseCompoundRedirects` is true
- **THEN** the parser SHALL apply its existing launch-value rules to that target
- **AND** this fact SHALL grant no filesystem authority

#### Scenario: Brace group remains outside this change
- **WHEN** the parser receives a redirect after a brace group
- **AND** `ParseCompoundRedirects` is true
- **THEN** the result SHALL remain unparseable until brace-group structural analysis has its own contract

#### Scenario: Assignment-only body has an outer redirect and no command occurrence
- **WHEN** the parser receives `for item in one; do VALUE=body; done < input.txt`
- **AND** `ParseCompoundRedirects` is true
- **THEN** the result SHALL expose the loop and its input redirect
- **AND** the result SHALL expose no command occurrences

#### Scenario: Redirect failure keeps the input state
- **WHEN** a loop body assigns a value and a later command follows the redirected loop
- **THEN** the later command SHALL include both the loop-success state and the pre-loop state
- **AND** an `||` fallback SHALL include the pre-loop state
- **AND** an `&&` command SHALL use only the loop-success state

#### Scenario: Decoded source keeps the fail-closed boundary
- **WHEN** a decoded `bash -c` command contains a compound redirect
- **AND** `ParseCompoundRedirects` is true
- **THEN** the outer parse result SHALL remain unparseable
- **AND** it SHALL expose no partial command occurrences
