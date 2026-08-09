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

#### Scenario: Bash tilde expansion retains prefix provenance
- **WHEN** Bash parses `~/x` or a backslash-newline continuation between `~` and `/x`
- **THEN** the unquoted tilde prefix expands from the configured home directory
- **WHEN** the slash or an empty intervening fragment is quoted or escaped
- **THEN** the decoded `~/x` remains a literal path resolved under the configured cwd

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

#### Scenario: Path-shaped PowerShell command names remain shadowable
- **WHEN** isolated-mode PowerShell parses an explicit native or `.ps1` path command before any command-resolution mutation
- **THEN** its spelling may supply the candidate native-versus-PowerShell argument-binding semantics
- **WHEN** a matching alias mutation has been observed, or the command executes in an unconstrained decoded child
- **THEN** path spelling alone does not prove command identity or argument-binding semantics
- **THEN** a shell-sensitive effective value remains Unknown

#### Scenario: Decoded host profiles can mutate home facts
- **WHEN** isolated-mode PowerShell decodes a child `pwsh -Command` or `-EncodedCommand` payload that references `$HOME` or `$env:USERPROFILE`
- **THEN** the child does not inherit exact automatic-variable or environment-variable facts from its parent
- **THEN** an uncontrolled profile may have mutated either value before the payload
- **THEN** configured provider/native tilde initialization remains an independent fact

#### Scenario: Adjacent redirect fragments form one target
- **WHEN** either shell parses its escaped-dollar spelling of `> $HOME".txt"`
- **THEN** the redirect target retains one ordered fragment sequence and the exact literal shell value `$HOME.txt`
- **THEN** the suffix is not emitted as an unrelated argument
- **THEN** redirect path interpretation receives its explicit shell-specific context
- **THEN** a Bash redirect whose expansion cannot prove exactly one target fails closed rather than reusing ordinary argument cardinality rules

#### Scenario: Bash redirect wildcard cardinality is quote-sensitive
- **WHEN** Bash parses unquoted `> *.txt`
- **THEN** the target is unknown without filesystem enumeration because expansion may produce zero, one, or multiple paths
- **THEN** the redirect and containing command occurrence remain incomplete
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

### Requirement: Bash parameter dereferences require proved attribute state
A syntactically simple Bash `$name` or `${name}` dereference SHALL be treated
as fully execution-accounted only when the incoming variable-attribute state
proves that the binding cannot be an integer, nameref, array reference, or
other recursively evaluated form. Value exactness SHALL remain independent:
an attribute-safe variable MAY still produce an `Unknown` value.

`BashInitialStateMode.Unknown` SHALL make a simple named-variable dereference
unparseable because an ambient nameref can evaluate an authored array
subscript. `IsolatedNonInteractive` MAY establish safe initial attributes for
a fresh-process variable before reachable source mutation. A modeled ordinary
loop binding MAY retain that proof. Any reachable unmodeled variable mutation
SHALL invalidate the proof in later regions that can observe it. Positional
and special parameters that cannot carry variable attributes remain governed
by their existing typed cardinality rules.

#### Scenario: Unknown ambient nameref fails closed
- **WHEN** default-mode Bash parses `printf '%s' "$x"`
- **THEN** the whole result is unparseable because ambient `x` may be a nameref
- **THEN** the parser does not report the outer `printf` complete while hiding recursive execution

#### Scenario: Fresh isolated scalar may remain unknown data
- **WHEN** isolated-mode Bash parses `printf '%s' "$x"` before any source mutation
- **THEN** the variable value remains unknown
- **THEN** the dereference does not by itself make the result unparseable because fresh-process attributes are proved safe

#### Scenario: Nameref mutation hides execution in a later heredoc
- **WHEN** isolated-mode Bash parses `declare -a a; declare -n x='a[$(hidden)0]'; cat <<EOF` followed by `${x}` and `EOF`
- **THEN** the whole result is unparseable
- **THEN** no complete heredoc or command projection hides `hidden`

