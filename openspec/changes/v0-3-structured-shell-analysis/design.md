## Context

ShellSyntaxTree v0.2 parses Bash and PowerShell into a flat list of `Clause`
records. The projection works for simple commands, pipelines, command lists,
groups, and supported static command-string wrappers, but control-flow tokens
are rejected because the public model cannot represent headers, nested bodies,
branch alternatives, repeated execution, or state joins.

Netclaw uses the parser as a security-gate input. It must account for every
command that may execute and fail closed when command identity, path scope,
redirect behavior, or another policy-sensitive value is unknown. Production
evidence behind issue #71 shows that flat-grammar rejection causes material
approval fatigue. The 0.25.3 redirect incident also shows why consumers must
not infer shell semantics from `DynamicSkip` and raw token prefixes.

The v0.2 public leaf records are already shipped. Version 0.3 may add public
types and members deliberately, but it must preserve source facts, existing
consumer projections, multi-targeting, AOT compatibility, and the rule that
incomplete executable regions never become authorization evidence.

## Goals / Non-Goals

**Goals:**

- Represent supported nested command structure for Bash and PowerShell.
- Expose every command that may execute without requiring consumers to walk an
  evolving syntax-node hierarchy.
- Resolve constrained loop values and shell state only when bounded proof is
  possible.
- Distinguish static redirect operations from dynamic redirect targets.
- Preserve existing v0.2 command leaves and a conservative flat projection.
- Deliver grammar support in corpus-driven vertical slices.
- Share semantic machinery only where both shell implementations prove the
  same abstraction.

**Non-Goals:**

- Execute commands, expand a filesystem glob, inspect the filesystem, or ask a
  live shell to determine a value.
- Embed executable-specific option, operand, object, or subcommand grammars.
- Unify the Bash and PowerShell lexers or introduce a shared parser base class.
- Treat Bash and PowerShell constructs as equivalent when their scoping,
  expansion, pipeline, or expression semantics differ.
- Make every construct named by issue #71 part of the first implementation
  slice.
- Classify URL-like arguments or environment assignments as harmless without
  executable-aware consumer semantics.
- Interpret heredoc bodies as commands merely because they contain text that
  resembles shell syntax.

## Decisions

### Use three public layers with one compatibility projection

`ParsedCommand` gains a canonical syntax root and a canonical may-execute
projection while retaining `Clauses`:

```text
ParsedCommand
├── Syntax       authored nested shell structure
├── Commands     every command occurrence that may execute
└── Clauses      conservative v0.2 compatibility projection
```

The syntax tree exists for structure, source display, and specialized
analysis. `Commands` is the authorization entry point. `Clauses` remains for
source and binary migration but is no longer the preferred traversal API.

This separates three questions that a single recursive AST cannot answer
safely for all consumers: what was authored, which commands may execute, and
what an older consumer can observe.

### Use a closed library-owned node family, not consumer-extensible nodes

Public syntax nodes derive from one `ShellSyntaxNode` base. The base prevents
external derivation so ShellSyntaxTree owns the complete node family. Common
execution structure may use shell-neutral nodes such as blocks, simple
commands, pipelines, command lists, groups, foreach-style loops, condition
loops, and branches. A shell-specific public node is preferred whenever a
shared type would erase material semantics.

Consumers are not required to exhaustively match node types for authorization.
They use `Commands`; a consumer that does inspect `Syntax` must fail closed or
ignore only for non-authorization display when it encounters a type introduced
after its package version.

A single enum-tagged record was considered. It would force unrelated optional
members onto every node and make invalid combinations representable. A public
interface was also considered, but it would permit external implementations
that the parser cannot produce or analyze. A library-owned record hierarchy is
the closest practical C# approximation to an evolvable discriminated union.

### Keep shell front ends separate and compose shared post-parse passes

`BashLexer` and `PwshLexer` remain independent. Each shell receives its own
recursive structural parser and adapts shell-owned syntax into the public node
contract. Shared internal components operate only on proven common inputs:

- native argument-fragment classification from issue #69;
- source spans and opaque-region handling;
- structural command-occurrence projection;
- the value-domain lattice and bounded combination rules;
- conservative state joins;
- compatibility flattening;
- path-normalization primitives whose semantics are identical.

There is no shared parser base class. Inheritance would couple token
consumption, error recovery, quoting, and expression boundaries that already
differ between the two shells. Shared components are composed as explicit
classifiers and analysis passes instead.

### Preserve resolver-relevant fragments through decoding

The v0.2 lexer-to-resolver contract is too weak for a security parser. A
decoded `string` plus `IsSingleQuoted` cannot distinguish Bash `\$HOME` from
`$HOME`, or PowerShell `` `$HOME `` from `$HOME`. Both pairs decode to the same
text, but the first value in each pair is literal and the second expands. The
current resolver consequently reports a different filesystem path from the
one the shell passes to the executable.

Both shell front ends SHALL retain resolver-relevant value fragments through
decoding. The internal representation is not public API, but it must preserve
the ordered decoded text, exact source range when available, one shell-owned
disposition, and the exact set of lexical transformations eligible for every
fragment:

- `Literal`: the fragment's produced text is exact without lexical expansion;
- `Expansion`: the front end recognizes a typed shell expansion, even when its
  runtime result is not proved;
- `Opaque`: the front end recognizes a computed or unsupported region but
  cannot model its produced value or boundary precisely.

The disposition is not itself a universal expansion bit. Shell lexical
transformations are operation-specific: variable interpolation, tilde
expansion, and globbing do not share identical quote rules. Post-formation path
semantics are a separate stage. In PowerShell, native-command arguments apply
PowerShell's lexical quote rules, while cmdlet `-Path` binding interprets a
quoted `~`, wildcard, provider qualifier, or PSDrive after quote removal;
`-LiteralPath` deliberately differs again. Therefore the parser SHALL retain
an operation-specific lexical capability map and SHALL pass an explicit
resolver consumer/binding context. It SHALL NOT reconstruct either fact from
decoded text, `IsSingleQuoted`, or whether the verb happens to look
cmdlet-shaped.

A representative internal shape is:

```csharp
internal enum ShellValueFragmentKind
{
    Literal,
    Expansion,
    Opaque,
}

[Flags]
internal enum ShellLexicalTransform
{
    None = 0,
    Variable = 1,
    Tilde = 2,
    Glob = 4,
    FieldSplit = 8,
}

internal enum ShellExpansionKind
{
    Variable,
    SpecialParameter,
    PositionalParameter,
    Tilde,
    Glob,
}

internal enum ShellValueCardinality
{
    ExactlyOne,
    ZeroOrOne,
    ZeroOrMore,
    Unknown,
}

internal enum ShellOpaqueCause
{
    None,
    CommandSubstitution,
    PowerShellSubexpression,
    Splat,
    Unsupported,
}

internal readonly record struct ShellExpansionReference(
    ShellExpansionKind Kind,
    string? Name);

internal readonly record struct ShellValueFragment(
    string Value,
    ShellValueFragmentKind Kind,
    ShellLexicalTransform AllowedTransforms,
    ShellExpansionReference? Expansion,
    ShellValueCardinality Cardinality,
    ShellOpaqueCause OpaqueCause,
    int? SourceStart,
    int? SourceLength);

internal readonly record struct ShellValue(
    string Decoded,
    IReadOnlyList<ShellValueFragment> Fragments);

