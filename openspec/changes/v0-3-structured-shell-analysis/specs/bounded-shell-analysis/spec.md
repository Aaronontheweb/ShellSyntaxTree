## ADDED Requirements

### Requirement: Decoded values retain resolver provenance
The parser SHALL retain enough shell-specific lexical provenance to distinguish
resolver-sensitive literal, recognized-expansion, and opaque fragments after escape and
quote decoding, including the operation-specific transformations each exact
fragment permits. It SHALL NOT infer whether text expands solely from the
decoded string or one aggregate literal/expandable bit. Lexical transformations
SHALL apply only to eligible fragments. Consumer-level path interpretation
SHALL receive explicit shell, argument-versus-redirect,
native-versus-cmdlet, and PowerShell `Path`-versus-`LiteralPath` context.
When every fragment, supported
transformation, binding fact, and required cwd or home fact is exact,
the parser SHALL compose the exact shell value and SHALL return the exact
compatibility path result for a path position. It SHALL NOT return `DynamicSkip`
solely because one value contains both literal and expandable fragments. An
opaque or incompletely mapped fragment, unknown required fact, or unsupported
transformation SHALL produce an unknown or `DynamicSkip` fact rather than a
false path.

The provenance representation is internal and SHALL NOT change the v0.2 public
leaf API. Raw spelling, decoded logical values, and exact-or-null source spans
retain their existing meanings.

#### Scenario: Bash escaped variable is a literal path component
- **WHEN** Bash parses `cat \$HOME` with an exact working directory
- **THEN** the shell value is the literal `$HOME`
- **THEN** the compatibility argument is a literal path resolved as `<cwd>/$HOME`
- **THEN** it is not `DynamicSkip` and is not resolved as the configured home directory