### Requirement: Unmodeled execution-bearing Bash builtins fail closed globally
Stable v0.3 SHALL fail the complete parse closed for direct or statically
wrapped Bash builtin forms whose argument text can execute, be evaluated as
arithmetic, install deferred execution, or assign through unproved variable
attributes. The bounded catalog SHALL include `eval`, `source` / `.`, `trap`,
`let`, `declare`, `typeset`, `local`, `readonly`, `export`, `unset`, `read`,
`readarray`, `mapfile`, `getopts`, and `set`, plus `printf -v`. Exact `command`
and `builtin` dispatch wrappers SHALL be recursively unwrapped. Dynamic or
invalid wrapper grammar SHALL fail closed. Ordinary `printf` without `-v`
SHALL retain its existing behavior.

#### Scenario: Eval payload is not mistaken for inert data
- **WHEN** Bash parses `eval 'rm target.txt'`
- **THEN** the whole result is unparseable until eval payload grammar is modeled
- **THEN** `eval` is not published as a complete occurrence that hides `rm`

#### Scenario: Integer declaration can execute quoted arithmetic data
- **WHEN** Bash parses `declare -i x='a[$(hidden)0]'`
- **THEN** the whole result is unparseable
- **THEN** quoting the assignment operand does not make the builtin evaluation inert

#### Scenario: Exact dispatch wrapper cannot bypass the boundary
- **WHEN** Bash parses `builtin eval 'rm target.txt'`
- **THEN** the whole result is unparseable under the same rule as direct `eval`

### Requirement: Bash command-resolution mutation fails closed globally
Stable v0.3 SHALL treat command-resolution state independently from variable
attributes and cwd. Direct or statically wrapped `exec` SHALL make the whole
result unparseable. Mutating or ambiguous `hash`, `alias`, `unalias`, `shopt`,
and `enable` forms SHALL likewise fail closed before later commands can inherit
an unmodeled executable identity.

The parser MAY retain only exact static query forms: bare `hash`, `alias`,
`shopt`, and `enable`; `hash -l` without operands and `hash -t NAME...`;
`alias [-p] [NAME...]` without an equals-bearing definition; `shopt` option
clusters that contain neither `s` nor `u`; and no-name `enable` listing flags
composed only from `a`, `n`, `p`, and `s`. A dynamic word, invalid option,
plain hash operand, alias definition, `unalias`, `shopt -s` / `-u`, or enable
name / `-d` / `-f` SHALL make the complete result unparseable. Exact `command`
and `builtin` dispatch wrappers SHALL NOT bypass this boundary.

#### Scenario: Hash mapping cannot replace a later executable
- **WHEN** Bash parses `hash -p /bin/rm git; git target.txt`
- **THEN** the whole result is unparseable
- **THEN** the later `git` occurrence is not published with a false executable identity

#### Scenario: Alias activation cannot hide a later command
- **WHEN** Bash parses newline-delimited `shopt -s expand_aliases`, `alias safe=rm`, and `safe target.txt`
- **THEN** the whole result is unparseable before the alias-expanded command can be hidden

#### Scenario: Exec is a global execution boundary
- **WHEN** Bash parses `exec /bin/rm target.txt` outside a loop
- **THEN** the whole result is unparseable
- **THEN** `exec` is not published as an ordinary complete occurrence

#### Scenario: Exact query forms remain structurally visible
- **WHEN** Bash parses `hash -t git`, `alias safe`, `shopt -q expand_aliases`, or bare `enable -n`
- **THEN** the query remains parseable under its exact option grammar
- **THEN** the parser does not infer that the queried identity is safe to authorize

### Requirement: Unmodeled Bash reserved execution syntax fails closed
Stable v0.3 SHALL NOT treat unquoted `time`, `!`, `coproc`, `{`, or `}` in
command position as ordinary verb-chain elements. Until timed/negated
pipelines, coprocesses, and brace groups have typed recursive structure and
state propagation, each form SHALL make the whole result unparseable. Quoted
or escaped spellings and an external `/usr/bin/time` identity SHALL NOT invent
reserved syntax.