internal enum ShellResolutionConsumer
{
    BashArgument,
    BashRedirect,
    PowerShellNativeArgument,
    PowerShellCmdletPath,
    PowerShellCmdletLiteralPath,
    PowerShellRedirect,
}
```

This mock is non-normative: an equivalent boundary map or compact segment
representation is acceptable. A single aggregate `IsLiteral` flag, a three-way
kind without operation-specific capabilities and typed expansion metadata, or
a resolver call without consumer/binding context is not acceptable. One
authored word can contain both expansion and escaped regions, and one decoded
PowerShell value can have
different correct meanings for a native executable, `-Path`, and
`-LiteralPath`. Lexical expansion transforms only eligible regions. Provider,
PSDrive, wildcard-binding, and filesystem normalization rules then apply to the
exact composed value only when the explicit consumer context permits them.
When every fragment, supported transformation, binding fact, and required cwd
or home fact is exact, the resolver must compose and resolve the exact result
even when literal and expansion fragments are mixed;
it cannot choose `DynamicSkip` merely because retaining provenance requires
more work. `DynamicSkip` is reserved for a recognized expansion whose value or
cardinality is unproved, an `Opaque` fragment, an incomplete boundary, an
unknown required fact, or an unsupported transformation that prevents an exact
path claim. The resolver never infers expansion solely from the decoded string.

In the representative shape, `Literal` carries `AllowedTransforms=None`, no
expansion reference, `ExactlyOne`, and `OpaqueCause=None`. `Expansion` carries
at least one transform plus a typed reference; the bounded analyzer may still
produce `Unknown` when no value proof exists. `Opaque` carries a non-`None`
cause and retains any authored later-stage transform and cardinality facts that
remain knowable. Thus `"$@"` records a special-parameter expansion with
`ZeroOrMore` cardinality rather than losing the reason one exact argument
cannot be claimed. The flags describe authored eligibility and shell ordering,
not a license to recursively rescan produced text. Provider and PSDrive
handling are intentionally absent from the flag set because they are
post-formation consumer semantics. They are owned by PowerShell cmdlet path and
redirect contexts; Bash arguments and redirects never strip a provider-looking
prefix.

PowerShell parameter binding SHALL retain at least `Path` versus `LiteralPath`
internally rather than collapsing both to `treatAsPath=true`. Positional cmdlet
paths use the verb table's explicit binding mode. `LiteralPath` suppresses
wildcard interpretation but still applies quoted tilde, provider-qualifier,
and PSDrive semantics; it is not an "all post-formation transforms off" mode.
Native arguments never gain
cmdlet provider or PSDrive semantics merely because their decoded text has the
same shape. When the parser cannot prove the consumer or binding mode, it
fails closed instead of selecting the more permissive interpretation.

Runtime-variable recognition is part of the shell front end, not the resolver.
Bash special and positional parameters, including the boundary-sensitive
`"$@"`, and PowerShell special, numeric, scoped, braced, and Unicode-named
variables must retain a typed expansion reference and cardinality. Unless the
bounded analyzer has a proof, their analyzed value domain remains `Unknown`;
the parser does not collapse the fragment into literal text or discard why it
is unknown. A recognized interpolation start that the bounded resolver cannot
evaluate is never silently reclassified as literal text. An unterminated
braced interpolation is a syntax error for the whole parse, even when the
outer quote closes cleanly.

Fragment aggregation applies to redirect targets as well as ordinary
arguments. Adjacent fragments after a redirect operator form the one target
the shell consumes; the parser must not expose a resolved prefix as the
redirect and leave the suffix as an unrelated argument. Bash and PowerShell
redirects retain separate consumer contexts. In particular, Bash redirect-word
expansion must prove exactly one target; it cannot reuse ordinary argument
splitting or glob cardinality rules. Here-string data remains on the separate
redirect-analysis path specified below.

`PowerShellRedirect` is its own Path-like post-formation consumer. After value
formation it applies tilde, wildcard, FileSystem provider, and PSDrive
semantics even when the authored value was quoted. A wildcard target remains
unknown without filesystem enumeration. A PSDrive target remains unknown
without a proved drive-to-provider mapping. Neither case is reported as a
static non-path target merely because the parser lacks the runtime fact.

The redesign is grounded in live GNU Bash 5.2.21 and PowerShell 7.6.4
observations on Linux:

| Probe | Observed shell behavior | Contract consequence |
|---|---|---|
| Bash `cat "${HOME"` and PowerShell `Get-Content "${HOME"` | Bash reports an unmatched quote/interpolation boundary; the PowerShell parser reports two syntax errors | Unterminated braced interpolation makes the whole parse unparseable |
| Each shell receives its escaped-dollar spelling of `${HOME` | Each receives the literal string `${HOME` without a parse error | An escaped interpolation start remains literal and complete |
| PowerShell variables `$?`, `$1`, and `$é` | Each produces a runtime value | Their typed expansions remain `Unknown` without a bounded proof, never literal filenames |
| Native `printf` receives quoted `~` and `FileSystem::/tmp` | It receives both strings byte-for-byte | Quoted native values do not gain cmdlet tilde or provider semantics |
| Bash `printf` receives `filesystem::/safe`, quoted or unquoted | It receives the provider-looking string byte-for-byte in both forms | Bash never applies PowerShell provider semantics |
| `Resolve-Path "~"` and `Get-Item "FileSystem::/tmp"` | They resolve to the home directory and `/tmp` | Cmdlet path binding applies after quote removal |
| `Resolve-Path -Path "*.txt"`, `Test-Path -LiteralPath "*.txt"`, and native `printf "*.txt"` in a directory containing `a.txt` | `-Path` matches once; `-LiteralPath` is false; native `printf` receives `*.txt` | `Path`, `LiteralPath`, and native contexts remain distinct without filesystem enumeration by this library |
| Native PowerShell receives unquoted and quoted `*.txt` in a directory containing `a.txt` | The unquoted form receives `a.txt`; the quoted form receives `*.txt` | Unquoted native wildcard cardinality is unknown without enumeration; quoting produces one exact literal value |
| Each shell redirects to its escaped-dollar spelling of `$HOME".txt"` | Each creates exactly one file named `$HOME.txt` | Adjacent redirect fragments form one target |
| Bash redirects unquoted and quoted `*.txt` in a directory with multiple `.txt` files | The unquoted form is an ambiguous redirect; the quoted form creates the literal file `*.txt` | `BashRedirect` requires one proved target and retains quote-sensitive glob eligibility |
| PowerShell redirects quoted `~`, `*.txt`, `FileSystem::...`, and a FileSystem PSDrive path | Tilde, wildcard, provider, and drive semantics apply after quote removal | `PowerShellRedirect` is Path-like but remains separate from cmdlet and native argument contexts |

Task 2.2 converts these probes into deterministic shell-oracle regressions on
the implementation branch; the design corpus records their desired semantic
shape before the v0.2 compatibility leaves can express it.

Issue #69's shared native argument classifier consumes these proved fragments
through explicit Bash and PowerShell adapters. It may own adjacency, decoded
concatenation, and source-span aggregation, but the adapters retain shell
quoting and escaping semantics. This correction is internal and leaves the
v0.2 public leaf records and the locked additive v0.3 public API unchanged.

### Represent simple commands with existing Clause leaves

A simple-command syntax node wraps the same `Clause` value exposed through the
occurrence and compatibility projections. Existing `VerbChain`, `Arg`,
`Redirect`, and `ClauseElement` facts are not replaced.

Executable command substitutions evaluated for the simple command's authored
words and redirects, including an expanding heredoc body, are owned by that
`SimpleCommandSyntax` through an authored-order `Substitutions` collection.
This is necessary because the unchanged `Clause` leaf records the
opaque/dynamic value but cannot retain a nested executable tree. A substitution
nested inside an inner command belongs to that inner simple command; it is not
promoted to a sibling or stored in a side table.

New structural nodes preserve their complete source range when it can be mapped
exactly. Expanded wrapper content retains the current nullable-span rule rather
than inventing offsets into escaped or encoded outer text.

### Project authored command occurrences exactly once

`Commands` contains one entry per authored simple command that may execute,
not one entry per predicted runtime iteration. Each occurrence carries its
`Clause`, immediate structural role, compositional ancestry suitable for
analysis and diagnostics, and an explicit completeness fact. Immediate roles
include at least ordinary, pipeline stage, condition, iterator, loop body,
branch, and substitution; ancestry frames retain every outer role, such as a
pipeline stage nested inside a loop body. The final names are locked with the
public API review.

Condition and iterator commands are never omitted. Mutually exclusive branch
commands all appear because the collection is a may-execute set. Runtime loop
counts do not duplicate occurrences; bounded variable domains describe the
possible effective values at the occurrence.

Completeness and value precision are independent. A structurally complete
occurrence may conservatively contain an `Unknown` value domain when the
command and its ancestry are fully discovered but a runtime value cannot be
proved.

If any executable region cannot be discovered completely, the containing
`ParsedCommand` remains `IsUnparseable=true`. Partial syntax may be returned
for diagnostics, but `Commands` and `Clauses` are empty so no authorization
projection exposes a discovered subset.

### Preserve Clauses as a conservative flattened view

For a fully parseable result, `ParsedCommand.Clauses` contains every authored
simple command occurrence in source order, including nested iterator,
condition, branch, body, and substitution commands. It does not invent
compound operators across structural boundaries. Existing `Clause.Operator`
values are retained only for actual authored relationships.

Projection order is deterministic. Disjoint executable regions follow authored
source order. An enclosed substitution precedes its containing simple command,
and nested substitutions are emitted innermost first. When wrapper decoding
makes outer source spans unavailable, containing structural collection order
is the tie-breaker. The occurrence and compatibility projections use the same
order.

Sibling substitution ancestry frames use `Region=Substitution` and the
authored zero-based index from the structural collection that owns each
`CommandSubstitutionSyntax`. This is `SimpleCommandSyntax.Substitutions` for an
embedded command value and the iterator-command collection for a direct
iterator substitution. It remains deterministic when decoded wrapper content
has no outer source span.

The compatibility clauses preserve authored arguments. They do not substitute
loop variables into `Arg` and therefore retain dynamic markers that make
v0.2-style security consumers prompt rather than silently authorize a broader
scope. Proven effective values belong to the new occurrence analysis.

### Use a small bounded value-domain lattice

The analysis domain distinguishes:

- `Exact`: one completely proved shell value;
- `FiniteSet`: a bounded, fully enumerated set of values;
- `Pattern`: a shell-specific symbolic pattern plus conservative covering
  scope when one can be proved without enumeration;
- `Unknown`: runtime-produced, mutated, indirectly expanded, unsupported, or
  above the configured proof bound.

The analysis never evaluates command substitutions or PowerShell pipelines.
It exposes their commands, then treats the resulting value as unknown. It does
not assume Bash glob options or PowerShell object-to-string conversion.
Combinations that exceed the locked candidate cap collapse to `Unknown`.

Initial loop-variable substitution is restricted to contexts whose quoting and
shell rules prove the resulting argument boundary. Unquoted Bash expansion,
PowerShell object-valued pipelines, indirect expansion, mutation, and
cross-product explosion remain unknown until separately specified.

Bash loop binding also requires a proved initial shell-variable environment.
`BashParserOptions.InitialStateMode` defaults to `Unknown`; bounded Bash loop
analysis is unavailable in that mode because an ambient binding may be
readonly, integer-valued, a nameref, exported, or otherwise stateful.
`IsolatedNonInteractive` is a caller assertion that the complete source runs in
a new non-interactive Bash process without profile, `BASH_ENV`, or `ENV`
startup content and without an inherited entry for the bound name. The v0.3
positive grammar is deliberately limited to lowercase ordinary scalar names
matching `[a-z][a-z0-9_]*`, excluding `auto_resume` and `histchars`. Names
outside that boundary fail the whole loop region closed. This excludes Bash
magic and identity-sensitive bindings including `RANDOM`, `LINENO`, `HOME`,
`PATH`, `CDPATH`, and `IFS` without attempting an incomplete denylist.
Recognized source-level variable mutation invalidates isolated mode in every
later scope that can observe it. A decoded `bash -c` after `export` therefore
enters with `Unknown` initial variable state, while a cwd-only transfer retains
the caller's variable-state assertion.

PowerShell needs the same explicit boundary for different reasons.
`PwshParserOptions.InitialStateMode` defaults to `Unknown`, which permits
structural discovery but withholds exact or finite `foreach` binding proofs.
`IsolatedNonInteractiveNoProfile` asserts that the complete source executes in
a newly spawned noninteractive, no-profile PowerShell process rather than an
interactive, pooled, reused, profile-initialized, or uncontrolled caller-initialized
runspace. The caller also controls startup configuration and the inherited
environment: module auto-loading is disabled, or available modules and module
search paths are pinned to the policy's reviewed baseline. A fixed bootstrap
may establish those constraints only when it cannot define or mutate
loop-bound variables or policy-relevant command identities.
`-NoProfile -NonInteractive` alone is not proof of that environment. Ambient
PowerShell bindings can be typed, validated, constant,
read-only, or scoped; aliases, functions, and modules can independently change
command identity. Syntax alone cannot erase any of those facts.

The positive binding grammar therefore accepts only ordinary unscoped names
that do not case-insensitively collide with automatic, constant, read-only,
typed, validated, preference, or configuration variables known to the
supported runtime. Built-in preference variables are excluded because
assignment can coerce or reject authored strings or alter host behavior. The
reviewed documented inventory is pinned independently from the live fresh-host
oracle so lazy and configuration-dependent preferences cannot escape the gate.
Current-runspace groups, `$()`, and
static `Invoke-Expression` share supported binding, command-resolution, and cwd
state. A decoded child `pwsh` host starts at `Unknown` unless its own invocation
independently proves the complete constrained-host contract. Recognized
variable, alias,
function, or module mutation invalidates every later observing proof; a cwd-only
transfer preserves the independent initial-runspace assertion.

Effective values are shell facts, not executable semantics. The analysis must
preserve both the authored shell classification and each proved effective
value. PowerShell does not retroactively turn a string value such as `-Force`
into a cmdlet parameter token, while the same value passed to a native
executable may participate in that executable's option grammar. A consumer
must therefore apply the relevant shell binding rules and re-run its complete
executable-aware grammar for every exact or finite candidate.

### Join state rather than selecting a path

The analysis carries abstract working-directory and supported variable state
through the structure. Sequential lists propagate state. Subshell or
scope-isolated groups do not leak state. Branches join their possible exit
states; loops include the zero-iteration path unless shell semantics prove at
least one iteration.

For Bash, one internal flow result carries separate reachable success and
failure states. A simple command records its occurrence facts from the joined
input, then applies shell-specific state transfer to each exit partition.
`&&` consumes only success, `||` consumes only failure, and `;` / newline
consume their join. This distinction remains internal: the public cwd domain
stays `Exact` or `Unknown`. In particular, literal `cd /x` produces `/x` only
on its success exit and retains the incoming cwd on failure; `cd /x; next`
therefore cannot make `next` exact unless both possibilities agree.

Implementation must run this as a structure-aware abstract-state pass over the
proved syntax tree, not by exposing the compatibility parser's mutable
parse-order cwd attribution. Parse order is not execution-state order for
pipelines or conditional lists, and one symbolic loop-body parse cannot prove
the cwd of later iterations. The compatibility attribution path remains a
v0.2 leaf-construction detail. The ordered-binding pass may retain exact cwd
for reached nonmutating loop occurrences. Loop cwd mutation, recognized shell-
state mutation before or inside a loop, and nested active-name reuse continue
to fail closed until complete effective-argv transfers and repeated mutable
state are modeled.

Bash command substitution executes in an isolated subshell state. State changes
affect later commands inside that substitution but never the containing command
or following outer commands. PowerShell `$()` evaluates in the current runspace
scope. Its sequential location changes affect later commands inside the
subexpression, then the containing command and outer continuation. An unknown
PowerShell location mutation therefore makes those later working-directory
facts unknown; the analyzer never falls back to the pre-subexpression cwd.

A decoded `bash -c` / `sh -c` wrapper enters with the abstract cwd of the
invocation occurrence. It does not inherit unexported loop bindings; exported
variable state is used only when the analyzer can prove the corresponding
export semantics, and is otherwise unknown. Inner sequential changes are
visible to later inner occurrences but do not leak on wrapper exit. This v0.3
occurrence rule corrects the old compatibility attribution shortcut without
requiring v0.2 leaves to invent a new exact path.

Decoded-wrapper cloning must remap every parser-owned loop plan and argument
provenance fact to the cloned syntax/Clause references. Losing a side-table
entry is not permission to analyze one representative iteration; the wrapper
region fails closed if the mapping cannot be proved complete.

Exact and finite Bash `for ... in` domains are analyzed in authored iteration
order within the 32-candidate cap. Each iteration consumes the joined reachable
state from the preceding iteration, and the loop exit joins every reachable
normal exit with the zero-iteration path when zero iterations remain possible.
Pattern and unknown domains use a bounded fixed point with widening to
`Unknown`; they are never treated as one representative iteration. Until
`break`, `continue`, `return`, `exit`, and `exec` have explicit transfer
semantics, a supported loop region containing one of them fails closed instead
of publishing incomplete continuation facts. Recognition includes statically
wrapped builtin forms such as `builtin break` and `command exit`. `eval`,
`source` / `.`, and execution-bearing `trap` also fail the whole region closed
unless every executable region and state transfer is discovered.

The analyzer also has a parse-wide budget of 4096 loop-body transitions.
Concrete nested products and fixed-point visits consume the same budget; an
overflow fails the complete parse atomically so an adversarial input cannot
force an unbounded cross-product or receive a partial security projection.

The internal loop plan is distinct from the public value-domain summary. It
retains an executable word plan parameterized by the incoming analyzer binding
map, plus a cardinality of `Never`, `OneOrMore`, or `ZeroOrMore`. Evaluating the
plan preserves every concrete candidate in authored order, including
duplicates. Thus `a b a` has a final exact binding of `a`, while an explicit
empty iterable has no body transition and preserves the incoming binding. All
reachable visits to one authored body occurrence join their input facts. The
32-candidate cap applies to ordered iterations, not distinct public values: 33
authored `a` words use a bounded fixed point and widening rather than selecting
one retained value. An inner iterable is re-evaluated for each concrete outer
binding so nested correlation is not flattened before execution-state
analysis.

The analyzer, not the structural parser, owns the variable map and binding
lifetime. Bash loop assignment is not lexical scope: a nonempty loop leaves the
last value after `done`, zero iterations preserve the incoming value, and a
same-name inner loop overwrites rather than restoring the outer value. The
first v0.3 pass may keep active same-name nesting unparseable, but it cannot use
push/pop shadowing or unconditional parser-time persistence.

Parser-owned side facts retain each argument's complete `ShellValue` fragment
sequence. For every concrete visit, the analyzer re-evaluates all arguments
from the current binding map, accumulates effective domains by authored element
coordinate, and applies state-transfer grammar to the effective argv. This is
required for `cd "$f"`: a candidate may be `-P`, `--`, `-`, or an operand even
though the authored expansion was not lexed as an option. Operand-only string
substitution is not an acceptable shortcut.

Static dispatch is interpreted from the same ordered element stream. Exact
`command -p`, `command --`, and `builtin --` wrappers may recurse to `cd` or
`chdir`; `command -v` / `-V` is a nonmutating query. Invalid or dynamic wrapper
grammar does not dispatch. If a rebound physical option changes which authored
element is the operand, every compatibility resolution made unsafe by that
visit is cleared by element coordinate in both `Clause.Args` and
`Clause.Elements`. The first operand ends option recognition; every later word
is a second operand and therefore an exact failure. Unknown quoted one-word
values may conservatively produce unknown cwd on success, but unquoted field
splitting or globbing of a tracked loop binding leaves argv cardinality
unproved and fails the region atomically. Ambient dynamic values retain the
older compatibility contract's conservative unknown-state behavior.

An unreachable success or failure partition stays unreachable. `&&` and `||`
must not replace a missing input partition with `JoinedState` to manufacture
facts for a structurally present but unreachable continuation. Such commands
remain projected with conservative facts. This is required before the analyzer
can expose the one-sided success result of an explicit empty loop.

Bash pipeline stages enter from the same pipeline input state. Ordinary stage
state does not leak, but the analyzer cannot assume the last stage is isolated
because `lastpipe` is shell-option and job-control dependent. If the last stage
can mutate supported parent state and execution options are not proved, the
pipeline exit joins the isolated and current-scope possibilities; disagreement
becomes `Unknown`. `pipefail` is modeled independently because it can change
whether a leaked state belongs to the success or failure exit partition. When
either option is unproved, both partitions conservatively include every
option-dependent reachable outcome.

The first implementation may collapse any differing cwd states to `Unknown`
rather than publish a finite cwd set. Selecting one branch's directory is
never allowed. A later additive version may expose bounded cwd alternatives if
the consumer contract demonstrates a need.

Compatibility leaves are produced after analysis. If a relative `Arg` or
`ClauseElement` depended on a cwd that is not exact at that occurrence, its raw
spelling, path relevance, and source coordinates remain, but `Resolved` is
cleared. Any false exact synthetic cwd-attribution argument is replaced by the
existing `Raw="<dynamic-cwd>"`, `Kind=DynamicSkip`, `Resolved=null`,
`IsCwdAttribution=true` marker. The projection never omits that fail-closed
signal merely because no exact cwd can be published. This is a security
correction allowed by the compatibility contract, not an invitation to rewrite
authored operands with analyzed loop values.

### Model redirect operation and target independently

The new redirect facts separate:

- source descriptor, when explicit;
- operation: file input/output/append, descriptor duplicate, close, move,
  combined output, heredoc/here-string where supported, or unknown;
- static target descriptor, when present;
- target value or value domain;
- path relevance;
- analysis completeness.

`2>&1`, `2>&1-`, and `2>&-` are static operations. `2>&$FD` is a computed
target and cannot be exempted merely because its raw value starts with `&`.
The existing `RedirectDirection`, target text, and `IsDynamicSkip` properties
remain compatibility facts during migration.

Heredoc body representation is a separate design task. A heredoc body is data,
not inherently a child command. Delimiter quoting, expansion mode, body source,
and any executable substitutions must be preserved before a heredoc can be
declared completely analyzable.

### Deliver alternating vertical slices

The implementation order is:

1. Lock public types, compatibility behavior, completeness, and fixed bounds.
2. Correct resolver-fragment provenance with paired real-shell oracles.
3. Extract issue #69 and other shared helpers against the corrected behavior.
4. Produce `Syntax`, `Commands`, and retained compatibility `Clauses` for
   existing grammar, including the explicit oracle-proved resolver corrections.
5. Validate the new consumer path on existing Netclaw cases.
6. Add Bash `for ... in` with literal values first.
7. Add PowerShell `foreach` with literal arrays next.
8. Extract shared occurrence/value/state machinery proven by both slices.
9. Add bounded patterns and iterator/substitution command discovery.
10. Add condition loops and branches in separately testable shell-specific
   slices.

This order prevents a complete Bash implementation from hardening a
Bash-shaped public abstraction before PowerShell exercises it.

### Make the corpus an execution-accounting contract

New corpus expectations cover syntax shape, compatibility clauses, command
occurrences, roles, completeness, value domains, redirects, and joined state.
Every supported input has adversarial pairs. The tests assert that every
authored executable region appears exactly once and no parser path can return
`IsUnparseable=false` after silently discarding an executable region.

Both direct parser tests and Netclaw integration cases are required. The public
corpus remains sanitized under the existing PII audit.

## Risks / Trade-offs

- **[Public hierarchy evolves after 0.3]** -> Consumers authorize through
  `Commands`, not exhaustive syntax matching; new node types require new corpus
  and compatibility tests.
- **[Adding record properties changes generated behavior]** -> Document
  equality, hashing, `ToString()`, and serialization changes and pin public API
  snapshots before the first prerelease.
- **[Compatibility flattening loses structure]** -> Preserve every command and
  dynamic authored operand so old security consumers remain conservative;
  direct new consumers to `Commands`.
- **[Finite analysis is mistaken for shell execution]** -> Use explicit domain
  kinds, fixed caps, no filesystem enumeration, and unknown fallback.
- **[Shared abstractions erase language semantics]** -> Keep lexers and
  structural parsers separate; extract only duplication demonstrated by both
  working slices.
- **[Decoded values erase expansion provenance]** -> Retain ordered internal
  literal, typed-expansion, and opaque fragments with transform, cardinality,
  and cause facts; never let a resolver reconstruct those facts from decoded
  text alone.
- **[Partial trees invite partial authorization]** -> Keep
  `IsUnparseable=true`, return empty `Commands` and `Clauses`, and keep any
  partial syntax diagnostic-only.
- **[Scope grows to every script construct]** -> Treat heredocs, process
  substitution, background lists, C-style loops, arithmetic, definitions, and
  `.ps1` files as separately gated slices.
- **[Candidate combinations become expensive]** -> Apply a small fixed cap and
  collapse the complete fact to `Unknown` before combinatorial growth.

## Migration Plan

1. Keep the Netclaw 0.25.4 static-descriptor workaround while it consumes
   ShellSyntaxTree 0.2.
2. Accept the OpenSpec and synchronize the locked API and grammar into
   `SPEC.md`, `SPEC.POWERSHELL.md`, `PROJECT_CONTEXT.md`, and
   `IMPLEMENTATION_PLAN.md`.
3. Correct the internal resolver-fragment contract and promote each paired
   design case into the executable corpus before extracting issue #69.
4. Ship the new structural and occurrence API in a 0.3.0 alpha while existing
   grammar preserves raw spelling, decoded logical values, source spans, and
   unaffected compatibility facts. Paired shell-oracle corrections to false
   path or `DynamicSkip` claims are explicit compatibility notes, not hidden
   behavior-preserving changes.
5. Migrate Netclaw to `Commands` and explicit redirect facts before enabling
   supported control flow for authorization reuse.
6. Add Bash and PowerShell vertical slices behind corpus and integration gates.
7. Promote 0.3.0 only after both shells, old-consumer fail-closed behavior, and
   the new Netclaw consumer path pass their acceptance matrices.

Before stable 0.3.0, a flawed new surface can be revised with prerelease
migration notes. After stable release, removals or renames follow the normal
minor-version rule for this 0.x library. `Clauses` is not removed in 0.3.

## Contract-Lock Decisions

The first paired design-corpus review resolves the original open questions as
follows. These decisions are normative for this change and are synchronized
into the release specifications before production types are added.

1. Appendix A locks the public type names, members, enum zero values, and
   defaults. The syntax hierarchy is a closed record family with an explicit
   kind; command discovery remains collection-based so authorization consumers
   never need a type switch.
2. A value domain contains at most 32 candidates. Supported structural nesting
   is at most 16 container nodes. Existing decoded-command wrapper recursion
   remains capped at 5. The limits are public static get-only properties, not
   caller-configurable parser options or compile-time constants. Candidate
   overflow produces `Unknown`; structural or wrapper-depth overflow makes the
   whole result unparseable.
3. Within one successful `ParsedCommand`, a simple-command syntax leaf, its
   command occurrence, and its compatibility `Clauses` entry reference the
   identical `Clause` instance. This is an in-memory parser-result guarantee,
   not a serialization reference-preservation guarantee.
4. The initial `Pattern` domain is limited to a Bash path-shaped glob with no
   dynamic root, substitution, indirect expansion, parent-traversal segment,
   or glob-bearing dot-prefixed segment that can match `..` when
   `globskipdots` is disabled. Its `CoveringDirectory` is the exact static directory prefix,
   resolved against an exact cwd when relative. The parser never enumerates the
   filesystem. Bash unmatched-glob settings can change whether the loop has
   zero iterations or yields the literal pattern, but neither outcome escapes
   that lexical cover. Every other pattern becomes `Unknown`.
5. v0.3 does not publish a finite cwd domain. Identical branch exits retain an
   exact cwd; the first disagreement, unknown mutation, or loop-exit ambiguity
   produces `Unknown`.
6. The stable v0.3 grammar includes the existing simple-command grammar,
   structural projection, Bash `for ... in`, `while` / `until`, and
   `if` / `elif` / `else`, plus PowerShell `foreach`, `while`, and
   `if` / `elseif` / `else` within the bounded subsets below. It also preserves
   the existing Bash heredoc grammar while adding explicit body, delimiter,
   expansion, and completeness facts, and adds Bash `<<<` here strings.
   Process substitution, single-`&` background lists, Bash `case`, PowerShell
   `switch`, arithmetic/C-style loops, implicit Bash positional-parameter
   loops, and function/definition bodies remain independently gated.

On every unparseable result, `Commands` and the v0.2 `Clauses` projection are
empty. `Syntax` may contain a partial diagnostic tree, but it cannot be used as
authorization evidence. This makes accidental subset authorization harder for
both old and new consumers.

## Appendix A: Locked Public API Contract

The following shape is normative for this OpenSpec change. XML documentation
and the public API snapshot must preserve these members and defaults when task
group 1 synchronizes the contract into `SPEC.md` and `SPEC.POWERSHELL.md`.

### Structural node family

```csharp
namespace ShellSyntaxTree;

public abstract record ShellSyntaxNode
{
    // Prevent consumers from extending the parser-owned node family.
    private protected ShellSyntaxNode() { }

    public abstract ShellSyntaxKind Kind { get; }
    public int? SourceStart { get; init; }
    public int? SourceLength { get; init; }
}

public enum ShellSyntaxKind
{
    Unknown,
    Block,
    SimpleCommand,
    Pipeline,
    CommandList,
    Group,
    ForEach,
    ConditionLoop,
    Conditional,
    ConditionalBranch,
    CommandSubstitution,
}

public sealed record ShellBlockSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.Block;
    public IReadOnlyList<ShellSyntaxNode> Statements { get; init; } = [];
}

public sealed record SimpleCommandSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.SimpleCommand;
    public Clause Clause { get; init; } = new();
    public IReadOnlyList<CommandSubstitutionSyntax> Substitutions { get; init; } = [];
}

