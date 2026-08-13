## ADDED Requirements

### Requirement: Working-directory effect is additive and fail-closed

`CommandOccurrence` SHALL expose an additive `WorkingDirectoryEffect` property
of type `ShellWorkingDirectoryEffect` with an assembly-owned setter. Every
parser-produced occurrence SHALL set the property to a non-null member of the
closed family. Missing, invalid, incomplete, or unrecognized internal facts
SHALL project as `ShellWorkingDirectoryEffect.Unknown`.

The family SHALL contain `Unknown`, `Unchanged`, and `ChangesOnSuccess`.
`ChangesOnSuccess` SHALL expose a non-null `Target` of type `ShellValueDomain`.
Every family constructor SHALL be non-public, and every alternative SHALL
implement the same library-ownership boundary as `ShellValueDomain`.
The property and family SHALL participate in record equality, hashing,
`ToString()`, reflection, and default serializer shape. No v0.3.3 public member
or signature SHALL change.

#### Scenario: Strict default is unknown

- **WHEN** projection receives no valid internal cwd-effect fact
- **THEN** `WorkingDirectoryEffect` is `Unknown`

#### Scenario: Public API extends v0.3.3

- **WHEN** both target-framework API surfaces are compared with public v0.3.3
- **THEN** this capability adds only `CommandOccurrence.WorkingDirectoryEffect`
  and the closed `ShellWorkingDirectoryEffect` family
- **AND** no existing public member or signature changes

#### Scenario: New fact participates in record semantics

- **WHEN** two otherwise equal command occurrences have different
  `WorkingDirectoryEffect` values
- **THEN** generated equality, hashing, and diagnostic rendering observe the
  difference

#### Scenario: Consumers cannot construct effect alternatives

- **WHEN** reflection inspects every `ShellWorkingDirectoryEffect` alternative
- **THEN** no public or protected constructor is available to consumers

### Requirement: Effect semantics preserve the incoming-state relation

`Unchanged` SHALL mean that every parser-modeled normal success and failure
exit retains the occurrence's incoming shell-scope directory.
`ChangesOnSuccess` SHALL mean that every modeled failure exit retains the
incoming directory and every modeled success exit takes `Target`. `Unknown`
SHALL mean that neither relation is completely proved.

The analyzer SHALL record this relation directly while it computes command
flow. It SHALL NOT infer `Unchanged` merely because incoming and outgoing
domains compare equal or are both unknown. The effect SHALL be relative to the
execution scope described by the occurrence's ancestry.

#### Scenario: Unknown domain equality does not prove preservation

- **GIVEN** an occurrence has an unknown incoming cwd
- **AND** a parser-known mutation produces an unknown outgoing cwd
- **WHEN** the effect is projected
- **THEN** it is not `Unchanged`

#### Scenario: Subshell-local mutation stays scope-relative

- **WHEN** Bash parses `(cd /tmp)`
- **THEN** the inner `cd` effect describes its subshell scope
- **AND** command ancestry preserves the subshell boundary
- **AND** the fact does not claim that the parent shell changes directory

#### Scenario: PowerShell in-process scope remains visible

- **WHEN** a modeled PowerShell execution region runs in the containing
  runspace
- **THEN** its nested occurrence effects use that runspace scope
- **AND** an unmodeled host-level location effect remains `Unknown`

### Requirement: Successful targets use a bounded value domain

`ChangesOnSuccess.Target` SHALL be `Unknown`, `Exact`, or `FiniteSet`.
`Exact` and every member of `FiniteSet` SHALL be a normalized absolute local
directory under the selected shell path style. `Unknown` SHALL mean a
success-only mutation is proved but its destination is not bounded.

`IntegerRange`, `Concatenation`, `PathPattern`, null, an empty finite set, an
over-limit union, mixed path styles, or an unrecognized domain SHALL make the
entire public effect `Unknown`. A positive target SHALL NOT prove existence,
accessibility, authorization, or runtime success.

#### Scenario: Exact transfer target