#### Scenario: Timed mutation remains in the current shell
- **WHEN** Bash parses `time hash -p /bin/rm git; git target.txt`
- **THEN** the whole result is unparseable
- **THEN** the hash mutation is not hidden inside a `time hash` verb chain

#### Scenario: Negated mutation remains in the current shell
- **WHEN** Bash parses `! hash -p /bin/rm git; git target.txt`
- **THEN** the whole result is unparseable even though negation changes exit status

#### Scenario: Brace group shares command-resolution state
- **WHEN** Bash parses `{ hash -p /bin/rm git; git target.txt; }`
- **THEN** the whole result is unparseable until current-scope brace groups are modeled

#### Scenario: Coprocess body is hidden concurrent execution
- **WHEN** Bash parses `coproc exec /bin/rm target.txt`
- **THEN** the whole result is unparseable until coprocess structure and timing are modeled

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

#### Scenario: Computed Invoke-Expression invalidates current-runspace state
- **WHEN** isolated-mode PowerShell parses `foreach ($f in 'safe.txt') { }; Invoke-Expression $code; git $f`
- **THEN** the computed payload remains an incomplete occurrence
- **THEN** the later `git` occurrence has Unknown working directory and effective `$f` value and is incomplete because the payload can mutate location, variables, aliases, functions, or modules
- **THEN** the canonical alias, static call-operator spelling, and supported module-qualified spelling have the same effect
- **WHEN** a computed `Invoke-Expression` occurs inside a bounded `foreach` region whose transfer cannot be modeled
- **THEN** the complete parse fails atomically rather than retaining stale loop state

#### Scenario: Unproved item-provider target invalidates binding proofs
- **WHEN** isolated-mode PowerShell parses `Set-Item -Path @('Alias:\\foo') -Value Remove-Item; foreach ($f in 'x') { foo $f }`
- **THEN** the array target is not treated as a proved filesystem path
- **THEN** the later command identity and loop value are incomplete
- **WHEN** the target is a proved filesystem path and only `-Value` is dynamic
- **THEN** the value alone does not invalidate independent binding proofs

#### Scenario: Observed alias mutation invalidates an ordinary continuation
- **WHEN** isolated-mode PowerShell parses `Set-Item Alias:git Remove-Item; git child.txt`
- **THEN** the second command occurrence is incomplete because authored identity `git` is no longer proved
- **THEN** this invalidation applies without requiring the command to be inside or after a loop

#### Scenario: Unknown ambient identity preserves leaves and invalidates continuation state
- **WHEN** default-mode PowerShell parses `Write-Output victim.txt; Get-Content relative.txt`
- **THEN** default ambient-state uncertainty alone does not invent an observed mutation or discard the v0.2 compatibility leaves
- **THEN** both v0.3 authorization occurrences are incomplete because their command identities are not proved
- **THEN** the second occurrence has unknown cwd and path-dependent facts because the first invocation may resolve to arbitrary in-process code
- **WHEN** default-mode PowerShell parses `Get-ChildItem | Remove-Item`
- **THEN** the unproved pipeline fails atomically until pipeline state propagation is modeled

#### Scenario: Imported session proxies invalidate command identity
- **WHEN** PowerShell parses `Import-PSSession $session -CommandName git -AllowClobber; git child.txt`
- **THEN** the later `git` occurrence is incomplete
- **THEN** current-runspace substitutions propagate that invalidation
- **THEN** a decoded child host isolates it from the parent continuation
- **WHEN** PowerShell uses `New-Module` to export a function into the current session
- **THEN** the same invalidation and scope-isolation rules apply
- **WHEN** PowerShell uses `Import-Alias` to load aliases into the current session
- **THEN** the same invalidation and scope-isolation rules apply