public sealed record PipelineSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.Pipeline;
    public IReadOnlyList<ShellSyntaxNode> Stages { get; init; } = [];
}

public sealed record CommandListSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.CommandList;
    public IReadOnlyList<CommandListItemSyntax> Items { get; init; } = [];
}

public sealed record CommandListItemSyntax
{
    public CompoundOperator Operator { get; init; }
    public ShellSyntaxNode Command { get; init; } = new ShellBlockSyntax();
}

public sealed record GroupSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.Group;
    public ShellGroupKind GroupKind { get; init; }
    public ShellBlockSyntax Body { get; init; } = new();
}

public enum ShellGroupKind
{
    Unknown,
    CurrentScope,
    IsolatedScope,
}

public sealed record ForEachSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.ForEach;
    public LoopBindingSyntax Binding { get; init; } = new();
    public ShellSourceFragment Iterable { get; init; } = new();
    public ShellBlockSyntax IteratorCommands { get; init; } = new();
    public ShellBlockSyntax Body { get; init; } = new();
}

public sealed record LoopBindingSyntax
{
    public string Name { get; init; } = "";
    public ShellSourceFragment Source { get; init; } = new();
}

public sealed record ShellSourceFragment
{
    public string Raw { get; init; } = "";
    public int? SourceStart { get; init; }
    public int? SourceLength { get; init; }
}