#### Scenario: PowerShell escaped variable is a literal path component
- **WHEN** PowerShell parses ``Get-Content `$HOME`` with an exact working directory
- **THEN** the shell value is the literal `$HOME`
- **THEN** the compatibility argument is a literal path resolved as `<cwd>/$HOME`
- **THEN** it is not `DynamicSkip` and is not resolved as the configured home directory

#### Scenario: Escaped prefix composes with an adjacent quoted suffix
- **WHEN** either shell parses its escaped-dollar spelling of `curl --data=@$HOME".json" URL`
- **THEN** the complete native argument retains exact raw and decoded provenance
- **THEN** curl's compatibility file operand is the literal path `<cwd>/$HOME.json`
- **THEN** it is not `DynamicSkip` and is not a path under the configured home directory

#### Scenario: All-static mixed quoting remains exact
- **WHEN** either shell parses `curl --data='@$HOME'".json" URL`
- **THEN** both adjacent fragments retain literal provenance
- **THEN** the compatibility file operand is the literal path `<cwd>/$HOME.json`
- **THEN** it is not `DynamicSkip`

#### Scenario: Escape provenance survives inside one quoted token
- **WHEN** either shell parses its escaped-dollar spelling of a quoted `$HOME.txt` path
- **THEN** the literal dollar and the rest of the token compose exactly
- **THEN** the compatibility argument resolves as `<cwd>/$HOME.txt`, not under the configured home directory

#### Scenario: Literal and expandable regions compose inside one token
- **WHEN** either shell parses its spelling of a quoted literal `${HOME}` followed by an expandable `$HOME`
- **THEN** the first region remains literal and the second uses the configured home fact
- **THEN** the exact composed shell value is retained rather than becoming `DynamicSkip`

#### Scenario: Expandable variable remains expandable
- **WHEN** either shell parses an unescaped expandable `$HOME` in a supported path position
- **THEN** the resolver may use the configured home-directory fact
- **THEN** the escaped and expandable spellings do not collapse to the same provenance

#### Scenario: Opaque fragment remains fail closed
- **WHEN** an adjacent native argument contains a command substitution, subexpression, splat, or another opaque fragment
- **THEN** the parser does not synthesize an exact path from the remaining decoded text

#### Scenario: Runtime variables remain unknown without a proved value
- **WHEN** Bash parses a special, positional, or argument-vector parameter such as `$?`, `$1`, or `"$@"` in a policy-relevant value
- **WHEN** PowerShell parses a special, numeric, or Unicode-named variable such as `$?`, `$^`, `$$`, `$1`, or `$é`
- **THEN** the front end retains the typed expansion identity and cardinality while the analyzed value is unknown
- **THEN** glob or literal-path classification is not inferred from its decoded punctuation
- **THEN** a boundary-sensitive expansion such as `"$@"` does not become one exact argument

#### Scenario: Unterminated braced interpolation is unparseable
- **WHEN** either shell parses a quoted value whose `${...}` interpolation never closes
- **THEN** the whole parsed command is unparseable even when the outer quote closes
- **THEN** the incomplete region is not reported as a literal or exact path

#### Scenario: Escaped braced interpolation start remains literal
- **WHEN** either shell parses its escaped-dollar spelling of quoted `${HOME`
- **THEN** the shell value is the exact literal `${HOME`
- **THEN** it remains parseable and resolves as a literal path when authored in a path position

#### Scenario: PowerShell quoted tilde depends on consumer context
- **WHEN** PowerShell parses a quoted `~` in a native file operand and in a cmdlet `Path` operand
- **THEN** the native operand remains the literal filename `~`
- **THEN** the cmdlet operand resolves through the configured home fact
- **THEN** both results retain the same raw and decoded spelling without sharing resolver context

#### Scenario: PowerShell unquoted native expansion remains eligible
- **WHEN** PowerShell parses unquoted `~` or `*.txt` in a native file operand
- **THEN** tilde remains eligible for native expansion
- **THEN** the wildcard remains unknown without filesystem enumeration rather than becoming a quoted literal
- **THEN** correcting quoted operands does not suppress expansion for unquoted operands

#### Scenario: PowerShell provider semantics are cmdlet owned
- **WHEN** PowerShell parses quoted `FileSystem::C:\logs\x` for a native executable and for a cmdlet path
- **THEN** the native operand retains the provider-looking text literally
- **THEN** the cmdlet path applies FileSystem provider semantics
- **THEN** a non-FileSystem PSDrive in cmdlet path context is not reported as a filesystem path

#### Scenario: Bash provider-looking text remains literal
- **WHEN** Bash parses quoted or unquoted `filesystem::/safe` in a path position
- **THEN** the shell value retains every character literally
- **THEN** the compatibility path resolves under the configured cwd as `<cwd>/filesystem::/safe`
- **THEN** no PowerShell provider prefix is stripped

#### Scenario: PowerShell Path and LiteralPath differ
- **WHEN** the same quoted wildcard is bound to `-Path`, `-LiteralPath`, and a native file operand
- **THEN** `-Path` retains wildcard semantics
- **THEN** `-LiteralPath` and the quoted native operand retain an exact literal value
- **THEN** `-LiteralPath` still applies quoted tilde, provider-qualifier, and PSDrive semantics
- **THEN** the parser does not enumerate the filesystem for any form

#### Scenario: Adjacent redirect fragments form one target
- **WHEN** either shell parses its escaped-dollar spelling of `> $HOME".txt"`
- **THEN** the redirect target retains one ordered fragment sequence and the exact literal shell value `$HOME.txt`
- **THEN** the suffix is not emitted as an unrelated argument
- **THEN** redirect path interpretation receives its explicit shell-specific context
- **THEN** a Bash redirect whose expansion cannot prove exactly one target fails closed rather than reusing ordinary argument cardinality rules

#### Scenario: Bash redirect wildcard cardinality is quote-sensitive
- **WHEN** Bash parses unquoted `> *.txt`
- **THEN** the target is unknown without filesystem enumeration because expansion may produce zero, one, or multiple paths
- **WHEN** Bash parses quoted `> "*.txt"`
- **THEN** the target is the exact literal filename `*.txt`

#### Scenario: PowerShell redirect is a Path-like consumer
- **WHEN** PowerShell parses a quoted redirect target containing `~`, a wildcard, a FileSystem provider qualifier, or a PSDrive
- **THEN** redirect binding applies those Path-like semantics after value formation
- **THEN** a wildcard remains unknown without filesystem enumeration
- **THEN** a PSDrive remains unknown without a proved drive-to-provider mapping
- **THEN** quoted syntax does not turn either target into native-style literal text

### Requirement: Bash loop proofs require an explicit initial-state contract
`BashParserOptions.InitialStateMode` SHALL default to `Unknown`. A Bash loop
whose binding semantics depend on an unknown ambient shell SHALL fail closed
rather than publishing an ordinary-scalar proof.

`IsolatedNonInteractive` SHALL be an explicit caller assertion that the entire
source runs in a newly spawned non-interactive Bash process, no profile or
`BASH_ENV` / `ENV` startup content is loaded, and no inherited environment
entry carries the bound name. The v0.3 bounded grammar SHALL accept only names
matching `[a-z][a-z0-9_]*`, excluding `auto_resume` and `histchars`, under that
mode. All other binding names SHALL make the complete loop region unparseable.
Recognized source-level variable mutation SHALL invalidate isolated mode for
later scopes that can observe it. A decoded Bash wrapper after `export` SHALL
not inherit the original isolated assertion; cwd-only mutation SHALL not erase
the independently proved variable-state mode.

#### Scenario: Unknown ambient variable state fails closed
- **WHEN** Bash parses `for f in a; do printf '%s' "$f"; done` with the default initial-state mode
- **THEN** it does not assume that `f` is an ordinary writable scalar
- **THEN** the complete result is unparseable

#### Scenario: Isolated ordinary scalar is eligible
- **WHEN** the caller selects `IsolatedNonInteractive` and parses `for f in a; do printf '%s' "$f"; done`
- **THEN** the bounded loop analyzer may prove `f` exact

#### Scenario: Magic and resolver-sensitive names fail closed
- **WHEN** isolated-mode Bash parses a loop binding named `HOME`, `RANDOM`, `LINENO`, `PATH`, `CDPATH`, `IFS`, `_`, `auto_resume`, or `histchars`
- **THEN** the complete loop region is unparseable
- **THEN** no compatibility path or effective argument is published from an ordinary-scalar assumption

#### Scenario: Outer export invalidates a decoded loop environment
- **WHEN** isolated-mode Bash parses `export f=ambient; bash -c 'for f in a; do printf %s "$f"; done'`
- **THEN** the decoded child enters with unknown initial variable state
- **THEN** the complete result is unparseable rather than publishing an isolated scalar proof

### Requirement: PowerShell loop proofs require an explicit initial-runspace contract
`PwshParserOptions.InitialStateMode` SHALL default to `Unknown`. In that mode,
the parser MAY expose supported `foreach` structure and command occurrences,
but SHALL NOT publish an exact or finite loop-binding proof whose semantics
could be changed by ambient runspace state.

`IsolatedNonInteractiveNoProfile` SHALL be an explicit caller assertion that
the complete source runs in a newly spawned noninteractive PowerShell process,
profiles are disabled, and the runspace has not been reused or initialized by
uncontrolled caller variables, aliases, functions, or modules. The caller SHALL
also control startup configuration and the inherited environment. Module
auto-loading SHALL be disabled, or available modules and module search paths
SHALL be pinned to the same reviewed baseline used by policy. A fixed bootstrap
MAY establish those constraints only when it cannot define or mutate loop-bound
variables or policy-relevant command identities. `-NoProfile -NonInteractive`
alone SHALL NOT satisfy the contract. Exact and finite
binding analysis SHALL remain limited to ordinary unscoped names that do not
case-insensitively collide with automatic, constant, read-only, typed,
validated, preference, or configuration variables known to the supported
PowerShell runtime. The preference inventory SHALL include documented lazy and
configuration-dependent names even when a fresh `Get-Variable` inventory omits
them.
Scoped/provider binding forms SHALL fail closed.

Current-runspace groups, `$()`, and static `Invoke-Expression` payloads SHALL
share supported binding, command-resolution, and cwd state. A decoded child
PowerShell host SHALL NOT inherit the parent's fresh-state assertion unless
that invocation independently proves the complete constrained-host contract.
Host flags alone SHALL NOT prove the launch environment or module baseline.
Recognized variable, alias, function, or module mutation SHALL invalidate later proofs in
every observing scope; cwd-only mutation SHALL retain the independent
initial-state assertion.

#### Scenario: Unknown ambient PowerShell state withholds a finite proof
- **WHEN** default-mode PowerShell parses `foreach ($f in @('a','b')) { Remove-Item -LiteralPath $f }`
- **THEN** the loop structure and body command may remain visible
- **THEN** the body occurrence is incomplete rather than assuming `$f` is an ordinary string binding

#### Scenario: Isolated no-profile runspace permits an ordinary binding proof
- **WHEN** the caller selects `IsolatedNonInteractiveNoProfile` for a newly spawned constrained host and parses `foreach ($f in @('a','b')) { Write-Output $f }`
- **THEN** the bounded analyzer may publish the finite string domain `a`, `b`

#### Scenario: Typed or read-only ambient binding is not erased by syntax
- **WHEN** a reused runspace already contains `[int]$f` or a read-only `$f` and parses a loop that assigns string values
- **THEN** default-mode analysis does not claim the authored strings are the effective loop values
- **THEN** selecting isolated mode for that reused runspace would violate the caller contract

#### Scenario: Built-in preference binding is not an ordinary string slot
- **WHEN** isolated-mode PowerShell parses a loop binding named `ConfirmPreference`, `ErrorActionPreference`, or another known built-in preference or configuration variable
- **THEN** the complete loop region is unparseable
- **THEN** the analyzer does not assume assignment avoids type coercion, validation, rejection, or host-behavior changes

#### Scenario: Child host does not inherit the parent's assertion
- **WHEN** isolated-mode PowerShell parses a supported `pwsh -NoProfile -Command` child containing a `foreach`
- **THEN** the child receives `Unknown` initial state unless the child invocation independently proves the complete constrained-host environment
- **THEN** `-NoProfile` by itself does not prove the inherited environment or module baseline

#### Scenario: Current-runspace evaluation shares state
- **WHEN** a supported `$()` or static `Invoke-Expression` region mutates a loop-relevant binding or command-resolution fact
- **THEN** every later observing occurrence is joined with or downgraded to the resulting conservative state

### Requirement: Shell values use explicit proof domains
The analysis SHALL classify a policy-relevant shell value as exact, finite,
bounded symbolic pattern, or unknown, and SHALL NOT present a weaker proof as a
stronger domain.

`Unknown` SHALL contain no values or pattern fields. `Exact` SHALL contain one
value. `FiniteSet` SHALL contain 2–32 distinct values. `Pattern` SHALL contain
no values and SHALL contain a non-empty pattern and covering directory. The
parser SHALL NOT emit any other member combination.

#### Scenario: One literal value
- **WHEN** an eligible isolated-mode loop binds a variable from the single literal `a.txt`
- **THEN** the binding domain is exact with value `a.txt`

#### Scenario: Finite literal values
- **WHEN** isolated-mode Bash parses `for f in a.txt b.txt; do rm -- "$f"; done`
- **THEN** the binding domain is the finite set `a.txt`, `b.txt`

#### Scenario: Runtime-produced values
- **WHEN** a loop iterable is produced by a command or PowerShell object pipeline
- **THEN** the binding domain is unknown even though the producing commands are exposed

### Requirement: Analysis is bounded and non-executing
The parser SHALL NOT execute commands, enumerate filesystem matches, inspect
runtime shell variables, or expand a value domain beyond 32 candidates. A
domain of 32 candidates remains finite; a domain that would contain 33 or more
becomes unknown rather than being truncated.

#### Scenario: Glob is not enumerated
- **WHEN** isolated-mode Bash parses `for f in /tmp/*.txt; do rm -- "$f"; done`
- **THEN** the parser does not read `/tmp`
- **THEN** it exposes a pattern with conservative covering directory `/tmp`

#### Scenario: Dynamic glob root is unknown
- **WHEN** isolated-mode Bash parses `for f in "$ROOT"/*.txt; do rm -- "$f"; done`
- **THEN** the dynamic root prevents a static covering-directory proof
- **THEN** the iterable value is unknown

#### Scenario: Candidate cross product exceeds the limit
- **WHEN** combining finite values would produce 33 candidates
- **THEN** the resulting domain is unknown
- **THEN** the parser does not truncate the set and call the truncated result complete

#### Scenario: Candidate cross product reaches the limit
- **WHEN** combining finite values produces exactly 32 candidates
- **THEN** the resulting domain may remain finite and complete

### Requirement: Structural analysis has fixed depth limits
The parser SHALL support at most 16 nested executable containers and at most 5
decoded command-string wrapper recursions. Structural depth starts at zero for
the root and increments once when entering a foreach loop, condition loop,
conditional, group, or command substitution. Blocks, conditional-branch
records, command lists, pipelines, and simple-command leaves do not increment
the depth independently. These bounds SHALL NOT be caller-configurable.
Exceeding either bound SHALL make the whole result unparseable rather than
returning an authorization projection for a subset.

The limits SHALL be exposed as static get-only properties rather than public
compile-time constants so downstream assemblies read the installed parser's
contract instead of inlining stale values.

#### Scenario: Structural nesting reaches the limit
- **WHEN** a supported input enters exactly 16 nested executable containers
- **THEN** the input remains structurally eligible for complete analysis

#### Scenario: Structural nesting exceeds the limit
- **WHEN** a seventeenth nested executable container is entered
- **THEN** the result is unparseable
- **THEN** command and compatibility projections are empty

#### Scenario: Existing wrapper recursion limit remains fixed
- **WHEN** a sixth decoded command-string wrapper would be entered
- **THEN** the result is unparseable under the existing wrapper-depth rule

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
- **WHEN** isolated-mode Bash parses `for f in -rf /tmp/x; do rm "$f"; done`
- **THEN** the finite domain preserves `-rf` and `/tmp/x`
- **THEN** the parser does not claim that quoting makes `-rf` a non-option

#### Scenario: Explicit option terminator
- **WHEN** isolated-mode Bash parses `for f in -rf /tmp/x; do rm -- "$f"; done`
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

The Bash analyzer SHALL internally partition reachable exit state by command
success and failure. `&&` SHALL continue from the success partition, `||`
SHALL continue from the failure partition, and `;` or a newline SHALL continue
from their conservative join. Unreachable partitions are internal analysis
facts and SHALL NOT require a public API addition.

Bash command substitution SHALL isolate its working-directory and variable
state from the containing command while retaining sequential state inside the
substitution. PowerShell `$()` SHALL evaluate in the current runspace scope;
its sequential location changes SHALL affect later commands inside the
subexpression, the containing command, and the following outer continuation.
An unknown mutation SHALL propagate as unknown wherever that shell's scope
rules make it observable.

Exact and finite Bash `for ... in` domains SHALL be analyzed in authored
iteration order, including duplicates, within the candidate cap. The analyzer
SHALL retain independent internal cardinality of `Never`, `OneOrMore`, or
`ZeroOrMore`; the public finite-set summary SHALL NOT be used as an ordered
iteration plan. The cap SHALL count ordered concrete iterations, not distinct
public values. Pattern, unknown, and over-budget domains SHALL use a bounded
conservative fixed point and SHALL NOT be represented by one arbitrarily
selected iteration. An inner iterable SHALL be evaluated from each current
outer binding rather than from a flattened public summary.

The analyzer SHALL own loop-variable lifetime and SHALL re-evaluate every
argument's complete shell-value provenance for each concrete visit. Effective
argument facts at one authored occurrence SHALL join across reachable visits.
State transfers such as `cd` SHALL parse the complete effective argv, including
candidate-derived options and option terminators, rather than substituting only
an operand. Exact `command` options `-p` and `--` and the exact `builtin --`
delimiter SHALL be recursively unwrapped; `command -v` / `-V` SHALL remain a
nonmutating query, while invalid or dynamic wrapper grammar SHALL fail closed.
When effective option grammar makes an authored path resolution unsafe, the
compatibility `Arg` and corresponding `ClauseElement` resolutions SHALL both be
cleared without rewriting their authored spelling or flag classification.
Option recognition SHALL stop at the first operand. An exact invalid option or
second operand SHALL have no success partition, and a tracked loop-binding
expansion whose argv cardinality is not proved SHALL make the containing region
unparseable. Ambient dynamic values retain the compatibility contract's
conservative unknown-state behavior. A
transfer such as `break`, `continue`, `return`, `exit`, or `exec`,
including recursively wrapped builtin forms, SHALL make the containing region
unparseable until the analyzer implements that transfer explicitly. `eval`,
`source` / `.`, execution-bearing `trap`, and mutation of tracked bindings
SHALL likewise fail closed unless all executable regions and transfers are
discovered.

An unreachable success or failure partition SHALL remain unreachable. The
analyzer SHALL NOT substitute a joined state for a missing `&&` or `||`
partition merely to publish exact continuation facts.

#### Scenario: Branch-dependent cwd
- **WHEN** one branch changes cwd to `/a` and another changes cwd to `/b`
- **THEN** a following relative path is not resolved solely under `/a` or solely under `/b`
- **THEN** the cwd is unknown because v0.3 does not publish divergent cwd alternatives

#### Scenario: Identical branch cwd
- **WHEN** every supported branch exits with the same exact cwd
- **THEN** the joined cwd remains exact

#### Scenario: Ungated cd failure keeps the prior cwd possible
- **WHEN** Bash parses `cd /maybe; pwd`
- **THEN** the `cd` occurrence uses the incoming cwd
- **THEN** the `pwd` cwd is unknown because `cd` may fail and `;` still continues
- **THEN** the analyzer does not publish `/maybe` as the sole cwd

#### Scenario: Bash cd environment and physical resolution stay explicit
- **WHEN** Bash parses a bare relative `cd sub` without proved `CDPATH` and `cdable_vars` state
- **THEN** the successful cwd is unknown rather than a lexical `<cwd>/sub` guess
- **WHEN** Bash parses `cd ./sub` or `cd ../sub` with an exact incoming cwd
- **THEN** the successful cwd may remain exact because those operands bypass directory search
- **WHEN** Bash parses `cd -P`, `cd -@`, `pushd`, or `popd` without the required filesystem or directory-stack facts
- **THEN** the successful cwd is unknown

#### Scenario: Zero-iteration loop path
- **WHEN** a loop may execute zero times and its body changes cwd
- **THEN** the post-loop state includes the pre-loop possibility

#### Scenario: Proved empty loop does not mutate state
- **WHEN** isolated-mode Bash parses `for f in; do cd /tmp; done; pwd`
- **THEN** the internal iteration cardinality is `Never`
- **THEN** the following `pwd` retains the exact incoming cwd

#### Scenario: Reached nonmutating loop body retains incoming cwd
- **WHEN** isolated-mode Bash parses a supported loop whose body cannot change cwd
- **THEN** each reached body occurrence retains the exact incoming cwd
- **THEN** a structurally present but unreachable body or continuation still receives conservative cwd facts

#### Scenario: Duplicate iteration values retain order
- **WHEN** isolated-mode Bash parses `for f in a b a; do :; done; printf '%s' "$f"`
- **THEN** the internal iteration plan retains `a`, `b`, `a` in that order
- **THEN** the following use of `f` has exact effective value `a`

#### Scenario: Ordered cap counts visits rather than distinct values
- **WHEN** an isolated-mode Bash loop authors the same literal candidate 33 times
- **THEN** the internal plan exceeds the concrete-iteration cap
- **THEN** it uses bounded fixed-point analysis instead of treating one distinct public value as one visit

#### Scenario: Nested loop analysis stays resource bounded
- **WHEN** nested concrete loops require more than 4096 total body transitions
- **THEN** the complete parse is unparseable
- **THEN** no partial occurrence or compatibility projection is published

#### Scenario: Loop-derived cd option is rebound from effective argv
- **WHEN** isolated-mode Bash analyzes `for f in -P /tmp; do cd "$f"; done`
- **THEN** the first visit treats `-P` as a `cd` option rather than a path operand
- **THEN** the second visit treats `/tmp` as the operand under the resulting option grammar
- **THEN** no state transfer reuses the authored `$f` flag classification

#### Scenario: Loop-derived physical option sanitizes compatibility paths
- **WHEN** isolated-mode Bash analyzes `for f in -P; do cd "$f" ./sub && cat file.txt; done`
- **THEN** the effective argv is `cd -P ./sub`
- **THEN** both compatibility projections clear the authored `/work/sub` resolution
- **THEN** the reached `cat` has unknown cwd and no exact relative-path resolution
- **WHEN** the loop candidate is `--` instead
- **THEN** `./sub` remains the exact logical operand and the reached path resolves under `/work/sub`

#### Scenario: Exact dispatch wrappers preserve cwd transfer grammar
- **WHEN** a loop body invokes `command -p -- builtin -- cd "$f"`
- **THEN** the analyzer recursively proves the wrapper grammar
- **THEN** it interprets only the words after `cd` as the effective transfer argv
- **WHEN** `command -v` or `command -V` is used
- **THEN** the wrapper is a query and does not mutate cwd
- **WHEN** a wrapper option is invalid or dynamic
- **THEN** the loop remains fail closed

#### Scenario: Empty-loop failure continuation is unreachable
- **WHEN** isolated-mode Bash parses `for f in; do false; done || cat relative.txt`
- **THEN** the empty loop has only a reachable success exit
- **THEN** `cat` remains structurally visible but receives no fabricated exact cwd or binding facts from a failure fallback

#### Scenario: Same-name loop binding is not lexical shadowing
- **WHEN** a nested Bash loop reuses its active outer binding name
- **THEN** v0.3 either implements the inner assignment as overwriting shell state or makes the whole region unparseable
- **THEN** it never restores an outer value through parser-frame pop semantics

#### Scenario: Isolated shell scope
- **WHEN** a supported subshell or scope-isolated group changes cwd
- **THEN** that cwd does not leak into the enclosing continuation

#### Scenario: Bash substitution cwd is isolated
- **WHEN** Bash parses `printf '%s' "$(cd /tmp && pwd)"; cat relative.txt`
- **THEN** `pwd` uses `/tmp` inside the substitution
- **THEN** `printf` and `cat` retain the exact outer cwd

#### Scenario: Bash last pipeline stage may share parent state
- **WHEN** a Bash pipeline ends in a cwd or variable-state mutator and parser options do not prove `lastpipe` behavior
- **THEN** every stage occurrence uses the pipeline input state
- **THEN** following parent-scope state is unknown when the last stage could run in either a subshell or the current shell
- **THEN** unproved `pipefail` behavior conservatively partitions every reachable option-dependent result by success and failure

#### Scenario: Decoded Bash wrapper inherits cwd and isolates exit state
- **WHEN** Bash parses `cd /outer && bash -c 'cd /inner && pwd' && pwd`
- **THEN** the decoded wrapper enters with exact cwd `/outer`
- **THEN** its inner `pwd` uses `/inner`
- **THEN** the following outer `pwd` uses `/outer`

#### Scenario: Decoded Bash wrapper does not inherit an unexported loop binding
- **WHEN** isolated-mode Bash parses `for f in a; do bash -c 'printf "%s" "$f"'; done`
- **THEN** the decoded child receives no exact effective `f` from the outer loop binding
- **THEN** a parenthesized subshell remains distinct because it inherits shell bindings while isolating exit state

#### Scenario: PowerShell subexpression cwd propagates
- **WHEN** PowerShell parses `Write-Output $(Set-Location /tmp; Get-Location); Get-Item relative.txt`
- **THEN** `Get-Location`, `Write-Output`, and `Get-Item` use `/tmp`
- **THEN** the analyzer does not restore the pre-subexpression cwd

#### Scenario: Unknown PowerShell subexpression mutation propagates
- **WHEN** a PowerShell subexpression changes location to an unknown value
- **THEN** later commands inside the subexpression and in the containing outer continuation have unknown working-directory facts
- **THEN** no prior exact cwd is selected as a fallback

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