- **GIVEN** the incoming Bash cwd is `/work`
- **WHEN** Bash parses `cd ../tmp`
- **THEN** the effect is `ChangesOnSuccess(Exact("/tmp"))`

#### Scenario: Finite loop target under proved initial state

- **GIVEN** Bash uses `BashInitialStateMode.IsolatedNonInteractive`
- **AND** the initial cwd is exact
- **WHEN** Bash parses
  `for d in /work/a /work/b; do cd "$d"; done`
- **THEN** the loop-body occurrence effect is
  `ChangesOnSuccess(FiniteSet(["/work/a", "/work/b"]))`

#### Scenario: Authored-only loop target remains strict

- **GIVEN** Bash uses unknown initial state with
  `PublishAuthoredSourceFacts=true`
- **WHEN** Bash parses
  `for d in /work/a /work/b; do cd "$d"; done`
- **THEN** the loop-body occurrence effect is `Unknown`

#### Scenario: Unbounded successful target

- **WHEN** a complete modeled cwd transfer has no bounded destination
- **THEN** the effect is `ChangesOnSuccess(Unknown)`

#### Scenario: Invalid target domain fails closed

- **WHEN** an internal effect carries an unsupported or malformed target
- **THEN** the public effect is `Unknown`

### Requirement: Effect joins are correlation-preserving

The analyzer SHALL join effect facts for repeated visits to the same authored
occurrence. Two `Unchanged` facts SHALL join to `Unchanged`. Two
`ChangesOnSuccess` facts SHALL join by the existing bounded-domain union.
Any join that contains `Unknown`, mixes `Unchanged` with
`ChangesOnSuccess`, exceeds limits, or cannot preserve visit correlation SHALL
be `Unknown`.

Default structural facts, unvisited branches, incomplete visits, and missing
provenance SHALL be `Unknown`; they SHALL NOT default to `Unchanged`.

#### Scenario: Equal successful transfers remain exact

- **WHEN** every reachable visit transfers successfully to `/work`
- **THEN** the joined effect is `ChangesOnSuccess(Exact("/work"))`

#### Scenario: Distinct bounded transfers form a finite set

- **WHEN** reachable visits transfer successfully to `/work/a` and `/work/b`
- **THEN** the joined target is the deduplicated finite set of both paths

#### Scenario: Mixed preservation and mutation remains unknown

- **WHEN** one reachable visit is `Unchanged`
- **AND** another reachable visit is `ChangesOnSuccess`
- **THEN** the joined effect is `Unknown`

#### Scenario: Missing visit evidence remains strict

- **WHEN** one reachable visit lacks a complete effect fact
- **THEN** the joined effect is `Unknown`

### Requirement: Bash effects come from Bash syntax and modeled state

For a complete Bash simple command, the analyzer SHALL publish `Unchanged`
only when authored Bash syntax has no parser-known current-scope cwd mutation.
A modeled `cd`, `command cd`, or `builtin cd` SHALL publish
`ChangesOnSuccess` with its bounded target or `Unknown` target. A statically
invalid `cd` shape with no successful transfer and an unchanged failure exit
SHALL publish `Unchanged`.

`pushd`, `popd`, a hidden current-scope executable region, or another accepted
unbounded source-level mutation SHALL publish `Unknown`. Computed identity or
incomplete executable syntax SHALL not receive a reusable effect. `source`,
`.`, `eval`, and the locked execution-bearing builtin family SHALL keep atomic
parse failure and SHALL publish no `CommandOccurrence`.

Bash `chdir` SHALL be treated as an ordinary external command name and SHALL
publish `Unchanged` when otherwise complete. It SHALL NOT update the v0.3
occurrence flow. The legacy compatibility `Clauses` projection MAY retain its
historical `chdir` cwd attribution, but consumers SHALL NOT use that leaf for
v0.3 authorization.

#### Scenario: Ordinary Bash command preserves cwd

- **WHEN** Bash parses `inspect artifact`
- **THEN** the occurrence effect is `Unchanged`