public sealed record ConditionLoopSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.ConditionLoop;
    public ConditionLoopKind LoopKind { get; init; }
    public ShellBlockSyntax Condition { get; init; } = new();
    public ShellBlockSyntax Body { get; init; } = new();
}

public enum ConditionLoopKind
{
    Unknown,
    While,
    Until,
}

public sealed record ConditionalSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.Conditional;
    public IReadOnlyList<ConditionalBranchSyntax> Branches { get; init; } = [];
    public ShellBlockSyntax? Else { get; init; }
}

public sealed record ConditionalBranchSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.ConditionalBranch;
    public ShellBlockSyntax Condition { get; init; } = new();
    public ShellBlockSyntax Body { get; init; } = new();
}

public sealed record CommandSubstitutionSyntax : ShellSyntaxNode
{
    public override ShellSyntaxKind Kind => ShellSyntaxKind.CommandSubstitution;
    public ShellBlockSyntax Body { get; init; } = new();
}
```

`ForEachSyntax` shares only proved execution structure. `Iterable.Raw` preserves
the shell-specific authored expression without claiming that Bash words and
PowerShell expressions share a grammar; `IteratorCommands` separately exposes
commands discovered inside that expression. Bounded values live on command
occurrences, not on this display tree. Internal parse nodes and expression
adapters remain shell-specific.

Every enum introduced in v0.3 reserves zero as `Unknown`, except existing v0.2
enums whose zero values are already locked. Consumers fail closed on `Unknown`
or an unrecognized numeric value. The source implementation pairs the shown
`private protected` ordinary base constructor with an assembly-only abstract
ownership member. Records synthesize a protected copy constructor, so the
ordinary constructor alone would still permit a specially constructed external
derived record. The non-public abstract member makes every external concrete
implementation fail compilation without adding to the public contract; every
library-owned sealed node implements it internally. Later library versions may
add derived records, so authorization code still needs a default fail-closed
type-switch arm.

### Command occurrence and bounded values

```csharp
public sealed record CommandOccurrence
{
    public Clause Clause { get; init; } = new();
    public CommandOccurrenceRole ImmediateRole { get; init; }
    public IReadOnlyList<CommandAncestryFrame> Ancestry { get; init; } = [];
    public IReadOnlyList<EffectiveArgument> EffectiveArguments { get; init; } = [];
    public ShellValueDomain WorkingDirectory { get; init; } = ShellValueDomain.Unknown;
    public IReadOnlyList<RedirectAnalysis> Redirects { get; init; } = [];
    public bool IsComplete { get; init; }
}