#### Scenario: Variable-writing parameters invalidate a proved binding
- **WHEN** isolated-mode PowerShell parses `foreach ($f in 'safe.txt') { }; Write-Output C:/sensitive.txt -OutVariable f; Remove-Item $f`
- **THEN** the final occurrence is incomplete and its effective `$f` value is Unknown rather than `safe.txt`
- **THEN** common-parameter aliases, accepted unambiguous prefixes, inline values, command-specific PowerShell 7 variable writers, and opaque splats have the same conservative effect
- **WHEN** `-PipelineVariable f` may affect a downstream pipeline stage before pipeline state propagation is fully modeled
- **THEN** the pipeline fails atomically rather than exposing a stale exact value
- **WHEN** the writer executes in a current-runspace substitution
- **THEN** its invalidation propagates to the outer continuation
- **WHEN** the writer executes in a decoded child host
- **THEN** its exit mutation does not escape into the parent continuation
- **WHEN** `Set-Location` carries a recognized writer such as `-ErrorVariable f`
- **THEN** writer invalidation composes with both reachable location outcomes, including a failure-gated continuation

#### Scenario: Alternate parameter dashes fail closed
- **WHEN** PowerShell source uses U+2013, U+2014, or U+2015 before `OutVariable`, `Path`, or another parameter name
- **THEN** the complete parse is unparseable with empty authorization projections
- **THEN** cwd, provider mutation, and variable-binding analysis never treats the runtime parameter as a literal positional argument

#### Scenario: Module-qualified mutation inside structured source fails closed
- **WHEN** a loop continuation or pipeline invokes an unsupported module-qualified cmdlet such as `Microsoft.PowerShell.Utility\Tee-Object -Variable f`
- **THEN** the complete parse is unparseable with empty authorization projections
- **THEN** quoted call-operator spelling and built-in cmdlets with unapproved verbs cannot bypass the same rule
- **THEN** `Microsoft.PowerShell.Utility\Invoke-Expression` remains the one separately modeled module-qualified wrapper

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
conditional, group, command substitution, or execution region. Blocks, conditional-branch
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

### Requirement: Sequential and loop state joins conservatively
Working-directory and supported variable state SHALL be propagated through
sequential regions and joined across loop exits. Disagreement SHALL never be
resolved by arbitrarily choosing one path.

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

#### Scenario: Unreachable relative redirect has no parse-time cwd proof
- **WHEN** isolated-mode PowerShell parses `foreach ($x in @()) { Write-Output x > relative.txt }`
- **THEN** the body occurrence and its working directory remain incomplete or Unknown
- **THEN** the explicit redirect target is Unknown rather than the parse-time absolute path
- **THEN** an authored absolute redirect target may remain exact because it is cwd-independent

#### Scenario: Outer child-host redirect uses parent binding
- **WHEN** isolated-mode PowerShell parses `foreach ($f in @('one.txt','two.txt')) { pwsh -Command 'Get-Date' > $f }`
- **THEN** the outer redirect target is the finite set of two parent-cwd paths
- **THEN** the decoded child command may remain independently incomplete because child runspace facts are not inferred
- **THEN** an inner redirect authored inside the decoded payload does not inherit the parent loop binding

#### Scenario: Nested child hosts retain outer redirect ownership
- **WHEN** the preceding outer redirect wraps two or more static `pwsh -Command` or `-EncodedCommand` child hosts
- **THEN** the outer redirect target still uses the parent loop binding
- **THEN** it does not bind to the nearest decoded child invocation scope

#### Scenario: Duplicate iteration values retain order
- **WHEN** isolated-mode Bash parses `for f in a b a; do :; done; printf '%s' "$f"`
- **THEN** the internal iteration plan retains `a`, `b`, `a` in that order
- **THEN** the following use of `f` has exact effective value `a`

#### Scenario: PowerShell duplicate values leave the final assignment
- **WHEN** isolated-mode PowerShell parses `foreach ($f in @('a','b','a')) { Write-Output $f }; Write-Output $f`
- **THEN** the body occurrence joins effective values `a` and `b`
- **THEN** the following use of `f` has exact effective value `a`
- **THEN** binding-name comparison is case-insensitive