#### Scenario: Bash cd transfers on success

- **GIVEN** an exact cwd of `/work`
- **WHEN** Bash parses `cd /tmp`
- **THEN** the effect is `ChangesOnSuccess(Exact("/tmp"))`
- **AND** failure retains `/work`

#### Scenario: Exact dispatch wrappers preserve cd semantics

- **WHEN** Bash parses `command cd /tmp` or `builtin cd /tmp`
- **THEN** each effect is `ChangesOnSuccess(Exact("/tmp"))`

#### Scenario: Invalid extra operand cannot transfer

- **WHEN** Bash parses `cd /tmp extra`
- **THEN** the effect is `Unchanged`
- **AND** a later semicolon command keeps its original incoming cwd

#### Scenario: Invalid option cannot transfer

- **WHEN** Bash parses `cd -z /tmp`
- **THEN** the effect is `Unchanged`

#### Scenario: Directory stack remains unmodeled

- **WHEN** Bash parses `pushd /tmp` or `popd`
- **THEN** the effect is `Unknown`

#### Scenario: Execution-bearing builtins still fail atomically

- **WHEN** Bash parses `source setup.sh`, `. setup.sh`, or `eval "$code"`
- **THEN** the whole parse is unparseable
- **AND** no clauses or command occurrences are published

#### Scenario: Bash chdir does not impersonate PowerShell

- **GIVEN** an exact Bash cwd of `/work`
- **WHEN** Bash parses `chdir /tmp; head file`
- **THEN** the `chdir` effect is `Unchanged`
- **AND** the `head` occurrence cwd remains `/work`

#### Scenario: Later stack mutation invalidates causal intent

- **WHEN** Bash parses `cd /tmp && pushd /other; head file`
- **THEN** the `cd` effect is a bounded success transfer
- **AND** the `pushd` effect is `Unknown`
- **AND** a consumer cannot prove that `head` runs under `/tmp`

#### Scenario: Pipeline stage effect does not prove parent leakage

- **WHEN** Bash parses `printf x | cd /tmp; pwd` without proved `lastpipe`
- **THEN** the `cd` pipeline-stage occurrence has
  `ChangesOnSuccess(Exact("/tmp"))` for its stage scope
- **AND** the following `pwd` occurrence has an unknown incoming cwd
- **AND** no effect fact claims that the parent shell reached `/tmp`

#### Scenario: First pipeline stage remains isolated

- **GIVEN** an exact outer cwd of `/work`
- **WHEN** Bash parses `cd /tmp | pwd; pwd`
- **THEN** the first-stage `cd` has
  `ChangesOnSuccess(Exact("/tmp"))` for its stage scope
- **AND** the final outer `pwd` starts in `/work`

#### Scenario: Command substitution discards inner cwd mutation

- **GIVEN** an exact outer cwd of `/work`
- **WHEN** Bash parses `echo "$(cd /tmp && pwd)"; pwd`
- **THEN** the inner `cd` is `ChangesOnSuccess(Exact("/tmp"))`
- **AND** its ancestry identifies substitution scope
- **AND** the final outer `pwd` starts in `/work`

#### Scenario: Decoded child shell isolates its exit state

- **GIVEN** an exact outer cwd of `/work`
- **WHEN** Bash parses `bash -c 'cd /tmp && pwd'; pwd`
- **THEN** the decoded child `cd` is
  `ChangesOnSuccess(Exact("/tmp"))`
- **AND** the final outer `pwd` starts in `/work`

#### Scenario: Unreachable loop body has no reusable effect

- **WHEN** a proved zero-iteration Bash loop contains `cd /tmp`
- **THEN** its projected body occurrence effect is `Unknown`

### Requirement: PowerShell effects use the selected native dialect

For a complete native PowerShell command, the analyzer SHALL publish
`Unchanged` only when authored PowerShell syntax has no parser-known
current-runspace location mutation. A modeled `Set-Location` SHALL publish
`ChangesOnSuccess` with a bounded or unknown target when a success exit is
reachable. A statically invalid parameter or operand shape whose only modeled
exit is an unchanged failure SHALL publish `Unchanged`. Aliases canonicalized
by the selected dialect (`cd`, `chdir`, and `sl`) SHALL share those rules.