public enum CommandOccurrenceRole
{
    Unknown,
    Ordinary,
    PipelineStage,
    Condition,
    Iterator,
    LoopBody,
    Branch,
    Substitution,
}

public sealed record CommandAncestryFrame
{
    public ShellSyntaxKind AncestorKind { get; init; }
    public CommandAncestryRegion Region { get; init; }
    public int? ChildIndex { get; init; }
    public int? SourceStart { get; init; }
    public int? SourceLength { get; init; }
}

public enum CommandAncestryRegion
{
    Unknown,
    Root,
    Statement,
    PipelineStage,
    GroupBody,
    Iterator,
    LoopBody,
    Condition,
    Branch,
    Substitution,
}

public sealed record EffectiveArgument
{
    // A stable authored coordinate is safer than correlating by string value.
    public int ClauseElementIndex { get; init; } = -1;
    public ShellValueDomain Value { get; init; } = ShellValueDomain.Unknown;
}

public sealed record ShellValueDomain
{
    public static ShellValueDomain Unknown { get; } = new();

    public ShellValueDomainKind Kind { get; init; }
    public IReadOnlyList<string> Values { get; init; } = [];
    public string? Pattern { get; init; }
    public string? CoveringDirectory { get; init; }
}

public enum ShellValueDomainKind
{
    Unknown,
    Exact,
    FiniteSet,
    Pattern,
}

