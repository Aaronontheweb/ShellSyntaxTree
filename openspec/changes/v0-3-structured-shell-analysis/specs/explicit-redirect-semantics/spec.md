## ADDED Requirements

### Requirement: Redirect operation is explicit
Each parsed redirect SHALL identify its operation independently from its raw
text and compatibility direction. Supported operations SHALL distinguish file
input, file output, append, descriptor duplicate, descriptor close,
descriptor move, combined output, and any separately supported here-document
or here-string form.

The redirect source SHALL distinguish the shell-default stream, a numeric
descriptor, and PowerShell's all-streams selector. `Unknown` SHALL be the zero
source kind and operation, and invalid source-kind/descriptor combinations
SHALL be incomplete.

#### Scenario: PowerShell all-streams redirect
- **WHEN** PowerShell parses `Get-ChildItem *> output.txt`
- **THEN** the source is explicitly PowerShell all streams
- **THEN** the target is a path-relevant file output rather than a guessed numeric descriptor

#### Scenario: Static descriptor duplication
- **WHEN** Bash parses `dotnet test 2>&1`
- **THEN** the redirect operation is descriptor duplicate
- **THEN** the source descriptor is `2` and the target descriptor is `1`

#### Scenario: Static descriptor close
- **WHEN** Bash parses `command 2>&-`
- **THEN** the redirect operation is descriptor close
- **THEN** it is not classified as a dynamic path

#### Scenario: Static descriptor move
- **WHEN** Bash parses `command 2>&1-`
- **THEN** the redirect operation is descriptor move
- **THEN** the source and target descriptors are explicit

### Requirement: Static and computed descriptor targets differ
A descriptor operation SHALL expose a static target descriptor only when the
complete target is a literal descriptor. A variable, substitution, malformed
suffix, or other computed target SHALL remain dynamic or incomplete.

#### Scenario: Variable descriptor target
- **WHEN** Bash parses `command 2>&$FD`
- **THEN** the target is computed rather than static descriptor `FD`
- **THEN** a consumer cannot exempt it using the raw `&` prefix

#### Scenario: Braced variable descriptor target
- **WHEN** Bash parses `command >&${FD}`
- **THEN** the redirect remains policy-relevant unknown input

### Requirement: Combined output redirects preserve their semantics
The Bash parser SHALL distinguish `&>` and `&>>` from background-list syntax
and SHALL identify whether combined standard output and standard error are
overwritten or appended.

#### Scenario: Combined output overwrite
- **WHEN** Bash parses `command &> output.log`
- **THEN** one combined-output file redirect targets `output.log`

#### Scenario: Combined output append
- **WHEN** Bash parses `command &>> output.log`
- **THEN** one combined-output append redirect targets `output.log`

### Requirement: Redirect path relevance is independent
Redirect details SHALL state whether the target is a filesystem path without
overloading dynamic-value classification. Static descriptor operations SHALL
not be paths; file targets SHALL retain normal literal, pattern, and unknown
value facts.

#### Scenario: Static error file
- **WHEN** Bash parses `command 2> error.log`
- **THEN** `error.log` is a path-relevant file target

#### Scenario: Duplication is not a path
- **WHEN** Bash parses `command 2>&1`
- **THEN** descriptor `1` is not path-relevant

### Requirement: Redirect compatibility facts remain available
Existing redirect members SHALL remain populated according to their documented
v0.2 compatibility meanings while consumers migrate to explicit operation
facts. New facts SHALL NOT silently reinterpret a dynamic target as static.

#### Scenario: Existing consumer inspects static duplication
- **WHEN** a v0.2-style consumer reads the compatibility redirect for `2>&1`
- **THEN** the raw target remains available
- **THEN** the v0.3 consumer can distinguish the operation without parsing that raw value

### Requirement: Heredoc bodies are data with explicit expansion facts
v0.3 SHALL preserve the existing Bash `<<` and `<<-` grammar while adding
delimiter spelling and span, body spelling and span, literal-versus-expanding
mode, tab-stripping mode, and analysis completeness. It SHALL NOT
classify the body itself as a child command merely because the receiving
executable may interpret that data as code.

#### Scenario: Quoted heredoc delimiter
- **WHEN** Bash parses a supported heredoc with a quoted delimiter
- **THEN** the body is preserved as non-expanding authored data
- **THEN** the parser does not execute or reinterpret the receiving command

#### Scenario: Quoted delimiter adjacent to the operator
- **WHEN** Bash parses `cat <<'EOF'` followed by a body and the `EOF` delimiter
- **THEN** the quoted delimiter is recognized without requiring whitespace after `<<`
- **THEN** the result is not reported as missing a delimiter

#### Scenario: Executable substitution in an expanding body
- **WHEN** a supported expanding heredoc body contains command substitution
- **THEN** the inner command is exposed or the result is unparseable
- **THEN** the body is not reported as completely static while executable content is hidden

#### Scenario: Tab-stripping heredoc
- **WHEN** Bash parses `<<-EOF` with tab-indented body and delimiter lines
- **THEN** the redirect records that leading tabs are stripped
- **THEN** authored body provenance remains available

### Requirement: Bash here strings are explicit data redirects
v0.3 SHALL parse Bash `<<< word` as a `HereString` redirect. Its operand SHALL
use the normal shell value domain, SHALL NOT be path-relevant, and SHALL account
for Bash's deterministic trailing newline when an exact effective data value is
reported.

PowerShell here-strings SHALL remain quoted value tokens under the existing
PowerShell grammar and SHALL NOT be reported as redirect operations.

#### Scenario: Literal Bash here string
- **WHEN** Bash parses `cat <<< "hello"`
- **THEN** the redirect operation is here string
- **THEN** its exact effective data is `hello` followed by one newline
- **THEN** the redirect is not path-relevant

#### Scenario: Dynamic Bash here string
- **WHEN** Bash parses `cat <<< "$value"`
- **THEN** the redirect target remains unknown unless the value is proved
- **THEN** the parser does not execute or inspect the runtime variable

#### Scenario: PowerShell here string remains a value
- **WHEN** PowerShell parses a literal here string passed to `Write-Output`
- **THEN** the here string remains an authored PowerShell argument
- **THEN** no `HereString` redirect operation is emitted