`Push-Location`, `Pop-Location`, provider ambiguity, script invocation,
unmodeled current-runspace code, an invalidated command identity, or another
unbounded location mutation SHALL publish `Unknown`. PowerShell aliases SHALL
NOT affect Bash analysis.

#### Scenario: Ordinary PowerShell command preserves location

- **WHEN** native PowerShell parses `Get-Content file.txt`
- **THEN** the occurrence effect is `Unchanged`

#### Scenario: Set-Location transfers on success

- **GIVEN** an exact Windows cwd of `C:/work`
- **WHEN** native PowerShell parses `Set-Location C:\temp`
- **THEN** the effect is `ChangesOnSuccess(Exact("C:/temp"))`

#### Scenario: Native aliases share selected-dialect semantics

- **WHEN** PowerShell parses `cd C:\temp`, `chdir C:\temp`, or `sl C:\temp`
- **THEN** each effect matches canonical `Set-Location`

#### Scenario: Parameter alias prefixes share flow and effect binding

- **GIVEN** an exact Windows cwd of `C:/work`
- **WHEN** PowerShell parses
  `Set-Location -psp C:\temp && Get-Location`
- **THEN** `-psp` resolves as an unambiguous `PSPath` prefix
- **AND** the effect is `ChangesOnSuccess(Exact("C:/temp"))`
- **AND** the success-gated `Get-Location` starts in `C:/temp`

#### Scenario: Invalid switch value is failure-only

- **WHEN** PowerShell parses `Set-Location -Verbose:no C:\temp`
- **THEN** the effect is `Unchanged`
- **AND** a later statement retains the incoming location

#### Scenario: PowerShell location stack remains unmodeled

- **WHEN** PowerShell parses `Push-Location C:\temp` or `Pop-Location`
- **THEN** the effect is `Unknown`

#### Scenario: Extra Set-Location operand cannot transfer

- **GIVEN** an exact incoming location of `C:/work`
- **WHEN** PowerShell parses `Set-Location C:\temp C:\other`
- **THEN** the effect is `Unchanged`
- **AND** a later statement keeps its original incoming location

#### Scenario: Missing Set-Location parameter value cannot transfer

- **WHEN** PowerShell parses `Set-Location -Path`
- **THEN** the effect is `Unchanged`

#### Scenario: Invalid static Set-Location parameter cannot transfer

- **WHEN** PowerShell parses `Set-Location -Unsupported C:\temp`
- **THEN** the effect is `Unchanged`

#### Scenario: Non-filesystem provider transfer remains unbounded

- **WHEN** PowerShell parses `Set-Location Env:`
- **THEN** no exact local-filesystem target is published
- **AND** the effect is `ChangesOnSuccess(Unknown)`

#### Scenario: Bash keeps language separation

- **WHEN** Bash parses `chdir /tmp` or `sl /tmp`
- **THEN** neither command is treated as PowerShell `Set-Location`

#### Scenario: PowerShell subexpression remains current-runspace scoped

- **GIVEN** an exact incoming location of `C:/work`
- **WHEN** PowerShell parses `$(Set-Location C:\temp); Get-Location`
- **THEN** the nested `Set-Location` is
  `ChangesOnSuccess(Exact("C:/temp"))`
- **AND** ancestry identifies the subexpression relation
- **AND** the later occurrence uses the failure-aware joined runspace location

#### Scenario: PowerShell parenthesized group is not isolated

- **WHEN** PowerShell parses `(Set-Location C:\temp); Get-Location`
- **THEN** the nested location effect applies to the containing runspace
- **AND** the later occurrence does not assume Bash subshell isolation

#### Scenario: PowerShell location transfer in a pipeline stays atomic

- **WHEN** PowerShell parses `Set-Location C:\temp | Get-Location`
- **THEN** the whole parse is unparseable until pipeline location state is
  modeled