#### Scenario: Empty PowerShell loop performs no state transition
- **WHEN** isolated-mode PowerShell parses `foreach ($f in @()) { Set-Location C:\\tmp }; Get-Location`
- **THEN** the body remains structurally visible with conservative occurrence facts
- **THEN** no body state transfer occurs
- **THEN** the following command retains the exact incoming location and binding state

#### Scenario: Ordered cap counts visits rather than distinct values
- **WHEN** an isolated-mode Bash loop authors the same literal candidate 33 times
- **THEN** the internal plan exceeds the concrete-iteration cap
- **THEN** it uses bounded fixed-point analysis instead of treating one distinct public value as one visit

#### Scenario: Nested loop analysis stays resource bounded
- **WHEN** nested concrete loops require more than 4096 total body transitions
- **THEN** the complete parse is unparseable
- **THEN** no partial occurrence or compatibility projection is published
- **THEN** the same parse-wide limit applies independently to Bash and PowerShell analysis

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

#### Scenario: PowerShell subexpression cwd propagation is failure aware
- **WHEN** PowerShell parses `Write-Output $(Set-Location /tmp && Get-Location); Get-Item relative.txt`
- **THEN** `Get-Location` uses `/tmp` on the success-only continuation
- **THEN** `Write-Output` and `Get-Item` have unknown cwd because the subexpression may exit with the prior location when `Set-Location` fails
- **THEN** the analyzer does not select either the pre-subexpression or successful cwd as a fallback

#### Scenario: PowerShell foreach location exit joins success and failure
- **WHEN** isolated-mode PowerShell parses `foreach ($d in @('C:\\a','C:\\b')) { Set-Location $d }; Get-Item relative.txt`
- **THEN** each successful visit uses its effective target location
- **THEN** each failure visit retains its incoming location
- **THEN** the post-loop occurrence has unknown cwd and no false exact relative-path resolution

#### Scenario: PowerShell provider location changes relative mutation semantics
- **WHEN** isolated-mode PowerShell parses `Set-Location Alias:; New-Item -Name foo -Value Remove-Item; foreach ($f in 'x') { foo $f }`
- **THEN** the successful provider-location transfer invalidates binding and command-resolution proofs
- **THEN** the later loop command is incomplete
- **WHEN** a command is reached only through `Set-Location Alias: || ...`
- **THEN** that failure-only occurrence retains the incoming filesystem cwd and state

#### Scenario: PowerShell exact failure continuation rebases compatibility paths
- **WHEN** PowerShell parses `Set-Location C:\\target || Get-Item child.txt` from `C:\\work`
- **THEN** the `Get-Item` occurrence has exact cwd `C:\\work`
- **THEN** its cwd-dependent compatibility argument, clause element, and attribution are rebased to `C:\\work`
- **THEN** no resolution derived from `C:\\target` survives on the failure continuation
- **THEN** the same rules apply inside a decoded child host while child exit state remains isolated
- **THEN** an unquoted comma-separated argument remains incomplete rather than being collapsed into one path
- **THEN** a fully quoted comma filename may still be promoted as one literal value

#### Scenario: Unknown PowerShell subexpression mutation propagates
- **WHEN** a PowerShell subexpression changes location to an unknown value
- **THEN** later commands inside the subexpression and in the containing outer continuation have unknown working-directory facts
- **THEN** no prior exact cwd is selected as a fallback

### Requirement: PowerShell execution regions use shell-specific state flow
The PowerShell analyzer SHALL interpret execution-region origin, phase, timing,
and cardinality together with the proved host command, parameter set, and current
abstract state. It SHALL NOT infer variable, location, command-resolution,
runspace, or process propagation from one public scope flag.

