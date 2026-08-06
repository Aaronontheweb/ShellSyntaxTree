## ADDED Requirements

### Requirement: Shell values use explicit proof domains
The analysis SHALL classify a policy-relevant shell value as exact, finite,
bounded symbolic pattern, or unknown, and SHALL NOT present a weaker proof as a
stronger domain.

#### Scenario: One literal value
- **WHEN** a loop binds a variable from the single literal `a.txt`
- **THEN** the binding domain is exact with value `a.txt`

#### Scenario: Finite literal values
- **WHEN** Bash parses `for f in a.txt b.txt; do rm -- "$f"; done`
- **THEN** the binding domain is the finite set `a.txt`, `b.txt`

#### Scenario: Runtime-produced values
- **WHEN** a loop iterable is produced by a command or PowerShell object pipeline
- **THEN** the binding domain is unknown even though the producing commands are exposed

### Requirement: Analysis is bounded and non-executing
The parser SHALL NOT execute commands, enumerate filesystem matches, inspect
runtime shell variables, or expand candidate combinations beyond a fixed
documented bound. Any value exceeding the bound SHALL become unknown.

#### Scenario: Glob is not enumerated
- **WHEN** Bash parses `for f in /tmp/*.txt; do rm -- "$f"; done`
- **THEN** the parser does not read `/tmp`
- **THEN** it may expose a pattern with conservative covering directory `/tmp`

#### Scenario: Candidate cross product exceeds the limit
- **WHEN** combining finite values would exceed the locked candidate cap
- **THEN** the resulting domain is unknown
- **THEN** the parser does not truncate the set and call the truncated result complete

### Requirement: Variable substitution preserves argument-boundary uncertainty
A loop binding SHALL affect an effective command value only when the selected
shell's quoting and expansion rules prove the resulting argument boundaries.

#### Scenario: Quoted Bash variable
- **WHEN** `f` has finite literal values and a body argument is exactly `"$f"`
- **THEN** the effective argument has the corresponding finite domain

#### Scenario: Unquoted Bash variable
- **WHEN** a Bash body uses `$f` unquoted and word splitting or glob expansion could change argument count
- **THEN** the effective argument is unknown until those semantics are explicitly supported

#### Scenario: PowerShell object value
- **WHEN** a PowerShell `foreach` variable may hold objects emitted by a pipeline
- **THEN** its effective string or path value is unknown

### Requirement: Shell and executable semantics are reapplied after substitution
ShellSyntaxTree SHALL preserve the authored shell classification together with
effective candidate values without claiming whether native-command candidates
are options, operands, subcommands, revisions, or paths. Consumers SHALL apply
the shell's binding rules and interpret every native candidate through a
complete executable-aware grammar before reusing authorization.

#### Scenario: Finite value injects an rm option
- **WHEN** Bash parses `for f in -rf /tmp/x; do rm "$f"; done`
- **THEN** the finite domain preserves `-rf` and `/tmp/x`
- **THEN** the parser does not claim that quoting makes `-rf` a non-option

#### Scenario: Explicit option terminator
- **WHEN** Bash parses `for f in -rf /tmp/x; do rm -- "$f"; done`
- **THEN** the authored `--` remains visible before the effective candidate
- **THEN** the consumer may account for it using rm semantics

#### Scenario: PowerShell cmdlet parameter-like value
- **WHEN** PowerShell parses `foreach ($value in '-Force') { Write-Output $value }`
- **THEN** the authored variable argument remains a positional expression
- **THEN** the effective string `-Force` is not retroactively classified as a cmdlet parameter token

#### Scenario: PowerShell native option-like value
- **WHEN** PowerShell parses `foreach ($value in '--force') { git clean $value }`
- **THEN** the authored variable argument remains distinct from its effective value
- **THEN** the consumer applies the native executable grammar to `--force`

### Requirement: Control-flow state joins conservatively
Working-directory and supported variable state SHALL be propagated through
sequential regions and joined across branches and loop exits. Disagreement
SHALL never be resolved by arbitrarily choosing one path.

#### Scenario: Branch-dependent cwd
- **WHEN** one branch changes cwd to `/a` and another changes cwd to `/b`
- **THEN** a following relative path is not resolved solely under `/a` or solely under `/b`
- **THEN** the cwd is unknown unless a bounded multi-state contract is explicitly supported

#### Scenario: Zero-iteration loop path
- **WHEN** a loop may execute zero times and its body changes cwd
- **THEN** the post-loop state includes the pre-loop possibility

#### Scenario: Isolated shell scope
- **WHEN** a supported subshell or scope-isolated group changes cwd
- **THEN** that cwd does not leak into the enclosing continuation

### Requirement: Unknown analysis remains policy-sensitive
An unknown value SHALL identify the occurrence and position it affects so a
consumer can determine whether command identity, option parsing, path scope,
redirect behavior, or another policy-sensitive fact requires prompt or deny.

#### Scenario: Unknown rm path
- **WHEN** a runtime-produced loop variable is used as the path operand of `rm`
- **THEN** the occurrence exposes that operand as unknown
- **THEN** no durable path approval is synthesized from its raw spelling

#### Scenario: Unknown echo data
- **WHEN** an unknown value is passed as ordinary data to `echo`
- **THEN** the parser still reports the unknown fact
- **THEN** the consumer, not the parser, decides whether that position affects its policy