- **AND** no clauses or command occurrences are published

#### Scenario: Modeled current-runspace mutation keeps host strict

- **WHEN** PowerShell completely decodes an accepted current-runspace host
  whose nested occurrence changes location and can later fail
- **THEN** the nested occurrence retains its precise effect
- **AND** the host occurrence effect is `Unknown`
- **AND** the host does not copy the nested success-only relation

#### Scenario: Unmodeled attached execution region keeps host strict

- **WHEN** an accepted PowerShell host contains a current-runspace execution
  region whose location effect is not completely modeled
- **THEN** the host occurrence effect is `Unknown`

#### Scenario: Current-runspace host with preserving body may be unchanged

- **WHEN** a proved receiver and every reachable nested occurrence have
  `Unchanged` effects
- **AND** the receiver has no separate location mutation
- **THEN** the host occurrence effect is `Unchanged`

#### Scenario: Unreachable PowerShell loop body has no reusable effect

- **WHEN** a proved empty PowerShell loop contains `Set-Location C:\temp`
- **THEN** its projected body occurrence effect is `Unknown`

### Requirement: Consumers combine effects with complete control-flow policy

A positive working-directory effect SHALL NOT authorize a command or path.
A security consumer SHALL require complete occurrences, use a default-deny
switch for unknown or future effect alternatives, validate every exact or
finite target through its own path policy, and evaluate all prerequisite and
consumer occurrences independently.

The consumer SHALL also evaluate occurrence ancestry, redirects, substitutions,
real fallback scopes, and the effect of every intervening current-scope
command. It SHALL NOT replace these checks with command-name parsing. Ambient
runtime resolution remains the same explicit externality as other v0.3 command
facts.

#### Scenario: Bounded causal chain can be evaluated without verb parsing

- **GIVEN** complete Bash occurrences for
  `cd /tmp && inspect; head result.log`
- **AND** `cd` has `ChangesOnSuccess(Exact("/tmp"))`
- **AND** `inspect` and `head` have `Unchanged`
- **WHEN** a consumer evaluates causal intent
- **THEN** it can use the parser-owned effects without a builtin-name list
- **AND** it still checks `/tmp`, the original cwd fallback, all redirects,
  and all command authority

#### Scenario: Unknown intervening effect keeps the chain strict

- **WHEN** an intervening occurrence has `Unknown` effect
- **THEN** the consumer does not infer a later cwd from the earlier transfer

#### Scenario: Target outside policy remains denied

- **WHEN** `ChangesOnSuccess.Target` lies outside the consumer's allowed roots
- **THEN** the parser fact does not override the consumer's path decision

#### Scenario: Future effect alternative fails closed

- **WHEN** a consumer receives an unrecognized effect subtype
- **THEN** it treats the occurrence as policy-sensitive and unresolved

### Requirement: Shared specifications and guidance remain coherent

`SPEC.md` SHALL define the shared API, relational semantics, Bash derivation,
and consumer contract. `SPEC.POWERSHELL.md` SHALL define native PowerShell
derivation and dialect behavior. `docs/CONSUMER_GUIDE.md` SHALL show input,
parser output, and bounded consumer evaluation for direct transfer, directory
stack, invalid transfer, language-separation, ancestry, and causal fallback
cases.

The sanitized corpus SHALL pin both positive and negative Bash behavior. Unit
tests SHALL pin native PowerShell behavior without requiring a host-installed
PowerShell parser at runtime. No corpus or guide example SHALL contain private
repository names, user names, tokens, or machine-specific paths.

#### Scenario: Guide distinguishes incoming cwd from effect

- **WHEN** a consumer reads the working-directory section
- **THEN** it sees that `WorkingDirectory` is the incoming value
- **AND** `WorkingDirectoryEffect` is the relational outcome fact

#### Scenario: Corpus is sanitized

- **WHEN** the corpus PII audit scans new working-directory examples
- **THEN** it finds zero forbidden patterns