The stable v0.3 built-in catalog SHALL cover direct call and dot-source script
blocks; `ForEach-Object` Begin, Process, End, RemainingScripts, and Parallel
binding; `Where-Object -FilterScript`; in-process and remote
`Invoke-Command -ScriptBlock`; `Measure-Command -Expression`;
`Trace-Command -Expression`; `Start-Job` ScriptBlock and
InitializationScript; and `New-Module -ScriptBlock`.
Optional-module `Start-ThreadJob` and deferred breakpoint, event, and argument-
completion receivers are not stable-v0.3 catalog-completeness requirements.
Existing conservative recognition MAY remain, but additional module/version or
trigger-time proof does not gate the release. Every unproved form SHALL follow
the unknown-receiver rule: supported non-pipeline body commands remain visible
while execution and affected state are incomplete. An interior pipeline whose
stage identity is unproved SHALL make the whole result unparseable with empty
`Commands` and `Clauses`.

Known aliases, supported module-qualified spellings, static call-operator
spellings, PowerShell parameter prefixes and inline values, positional
binding, parameter-set selection, and `ScriptBlock[]` binding SHALL resolve to
the same catalog entry. In particular, multiple `ForEach-Object` script blocks
SHALL be assigned Begin, Process, and End semantics according to PowerShell's
binder rather than assumed to share the authored parameter name or position.
An ambiguous binding SHALL retain every body as an unknown execution region,
make affected analysis incomplete, and conservatively invalidate following
state that may be observed or mutated.

Direct `& {}` executes once synchronously in a child variable/command scope
while sharing runspace location. Direct `. {}` executes once synchronously in
the current scope. Their `DirectCall` and `DotSource` origins SHALL remain
distinguishable without source-text reparsing. `ForEach-Object` Begin and End
execute once per invocation;
Process and `Where-Object` Filter execute once per input object and share the
current runspace state. `Measure-Command` and `Trace-Command` expressions
execute synchronously in the current scope. In-process `Invoke-Command`
without `-NoNewScope` isolates ordinary assignment while sharing location;
`-NoNewScope` shares supported state. The in-process parameter set does not
support `-AsJob` and is always synchronous/once. Remote/session/SSH/VM/container
`Invoke-Command` SHALL start from Unknown remote cwd, bindings, aliases,
functions, modules, profiles, and command resolution, and SHALL NOT flow remote
exit state into the invoking host. One complete proved target SHALL publish
Synchronous timing and Once cardinality. Multiple complete proved targets SHALL
publish Concurrent timing and Unknown cardinality. Named, inline, and positional
target arrays SHALL bind consistently. Only a top-level unescaped comma SHALL
separate targets; quoted, backtick-escaped, and structurally nested commas SHALL
remain part of one target. An enabled remote `-AsJob`
or `-InDisconnectedSession` SHALL publish Concurrent timing independently of
target cardinality. A dynamic target or session collection SHALL otherwise
retain Unknown timing and Unknown cardinality. Explicit false-valued switches
SHALL NOT prove concurrency.

#### Scenario: In-process Invoke-Command does not invent AsJob semantics
- **WHEN** PowerShell parses `Invoke-Command -ScriptBlock { Get-Date }`
- **THEN** the region is synchronous and activates once
- **THEN** analysis does not model `-AsJob` as an in-process option

#### Scenario: One remote target has an isolated synchronous region
- **WHEN** isolated-mode PowerShell parses `Invoke-Command -ComputerName server -ScriptBlock { Get-Item child.txt }; Get-Item host.txt`
- **THEN** the region timing is Synchronous and its cardinality is Once
- **THEN** the body working directory and mutable state are Unknown and incomplete
- **THEN** the following host command retains its exact local state

#### Scenario: Remote asynchronous switches prove only concurrency
- **WHEN** PowerShell parses a remote invocation with enabled `-AsJob` or `-InDisconnectedSession`
- **THEN** the execution region timing is Concurrent
- **THEN** target cardinality is proved independently
- **THEN** an explicit `:$false` value does not change synchronous single-target scheduling