public static class ShellAnalysisLimits
{
    public static int MaxValueCandidates => 32;
    public static int MaxStructuralNesting => 16;
    public static int MaxWrapperRecursionDepth => 5;
}
```

`Ancestry` is ordered outermost to innermost and contains structural containers,
not the `SimpleCommandSyntax` leaf itself. `ChildIndex` disambiguates repeated
regions such as a pipeline stage or conditional branch. `ImmediateRole`
describes the nearest execution relation; ancestry retains outer relations.
An embedded substitution adds a substitution ancestry frame while its
containing simple-command leaf remains excluded. Iterator or other outer
frames are therefore preserved outside that substitution frame. Its
`ChildIndex` is the authored index within the owning structural collection.

The contract uses a source-authored element coordinate rather than attaching
derived values directly to `Arg`. This prevents a loop iteration from mutating
the compatibility leaf and provides a place for one authored token to have
multiple possible effective values. Redirect operands use the separate redirect
coordinate below. Inline option bindings retain their one authored
`ClauseElement`; shell expansions that might create more than one argument are
`Unknown` until their boundaries can be proved. An occurrence lifted from a
wrapper may therefore have a valid clause-element index even when that
element's outer source span is unavailable.
An `Unknown` value at one of those coordinates does not by itself make the
occurrence structurally incomplete.

The parser emits only valid value-domain combinations:

- `Unknown`: no values, pattern, or covering directory;
- `Exact`: exactly one value and no pattern fields;
- `FiniteSet`: 2–32 distinct values and no pattern fields;
- `Pattern`: no values, a non-empty pattern, and a non-empty covering directory.

Any internally invalid combination is a parser bug. A consumer reading an
externally persisted or reconstructed instance fails closed rather than trying
to repair it.

### Explicit redirect facts

```csharp
public sealed record RedirectAnalysis
{
    public int RedirectIndex { get; init; } = -1;
    public RedirectSource Source { get; init; } = new();
    public RedirectOperation Operation { get; init; }
    public int? TargetDescriptor { get; init; }
    public ShellValueDomain Target { get; init; } = ShellValueDomain.Unknown;
    public HereDocumentAnalysis? HereDocument { get; init; }
    public bool IsPathRelevant { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record HereDocumentAnalysis
{
    public ShellSourceFragment Delimiter { get; init; } = new();
    public ShellSourceFragment Body { get; init; } = new();
    public HereDocumentExpansionMode ExpansionMode { get; init; }
    public bool StripLeadingTabs { get; init; }
    public bool IsComplete { get; init; }
}

public enum HereDocumentExpansionMode
{
    Unknown,
    Literal,
    Expand,
}

public sealed record RedirectSource
{
    public RedirectSourceKind Kind { get; init; }
    public int? Descriptor { get; init; }
}

public enum RedirectSourceKind
{
    Unknown,
    Default,
    Descriptor,
    PowerShellAllStreams,
}

public enum RedirectOperation
{
    Unknown,
    FileInput,
    FileOutput,
    FileAppend,
    DescriptorDuplicate,
    DescriptorClose,
    DescriptorMove,
    CombinedOutput,
    CombinedOutputAppend,
    HereDocument,
    HereString,
}
```

Occurrence-specific redirect analysis stays on `CommandOccurrence`; the
existing `Redirect` record remains untouched. This is necessary because a loop
can give one authored redirect target several effective values, while mutating
`Redirect` would also change v0.2 equality and serialization. `RedirectIndex`
correlates to `Clause.Redirects`. `RedirectSource` represents the operator's
default stream, a numeric Bash/PowerShell descriptor, or PowerShell's `*`
selector without losing shell identity. Invalid source-kind/descriptor
combinations are incomplete and fail closed.

`HereDocument` is non-null only for `HereDocument` operations and preserves the
authored delimiter and body independently. `Literal` means delimiter quoting
disables body expansion; `Expand` means every execution-bearing substitution
must be discovered and surfaced before the redirect can be complete.
`HereString` specifically represents Bash `<<<`; its operand and exact, finite,
or unknown effective data use `Target`. PowerShell `@"..."@` and `@'...'@`
here-strings remain ordinary PowerShell value tokens, not redirects.

### ParsedCommand composition and consumer entry point

```csharp
public sealed record ParsedCommand
{
    public string Source { get; init; } = "";

    public ShellBlockSyntax Syntax { get; init; } = new();
    public IReadOnlyList<CommandOccurrence> Commands { get; init; } = [];

    // v0.2 compatibility projection; not canonical for structured analysis.
    public IReadOnlyList<Clause> Clauses { get; init; } = [];

    public bool IsUnparseable { get; init; }
    public string? UnparseableReason { get; init; }
}
```

For a successful result, each `SimpleCommandSyntax.Clause`, matching
`CommandOccurrence.Clause`, and matching entry in `Clauses` is reference-equal.
For an unparseable result, `Commands` and `Clauses` are empty even when `Syntax`
contains partial diagnostics.

The new records participate in generated record equality, hashing, and
`ToString()`, and the new `ParsedCommand` members change those generated results.
The library does not define a stable JSON wire format and does not add serializer
attributes or a serialization dependency for the polymorphic syntax family.
Consumers that persist parser results must own a versioned DTO or configure
their serializer explicitly; ordinary in-memory consumers use the typed API.

The intended security-consumer shape is therefore:

```csharp
var parsed = parser.Parse(source);
if (parsed.IsUnparseable || parsed.Commands.Count == 0)
{
    return Prompt(parsed.UnparseableReason ?? "no complete command occurrences");
}

foreach (var occurrence in parsed.Commands)
{
    if (!occurrence.IsComplete
        || occurrence.ImmediateRole == CommandOccurrenceRole.Unknown
        || occurrence.Clause.Verb.IsDynamic)
    {
        return Prompt("command execution is not statically bounded");
    }

    var interpreted = executableGrammar.InterpretAuthoredShellShape(occurrence);
    if (!interpreted.IsComplete)
    {
        return Prompt("executable arguments are ambiguous");
    }

    EvaluateEveryCandidate(interpreted);
    EvaluateEveryRedirect(occurrence.Redirects);
}
```

## Appendix B: Locked Grammar Boundaries and Non-Normative Parser Mocks

The BNF and support matrices in this appendix lock the v0.3 boundary. The C#
parser sketches show one implementation route and remain non-normative. Task
group 1 synchronizes the BNF into `SPEC.md` and `SPEC.POWERSHELL.md`.

### Shared structural vocabulary, not a shared grammar

The two front ends can target a similar internal structural vocabulary:

```text
block            := ordered structural statements
statement        := simple_command
                  | pipeline
                  | command_list
                  | group
                  | foreach_loop
                  | condition_loop
                  | conditional
```

This vocabulary describes output relationships only. Each shell defines its
own token boundaries, contextual keywords, expression forms, terminators,
scope, and recovery rules.

### Locked Bash grammar delta

The initial Bash slice extends the current `command := clause
(compound_op clause)*` grammar into recursive command lists. Only contextual
keywords in command position participate; `echo for` continues to parse `for`
as an argument.

```text
bash_script(stop)    := bash_list_item (list_sep bash_list_item)*
bash_list_item       := bash_and_or
bash_and_or          := bash_pipeline (("&&" | "||") bash_pipeline)*
bash_pipeline        := bash_command ("|" bash_command)*
bash_command         := bash_for_in
                      | bash_condition_loop
                      | bash_if
                      | bash_group
                      | bash_subshell
                      | bash_c_wrapper
                      | bash_simple_command

// Explicit `in` form only.
bash_for_in          := "for" binding_name "in" iterable_word*
                        list_terminator "do"
                        bash_script(stop = "done")
                        "done"

bash_condition_loop  := ("while" | "until")
                        bash_script(stop = "do") "do"
                        bash_script(stop = "done") "done"

bash_if              := "if" bash_script(stop = "then") "then"
                        bash_script(stop = "elif" | "else" | "fi")
                        bash_elif* bash_else? "fi"
bash_elif            := "elif" bash_script(stop = "then") "then"
                        bash_script(stop = "elif" | "else" | "fi")
bash_else            := "else" bash_script(stop = "fi")

list_sep             := ";" | NEWLINE
list_terminator      := ";" | NEWLINE+
binding_name         := shell_identifier
iterable_word        := word | quoted_string | supported_substitution
```

Stop keywords are contextual and match only at command position after a list
separator. C-style `for ((...))`, implicit `for name` iteration over positional
parameters, `case`, arithmetic execution, and substitutions whose inner
commands cannot be discovered remain rejected until separately specified.

The current Bash lexer already emits `for`, `in`, `do`, and `done` as `Word`
tokens. The first slice therefore does not require dedicated keyword token
kinds. The structural parser interprets them contextually and preserves the
existing lexer values and spans.

| Bash construct | Stable v0.3 status |
|---|---|
| Existing simple commands, `&&`, `||`, `;`, pipelines, groups, subshells, and static command-string wrappers | Supported and structurally projected |
| `for name in words; do ...; done` | Supported |
| `while` / `until` command lists | Supported |
| `if` / `elif` / `else` command lists | Supported |
| Completely delimited `$()` substitution in a supported word, redirect value, iterable, or expanding heredoc body | Inner commands visible; produced value `Unknown` |
| Legacy backtick command substitution | Whole result unparseable until its distinct escape and nesting rules are modeled |
| Static path-shaped glob in a supported iterable | `Pattern` only under the locked covering-directory rule |
| Existing `<<` / `<<-` heredocs | Supported; preserve delimiter, body, expansion mode, and completeness without treating body data as commands |
| Bash `<<<` here strings | Supported with explicit here-string redirect facts |
| Process substitution and single-`&` background lists | Independently gated; whole result unparseable until supported |
| `case`, C-style or implicit loops, functions, arithmetic execution | Deferred; whole result unparseable when execution may be hidden |

### Candidate Bash recursive-descent flow

```csharp
internal sealed class BashStructuralParser
{
    private readonly BashTokenCursor _tokens;
    private readonly string _source;

    internal BashNode ParseRoot() => ParseList(BashStopSet.EndOfInput);

    private BashBlockNode ParseList(BashStopSet stop)
    {
        var statements = new List<BashNode>();
        while (!_tokens.AtEnd && !stop.Matches(_tokens))
        {
            statements.Add(ParseAndOr());
            ConsumeListSeparatorOrStop(stop);
        }

        return new BashBlockNode(statements, SpanFrom(statements));
    }

    private BashNode ParseCommand()
    {
        if (_tokens.AtCommandPositionWord("for"))
        {
            return ParseForIn();
        }

        if (_tokens.AtOperator("("))
        {
            return ParseSubshell();
        }

        return ParseExistingSimpleCommand();
    }

    private BashForInNode ParseForIn()
    {
        var start = _tokens.ExpectWord("for");
        var binding = _tokens.ExpectIdentifier();
        _tokens.ExpectWord("in");
        var iterable = ParseWordsUntilListTerminator();
        ConsumeListTerminator();
        _tokens.ExpectWord("do");
        var body = ParseList(BashStopSet.Word("done"));
        var end = _tokens.ExpectWord("done");
        return new BashForInNode(binding, iterable, body, Span(start, end));
    }
}
```

`ParseExistingSimpleCommand` is an adapter around today's segment and
`ParseClauseSegment` machinery. The structural parser replaces the current
whole-stream top-level split; it does not replace verb extraction, native
argument classification, redirects, resolver behavior, provenance, or wrapper
recursion inside a simple-command leaf.

After the existing leaf is built, the Bash front end recursively parses each
completely delimited `$()` retained in an executable value position and attaches
the resulting `CommandSubstitutionSyntax` to the containing simple command (or
the foreach iterator region). Single-quoted or escaped `$()` text remains
literal. An unsupported inner region, a backtick substitution, or a failed
boundary makes the whole result unparseable.

All `Expect*` failures return one outer unparseable result. They do not skip to
`done` and return a partial tree that could be mistaken for authorization
evidence.

### Locked PowerShell grammar delta

PowerShell retains its statement-versus-pipeline distinction and contextual
keyword rules. In particular, `foreach` is a language keyword only at a
statement position when followed by `(`; `Get-ChildItem | foreach { ... }`
and `Write-Output x | foreach ($_)` continue to treat `foreach` as command or
alias syntax when the parenthesized argument is proved non-executing. `&&` and
`||` join pipelines rather than arbitrary control-flow
statements, so only `;` and newline terminate a statement around `foreach`.

```text
pwsh_script(stop)    := pwsh_statement (statement_terminator pwsh_statement)*
pwsh_statement       := pwsh_foreach
                      | pwsh_while
                      | pwsh_if
                      | pwsh_and_or

statement_terminator := ";" | NEWLINE
pwsh_and_or           := pwsh_pipeline
                        (("&&" | "||") pwsh_pipeline)*
pwsh_pipeline         := pipeline_element ("|" pipeline_element)*

pwsh_foreach         := "foreach" "(" variable "in" foreach_expression ")"
                        script_block_body

pwsh_while           := "while" "(" condition_pipeline ")"
                        script_block_body

pwsh_if              := "if" "(" condition_pipeline ")" script_block_body
                        pwsh_elseif* pwsh_else?
pwsh_elseif          := "elseif" "(" condition_pipeline ")" script_block_body
pwsh_else            := "else" script_block_body

foreach_expression   := literal_value
                      | literal_array
                      | pipeline_expression
                      | supported_subexpression
supported_subexpression := "$(" pwsh_script(stop = ")") ")"
literal_array        := "@(" literal_value ("," literal_value)* ")"
script_block_body    := "{" pwsh_script(stop = "}") "}"
```

`pipeline_expression` is structurally parsed so its commands become iterator
occurrences, but its produced object values are `Unknown`. The initial exact or
finite domain is limited to literal scalar and array elements whose PowerShell
conversion and argument boundaries are completely specified.

The current PowerShell lexer emits a balanced `{ ... }` as one `ScriptBlock`
token. The first slice can preserve that behavior for ordinary command
arguments while recursively tokenizing the interior only after the structural
parser has proved that the token is the body of a recognized statement. The
recursive call carries the body's absolute source offset so direct-source
child spans still index `ParsedCommand.Source`.

### Candidate PowerShell recursive-descent flow

```csharp
internal sealed class PwshStructuralParser
{
    private readonly PwshTokenCursor _tokens;

    private PwshNode ParseStatement()
    {
        if (_tokens.AtStatementKeywordFollowedBy("foreach", "("))
        {
            return ParseForeach();
        }

        return ParseExistingPipeline();
    }

    private PwshForEachNode ParseForeach()
    {
        var start = _tokens.ExpectWord("foreach");
        _tokens.ExpectOperator("(");
        var binding = _tokens.ExpectVariable();
        _tokens.ExpectWord("in");
        var iterable = ParseForeachExpressionUntilMatchingParen();
        _tokens.ExpectOperator(")");

        var bodyToken = _tokens.Expect(PwshTokenKind.ScriptBlock);
        var body = ParseScriptBlockInterior(
            bodyToken,
            absoluteOffset: bodyToken.SourceStart + 1);

        return new PwshForEachNode(
            binding, iterable, body, Span(start, bodyToken));
    }
}
```

`ParseExistingPipeline` adapts the current `SplitIntoSegments` and
`BuildSegment` logic. A `ScriptBlock` token remains an opaque `DynamicSkip`
argument everywhere the enclosing grammar does not explicitly own that block
as a statement body. This avoids accidentally executing or authorizing the
contents of `ForEach-Object { ... }`, arbitrary script-block arguments, or a
dynamic call operator.

`condition_pipeline` is limited to a pipeline that the existing command parser
can delimit completely. Pure literal and comparison expressions may be
preserved as non-executable condition syntax, but any subexpression, member
invocation, script block, or other form that can execute while escaping
complete command discovery makes the whole result unparseable.

| PowerShell construct | Stable v0.3 status |
|---|---|
| Existing simple commands, pipelines, statement separators, grouping, and static wrapper / `Invoke-Expression` recursion | Supported and structurally projected |
| Completely delimited `$()` in a supported word, call-operator dynamic identity, redirect, foreach expression, double-quoted string, or expandable here-string | Inner commands visible; produced value `Unknown`; current-scope state propagates |
| Standalone `$()` expression statement | Inner commands visible; no outer invocation is invented |
| `& $(...)` dynamic invocation | Inner commands visible, followed by one incomplete dynamic outer occurrence |
| `& { ... }` script-block invocation | Whole result unparseable until body, scope, and state propagation are modeled |
| Single-quoted, literal-here-string, or backtick-escaped `$()` text | Literal/opaque data; no invented substitution occurrence |
| Execution-bearing `@()` / `@{}` outside a completely modeled foreach literal expression | Whole result unparseable until complete command discovery is modeled |
| `foreach ($name in expression) { ... }` for literal scalar, literal array, or fully delimited pipeline iterables | Supported |
| `while (condition_pipeline) { ... }` | Supported |
| `if` / `elseif` / `else` with fully delimited condition pipelines | Supported |
| Pipeline-produced iterator objects | Iterator commands visible; produced values `Unknown` |
| `ForEach-Object` / `foreach` alias script blocks and ordinary script-block arguments | Existing opaque argument; no invented child execution |
| `do`, `switch`, functions, definitions, class/type bodies, or execution-bearing expressions outside the locked subset | Deferred; whole result unparseable when execution may be hidden |

PowerShell `$()` is a value-producing subexpression, not an invocation.
Standalone `$()` lowers directly to `CommandSubstitutionSyntax` and exposes
only its inner commands. Only a call-operator form such as `& $(...)` also
lowers an outer dynamic simple command, after the substitution. A standalone
subexpression followed by command-style arguments is unparseable. Bash command
word formation deliberately differs; a substitution in Bash command-name
position makes the whole result unparseable after diagnostic discovery rather
than changing the PowerShell-specific `VerbChain.IsDynamic` contract.
Call-operator script-block invocation remains unparseable; an ordinary
script-block argument remains non-executing opaque data.

### Candidate internal nodes and lowering pipeline

The internal tree may retain shell-specific syntax even if the reviewed public
tree shares a smaller set of structural records:

```csharp
internal abstract record BashNode(SourceRange Range);
internal sealed record BashBlockNode(
    IReadOnlyList<BashNode> Statements,
    SourceRange Range) : BashNode(Range);
internal sealed record BashSimpleCommandNode(
    Clause Clause,
    SourceRange Range) : BashNode(Range);
internal sealed record BashForInNode(
    BashBinding Binding,
    IReadOnlyList<BashWordExpression> Iterable,
    BashBlockNode Body,
    SourceRange Range) : BashNode(Range);

internal readonly record struct BashFlowResult(
    BashAbstractState? OnSuccess,
    BashAbstractState? OnFailure);

internal enum BashIterationCardinality
{
    Never,
    OneOrMore,
    ZeroOrMore,
}

internal sealed record BashIterationPlan(
    IReadOnlyList<ShellValueDomain> OrderedWordCandidates,
    BashIterationCardinality Cardinality,
    bool RequiresFixedPoint);

internal abstract record PwshNode(SourceRange Range);
internal sealed record PwshForEachNode(
    PwshBinding Binding,
    PwshExpression Iterable,
    PwshBlockNode Body,
    SourceRange Range) : PwshNode(Range);
```

Analysis metadata remains attached to the internal authored nodes until the
syntax, occurrence, and compatibility projections are produced together. An
implementation may instead use ordered side tables aligned by occurrence
index, but SHALL NOT key record-valued public nodes with default value equality:
two textually identical authored commands are still distinct occurrences.

The proposed data flow is:

```text
shell-specific lexer
        ↓
shell-specific recursive structural parser
        ↓
shell-specific internal syntax tree
        ↓
public syntax lowering
        ↓
complete authored command-occurrence projection
        ↓
bounded shell-value and state analysis
        ↓
occurrences enriched with effective facts
        ↓
conservative v0.2 Clauses compatibility projection
```

The occurrence projector and compatibility flattener consume structure; they
do not re-tokenize `ParsedCommand.Source`. The bounded analyzer consumes
shell-specific expression adapters and a shared value-domain/state-join
lattice. Executable-aware interpretation still occurs only in the consumer.

### Parser failure matrix

| Input condition | Structural result | Authorization-facing result |
|---|---|---|
| Missing Bash `do` or `done` | Parse failure with the offending range | `IsUnparseable=true`; `Commands` and `Clauses` empty |
| Bash keyword used as an argument | Existing simple-command leaf | No false control-flow node |
| Unsupported Bash substitution in any executable value position | Inner commands surfaced only if completely parsed | Otherwise the entire result is unparseable |
| Supported PowerShell `$()` value | Inner commands surfaced before its consumer; current-scope state applied | Produced value remains unknown |
| PowerShell `foreach` at statement position followed by `(` | `PwshForEachNode` | Iterator and body occurrences exposed |
| PowerShell `foreach` in a pipeline command slot | Existing pipeline/simple-command path | Alias or command semantics preserved |
| PowerShell statement body `ScriptBlock` | Interior recursively parsed with adjusted spans | Every body command exposed |
| PowerShell script block used as an ordinary argument | Existing opaque argument | `DynamicSkip`; contents are not invented as executed commands |
| Candidate cap or state-join overflow | Structure remains parseable | Affected effective fact becomes `Unknown` |
| Any executable region is skipped or cannot be delimited | Partial diagnostic tree allowed | `IsUnparseable=true`; `Commands` and `Clauses` empty |