#### Scenario: Multiple and dynamic remote targets remain bounded
- **WHEN** PowerShell parses a complete two-computer target list
- **THEN** the execution region timing is Concurrent and cardinality is Unknown
- **WHEN** the comma is instead quoted, backtick-escaped, or nested in one hashtable
- **THEN** it does not prove multiple targets
- **WHEN** PowerShell instead parses a runtime `-Session $session` target
- **THEN** timing and cardinality are Unknown unless an enabled asynchronous switch independently proves Concurrent timing
- **THEN** every remote body command remains visible and incomplete

#### Scenario: Direct invocation origin survives without source text
- **WHEN** a consumer receives direct call and dot-source execution-region nodes
- **THEN** their origins are `DirectCall` and `DotSource` respectively
- **THEN** the consumer can select child-scope or current-scope state flow without reparsing source text

`Start-Job` executes initialization before its main block in a child process;
`ForEach-Object -Parallel` executes in child runspaces. Runspace-local variable
and location exit mutation does not flow into
the containing continuation. In-process child runspaces share process-wide
state such as the environment provider, so a possibly escaping mutation SHALL
invalidate later host binding, command-resolution, and location facts. It SHALL
also invalidate later child-activation facts under `-UseNewRunspace`, which
creates a fresh runspace but not a fresh process.
Runspace `global:` variables, functions, aliases, and location SHALL remain
runspace-local and SHALL NOT by themselves invalidate host facts. After child
command resolution is mutated, each exact changed alias or function name SHALL
be retained in bounded case-insensitive state. A later matching invocation
whose identity is not independently proved SHALL be treated as a possible
process-wide mutation. An ambiguous name, wildcard, or candidate-set overflow
SHALL fail closed to every unproved command name.

#### Scenario: Child scope and shared location are independent
- **WHEN** isolated-mode PowerShell parses `foreach ($x in 'outer') { }; & { Write-Output inner -OutVariable x; Set-Location /tmp }; Write-Output $x; Get-Location`
- **THEN** the following `$x` proof retains `outer` when the child-scope writer succeeds
- **THEN** supported location outcomes include the call region's `/tmp` mutation
- **THEN** the analyzer does not label both facts shared or both isolated

#### Scenario: Dot source shares variable and location state
- **WHEN** isolated-mode PowerShell parses `foreach ($x in 'outer') { }; . { Write-Output inner -OutVariable x; Set-Location /tmp }; Write-Output $x; Get-Location`
- **THEN** following analysis observes the supported `x` and location transfers

#### Scenario: ForEach phases use semantic schedule
- **WHEN** PowerShell authors End, Begin, and Process script blocks out of phase order
- **THEN** syntax and occurrence projection retain authored order
- **THEN** abstract-state execution applies Begin, then Process per input, then End
- **THEN** facts are joined back to their authored occurrences

#### Scenario: Ambiguous custom receiver fails closed without hiding the body
- **WHEN** PowerShell parses `Invoke-Custom { Remove-Item target.txt }` without a proved receiver contract
- **THEN** both host and body commands remain visible
- **THEN** their execution-region facts and observing continuation are incomplete
- **THEN** an unsupported body interior makes the whole result unparseable

#### Scenario: Module-qualified-looking data receiver is shadowable
- **WHEN** isolated-mode PowerShell assigns alias name `Microsoft.PowerShell.Utility\Write-Output` to `Invoke-Command` and then invokes that exact spelling with `{ Remove-Item target.txt }`
- **THEN** static catalog lookup does not independently prove the receiver identity
- **THEN** the `Remove-Item` body remains visible in an unknown incomplete execution region
- **THEN** an unrelated exact alias mutation does not invalidate a different catalog spelling

#### Scenario: Canonical alias-target mutation invalidates authored alias
- **WHEN** isolated-mode PowerShell reassigns `Write-Output` to `Invoke-Command` and then invokes `echo { Remove-Item target.txt }`
- **THEN** receiver proof matches the mutation against canonical `Write-Output` as well as authored `echo`
- **THEN** the `Remove-Item` body remains visible in an unknown incomplete execution region

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
