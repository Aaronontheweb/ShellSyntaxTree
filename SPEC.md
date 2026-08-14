# ShellSyntaxTree — bash & shared-contract Specification

**Status:** v0.2.0 shipped; the accepted v0.3 contract adds structured syntax,
complete command occurrences, explicit redirect analysis, bounded `for` /
`foreach`, substitutions, and execution regions while retaining the v0.2
compatibility leaves.
**Audience:** Whoever (human or agent) works on ShellSyntaxTree.
**Read this end-to-end before writing any code.**
**PowerShell support is specified separately in `SPEC.POWERSHELL.md` (v0.2.0);
this document is the canonical home of the public API, AST, sanitization
workflow, and consumer contract that PowerShell reuses.**

This document specifies the shared public API, AST, Bash grammar, verb tables,
resolver semantics, and corpus contract through ShellSyntaxTree v0.3. The library is a
focused bash command parser designed for **security gate evaluators** —
tools that inspect agent-emitted shell commands to decide whether to allow,
prompt for, or deny execution.

It is **not** a general-purpose shell interpreter. It does not execute,
expand, or evaluate commands. It returns a structured AST that consumers
walk to make decisions.

The original consumer is [Netclaw](https://github.com/Aaronontheweb/netclaw)'s
approval policy. The library is designed to be reusable beyond Netclaw —
any tool that needs to reason about the shape of an agent-emitted bash
command can consume it.

---

## 1. Goals & Non-Goals

### Goals (v0.1)

1. **Parse bash commands into a structured AST** with per-clause verbs,
   args, redirects, and compound operators.
2. **Extract paths a command operates on** with per-verb knowledge of which
   positional args are paths vs flags vs literal values (`chmod 755 file`
   knows `755` is a mode).
3. **Honor `cd <dir> && cmd` propagation** within a compound — `<dir>`
   counts as a path each subsequent command operates on.
4. **Recurse into `bash -c "<inner>"`** so the inner command is parsed and
   its clauses surface to the consumer.
5. **Mark dynamic-content tokens** (unresolved `$VAR`, unexpanded globs)
   so consumers don't misextract literal `$VAR/foo` as a path.
6. **Multi-shell-ready** via `IShellParser` interface — bash is the only
   v0.1 implementation; PowerShell and cmd are deferred to later versions
   without breaking the seam.

### Non-Goals (v0.1)

- PowerShell parsing (deferred; interface seam is present).
- Windows cmd parsing (deferred).
- Command execution. The library never runs anything.
- Variable expansion. We mark dynamic tokens, never resolve them.
- Function definitions, here-docs body extraction, complex parameter
  expansion (`${var//pattern/replacement}`), arithmetic expansion.
- Command-substitution evaluation. The library never executes a substitution
  or claims its produced value is known. Stable v0.3 recursively discovers
  commands inside supported Bash `$()` positions while retaining the authored
  `Kind=DynamicSkip, IsPath=false` compatibility value. Legacy backticks and
  incomplete executable interiors fail closed.
- Performance tuning beyond "fast enough to invoke per shell call without
  noticeable latency" (~1ms per typical input).

### Parser selection and language boundary (v0.3)

The execution environment selects exactly one top-level parser. Consumers use
`BashParser` only when Bash will execute the submitted source and `PwshParser`
only when PowerShell will execute it. Neither parser guesses a language from
command text or delegates an argument payload to the other parser.

Therefore Bash input such as `pwsh -Command 'Get-Content x'` remains one
ordinary external `pwsh` command with a Bash argument; it does not surface a
PowerShell child command. PowerShell input such as `bash -c 'rm x'` likewise
remains one ordinary external `bash` command. Same-language wrapper recursion
remains parser-local: Bash owns supported `bash` / `sh -c` recursion, while
PowerShell owns its PowerShell-host and static `Invoke-Expression` recursion.
The library never auto-detects the host shell or probes the machine.

---

## 2. Public API Surface

The package exposes a small surface from a single namespace
`ShellSyntaxTree`. **Public types only**:

```csharp
namespace ShellSyntaxTree;

/// <summary>
/// Parses shell command strings into structured ASTs.
/// </summary>
public interface IShellParser
{
    /// <summary>
    /// Parse the command. Always returns a ParsedCommand; sets
    /// <see cref="ParsedCommand.IsUnparseable"/> when the input cannot
    /// be tokenized (unbalanced quotes, etc.). Never throws on
    /// well-formed strings; throws ArgumentNullException on null input.
    /// </summary>
    ParsedCommand Parse(string command);
}

/// <summary>Bash implementation of IShellParser.</summary>
public sealed class BashParser : IShellParser
{
    public BashParser();
    public BashParser(BashParserOptions options);
    public ParsedCommand Parse(string command);
}

/// <summary>PowerShell implementation of IShellParser (v0.2.0). The
/// PowerShell grammar, tables, and resolver are specified in
/// SPEC.POWERSHELL.md.</summary>
public sealed class PwshParser : IShellParser
{
    public PwshParser();
    public PwshParser(PwshParserOptions options);
    public ParsedCommand Parse(string command);
}

/// <summary>Shell-neutral resolver configuration shared by every parser
/// (added v0.2.0). HomeDirectory / WorkingDirectory live here.</summary>
public abstract record ShellParserOptions { ... }

/// <summary>Declares which ambient Bash variable facts the caller can prove.</summary>
public enum BashInitialStateMode
{
    Unknown,
    IsolatedNonInteractive,
}

/// <summary>Configuration knobs for BashParser. As of v0.2.0 a sealed
/// record deriving from ShellParserOptions; the v0.1 object-initializer
/// shape is unchanged.</summary>
public sealed record BashParserOptions : ShellParserOptions
{
    public BashInitialStateMode InitialStateMode { get; init; }
    public bool PublishAuthoredSourceFacts { get; init; }
}

/// <summary>Compatibility option for PowerShell initial host-state analysis.</summary>
public enum PwshInitialStateMode
{
    Unknown,
    IsolatedNonInteractiveNoProfile,
}

/// <summary>Selects the PowerShell grammar and versioned metadata.</summary>
public enum PwshDialect
{
    Unknown,
    // PowerShell 7.6 servicing releases from 7.6.4; versioned tables are pinned.
    PowerShell7,
    WindowsPowerShell51,
}

/// <summary>Configuration knobs for PwshParser.</summary>
public sealed record PwshParserOptions : ShellParserOptions
{
    public PwshInitialStateMode InitialStateMode { get; init; }
    public PwshDialect Dialect { get; init; } = PwshDialect.PowerShell7;
}

`PwshDialect` and `PwshParserOptions.Dialect` are source- and binary-additive.
The property initializer preserves the released PowerShell 7 parser semantics
for existing constructors and object initializers. Like every additive public
record property, it deliberately changes generated equality, hashing,
`ToString()`, reflection, and default serializer shape; parser results and
options are not a stable implicit wire format.

// The pre-v0.2.0 BashParserOptions body, now hoisted onto ShellParserOptions:
public abstract record ShellParserOptions
{
    /// <summary>
    /// User home directory used to expand `~` and `$HOME` tokens during
    /// resolution. Defaults to <see cref="Environment.SpecialFolder.UserProfile"/>.
    /// </summary>
    public string? HomeDirectory { get; init; }

    /// <summary>
    /// Working directory used to resolve relative path tokens during
    /// resolution. Defaults to the daemon-process cwd.
    /// </summary>
    public string? WorkingDirectory { get; init; }
}

// v0.2 compatibility leaves — see §3.
public sealed record ParsedCommand { ... }
public sealed record Clause { ... }
public sealed record ClauseElement { ... }
public sealed record VerbChain { ... }
public sealed record Arg { ... }
public sealed record Redirect { ... }
public enum ClauseElementRole { Verb, Argument, Redirect }
public enum ArgKind { Literal, EnvVar, Glob, Tilde, DynamicSkip }
public enum RedirectDirection { In, Out, Append, ErrOut, ErrAppend }
public enum CompoundOperator { None, AndIf, OrIf, Sequence, Pipe }

// v0.3 authored structure — see §3.
public abstract record ShellSyntaxNode { ... }
public sealed record ShellBlockSyntax : ShellSyntaxNode { ... }
public sealed record SimpleCommandSyntax : ShellSyntaxNode { ... }
public sealed record PipelineSyntax : ShellSyntaxNode { ... }
public sealed record CommandListSyntax : ShellSyntaxNode { ... }
public sealed record CommandListItemSyntax { ... }
public sealed record GroupSyntax : ShellSyntaxNode { ... }
public sealed record ForEachSyntax : ShellSyntaxNode { ... }
public sealed record ShellSourceFragment { ... }
public sealed record CommandSubstitutionSyntax : ShellSyntaxNode { ... }
public sealed record ExecutionRegionSyntax : ShellSyntaxNode { ... }
public enum ShellGroupKind { ... }
public enum ExecutionRegionOrigin { ... }
public enum ExecutionRegionPhase { ... }
public enum ExecutionRegionTiming { ... }
public enum ExecutionRegionCardinality { ... }

// v0.3 authorization and bounded-analysis projections — see §3.
public sealed record CommandOccurrence { ... }
public sealed record CommandAncestryFrame { ... }
public sealed record AnalyzedArgument { ... }
public abstract record ShellValueDomain { ... }
public enum ShellPathShape { ... }
public abstract record RedirectAnalysis { ... }
public sealed record HereDocumentAnalysis { ... }
public abstract record RedirectSource { ... }
public sealed record FileRedirectAnalysis : RedirectAnalysis { ... }
public sealed record UnresolvedRedirectAnalysis : RedirectAnalysis { ... }
public sealed record DescriptorDuplicateRedirectAnalysis : RedirectAnalysis { ... }
public sealed record DescriptorMoveRedirectAnalysis : RedirectAnalysis { ... }
public sealed record DescriptorCloseRedirectAnalysis : RedirectAnalysis { ... }
public sealed record HereDocumentRedirectAnalysis : RedirectAnalysis { ... }
public sealed record HereStringRedirectAnalysis : RedirectAnalysis { ... }
public enum CommandOccurrenceRole { ... }
public enum CommandAncestryRegion { ... }
public enum FileRedirectMode { ... }
public enum HereDocumentExpansionMode { ... }
```

Stable v0.3 exposes only structural types the parsers can emit. Condition-loop
and branch grammar remains fail closed and reserves no public type or enum
member. Every new v0.3 result type is parser-owned; its constructor and result
setters are not public. The stable v0.2 constructors and setters are unchanged.

That's the entire public API. **Everything else is internal.** The lexer,
parser internals, verb tables, resolver — all implementation detail.

---

## 3. AST Reference

### `ParsedCommand`

The top-level result of parsing. Always returned (never null).

```csharp
public sealed record ParsedCommand
{
    /// <summary>The original input string, verbatim.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Canonical authored nested structure. Direct-source nodes have exact
    /// source ranges; decoded wrapper nodes report unavailable ranges unless
    /// an exact outer mapping exists.
    /// </summary>
    public ShellBlockSyntax Syntax { get; internal init; } = new();

    /// <summary>
    /// Canonical authorization projection containing every authored simple
    /// command that may execute exactly once, in deterministic source order.
    /// </summary>
    public IReadOnlyList<CommandOccurrence> Commands { get; internal init; } = [];

    /// <summary>
    /// Conservative v0.2 compatibility projection. Existing simple-command
    /// behavior remains available, but v0.3 security consumers use Commands.
    /// </summary>
    public IReadOnlyList<Clause> Clauses { get; init; } = [];

    /// <summary>
    /// True when the parser could not account for every executable region.
    /// When true, Commands and Clauses are empty; Syntax may contain partial
    /// diagnostic evidence only. Consumers must prompt or deny.
    /// </summary>
    public bool IsUnparseable { get; init; }

    /// <summary>
    /// Human-readable diagnostic when IsUnparseable=true; null otherwise.
    /// </summary>
    public string? UnparseableReason { get; init; }
}
```

`BashInitialStateMode.Unknown` is the default. In this mode the parser does
not publish bounded loop-variable facts: an ambient shell may already have
made the binding readonly, integer-valued, a nameref, exported, or otherwise
semantically significant. A Bash loop whose safety depends on such a binding
is therefore unparseable rather than being analyzed as an ordinary scalar.

`BashInitialStateMode.IsolatedNonInteractive` is an explicit caller assertion,
not a parser discovery. It means the complete source is executed by a newly
spawned non-interactive Bash process, no profile or `BASH_ENV` / `ENV` startup
content is loaded, and no inherited environment entry carries the loop-bound
name. A consumer may select this mode only when its execution path enforces
those conditions. Supplying this option while executing in a reused,
interactive, startup-scripted, or uncontrolled environment invalidates the
analysis.

Recognized variable-state mutation in the analyzed source invalidates isolated
mode for every later region that can observe it. In particular, a decoded
`bash -c` child after `export` is analyzed with unknown initial variable state;
the option is not blindly copied into the child. Cwd-only state changes retain
the caller's initial-variable assertion.

The same attribute-state proof governs every simple named-parameter
dereference. With `BashInitialStateMode.Unknown`, `$name` and `${name}` are
unparseable because an ambient nameref can evaluate an arithmetic array
subscript and execute authored command text. In isolated mode, a fresh-process
variable before reachable source mutation may remain an unknown value while
still being proved free of recursive variable attributes. A modeled ordinary
loop binding retains its explicit proof. Positional and special parameters
that cannot carry variable attributes keep their existing typed cardinality
rules.

Even in isolated mode, the v0.3 bounded loop grammar accepts only ordinary
lowercase scalar binding names matching `[a-z][a-z0-9_]*`, excluding
`auto_resume` and `histchars`. `_`, uppercase names, and every name outside
that boundary fail the complete loop region closed. This deliberately excludes
Bash magic variables and resolver- or executable-identity-sensitive names such
as `RANDOM`, `LINENO`, `HOME`, `PATH`, `CDPATH`, and `IFS`. The boundary is
extend-only: a later version may add a proved variable-state model or
additional explicitly reviewed ordinary names.

`PwshInitialStateMode.Unknown` is the default. PowerShell authorization uses
the same boundary as Bash authorization: it proves the static authored command
and every authored executable region, not the runtime implementation selected
through aliases, functions, modules, profiles, executable lookup, inherited
variables, or other ambient host state. Ambient uncertainty alone therefore
does not make a static command occurrence incomplete and does not poison later
authored command occurrences.

`PwshInitialStateMode.Unknown` does not publish an exact or finite
loop-dependent effective value. An ambient typed, validated, read-only, or
constant binding may coerce or reject an assignment, so the authored iterable
text is not a proved runtime argument. The surrounding static command
occurrence may still be complete; value precision is a separate fact.

`PwshInitialStateMode.IsolatedNonInteractiveNoProfile` is a caller assertion
that the complete source runs in a newly spawned noninteractive PowerShell
process with profiles disabled and without a reused or caller-initialized
runspace. It permits exact or finite loop-dependent values for ordinary
unscoped names. It does not assert a pinned module, alias, function, `PATH`, or
executable-resolution baseline, because those runtime externalities are outside
authored-command completeness.

The bounded grammar remains limited to ordinary unscoped variable names that
do not collide, case-insensitively, with automatic, constant, read-only,
preference, or configuration bindings known to the supported PowerShell
runtime. Scoped/provider forms such as `$global:x`, `$script:x`, and `$env:X`
remain outside the bounded loop-binding grammar.

Current-runspace regions such as `( ... )`, `$()`, and a static
`Invoke-Expression` payload share supported authored binding and location
state. A decoded `pwsh -Command` or `pwsh -EncodedCommand` child does not
inherit exact `$HOME`, environment, provider, or cwd facts unless those facts
are independently proved, but it retains complete static authored command
occurrences. Explicit native, `.ps1`, cmdlet, alias, and module-qualified
spellings use their authored parser classification even though runtime state
may shadow them.

Recognized source-level mutation of variables, aliases, functions, or modules
invalidates later affected proofs wherever PowerShell scope rules make the
mutation observable. A computed `Invoke-Expression` payload can hide commands
and mutate current-runspace facts; it therefore invalidates later binding and
cwd proofs and remains incomplete. Cwd-only state changes retain independent
authored-binding facts.

Variable mutation recognition includes argument-vector binding, not only the
invoked verb. The PowerShell common parameters `-OutVariable` / `-ov`,
`-PipelineVariable` / `-pv`, `-ErrorVariable` / `-ev`, `-WarningVariable` /
`-wv`, and `-InformationVariable` / `-iv`, including accepted unambiguous
prefixes and inline `:` values, invalidate later observing proofs. The same
rule covers PowerShell 7 variable-writing parameters on `Tee-Object`,
`Import-LocalizedData`, `Invoke-RestMethod`, and `Invoke-WebRequest`. An opaque
splat can supply any of those parameter keys and therefore also invalidates
later proofs. A recognized writer on `Set-Location` composes with its
success/failure cwd transfer and invalidates bindings on both reachable
outcomes; location analysis does not bypass argument-vector mutation. This
check is conservative for a command whose runtime command
type is unavailable; treating a possible native argument as a writer can
cause a prompt, but ignoring an advanced-function writer can authorize a stale
value.

PowerShell also accepts U+2013 EN DASH, U+2014 EM DASH, and U+2015 HORIZONTAL
BAR as parameter prefixes. Stable v0.3 fails a token beginning with one of
those alternate dashes atomically rather than exposing it as a literal
positional argument: the retained v0.2 `Arg.IsFlag` contract recognizes only
an ASCII `-`, so normalizing the authored `Raw` spelling would either lose
provenance or require a breaking API change. Unsupported module-qualified
cmdlets are likewise rejected inside structural regions, not only in a flat
command. The existing module-qualified `Invoke-Expression` exception and the
version-pinned PowerShell execution-region receiver catalog are the only
specified exceptions.

For a successful result, every authored simple command appears once in
`Syntax`, once in `Commands`, and once in `Clauses`, with all three projections
referencing the identical in-memory `Clause` instance. Serialization is not
required to preserve that reference identity. For an unparseable result,
`Commands` and `Clauses` are empty even when `Syntax` retains partial evidence
for diagnostics.

### Authored structural nodes (v0.3)

The syntax family is a closed, library-owned record hierarchy. The
`private protected` ordinary constructor is paired with an assembly-only
abstract ownership member because C# records also synthesize a protected copy
constructor; together they prevent an external concrete node implementation.
Later library versions may add node kinds, so
authorization code that inspects `Syntax` must fail closed on an unknown type
or enum value. Security consumers normally enumerate `Commands`; `Syntax` is
for structure, explanation, display, and specialized analysis.

```csharp
public abstract record ShellSyntaxNode
{
    private protected ShellSyntaxNode() { }
    private protected abstract object LibraryOwnership { get; }

    public int? SourceStart { get; internal init; }
    public int? SourceLength { get; internal init; }
}

public sealed record ShellBlockSyntax : ShellSyntaxNode
{
    internal ShellBlockSyntax() { }
    private protected override object LibraryOwnership => this;
    public IReadOnlyList<ShellSyntaxNode> Statements { get; internal init; } = [];
}

public sealed record SimpleCommandSyntax : ShellSyntaxNode
{
    internal SimpleCommandSyntax() { }
    private protected override object LibraryOwnership => this;
    public Clause Clause { get; internal init; } = new();
    public IReadOnlyList<CommandSubstitutionSyntax> Substitutions { get; internal init; } = [];
    public IReadOnlyList<ExecutionRegionSyntax> ExecutionRegions { get; internal init; } = [];
}

public sealed record PipelineSyntax : ShellSyntaxNode
{
    internal PipelineSyntax() { }
    private protected override object LibraryOwnership => this;
    public IReadOnlyList<ShellSyntaxNode> Stages { get; internal init; } = [];
}

public sealed record CommandListSyntax : ShellSyntaxNode
{
    internal CommandListSyntax() { }
    private protected override object LibraryOwnership => this;
    public IReadOnlyList<CommandListItemSyntax> Items { get; internal init; } = [];
}

public sealed record CommandListItemSyntax
{
    internal CommandListItemSyntax() { }
    public CompoundOperator Operator { get; internal init; }
    public ShellSyntaxNode Command { get; internal init; } = null!;
}

public sealed record GroupSyntax : ShellSyntaxNode
{
    internal GroupSyntax() { }
    private protected override object LibraryOwnership => this;
    public ShellGroupKind GroupKind { get; internal init; }
    public ShellBlockSyntax Body { get; internal init; } = null!;
}

public enum ShellGroupKind
{
    Unknown,
    CurrentScope,
    IsolatedScope,
}

public sealed record ForEachSyntax : ShellSyntaxNode
{
    internal ForEachSyntax() { }
    private protected override object LibraryOwnership => this;
    public string BindingName { get; internal init; } = "";
    public ShellSourceFragment BindingSource { get; internal init; } = null!;
    public ShellSourceFragment Iterable { get; internal init; } = null!;
    public ShellBlockSyntax IteratorCommands { get; internal init; } = null!;
    public ShellBlockSyntax Body { get; internal init; } = null!;
}

public sealed record ShellSourceFragment
{
    internal ShellSourceFragment() { }
    public string Raw { get; internal init; } = "";
    public int? SourceStart { get; internal init; }
    public int? SourceLength { get; internal init; }
}

public sealed record CommandSubstitutionSyntax : ShellSyntaxNode
{
    internal CommandSubstitutionSyntax() { }
    private protected override object LibraryOwnership => this;
    public ShellBlockSyntax Body { get; internal init; } = null!;
}

public sealed record ExecutionRegionSyntax : ShellSyntaxNode
{
    internal ExecutionRegionSyntax() { }
    private protected override object LibraryOwnership => this;
    public ExecutionRegionOrigin Origin { get; internal init; }
    public ClauseElement? HostArgument { get; internal init; }
    public ExecutionRegionPhase Phase { get; internal init; }
    public ExecutionRegionTiming Timing { get; internal init; }
    public ExecutionRegionCardinality Cardinality { get; internal init; }
    public ShellBlockSyntax Body { get; internal init; } = null!;
}

public enum ExecutionRegionOrigin
{
    Unknown,
    DirectCall,
    DotSource,
    CommandArgument,
}

public enum ExecutionRegionPhase
{
    Unknown,
    Main,
    Initialization,
    Begin,
    Process,
    End,
    Filter,
    Action,
    Completion,
}

public enum ExecutionRegionTiming
{
    Unknown,
    Synchronous,
    Concurrent,
    Deferred,
}

public enum ExecutionRegionCardinality
{
    Unknown,
    Once,
    OncePerInputObject,
    ZeroOrMore,
}
```

`ForEachSyntax` shares proved execution structure only. `Iterable.Raw` keeps
the shell-specific authored expression; it does not claim that Bash words and
PowerShell expressions share a grammar. Direct-source nodes have exact ranges
into `ParsedCommand.Source`. Nodes lifted from decoded, escaped, or encoded
wrapper content use null ranges unless an exact outer mapping exists.

Every new v0.3 result-type constructor and new v0.3 member setter is
library-owned. Every `IReadOnlyList<T>` introduced by v0.3 is a defensive
snapshot, not a mutable array or list that a consumer can cast and change.
Stable v0.2 construction, setters, and list semantics remain unchanged.
Runtime type is the discriminant for each closed
record family; consumers keep a default fail-closed switch arm for future
library-owned alternatives. Compatibility is required against stable v0.2,
not against any experimental `0.3.0-alpha.*` surface.

Every v0.3 enum whose model admits an unknown state reserves zero as
`Unknown`. Consumers fail closed on `Unknown` or an unrecognized numeric value
when the fact affects policy. `FileRedirectMode` has no `Unknown` member:
unresolved operations use `UnresolvedRedirectAnalysis`, and only the library
can construct a `FileRedirectAnalysis` with a known mode.

### Command occurrences and bounded values (v0.3)

```csharp
public sealed record CommandOccurrence
{
    internal CommandOccurrence() { }
    public Clause Clause { get; internal init; } = new();
    public CommandOccurrenceRole ImmediateRole { get; internal init; }
    public IReadOnlyList<CommandAncestryFrame> Ancestry { get; internal init; } = [];
    public IReadOnlyList<AnalyzedArgument> Arguments { get; internal init; } = [];
    public ShellValueDomain WorkingDirectory { get; internal init; } = null!;
    public ShellWorkingDirectoryEffect WorkingDirectoryEffect
        { get; internal init; } = new ShellWorkingDirectoryEffect.Unknown();
    public IReadOnlyList<RedirectAnalysis> Redirects { get; internal init; } = [];
    public bool IsComplete { get; internal init; }
}

public abstract record ShellWorkingDirectoryEffect
{
    private protected ShellWorkingDirectoryEffect() { }
    private protected abstract object LibraryOwnership { get; }

    public sealed record Unknown : ShellWorkingDirectoryEffect { ... }
    public sealed record Unchanged : ShellWorkingDirectoryEffect { ... }
    public sealed record ChangesOnSuccess : ShellWorkingDirectoryEffect
    {
        public ShellValueDomain Target { get; }
    }
}

public enum CommandOccurrenceRole
{
    Unknown,
    Ordinary,
    PipelineStage,
    Iterator,
    LoopBody,
    Substitution,
    ExecutionRegion,
}

public sealed record CommandAncestryFrame
{
    internal CommandAncestryFrame() { }
    public ShellSyntaxNode Ancestor { get; internal init; } = null!;
    public CommandAncestryRegion Region { get; internal init; }
    public int? ChildIndex { get; internal init; }
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
    Substitution,
    ExecutionRegion,
}

public sealed record AnalyzedArgument
{
    internal AnalyzedArgument() { }
    public Arg Argument { get; internal init; } = null!;
    public ClauseElement Element { get; internal init; } = null!;
    public ShellValueDomain Value { get; internal init; } = null!;
    public ShellValueDomain AuthoredValue { get; internal init; } = null!;
    public ShellValueDomain AuthoredFileSystemValue { get; internal init; } =
        new ShellValueDomain.Unknown();
    public ShellValueDomain AuthoredNonFileSystemValue { get; internal init; } =
        new ShellValueDomain.Unknown();
    public ShellPathShape AuthoredPathShape { get; internal init; }
}

public enum ShellPathShape
{
    Unknown,
    Posix,
    Windows,
}

public abstract record ShellValueDomain
{
    private protected ShellValueDomain() { }
    private protected abstract object LibraryOwnership { get; }

    public sealed record Unknown : ShellValueDomain { ... }
    public sealed record Exact : ShellValueDomain { public string Value { get; } }
    public sealed record FiniteSet : ShellValueDomain
    {
        public IReadOnlyList<string> Values { get; }
    }
    public sealed record IntegerRange : ShellValueDomain
    {
        public long MinimumInclusive { get; }
        public long MaximumInclusive { get; }
    }
    public sealed record Concatenation : ShellValueDomain
    {
        public IReadOnlyList<ShellValueDomain> Parts { get; }
    }
    public sealed record PathPattern : ShellValueDomain
    {
        public string Pattern { get; }
        public string CoveringDirectory { get; }
    }
}
```

`IsComplete` proves that the parser discovered the complete authored
executable region, assigned its structural ancestry, and completed every
parser-owned authored-syntax check. It does not prove which runtime executable
an ambient alias, function, module, profile, `PATH`, or inherited environment
will select. Static authored command identities remain complete under that
external uncertainty. Computed identities, hidden executable text, and
unsupported regions remain incomplete or make the whole result unparseable.
After an explicit source-level mutation that the parser cannot model, affected
later identities or values remain incomplete.

`Commands` contains one entry per authored simple command that may execute,
not one per predicted runtime iteration. `Ancestry` is ordered outermost to
innermost, excludes the simple-command leaf, and retains every enclosing
execution relation. `ImmediateRole` describes the nearest relation.

`WorkingDirectory` is the occurrence's incoming shell-scope directory.
`WorkingDirectoryEffect` is a separate relational outcome fact:

- `Unchanged` proves that every modeled normal success and failure exit keeps
  the incoming directory;
- `ChangesOnSuccess(Target)` proves that failure keeps the incoming directory
  and success takes `Target`; and
- `Unknown` means neither relation is completely proved.

The relation is recorded during abstract-state transfer. Equal incoming and
outgoing domains do not create `Unchanged`, especially when both are unknown.
The effect is local to the execution scope expressed by `Ancestry`; a mutation
inside a Bash subshell or decoded child remains precise inside that scope but
does not claim to change the parent.

`ChangesOnSuccess.Target` is `Unknown`, `Exact`, or `FiniteSet`. Exact and
finite targets are normalized absolute local directories under the selected
shell path style. Unknown proves a success-only mutation without a bounded
destination. Any other domain, malformed set, over-limit join, or missing fact
makes the whole public effect `Unknown`. A target does not prove existence,
accessibility, authorization, or runtime success.

Effects join per authored occurrence. Two `Unchanged` visits remain
`Unchanged`; two success-only changes join their bounded targets. Unknown,
mixed unchanged/change visits, missing visits, or lost correlation join to
`Unknown`. Unvisited bodies and default structural facts are `Unknown`, not
`Unchanged`.

For Bash, complete ordinary commands with no parser-known current-scope cwd
mutation are `Unchanged`. A modeled `cd`, `command cd`, or `builtin cd` is
`ChangesOnSuccess`; a statically invalid shape that can only fail without a
transfer is `Unchanged`. `pushd` and `popd` are `Unknown` until a directory
stack is modeled. Execution-bearing builtins such as `source`, `.`, and `eval`
retain atomic parse failure and publish no occurrence.

Bash has no `chdir` builtin. The v0.3 occurrence flow treats `chdir` as an
ordinary external command and does not change the cwd. The v0.2 compatibility
`Clauses` leaf retains its locked historical `chdir` attribution only for
compatibility; v0.3 security consumers use `Commands`.

The effect describes authored shell syntax under the selected grammar, parse
options, and modeled source state. Ambient aliases, functions, modules,
profiles, `PATH`, executable behavior, and inherited external state remain
outside this fact, matching the `IsComplete` boundary. Consumers fail closed
on unknown or future alternatives and combine the fact with ancestry,
redirects, substitutions, path policy, all intervening occurrences, and every
reachable fallback scope. They do not recreate a shell builtin list.

`SimpleCommandSyntax.Substitutions` owns each completely delimited executable
command substitution evaluated for that command's authored words and redirects,
including an expanding heredoc body. The collection is in authored order and
preserves nesting: a substitution inside an inner simple command belongs to
that inner command, not to the outer command or a side table. `Clause` remains
the unchanged v0.2 compatibility leaf and retains the authored dynamic value.

`SimpleCommandSyntax.ExecutionRegions` owns each completely delimited body
that a proved or conservatively unknown command binding may execute. The
unchanged host `Clause` retains its authored script-block `DynamicSkip`
argument. A direct PowerShell `& {}` or `. {}` region appears as a statement,
carries `Origin=DirectCall` or `Origin=DotSource`, has
`HostArgument=null`, and creates no synthetic occurrence for the
invocation operator. A command-owned region carries `Origin=CommandArgument`
and `HostArgument` references the exact script-block `ClauseElement` in the
host command. Consumers use `Origin`, rather than reparsing source text, to
distinguish the different direct-invocation state semantics.

Execution-region origin, phase, timing, and cardinality are independent
structural facts. Attached regions retain authored script-block order even when a
shell-specific analyzer schedules Begin/Process/End or initialization/main in
another semantic order. `Synchronous`, `Concurrent`, and `Deferred` do not
claim variable, cwd, command-resolution, runspace, or process scope. Those
dimensions remain shell-specific analysis because PowerShell can isolate
ordinary variable assignment while sharing location. Unknown enum values fail
closed.

The canonical `Commands` and compatibility `Clauses` projections use these
deterministic ordering rules: disjoint executable regions follow authored
source order; an enclosed substitution precedes its containing command;
nested substitutions are emitted innermost first; a command-owned execution
region follows its host and sibling regions retain authored order; and nodes
without comparable
outer source spans use their containing structural collection order. Thus
`rm "$(find /tmp)"` projects `find`, then `rm`, exactly once each.
Each substitution ancestry frame uses `Region=Substitution` and its authored
zero-based `ChildIndex` in the structural collection that owns the
`CommandSubstitutionSyntax`. For a simple command this is its `Substitutions`
collection; for an iterator it is the containing iterator-command collection.

Each ancestry frame references the actual `Ancestor` node and describes its
relationship to the next node on the path. The root block uses `Root`; non-root blocks and command
lists use `Statement`; pipelines use `PipelineStage`; groups use `GroupBody`;
foreach nodes use `Iterator` or `LoopBody`; command substitutions use `Substitution`; and
execution regions use `ExecutionRegion`.
Repeated children use their zero-based authored index. Source ranges are read
from the referenced ancestor. Blocks, command lists, and groups retain the
incoming immediate role; pipeline stages, iterator/body regions,
substitutions, and execution regions replace it with their nearer execution role.

Projection accepts only a parser-owned tree: a syntax-node or `Clause`
reference cannot appear at two authored positions, node and fragment spans are
either both unavailable or a non-negative start/length pair, and structural
enum values consumed by the projector must be known. Empty blocks remain
valid, but empty pipelines and command lists are malformed.
Joined value domains, analyzed-argument references, cwd facts, redirect
coordinates, redirect shapes, and heredoc facts must satisfy their contracts.
Any violation discards the partial `Commands` and `Clauses` collections and
makes the outer parse unparseable; it is never published as a complete
occurrence.

`Arguments` contains exactly one `AnalyzedArgument` for each non-cwd
`Clause.Args` entry in authored order. `Argument` and `Element` reference the
existing compatibility objects directly. Inline equals/colon forms may create
multiple arguments that share one source element. The analysis never mutates a
compatibility `Arg` to hold loop-specific values. An occurrence can be
structurally complete while one value remains `Unknown`; completeness and
value precision are independent.

The parser emits only these value-domain combinations:

- `Unknown`: no payload;
- `Exact`: exactly one non-null value;
- `FiniteSet`: 2–32 distinct non-null values;
- `PathPattern`: a non-empty pattern and non-empty covering directory;
- `IntegerRange`: inclusive signed 64-bit bounds with minimum no greater than
  maximum, representing canonical signed ASCII decimal without a plus sign or
  leading zeros; and
- `Concatenation`: two through 16 normalized `Exact`, `FiniteSet`, or
  `IntegerRange` parts. Empty exact parts are removed, adjacent exact parts are
  merged, an all-exact result collapses to `Exact`, and one remaining
  non-exact part is returned directly. Unknown, pattern, nested concatenation,
  and over-cap inputs produce `Unknown`; the parser never truncates.

The parser does not execute commands, inspect runtime variables, enumerate the
filesystem, or truncate an over-limit set and call it complete. A result with
33 or more candidates becomes `Unknown`. The candidate cap of 32, structural
depth cap of 16, and wrapper-recursion cap of 5 are parser contracts, not
public tuning knobs. Structural depth starts at zero for the root and
increments on foreach loops, groups, command substitutions, and execution
regions; blocks, lists, pipelines, and
simple-command leaves do not independently increment it. Exceeding 16
structural containers or 5 decoded-command wrapper recursions makes the entire
result unparseable.

`AnalyzedArgument.Value` retains the effective shell-value proof under the
selected initial-state contract. `AuthoredValue` separately describes the
bounded authored shell word before field splitting, pathname expansion,
ambient variable attributes, and ambient `IFS` can transform it. It applies
quote removal and bounded source bindings but is not an argv-count or runtime-
value claim. Where the distinction does not matter, `AuthoredValue` equals
`Value`.

Double-quoted Bash `$?` is one field and publishes `IntegerRange(0, 255)`.
Literal prefixes or suffixes produce a normalized `Concatenation`; repeated
status positions are independent upper bounds. Unquoted status remains
`Unknown` because ambient `IFS` can split it. Status in command identity or
redirect-target position remains strict, and single-quoted `$?` is exact
literal text.

`BashParserOptions.PublishAuthoredSourceFacts` defaults to `false`. The default
retains v0.3.0 static-loop admission exactly: under `Unknown` initial state the
parse is unparseable and publishes empty `Commands` and `Clauses`. When true,
a supported static loop that fails only the ambient attribute or field-
splitting proof may publish a structurally complete authored occurrence with
effective `Value=Unknown` and a finite `AuthoredValue`. Explicit attribute
mutation, hidden execution, dynamic identity, command substitution, runtime
iteration, redirects, and unsupported control flow remain strict. The
`IsolatedNonInteractive` mode retains its existing effective proof and does not
require the option. The opt-in applies only to the source submitted to that
`Parse` call. It does not cross into a decoded `bash -c` or `sh -c` child;
without an independently asserted isolated state, a static loop in that child
remains unparseable.

`AuthoredPathShape` is lexical evidence only. URI-shaped words matching
`^[A-Za-z][A-Za-z0-9+.-]*://` are `Unknown`; otherwise drive, UNC, or
backslash-bearing forms are `Windows`; otherwise absolute, `./`, `../`,
eligible or literal tilde-prefix, or slash-bearing forms are `Posix`; all other
forms are `Unknown`. Windows classification takes precedence for `C:/...`.
A bounded domain publishes a known shape only when every represented word has
the same known shape. Mixed, partially unknown, or unprovable symbolic domains
are `Unknown`. This fact does not claim filesystem operand semantics or grant
authority: repository slugs, container images, API routes, and other data can
be path-shaped. PowerShell 0.3.1 sets `AuthoredValue=Value` and
`AuthoredPathShape=Unknown` for exact compatibility.

`AnalyzedArgument.AuthoredFileSystemValue` is a stronger, independent parser
fact. `Unknown` means the parser proves no bounded local-filesystem value. In
v0.3.3, only `Exact` and `FiniteSet` are positive. Every represented value is
an absolute path normalized by the existing shell resolver. Publication
requires an audited local-filesystem binding, one-field authored transform
semantics, and an exact occurrence working directory. Compatibility
`Arg.IsPath`, `ClauseElement.IsPath`, `FileVerbs`, lexical path shape, and
generic positional fallback never create this fact by themselves.

The initial audited catalog contains Bash `cat` file operands and the selected
PowerShell dialect's exact `Get-Content -LiteralPath` value. Bash option values,
`-` stream operands, active field splitting or pathname expansion, remote
endpoints, and unaudited executable positions remain `Unknown`. PowerShell
filters, rename fragments, non-filesystem or unresolved providers, remote
native endpoints, and ambiguous parameter bindings remain `Unknown`. The
catalog is parser-owned data, not an authorization policy or a full executable
grammar.

A positive authored filesystem value does not prove existence, safety,
trust-zone membership, or authority. Security consumers still require a
complete occurrence, explicitly accept the authored-source initial-state
contract, check every represented path through their own path policy, and
independently evaluate executable identity, redirects, substitutions,
ancestry, and all other occurrences. `AuthoredValue`, `AuthoredPathShape`, and
compatibility `IsPath` are not substitutes for this fact.

`AnalyzedArgument.AuthoredNonFileSystemValue` is a separate positive parser
fact for bounded authored values that an audited binding proves are not local-
filesystem operands. `Unknown` combines unaudited semantics and values whose
non-filesystem role is not proved. In v0.3.5, only `Exact` and `FiniteSet` are
positive. The fact retains the authored value and does not alter lexical path
shape. One argument never has positive filesystem and non-filesystem domains.

The initial non-filesystem catalog contains all Bash `tr` arguments. These
arguments are options or translation data; `tr` reads standard input and writes
standard output. The same catalog entry keeps `tr` as a single-token command
identity and classifies its arguments as compatibility non-path data. Active
field splitting, pathname expansion, command substitution, dynamic identity,
over-limit joins, and incomplete provenance remain `Unknown` or incomplete.

A positive authored non-filesystem value permits a consumer to omit only the
same argument's compatibility and lexical local-path interpretations. It does
not prove that the command is safe or read-only. Redirects, command effects,
substitutions, ancestry, completeness, working directory, and every other
argument remain independent. Unknown commands and unknown future domains stay
strict.

#### Bash bounded loop state

Loop bindings are analyzer-owned shell state; they are not lexical parser
frames. A nonempty Bash loop leaves its final assigned value visible after
`done`, a loop that executes zero times preserves the incoming value, and a
same-name nested loop does not restore an outer value. v0.3 may continue to
reject nested active-name reuse until that overwrite behavior is implemented;
it must never model the construct as lexical shadowing.

The Bash front end retains every iterable word and every resolver-relevant
argument fragment as parser-owned internal provenance. The abstract-state pass
then evaluates the iterable once from its incoming variable state and creates
one of these internal plans:

- `Never` for an explicit empty iterable;
- an ordered, duplicate-preserving sequence for at most 32 concrete
  iterations; or
- `ZeroOrMore` / `OneOrMore` fixed-point analysis when cardinality or an
  ordered sequence is not bounded.

The public `FiniteSet` is only a value summary. It is never used as an
iteration plan: `a b a` performs three state transitions and leaves an exact
final binding of `a`; 33 authored values use widening even when every value is
the same. An iterable that depends on an outer binding is evaluated separately
for each concrete outer visit so correlated nested state is not flattened into
an artificial cross-product. The analyzer permits at most 4096 total loop-body
transitions per parse; exceeding that resource budget makes the complete result
unparseable rather than returning a partial cross-product.

Each concrete iteration assigns its candidate into the analyzer variable map,
re-evaluates the complete effective argument vector for every body occurrence,
and carries the joined reachable cwd and variable state into the next
iteration. This re-evaluation includes state-transfer option grammar. For
example, a loop-derived `cd` argument may become `-P`, `--`, `-`, or an
operand; the analyzer may not substitute only an operand string while retaining
authored flag classification. Effective argument facts at one authored
occurrence join the values from every reachable visit.

Bash flow retains separate reachable success and failure states. `&&` analyzes
only a reachable success continuation, `||` only a reachable failure
continuation, and sequence operators consume their join. A missing partition
is unreachable and must not be replaced with the joined input merely to
populate exact facts. Structurally present but unreachable commands remain in
the syntax/occurrence projection with conservative facts. An empty loop exits
successfully without a body transition; a known nonempty loop exposes the
final body's exit status; a zero-or-more loop joins its zero path with every
reachable normal exit. Bounded fixed-point analysis widens differing cwd or
variable values to `Unknown` rather than selecting one path.

`cd` uses the effective argument vector for the current visit. Bash `chdir`
remains a v0.2 compatibility alias only; v0.3 flow treats it as an ordinary
external command.
`pushd` and `popd` may be recognized only with unknown success cwd until the
directory stack is modeled. Unmodeled execution-bearing or
attribute-mutating builtins fail the complete parse closed globally, not only
inside loops. The stable catalog is `eval`, `source` / `.`, `trap`, `let`,
`declare`, `typeset`, `local`, `readonly`, `export`, `unset`, `read`,
`readarray`, `mapfile`, `getopts`, and `set`, plus `printf -v`. These forms can
evaluate argument text, install deferred execution, or assign through
unproved integer, nameref, or array attributes. Recognition recursively
unwraps statically proved `command` and `builtin` dispatch; dynamic or invalid
wrapper grammar fails closed. Ordinary `printf` without `-v` remains
supported.

Authored command-resolution mutation is independent from variable attributes
and cwd.
`exec` fails the complete parse closed globally because it replaces the shell
or makes commandless redirections persistent. Mutating or ambiguous `hash`,
`alias`, `unalias`, `shopt`, and `enable` forms likewise fail globally before a
later command can inherit an unmodeled executable identity. The only retained
forms are exact static queries: bare `hash`, `alias`, `shopt`, and `enable`;
`hash -l` without operands and `hash -t NAME...`; `alias [-p] [NAME...]`
without a definition; `shopt` option clusters without `s` or `u`; and no-name
`enable` listing flags composed only from `a`, `n`, `p`, and `s`. Dynamic or
invalid grammar fails closed, and exact `command` / `builtin` wrappers cannot
bypass the boundary. `break`, `continue`, `return`, and `exit` remain
loop-region failures until their transfers are implemented.

Unquoted `time` and `!` reserved prefixes execute the following pipeline with
current-shell state; `coproc` starts hidden concurrent execution; and
`{ ...; }` is a current-shell group. Stable v0.3 fails exact unquoted `time`,
`!`, `coproc`, `{`, or `}` in command position closed until their nested
structure and state propagation are modeled. Quoted/escaped spellings,
`/usr/bin/time`, and `command time ...` remain ordinary command identities and
do not acquire reserved-word semantics.

Substitutions and subshells inherit the current variable/cwd state but discard
their state changes on exit. Decoded Bash command wrappers inherit invocation
cwd but no loop binding unless export is separately proved. Pipeline stages
enter from the same pipeline input; possible `lastpipe` leakage joins the full
cwd and variable state, independently of conservative `pipefail` exit
partitioning.

Compatibility arguments always retain authored loop-variable spelling. A
variable-derived path that is not independently exact keeps or becomes
`DynamicSkip`, and a relative path whose reachable visit cwds disagree loses
its static resolution. In particular, compatibility projection may not retain
the configured `$HOME` resolution after a loop binds `HOME`, even though that
binding is outside the v0.3 supported-name boundary.

#### PowerShell bounded `foreach` state

PowerShell `foreach` analysis owns a case-insensitive persistent binding map;
the structural parser does not push and restore lexical loop bindings. A proved
nonempty ordered plan leaves the final assigned value after the loop, including
when a nested loop reuses the same name. A proved empty plan performs no body
transition and preserves the incoming binding and cwd. A zero-or-more plan
joins its zero-iteration entry with every reachable iteration exit. Repeated
visits to one authored occurrence join effective argument and cwd facts rather
than selecting a representative visit.

Concrete and fixed-point PowerShell loop visits share the parse-wide 4096
transition budget. Overflow makes the complete parse unparseable and publishes
no partial command or compatibility projection.

`Set-Location` is modeled from the complete effective argument vector. Its
success exit takes the proved filesystem target cwd and its failure exit retains
the incoming cwd. One selected-dialect parameter-binding pass produces both
that flow and `WorkingDirectoryEffect`; aliases, common parameters, and
unambiguous prefixes cannot diverge between them. A successful non-filesystem or unproved target also
invalidates binding and command-resolution proofs because relative provider
operations may mutate that state. `&&` consumes only success, `||` only failure, and statement
sequence consumes their join. The analyzer publishes no finite cwd set, so any
disagreement becomes `Unknown`. Unsupported location-stack operations,
state/command-resolution mutation, and dynamic dispatch remain fail closed.
For provider-capable item mutators, a target that cannot be proved outside the
Alias, Function, Variable, and Environment providers invalidates binding
proofs; a dynamic value with a proved filesystem target does not.

PowerShell `$()` and parenthesized groups propagate supported state in the
current runspace. Decoded child hosts isolate their exit state and do not
inherit the isolated initial-state assertion unless their own invocation proves
it. Compatibility parse-time location attribution is cloned for loop iterator
and body parsing: a proved empty body cannot leak a location change, while a
possibly reached loop mutation poisons any stale exact compatibility
attribution rather than choosing one execution path.

After outcome analysis, cwd-dependent compatibility arguments, clause
elements, redirects, and cwd attribution are rebased to the occurrence's exact
cwd. When the occurrence cwd is unknown, cwd-dependent resolutions are cleared
and attribution uses the existing `<dynamic-cwd>` marker. A success-path parse
location therefore cannot leak into an exact failure continuation. Decoded
child-host leaves retain their inherited invocation-cwd attribution for this
projection even though child exit state remains isolated.

Correcting a v0.2 compatibility `Redirect` does not by itself mark the v0.3
occurrence complete. Occurrence-level redirect value completeness remains
governed by the explicit redirect analysis contract below.

### Explicit redirect analysis (v0.3)

Occurrence-specific redirect analysis is additive. The existing `Redirect`
record remains the v0.2 compatibility leaf and is not reinterpreted.

```csharp
public abstract record RedirectSource
{
    private protected RedirectSource() { }
    private protected abstract object LibraryOwnership { get; }

    public sealed record Unknown : RedirectSource { ... }
    public sealed record Default : RedirectSource { ... }
    public sealed record Descriptor : RedirectSource { public int Value { get; } }
    public sealed record PowerShellAllStreams : RedirectSource { ... }
}

public abstract record RedirectAnalysis
{
    private protected RedirectAnalysis() { }
    private protected abstract object LibraryOwnership { get; }

    public Redirect Authored { get; internal init; } = null!;
    public RedirectSource Source { get; internal init; } = null!;
    public bool IsComplete { get; internal init; }
}

public sealed record FileRedirectAnalysis : RedirectAnalysis
{
    public FileRedirectMode Mode { get; }
    public ShellValueDomain Target { get; internal init; } = null!;
}

public sealed record UnresolvedRedirectAnalysis : RedirectAnalysis { ... }
public sealed record DescriptorDuplicateRedirectAnalysis : RedirectAnalysis
{
    public int TargetDescriptor { get; internal init; }
}
public sealed record DescriptorMoveRedirectAnalysis : RedirectAnalysis
{
    public int TargetDescriptor { get; internal init; }
}
public sealed record DescriptorCloseRedirectAnalysis : RedirectAnalysis { ... }
public sealed record HereDocumentRedirectAnalysis : RedirectAnalysis
{
    public HereDocumentAnalysis Document { get; internal init; } = null!;
}
public sealed record HereStringRedirectAnalysis : RedirectAnalysis
{
    public ShellValueDomain Data { get; internal init; } = null!;
}

public enum FileRedirectMode
{
    Input,
    Output,
    Append,
    CombinedOutput,
    CombinedOutputAppend,
}

public sealed record HereDocumentAnalysis
{
    internal HereDocumentAnalysis() { }
    public ShellSourceFragment Delimiter { get; internal init; } = null!;
    public ShellSourceFragment Body { get; internal init; } = null!;
    public HereDocumentExpansionMode ExpansionMode { get; internal init; }
    public bool StripLeadingTabs { get; internal init; }
    public bool IsComplete { get; internal init; }
}

public enum HereDocumentExpansionMode
{
    Unknown,
    Literal,
    Expand,
}

```

`Authored` references the exact corresponding `Clause.Redirects` leaf.
`RedirectSource` preserves a shell-default stream, an explicit numeric
descriptor, or PowerShell's `*` selector without erasing shell identity. File
alternatives are path-relevant; descriptor alternatives are not paths.

A quoted heredoc delimiter makes the body `Literal`; an expanding body is
complete only when every supported execution-bearing substitution has been
discovered as its own command occurrence. Bash `HereString` data uses `Data`,
includes the shell's trailing newline in an exact value, and is not
path-relevant. PowerShell here-strings remain ordinary value tokens.

`RedirectSource.Unknown` is valid only with `UnresolvedRedirectAnalysis`.
`RedirectSource.Default` may own ordinary or combined file, descriptor,
heredoc, or here-string alternatives.
`RedirectSource.Descriptor` may own ordinary file, descriptor, heredoc, or
here-string alternatives but never combined-output. PowerShell all-streams may
own only output/append file alternatives or descriptor duplication to stream
1. Any other internal pairing discards the authorization projection.

A Bash file redirect whose expansion cannot prove exactly one target has an
`Unknown` target and `IsComplete=false`; its containing command occurrence is
also incomplete. In particular, an unquoted wildcard target is not completed
by enumerating the parser process's filesystem.

The public records define an in-memory typed API, not a stable polymorphic JSON
wire format. Their generated equality, hashing, and `ToString()` behavior is
part of the normal record shape. Consumers that persist parser results own a
versioned DTO or explicit serializer mapping.

### `Clause`

One logical command within a compound. Each clause has its own verb chain,
args, redirects, and the operator that joined it to the previous clause.

```csharp
public sealed record Clause
{
    /// <summary>
    /// The operator joining this clause to the previous one. The first
    /// clause in a ParsedCommand has Operator=None. Subsequent clauses
    /// carry the operator that preceded them in the source
    /// (e.g. `a && b` produces clauses [{None,a}, {AndIf,b}]).
    /// </summary>
    public CompoundOperator Operator { get; init; }

    /// <summary>The verb chain (see §3.3 and §6).</summary>
    public VerbChain Verb { get; init; } = new();

    /// <summary>
    /// All argument tokens after the verb chain, in source order. Includes
    /// flags and positional args. See <see cref="Arg.Kind"/> for token kind.
    /// </summary>
    public IReadOnlyList<Arg> Args { get; init; } = [];

    /// <summary>
    /// Compatibility redirects on this clause (>, >>, <, 2>, 2>>, &>,
    /// &>>). Each entry retains the v0.2 direction and target projection;
    /// v0.3 consumers use CommandOccurrence.Redirects for exact semantics.
    /// </summary>
    public IReadOnlyList<Redirect> Redirects { get; init; } = [];

    /// <summary>
    /// Significant source-authored verbs, arguments, and redirects in source
    /// order. This is the provenance view; Verb, Args, and Redirects remain
    /// compatibility projections. Synthetic cwd attribution is excluded.
    /// </summary>
    public IReadOnlyList<ClauseElement> Elements { get; init; } = [];

    /// <summary>
    /// True when this clause is wrapped in a subshell (parens). Subshells
    /// isolate cd state — see §9.
    /// </summary>
    public bool IsSubshell { get; init; }

    /// <summary>
    /// True when this clause is the result of parser-local recursion into a
    /// command-string wrapper — Bash `bash -c "..."` / `sh -c "..."`, or
    /// PowerShell `pwsh -Command "..."` / `pwsh -EncodedCommand ...` /
    /// static `Invoke-Expression '...'`. One parser never delegates wrapper
    /// payloads to the other parser. Useful
    /// for consumers that want to surface "this came from a wrapped
    /// invocation" in UI.
    /// </summary>
    /// <remarks>Renamed from `IsCommandStringWrapped` in v0.2.0 — see RELEASE_NOTES.md
    /// and SPEC.POWERSHELL.md §3 for the old→new mapping.</remarks>
    public bool IsCommandStringWrapped { get; init; }
}
```

### `ClauseElement`

One significant source-authored element of a clause. `Elements` preserves the
cross-projection order that `Verb`, `Args`, and `Redirects` cannot represent on
their own.

```csharp
public sealed record ClauseElement
{
    /// <summary>Exact authored source slice, including quote delimiters.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Lexer-decoded logical value. For a redirect this is the decoded target;
    /// for an inline binding it remains the complete decoded source token.
    /// </summary>
    public string Value { get; init; } = "";

    public ClauseElementRole Role { get; init; }

    /// <summary>
    /// Span in ParsedCommand.Source. Null for elements surfaced through a
    /// decoded command-string wrapper when no exact outer mapping exists.
    /// </summary>
    public int? SourceStart { get; init; }
    public int? SourceLength { get; init; }

    /// <summary>
    /// Number of parser-classified verb elements authored before this element
    /// in the clause. For a verb element, this is its zero-based Verb.Tokens
    /// index. This is an AST coordinate, not an executable-specific semantic
    /// boundary.
    /// </summary>
    public int PrecedingVerbElementCount { get; init; }

    /// <summary>
    /// Argument classification for this token, inline bound value, or redirect
    /// target. Verb elements use Literal, except dynamic command names use
    /// DynamicSkip.
    /// </summary>
    public ArgKind Kind { get; init; }
    public bool IsFlag { get; init; }
    public bool IsPath { get; init; }
    public string? Resolved { get; init; }
}

public enum ClauseElementRole
{
    Verb,
    Argument,
    Redirect
}
```

The collection contains significant leaves only: whitespace, comments,
compound operators, grouping delimiters, and shell call operators are excluded.
Each verb token appears exactly once with `Role=Verb`. Each authored argument
token appears once with `Role=Argument`; inline forms such as
`--work-tree=../repo` stay one element even when `Args` exposes separate flag
and value projections. Shell-adjacent fragments that form one native argument,
such as `--data="@request file.json"`, likewise stay one element spanning the
complete authored argument. The parser consumes the full contiguous fragment
run, including an unquoted value prefix such as `--data=@request".json"`.
Mixed quoting that prevents safe reconstruction of resolver-sensitive literal
syntax (`$`, glob metacharacters, `~`, provider prefixes) safe-fails the bound
value as `DynamicSkip`, including syntax exposed only after an operand marker
such as curl's leading `@` is removed. A parser-defined opaque computed region that is
safe-failed as one `DynamicSkip` argument also appears as one argument element;
its `Raw` and `Value` are the complete source slice rather than a claim that
the parser understood the region's interior. Each redirect appears once with
`Role=Redirect`; its ordinal among redirect elements matches its ordinal in
`Redirects`, and `Raw` spans the operator through its target.

`PrecedingVerbElementCount` is clause-local and resets to zero at every clause.
For `git -C /repo commit`, `-C` and `/repo` carry `1`; for
`git commit -C HEAD~1`, `-C` and `HEAD~1` carry `2`. ShellSyntaxTree reports
that parser-relative coordinate but does not assign Git-specific meaning to
it. `Role=Verb` mirrors the greedy `Clause.Verb` heuristic. Therefore an
unrecognized option can stop verb extraction and cause a later semantic
subcommand to appear with `Role=Argument`; consumers SHALL use the complete
authored element order rather than treating this count as an executable's
semantic command boundary.

Synthetic cwd-attribution args are deliberately absent from `Elements`: they
remain available through `Args` with `IsCwdAttribution=true`. Clauses expanded
from command-string wrappers preserve each element's inner `Raw` and `Value`,
but set `SourceStart` and `SourceLength` to null rather than guessing how a
decoded or escaped inner character maps into the outer `ParsedCommand.Source`.

Because `Clause` is a record, `Elements` participates in its generated value
equality and hashing. Generated `ToString()` and default JSON serialization
also include the projection. The API addition is source- and binary-additive,
but these generated behaviors are observably different.

### `VerbChain`

The verb of a clause. Multi-token to handle commands like `git push`,
`docker compose up`, `dotnet ef migrations add`. Length determined by the
greedy verb-chain heuristic in §6.1 — consecutive verb-like Word tokens
from the start of the clause, transparently consuming flag-with-value
pairs, with a 1-token carveout for FILE verbs.

```csharp
public sealed record VerbChain
{
    /// <summary>
    /// Verb tokens in source order. Empty when the clause has no verb
    /// (e.g. clause is just a redirect or an empty fragment).
    /// </summary>
    public IReadOnlyList<string> Tokens { get; init; } = [];

    /// <summary>
    /// The canonical, alias-resolved verb identity (added v0.2.0). Non-null
    /// only when the parser rewrote a built-in alias — `ls` → `Get-ChildItem`.
    /// Null for every bash clause. See SPEC.POWERSHELL.md §3.
    /// </summary>
    public string? CanonicalVerb { get; init; }

    /// <summary>
    /// True when the clause's command name is a dynamic token the parser
    /// cannot statically identify — `& $exe`, `& "tool-$name"`,
    /// or supported `& $(Get-Thing)` (added v0.2.0). An unsupported
    /// executable identity expression makes the whole result unparseable.
    /// Always false for bash clauses. See SPEC.POWERSHELL.md §3.
    /// </summary>
    public bool IsDynamic { get; init; }

    /// <summary>Convenience: tokens joined with spaces.</summary>
    public string Joined => string.Join(" ", Tokens);
}
```

> **Note:** The single-space form `string.Join(" ", …)` is used (not the
> `char` overload `string.Join(' ', …)`) so the implementation compiles
> on both `netstandard2.0` and `net8.0`. The `char` overload is net5+
> only.

### `Arg`

One argument token after the verb chain. Includes resolution state.

```csharp
public sealed record Arg
{
    /// <summary>Verbatim token from the source.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Resolved value for path tokens — tilde expanded, env vars
    /// substituted, normalized to absolute path against
    /// BashParserOptions.WorkingDirectory. Null when Kind is not a path
    /// (Literal non-path / Glob / DynamicSkip).
    /// </summary>
    public string? Resolved { get; init; }

    /// <summary>Token kind. See <see cref="ArgKind"/>.</summary>
    public ArgKind Kind { get; init; }

    /// <summary>
    /// True when this token starts with '-' or '--' (a flag, not a
    /// positional arg).
    /// </summary>
    public bool IsFlag => Raw.StartsWith('-');

    /// <summary>
    /// True when this token is a path the clause operates on (per the
    /// per-verb pathArgs table; see §7). Set during parsing so consumers
    /// don't reapply per-verb rules.
    /// </summary>
    public bool IsPath { get; init; }

    /// <summary>
    /// True when this Arg is a synthetic attribution arg representing
    /// the working directory inherited from a preceding `cd`/`chdir`
    /// clause in the same compound. Default false. See §9 for
    /// propagation semantics.
    /// </summary>
    public bool IsCwdAttribution { get; init; }
}

public enum ArgKind
{
    /// <summary>Literal value (string, number, flag).</summary>
    Literal,
    /// <summary>Token containing an unresolved env var reference.</summary>
    EnvVar,
    /// <summary>Token containing glob metachars (* ? [).</summary>
    Glob,
    /// <summary>Token starting with ~ (tilde).</summary>
    Tilde,
    /// <summary>
    /// Token whose value cannot be safely resolved (unresolved env var,
    /// unexpandable glob). Consumers SHALL treat as "no value extracted"
    /// rather than using Raw as a literal path.
    /// </summary>
    DynamicSkip
}
```

### `Redirect`

```csharp
public sealed record Redirect
{
    public RedirectDirection Direction { get; init; }
    /// <summary>
    /// Redirect target. Normally a path resolved per Arg conventions
    /// (§8); for fd-dup / fd-close shorthand (`&N`, `&N-`, `&-`)
    /// the raw token is carried verbatim and IsDynamicSkip is true.
    /// </summary>
    public string Target { get; init; } = "";
    /// <summary>
    /// True when the target is opaque to path resolution — a dynamic
    /// token (env var, command substitution) or an fd-dup / fd-close
    /// form. Consumers MUST NOT treat Target as a path when this is true.
    /// </summary>
    public bool IsDynamicSkip { get; init; }
}

public enum RedirectDirection
{
    In,         // <
    Out,        // >
    Append,     // >>
    ErrOut,     // 2>
    ErrAppend   // 2>>
}
```

### `CompoundOperator`

```csharp
public enum CompoundOperator
{
    None,      // first clause; no prior operator
    AndIf,     // &&
    OrIf,      // ||
    Sequence,  // ;
    Pipe       // |
}
```

---

## 4. Grammar

Approximate BNF for what the parser accepts. Anything outside this grammar
is unparseable (`ParsedCommand.IsUnparseable = true`).

```
command         := clause (compound_op clause)*
compound_op     := "&&" | "||" | ";" | "|" | NEWLINE
clause          := subshell | bash_c_wrapper | simple_clause
subshell        := "(" command ")"
bash_c_wrapper  := ("bash" | "sh") static_flag* "-c" STATIC_QUOTED_STRING
static_flag     := exact-one literal Word beginning with "-"
STATIC_QUOTED_STRING := QuotedString whose outer-shell provenance is entirely
                        literal and exactly one value
simple_clause   := verb_chain arg* redirect*
verb_chain      := verb_like_word (FW_pair? verb_like_word)*
                                     // greedy walk per §6.1; FW_pair is a
                                     // flag-with-value pair owned by word_0
                                     // (transparent to the walk); stops at
                                     // the first path-shaped or non-verb-like
                                     // token. For
                                     // word_0 ∈ FileVerbs, exactly 1 token.
verb_like_word  := static word satisfying §6.1; the initial command-name
                   element contains no supported_substitution
arg             := word | flag | quoted_string | supported_substitution
flag            := "-" letter+ | "--" word
redirect        := redirect_op target
redirect_op     := descriptor? (">" | ">>" | "<") | "&>" | "&>>"
descriptor      := digit+
target          := word | quoted_string | supported_substitution
supported_substitution := "$(" command ")"
word            := non-whitespace, non-operator fragments; may contain
                   supported_substitution children in v0.3
quoted_string   := single-quoted | double-quoted
                   // double-quoted values may contain supported_substitution;
                   // single-quoted and escaped spellings remain literal
```

**Notes:**

- Whitespace between tokens is one or more spaces or tabs.
- A bare newline outside quotes, heredoc bodies, line continuations, and
  `$(...)` / backtick substitutions is a **statement separator** —
  semantically equivalent to `;`, producing `CompoundOperator.Sequence`.
  Consecutive newlines, leading and trailing newlines, and a newline
  immediately following a compound operator all collapse: they never
  yield an empty clause. The newline after a heredoc terminator likewise
  separates the heredoc's clause from what follows.
- `\` followed by a newline is removed before word-boundary analysis. It joins
  adjacent fragments (`r\` + newline + `m` is the command name `rm`); actual
  surrounding spaces still separate words.
- Bash line comments (`#` at a word boundary through end-of-line) are
  whitespace-equivalent at the lexer level — they emit a Comment token
  for source fidelity but are filtered alongside Whitespace by the
  parser, so they do not appear in the grammar. See §5 "Comment
  handling" for boundary rules.
- `\` before a metachar inside a double-quoted string escapes the metachar.
- Single-quoted strings preserve all bytes literally — no escape processing.
- v0.2 recognizes heredocs (`<<EOF ... EOF`) as redirect syntax while the
  body is skipped. Stable v0.3 preserves delimiter, body, expansion mode,
  tab-stripping mode, and completeness through `HereDocumentAnalysis`.
  The bounded grammar accepts one terminal `<<` / `<<-` redirect, including a
  literal numeric source descriptor such as `3<<EOF`, on a command header,
  with optional whitespace or a trailing comment after the delimiter.
  Additional header tokens, pipelines, and queued heredocs are unparseable
  until their body-association grammar is modeled. Quote removal determines
  the delimiter spelling; any quoted or escaped delimiter fragment makes the
  body literal. In an expanding body, unescaped `$()` substitutions are
  executable even when their spelling is surrounded by quote characters,
  because heredoc body quotes are data rather than shell quoting syntax.
  Escaped substitutions remain literal. Legacy backticks, `$((...))` and
  obsolete `$[...]` arithmetic expansion, prompt-transformed `${name@P}` or
  other parameter operators, line continuations that could hide a
  substitution boundary, and incomplete substitutions make the whole result
  unparseable.
- Redirect targets matching the POSIX fd-dup / fd-close shorthand —
  `&N`, `&N-`, or `&-` (where `N` is one or more decimal digits) — are
  NOT path-resolved. The parser carries the raw token (e.g. `&1`) on
  `Redirect.Target` and sets `Redirect.IsDynamicSkip = true`. This
  prevents `2>&1` from being incorrectly resolved to `<cwd>/&1`.
- Function definitions, assignment-prefix commands, `while` / `until`, `if` /
  `elif` / `else`, `case`/`esac`, C-style or implicit loops, arithmetic
  execution, process substitution, and single-`&` background lists remain
  unparseable in stable v0.3 because they can hide executable regions outside
  the bounded grammar below.

### v0.3 structured Bash grammar

Contextual keywords match only in command position. `echo for` therefore
remains a simple command argument rather than starting a loop.

```text
bash_script(stop)    := bash_list_item (list_sep bash_list_item)*
bash_list_item       := bash_and_or
bash_and_or          := bash_pipeline (("&&" | "||") bash_pipeline)*
bash_pipeline        := bash_command ("|" bash_command)*
bash_command         := bash_for_in
                      | bash_subshell
                      | bash_c_wrapper
                      | bash_simple_command

bash_for_in          := "for" binding_name "in" iterable_word*
                        list_terminator "do"
                        bash_script(stop = "done")
                        "done"

list_sep             := ";" | NEWLINE
list_terminator      := ";" | NEWLINE+
binding_name         := supported_scalar_binding
supported_scalar_binding := [a-z][a-z0-9_]*
                            except "auto_resume" and "histchars"
iterable_word        := word | quoted_string | supported_substitution
```

The supported stable-v0.3 set is the existing simple-command grammar plus
`for name in words`. Bash
accepts additional shell identifiers as loop variables, but this bounded
grammar fails them closed for the initial-state reasons specified in §2.
Every fully
delimited `$()` command substitution in a supported simple-command argument,
redirect value, iterable, or expanding heredoc body is recursively parsed and
exposes its inner commands; its produced value remains `Unknown`. A nested
substitution is recursively attached to the nearest containing simple command.
Simple `$name` and `${name}` forms additionally require the proved
variable-attribute state from §2; syntactic simplicity alone is not evidence
that dereferencing a nameref cannot execute an array subscript.
Legacy backtick substitution becomes unparseable in v0.3 until its distinct
escape and nesting rules can be mapped without guessing. A Bash path-shaped
glob may produce a `Pattern` only when its exact static covering directory is
proved without filesystem enumeration. Bash `<<<` is a non-path `HereString`
redirect.

A `$()` fragment in Bash command-name position leaves the outer command
identity runtime-dependent. Stable v0.3 makes the whole result unparseable
rather than changing the v0.2 `VerbChain.IsDynamic` contract, which remains
PowerShell-specific. Diagnostic `Syntax` may retain the discovered substitution,
but `Commands` and `Clauses` are empty.

Missing `do` or `done`; an unsupported substitution whose
commands cannot all be discovered; or any skipped executable region makes the
entire result unparseable. The parser may preserve a diagnostic syntax tree,
but it returns empty `Commands` and `Clauses` so consumers cannot authorize a
discovered subset.

---

## 5. Tokenization Rules

The lexer produces tokens consumed by the parser. Token kinds:

- **WORD** — sequence of non-whitespace, non-operator, non-quote chars.
  Example: `git`, `/etc/foo`, `--force`, `~/path`, `$VAR`. A braced
  parameter is absorbed only when its body is a simple shell identifier,
  positional parameter, or special parameter. Parameter operators are
  unparseable because their operands can contain hidden execution; the
  resolver in §8 decides `Kind` for accepted simple forms.
- **QUOTED_STRING** — single- or double-quoted string. The lexer strips
  the quote delimiters from the token value. Example: `"hello world"`
  becomes the token value `hello world`.
- **OPERATOR** — `&&`, `||`, `;`, `|`, `>`, `>>`, `<`, numeric-descriptor
  forms such as `2>`, `3>>`, `10<`, `3<<`, `4<<-`, and `5<<<`, `&>`,
  `&>>`, `(`, `)`, `<<`, `<<-`, `<<<`.
- **WHITESPACE** — one or more spaces, tabs, or newlines (newlines inside
  a heredoc body are not emitted as ordinary tokens; the delimiter token
  retains the body's resolver fragments and authored extent). A whitespace run that
  contains a newline — including the newline after a heredoc terminator —
  is flagged as a **statement separator**; the parser retains those
  tokens past `FilterSignificant` and splits clauses on them per §4. A
  pure space/tab run carries no flag and is discarded after splitting.
- **CONTINUATION** — `\` + `\n` (or `\r\n`). Removed before word-boundary
  analysis; adjacent lexical fragments remain one authored word.
- **OPAQUE_SUBSTITUTION** — `$(cmd)` or backtick `` `cmd` ``. The full
  substitution slice (including delimiters) becomes a single token.
  Boundary tracking handles nested same-kind regions, nested quotes,
  and `\X` escapes via a shared opaque-region scanner. The parser
  consumes this token as `Arg{ Kind=DynamicSkip, IsPath=false,
  Resolved=null }` per locked interpretation #2.
  Expanding-heredoc substitutions use the same opaque fragment semantics but
  remain attached to the delimiter token rather than entering the ordinary
  command-token stream.
- **UNPARSEABLE_SENTINEL** — `$((expr))` or obsolete `$[expr]` arithmetic
  expansion, or any operator-bearing parameter expansion such as
  `${var:-$(cmd)}`, `${var//pat/repl}`, or `${var@P}`. The lexer skips past
  the matching close (`))` or `}` respectively) and emits a sentinel
  whose reason names the rejected construct. The parser consumes this
  token by setting outer `ParsedCommand.IsUnparseable = true` (see §11).
- **COMMENT** — `#` at a word boundary (start of input, or preceded by
  whitespace, a newline, an operator, or any other lexer-recognized
  boundary) starts a line comment running to (but not including) the
  next newline. The lexer emits a single Comment token covering the
  `#` and the comment text, for source fidelity. The parser drops
  Comment tokens in `FilterSignificant` alongside Whitespace and
  Continuation — comments produce no clauses, args, redirects, or
  flags. See "Comment handling" below for boundary rules.

### Quote handling

- **Single quotes** `'...'` preserve bytes literally. No escape processing,
  no variable expansion. Anything inside is one token.
- **Double quotes** `"..."` preserve whitespace but allow:
  - `\"` escapes the closing quote.
  - `\\` escapes a backslash.
  - `\$` escapes a dollar sign.
  - `$VAR` and `${VAR}` are recognized as env var references but **not
    expanded** — the token is marked `ArgKind.EnvVar` (or `DynamicSkip`
    if resolution would be required for path classification).
- Unbalanced quotes → `IsUnparseable = true` with reason
  `"unbalanced quote at position N"`.

### Escape handling

- `\X` outside quotes: removes the backslash, takes X literally. Example:
  `echo \$HOME` produces token `$HOME` with `ArgKind.Literal`.
- `\X` inside double quotes: only `\"`, `\\`, `\$`, `\\`, and `\\`+newline
  are recognized escape sequences. Other backslashes preserved literally.

### Operator boundaries

Operators terminate the current token. `cd /tmp&&ls` lexes as
`[cd, /tmp, &&, ls]` — no whitespace required around operators. The lexer
must handle this. A numeric descriptor is an operator prefix only when its
digits begin at a shell-token boundary and become adjacent to `<`, `>`, `>>`,
`<<`, `<<-`, or `<<<` after Bash removes unquoted line continuations.
Continuations may join digit fragments or the descriptor and operator; LF and
CRLF spellings retain their authored span while producing the same descriptor.
Digits joined to an ordinary, quoted, or escaped word remain part of that word;
`command3>file` therefore uses command name `command3` and a default-source `>`
redirect.

### Comment handling

- An unquoted `#` that appears at a **word boundary** starts a comment
  that runs to (but does not include) the next newline. A word boundary
  is: start of input, or the position immediately after a whitespace
  run, a newline, an operator (`&&`, `||`, `;`, `|`, `>`, `>>`, `<`,
  a numeric descriptor adjacent to `>`, `>>`, `<`, `<<`, `<<-`, or `<<<`,
  `&>`, `&>>`, `(`, `)`, `<<`, `<<-`, `<<<`), a quoted string, or an opaque
  substitution. Equivalently: `#` is comment-start everywhere the
  outer lexer dispatch loop sits, because every other lexer rule has
  already consumed its territory before `#` is considered.
- `#` **inside** single or double quotes is a literal character (no
  comment).
- `#` in the **interior** of an unquoted word (e.g. `abc#def`) is a
  literal character. `ReadWord` consumes the whole word before the
  outer loop can see the embedded `#`; there is no re-scanning.
- `\#` (backslash-escaped `#` outside quotes) is consumed by the
  normal escape rule — the backslash is dropped and `#` becomes a
  regular word character. Equivalent example: `cmd \#abc` produces
  one Word token `#abc`.
- The terminating newline is **not** consumed by the Comment token.
  It survives as a Whitespace token, preserving statement-boundary
  semantics for the parser (see §4).
- A Comment token's `Value` is empty (matching `Whitespace` /
  `Continuation`); `SourceStart` / `SourceLength` identify the slice
  including the leading `#` so callers that need the literal text can
  recover it from the original input span.
- **Effect on parsing**: comment-only input parses to
  `Clauses = []`, `IsUnparseable = false` — mirroring empty-input
  behavior. A comment leading, trailing, or interleaved with a clause
  contributes no tokens to the verb chain, args, or redirects of any
  clause.

---

## 6. Verb Tables

These are **data**, not logic. Implement as `static readonly` collections.

### 6.1 Verb-chain extraction (greedy heuristic)

Per issue #27 (locked in v0.1.4-alpha), the parser does not consult a
static arity table. It walks consecutive verb-like Word tokens from the
clause start. The walk stops before a path-shaped or non-verb-like token.
This rule naturally scales to unknown CLIs
(`freshdesk ticket list`, `kubectl get pods`, `dotnet ef migrations add`)
without curated table entries.

#### IsVerbLikeToken predicate

A token is "verb-like" when **all** of these hold:

- `Kind == BashTokenKind.Word` (quoted strings are values, never verbs at
  index ≥ 1).
- Length is in `[1, 64]` characters.
- First character is an ASCII lowercase letter `[a-z]`.
- Remaining characters are drawn from `[a-z0-9._-]` only.

The predicate is implemented in `BashVerbs.IsVerbLikeToken`. The leading
lowercase requirement mirrors real CLI subcommand convention; the
character allow-list naturally excludes flags (`-x` starts with `-`),
paths (`/`, `\`, `~`), env-var refs (`$VAR`), URLs (`://`), globs
(`* ? [`), and user-named identifiers (uppercase first char like
`InitialCreate`).

The walk also rejects a token that matches the §8 path-shape heuristic.
This rule applies even when the lexical predicate accepts the token.

#### Walk algorithm

For a clause whose first token is a Word `firstVerb`:

1. Append `firstVerb` to the verb chain (it does not need to satisfy
   `IsVerbLikeToken` — bare commands like `Curl` or `_init` are still
   commands). Do not apply the path-shape boundary at command position.
   A command such as `deploy.sh` or `./deploy.sh` remains the first verb.
2. Iterate the remaining tokens in order. For each token `t`:
   - If `t.Kind != Word`: **stop**.
   - If `t` is a flag (`IsFlagWord`):
     - If `firstVerb` has a `FlagsWithValue` entry containing
       `StripEqualsValue(t.Value)` AND the next token is `Word` or
       `QuotedString` AND `t.Value` has no inline `=`: consume both as a
       flag-value pair, mark their indices for `consumedFlagValueIndices`,
       and continue walking.
     - Otherwise: **stop**.
   - If `firstVerb ∈ FileVerbs`: **stop** (1-token carveout — see below).
   - If `BashResolver.LooksLikePath(t.Value)`: **stop**. The argument pass
     uses the same classifier and preserves the token as a path argument.
   - If `!IsVerbLikeToken(t)`: **stop**.
   - Otherwise: append `t.Value` to the verb chain and continue.

If the first token is a `QuotedString` (e.g. `"git" push origin main`),
emit a 1-token verb chain `[firstVerb]` and skip the walk entirely. Bash
treats the quoted form as a verb-identity carrier; remaining tokens are
arg-list material.

#### FileVerb 1-token carveout

For verbs in §6.3 `FileVerbs` (file-mutation, file-read, editors,
compression, shell loaders, etc.), the verb chain stops at exactly one
token. The flag-with-value consumption still runs so the value of
`curl -o file`, `tar -C /path`, `git -C /repo` style flags picks up
`IsPath=true` via the `FlagValueIsPath` mechanism.

The carveout exists because FileVerbs use SPEC §7 per-verb positional
rules to classify args as paths. Without it, a bare-name target like
`cat README` would over-extract — `README` is shape-wise verb-like —
and lose the `IsPath=true` classification downstream consumers depend
on for zone-gate evaluation.

#### Examples

| Input | Verb chain | Args |
|---|---|---|
| `git push origin main` | `[git, push, origin, main]` | `[]` (over-extracts; see §6.1.1) |
| `git -C /repo worktree list --porcelain` | `[git, worktree, list]` | `[-C, /repo, --porcelain]` |
| `freshdesk ticket list --status open` | `[freshdesk, ticket, list]` | `[--status, open]` |
| `kubectl get pods my-pod` | `[kubectl, get, pods, my-pod]` | `[]` |
| `aws s3 cp src dst` | `[aws, s3, cp, src, dst]` | `[]` (bare-word path args over-extract) |
| `dotnet ef migrations add InitialCreate` | `[dotnet, ef, migrations, add]` | `[InitialCreate]` (stops at uppercase) |
| `deploy.sh status` | `[deploy.sh, status]` | `[]` (command position wins) |
| `git diff install-skills.sh` | `[git, diff]` | `[install-skills.sh]` (path-shaped operand) |
| `kubectl apply deployment.yaml` | `[kubectl, apply]` | `[deployment.yaml]` (path-shaped operand) |
| `tool plugin.sh list` | `[tool]` | `[plugin.sh, list]` (path evidence wins) |
| `cat /etc/passwd` | `[cat]` | `[/etc/passwd]` (FileVerb carveout) |
| `cat README` | `[cat]` | `[README]` (FileVerb carveout preserves IsPath) |
| `ls -la /tmp` | `[ls]` | `[-la, /tmp]` (FileVerb carveout) |
| `chmod 755 file` | `[chmod]` | `[755, file]` (digit-start kills walk; FileVerb anyway) |
| `echo hello` | `[echo, hello]` | `[]` (echo is not a FileVerb; over-extracts) |

### 6.1.1 Consumer pattern-matching guidance

`Clause.Verb` is a **convenience hint, not a security contract**.
The parser deliberately over-extracts on bare-word args because no
syntactic rule disambiguates `origin` (a branch name) from `worktree`
(a subcommand verb) without per-CLI semantic knowledge — and we will
not bake per-CLI knowledge into the parser.

Consumers needing security-grade command identification choose one of two
strategies over the source-ordered `Clause.Elements` view:

1. **Strict authored-stream matching.** Match every modeled significant
   element in source order. A strict matcher may define explicit operand slots
   or wildcards, but it SHALL NOT discard an intervening argument merely
   because the parser assigned it `Role=Argument`. Therefore a strict
   `git commit` pattern does not match `git -C /repo commit`.
2. **General executable-aware matching.** Pass the complete authored stream to
   a grammar owned by the consumer. The grammar consumes known options and
   operands, identifies the executable's semantic command, and returns both a
   normalized approval identity and every policy-relevant operand or scope.
   Equivalent syntax may reuse an approval only after complete interpretation.

For example, a Git-aware matcher may interpret `git -C /repo commit` as the
general identity `git commit` with effective directory `/repo`. It may then
reuse a `git commit` approval only when that approval's directory policy covers
`/repo`. Likewise, executable-aware matchers may intentionally normalize
`git push origin main` to `git push` or `kubectl get pods my-pod` to
`kubectl get pods` when their grammars establish which suffixes are operands.

There is no shell-generic rule that selects all `Role=Verb` elements and
compares them as a contiguous semantic prefix. For unknown executables or an
unrecognized option shape, consumers should use strict matching or prompt;
they should not silently fall back to a broader general identity.

False-negative (re-prompt) is recoverable. False-positive (silent
destructive grant) is not. Narrow-by-default favors the recoverable
failure mode.

The path-shape boundary requires no command dictionary. It uses the same
curated evidence as argument classification. A rare extension-shaped
subcommand becomes a path argument because the stronger path evidence wins.

### 6.2 CWD verbs

Verbs whose first non-flag positional arg becomes the cwd for subsequent
clauses in the same compound (see §9).

```csharp
internal static readonly HashSet<string> CwdVerbs =
    new(StringComparer.OrdinalIgnoreCase)
{
    "cd", "chdir", "popd", "pushd",
    "push-location", "set-location"  // PowerShell idioms (forward-compat)
};
```

### 6.3 FILE verbs

Verbs whose positional args are paths. The default extraction rule is "all
non-flag positional args after the verb chain are paths." Per-verb overrides
in §7.

```csharp
internal static readonly HashSet<string> FileVerbs =
    new(StringComparer.OrdinalIgnoreCase)
{
    // CWD verbs are also FILE verbs (their target is a path)
    "cd", "chdir", "popd", "pushd", "push-location", "set-location",
    // File mutation
    "rm", "cp", "mv", "mkdir", "rmdir", "touch", "ln",
    "chmod", "chown", "chgrp", "stat", "test",
    // Read
    "cat", "less", "more", "head", "tail", "grep", "rg",
    "find", "fd", "locate", "wc", "file",
    // Editors / text tools
    "sed", "awk", "vi", "vim", "nano", "emacs", "ed",
    // Compression
    "tar", "zip", "unzip", "gzip", "gunzip", "bzip2", "xz",
    // Network with file targets
    "curl", "wget", "scp", "rsync", "sftp",
    // Shell / interpreter loaders
    "bash", "sh", "zsh", "fish",
    "python", "python3", "node", "ruby", "perl", "php",
    // Diff / patch
    "diff", "patch", "cmp",
    // Listing
    "ls", "dir", "tree",
};
```

### 6.4 CMD_FILE verbs (Windows cmd / PowerShell file utilities)

The Windows native file utilities. As of v0.2.0 the PowerShell parser's
`PwshVerbs.FileVerbs` table consumes this reserved set
(`type`, `copy`, `move`, `del`, `xcopy`, `robocopy`, `findstr`) so a
native Windows file tool in a PowerShell command still gets path
classification. PowerShell *cmdlet* file verbs (`Get-Content`,
`Remove-Item`, `Copy-Item`, ...) are owned by `SPEC.POWERSHELL.md` §6.4 —
they are recognized by cmdlet shape and alias resolution, not by this
table. A Windows `cmd` parser remains deferred (§18).

```csharp
internal static readonly HashSet<string> CmdFileVerbs =
    new(StringComparer.OrdinalIgnoreCase)
{
    "type", "copy", "move", "del", "erase", "ren",
    "xcopy", "robocopy", "findstr",
};
```

---

## 7. Per-Verb Path-Arg Extraction Rules

The default rule for FILE verbs: every non-flag positional arg after the
verb chain is a path. Per-verb overrides:

| Verb | Rule |
|---|---|
| `chmod` | First non-flag positional is **mode** (e.g. `755`, `+x`); rest are paths. |
| `chown` | First non-flag positional is **user[:group]**; rest are paths. |
| `chgrp` | First non-flag positional is **group**; rest are paths. |
| `ln` | All positionals are paths (source then target). |
| `find` | First positional is a path; rest are predicate args (skip). |
| `grep` | First positional is **pattern**; rest are paths. |
| `rg` | First positional is **pattern**; rest are paths. |
| `sed` | First positional is **script**; rest are paths. |
| `awk` | First positional is **program**; rest are paths. |
| `tar` | Action flag determines path roles; default to extracting all non-flag positionals as paths. `-F` / `--info-script` / `--new-volume-script` values are executable command text and safe-fail as `DynamicSkip`, never paths. |
| `curl` | First positional is **URL**, not a path. `-o` / `--output` and `-D` / `--dump-header` values are paths. `-d` / `--data` values are request data unless prefixed with `@`, which reads a file; `@-` reads stdin and is not a path. |
| `wget` | First positional is **URL**, not a path. `-o` / `--output-file` writes a log path; `-O` / `--output-document` writes the downloaded document path. |
| `scp`, `rsync`, `sftp` | All positionals are paths (some remote). |
| `cd`, `chdir`, `pushd`, `popd` | First non-flag positional is the cwd target (a path). |
| Others (in FileVerbs, no override) | All non-flag positionals are paths. |

### Flag-with-value handling

Some flags take values (`-o file`, `-C /repo`, `--output=file`). The parser
must know which flags consume the next token as a value. Curated table:

```csharp
internal static readonly IReadOnlyDictionary<string, HashSet<string>>
    FlagsWithValue = new Dictionary<string, HashSet<string>>(
        StringComparer.OrdinalIgnoreCase)
{
    ["git"]   = new HashSet<string>(StringComparer.Ordinal) { "-c", "-C", "--git-dir", "--work-tree" },
    ["curl"]  = new HashSet<string>(StringComparer.Ordinal) { "-o", "--output", "-d", "--data", "-D", "--dump-header" },
    ["wget"]  = new HashSet<string>(StringComparer.Ordinal) { "-o", "--output-file", "-O", "--output-document" },
    ["docker"]= new HashSet<string>(StringComparer.Ordinal) { "-v", "--volume", "-f", "--file" },
    ["tar"]   = new HashSet<string>(StringComparer.Ordinal) { "-f", "--file", "-C", "--directory", "-F", "--info-script", "--new-volume-script" },
    ["openssl"] = new HashSet<string>(StringComparer.Ordinal) { "-subj" },
    // Add as corpus surfaces real cases.
};
```

> **Note:** the value type is `HashSet<string>` (not `IReadOnlySet<string>`)
> because `IReadOnlySet<string>` is .NET 5+ only and the library
> multi-targets `netstandard2.0`. Internal-only — no public-API impact.

> **Native option case.** The outer verb dictionary retains its existing
> case-insensitive lookup, but each native option set uses `Ordinal`. Native
> executables receive option spelling unchanged in Bash and PowerShell and may
> assign different meanings by case. Git lists both `-c` and `-C`: both consume
> a value. The generic table classifies uppercase `-C` values as paths and
> lowercase `-c` values as non-paths. Executable-aware consumers still
> reinterpret command-scoped forms such as `git commit -c/-C`, where Git uses
> the operand as a revision rather than the generic table's global meaning.
> Every supported case-distinct spelling is listed explicitly: curl `-d`
> consumes request data while `-D` consumes a header-output path;
> Wget `-o` and `-O` both consume paths but write different files.

> **Operand-sensitive values.** A fixed `(verb, flag)` boolean is insufficient
> for curl `-d` / `--data`: a value beginning with `@` names a file curl reads.
> The parser preserves the authored marker in the value `Arg.Raw` and in the
> complete `ClauseElement.Value`, strips the leading `@` only for path
> resolution, and leaves `@-` non-path because it denotes stdin. Dynamic and
> glob filenames continue through the normal §8 safe-fail rules after the
> prefix is removed.

> **Command-valued options.** GNU tar executes `-F` / `--info-script` /
> `--new-volume-script` operands. Those options still consume a value, but the
> value is `Kind=DynamicSkip`, `IsPath=false`, and `Resolved=null`; resolving
> command text as a path would give a security gate false confidence.

> **Executable context.** `FlagsWithValue` is a curated parser heuristic, not
> a complete executable grammar. In particular, Docker's global `-v` means
> `--version`, while `docker run -v` consumes a volume specification. The
> generic table preserves the established `docker run` projection; a
> Docker-aware consumer uses `Clause.Elements` to interpret placement and MUST
> NOT treat the table as universal Docker semantics.

> **OpenSSL subcommands.** The table includes only `-subj`, whose operand is
> X.509 Distinguished Name data for the documented `req`, `x509`, and `ca`
> subcommands. A slash-prefixed DN is therefore non-path data. Other OpenSSL
> options remain outside this verb-wide heuristic when their arity or role is
> subcommand-specific: `openssl x509 -serial` is valueless, and `openssl ca
> -key` consumes private-key password data rather than a filesystem path.

> **Note:** the verb-chain walk consumes flag-with-value pairs
> transparently. For `git -C /repo log`, the walk consumes `-C /repo`
> before evaluating the next token; `log` is then verb-like and extends
> the chain, producing `Verb.Tokens = ["git", "log"]` per §12's example.
> The same mechanic lets `git -C /repo worktree list` extract the full
> 3-token chain per §6.1.

When a flag-with-value consumes the next token, the consumed token's
`IsPath` flag is set if the value is path-shaped (per the resolver in §8).
For `git -C /repo log`: the `-C` flag consumes `/repo`, marks it as a
path, then the verb chain continues with `log`.

`--output=file` (equals form) is parsed as one token; the path value after
`=` is extracted into a synthetic Arg with `IsPath=true`.

---

## 8. Resolver

For each Arg with potential path content, the resolver attempts to produce
a normalized absolute path. Resolution order:

0. **Single-quoted bypass.** If the source token came from a single-quoted
   string (per §5: bytes are preserved literally — no escape processing,
   no variable expansion), the resolver skips steps 1–5 entirely. Kind is
   `Literal`; `IsPath` is `true` and `Resolved` is set only when the slot
   is a path AND `TryResolveAbsolutePath` on the raw bytes succeeds. So
   `cat '/etc/passwd'` still produces a resolved path, but `echo '$HOME'`
   stays literal — `$HOME` is not expanded inside single quotes.

1. **Tilde expansion.** `~` → `BashParserOptions.HomeDirectory`.
   `~/foo` → `<home>/foo`. The complete tilde prefix must be unquoted;
   quoted or escaped slash spellings remain literal, while backslash-newline
   is removed before this test. `~user` not supported → `DynamicSkip`.

2. **Env-var substitution.** `$VAR` and `${VAR}` are **not expanded**
   even if the value is in `Environment`. We treat any env var reference
   as `DynamicSkip` because the env var available at parse time may
   differ from what's available when the agent's command actually runs.
   `$HOME` is the **only** exception — we treat it as equivalent to `~`
   and expand it from `BashParserOptions.HomeDirectory`.

3. **`filesystem::/path` prefix stripping.** Some tools emit
   `filesystem::/path/to/file`; strip the prefix. Become `/path/to/file`.

4. **Glob detection.** Tokens containing `*`, `?`, or `[` are marked
   `ArgKind.Glob`. The resolver does **not** expand globs. The token
   stays as-is in `Raw`; `Resolved` is null.

   **In a path-arg slot:** `IsPath = true`. Consumers can apply the
   "covering directory" heuristic (`Path.GetDirectoryName(Raw)`) to
   reason about the directory the glob resolves under (e.g.
   `/tmp/*.bak` → `/tmp`).

   **In a non-path slot:** `IsPath = false`.

   Per locked interpretation #3, glob and DynamicSkip carry **distinct**
   signals — globs preserve a useful covering-dir hint that DynamicSkip
   tokens lack.

5. **Relative path resolution.** Tokens not starting with `/` (or `\\` on
   Windows, or a Windows drive letter `X:`) are joined to
   `BashParserOptions.WorkingDirectory` (lazy fallback to
   `Environment.CurrentDirectory` when null). On
   `IOException` / path-format exceptions during resolution, fall through
   to `Kind = DynamicSkip, IsPath = false, Resolved = null`.

6. **DynamicSkip predicates.** A token is `Kind = DynamicSkip,
   IsPath = false, Resolved = null` when:
   - It contains an unresolved env-var reference (other than `$HOME`)
     in a slot the verb's rule classifies as a path.
   - Resolution throws an `IOException` or path-format exception.

   Globs do NOT downgrade to DynamicSkip — they carry their own Kind so
   consumers can still apply the covering-dir heuristic. Consumers must
   not use `Raw` as a literal path for `DynamicSkip` tokens.

### Path-shape heuristic

When deciding whether a token "looks like a path" (used to decide whether
to apply the resolver):

```
LooksLikePath(token) =
   token starts with '/' (Unix absolute)
|| token starts with '\\' or '<letter>:' (Windows absolute)
|| token starts with './' or '../' (Unix relative)
|| token starts with '~' (Tilde)
|| token contains '/' anywhere
|| token contains '\\' at a NON-TRAILING position
|| token ends with a known file extension (.json, .md, .txt, .conf, ...)
|| token is in the args of a FileVerb at a position the per-verb rule
   marks as a path
```

A lone trailing `\\` is excluded because it commonly appears as a
double-quote escape-collapse artifact (`"foo\\"` lexes to Value `foo\\`)
and is not a meaningful path signal on its own.

The per-verb rule wins when present; the heuristic is the fallback.

---

## 9. cd-in-Compound Propagation

The agent's natural idiom is `cd /target && cmd1 && cmd2`. Bash semantics:
`cmd1` and `cmd2` execute with cwd `/target`. The parser honors this for
**path attribution within the same compound**.

### Rules

1. **First clause is a `cd` or `chdir` verb**: the cd target becomes the
   **attributed cwd** for subsequent clauses in the same compound. **Only
   `cd` and `chdir`** propagate attribution per locked interpretation #5.
   `pushd`, `popd`, `push-location`, and `set-location` are still listed
   in `CwdVerbs` so their first non-flag positional is path-classified
   (the target shows up as `IsPath=true`), but they do **not** add a
   synthetic attribution arg to subsequent clauses. A future v0.1.x or
   v0.2 with PowerShell support may model `pushd`/`popd` as a proper
   directory stack.
2. **Subsequent clauses inherit the attributed cwd** as if it were
   prepended with `-C` semantics. Specifically: a synthetic `Arg` with
   `IsPath=true`, `Resolved=<cd target>`, and `Kind=Literal` is added to
   each subsequent clause's `Args` list at the **end**, marked with a flag
   `IsCwdAttribution=true` so consumers can distinguish it from
   user-emitted args.

   *(Add `IsCwdAttribution: bool` to the `Arg` record. Default false.)*

3. **A subsequent `cd`** in the same compound **replaces** the attributed
   cwd for clauses after it. (`cd /a && cmd1 && cd /b && cmd2` → cmd1
   inherits `/a`, cmd2 inherits `/b`.) The replacing `cd /b` itself still
   *receives* `/a` as a synthetic attribution arg (rule 2) before becoming
   the new source — additive semantics per rule 5.

4. **Subshell boundaries reset attribution.** `cd /a && (cd /b && cmd1) && cmd2`:
   cmd1 (inside subshell) inherits `/b`; cmd2 (outside subshell) inherits
   `/a` (the subshell's `cd /b` does not leak out). A subshell *inherits*
   outer attribution on entry (so `cd /a && (cmd)` still attributes cmd
   to /a) but its own cd changes stay isolated.

5. **Attribution does not change the clause's verb or original args.**
   The attribution is purely additive — the `cd` clause itself is still
   parsed normally, and subsequent clauses retain everything the user
   typed, plus the synthetic Arg.

### Dynamic-cd attribution (locked interpretation #6)

When the cd target itself is `Kind=DynamicSkip` (e.g. `cd $REPO`), we
statically don't know the resolved cwd. To preserve the cwd-uncertainty
signal for subsequent clauses:

- A synthetic `Arg { Raw="<dynamic-cwd>", Resolved=null, Kind=DynamicSkip,
  IsPath=false, IsCwdAttribution=true }` is appended to each subsequent
  clause (instead of the literal-cd flavor).
- Relative path args in subsequent clauses are not re-resolved against a
  fall-back cwd; they surface as `Kind=DynamicSkip, IsPath=false,
  Resolved=null` so consumers route to safe-fail rather than trust a
  guessed working directory.

Consumers that iterate `IsPath=true` args won't see the synthetic
attribution arg; consumers that specifically check `IsCwdAttribution`
can detect "this clause's cwd context is unknown" and elevate to
user-prompt instead of treating it like a default-cwd command.

### Example

Input: `cd /target && git -C /other log && cat file.txt`

Parsed clauses:

```
Clause 0: Operator=None, Verb=[cd], Args=[/target]
Clause 1: Operator=AndIf, Verb=[git, log],
          Args=[
            Arg{Raw="-C",IsFlag=true},
            Arg{Raw="/other",IsPath=true,Resolved="/other"},
            Arg{Raw="/target",IsPath=true,Resolved="/target",IsCwdAttribution=true}
          ]
Clause 2: Operator=AndIf, Verb=[cat],
          Args=[
            Arg{Raw="file.txt",IsPath=true,Resolved="/target/file.txt"},
            Arg{Raw="/target",IsPath=true,Resolved="/target",IsCwdAttribution=true}
          ]
```

Note: `file.txt` in clause 2 resolves against the attributed cwd
`/target` to produce `/target/file.txt`. The attributed-cwd Arg is also
appended for completeness, even though the resolver already used it.

Consumers can choose to ignore `IsCwdAttribution=true` args if they
already see the resolved path in another arg.

---

## 10. Subshell & `bash -c` Recursion

### Subshells

Subshells are clauses wrapped in parens: `(cd /a && cmd)`. The parser
recognizes the parens and **flattens** the subshell's inner clauses into
the parent's `Clauses` list, marking each with `IsSubshell=true` so
consumers can distinguish them from outer-compound clauses. A subshell
*inherits* the outer compound's cd attribution on entry but its own cd
changes stay isolated to the subshell (rule 4 above).

Specifically: `(cd /b && cmd) && cmd2` produces three clauses:

```
Clause 0: Op=None, Verb=cd, Args=[/b], IsSubshell=true
Clause 1: Op=AndIf, Verb=cmd, Args=[/b attribution], IsSubshell=true
Clause 2: Op=AndIf, Verb=cmd2, Args=[]   // no /b attribution — subshell isolated
```

### `bash -c` Recursion

`bash -c "inner command"` and `sh -c "inner command"` are common wrappers
the agent emits. The parser:

1. Recognizes the `bash -c` or `sh -c` prefix.
2. Parses the quoted argument as a fresh `ParsedCommand`.
3. Surfaces the inner command's clauses inline in the outer's `Clauses`
   list, each with `IsCommandStringWrapped=true`.

Example: `bash -c "cd /a && cmd"` produces:

```
Clause 0: Op=None, Verb=cd, Args=[/a], IsCommandStringWrapped=true
Clause 1: Op=AndIf, Verb=cmd, Args=[/a attribution], IsCommandStringWrapped=true
```

The outer `bash -c` itself does not appear as a clause — it's "consumed"
by the recursion. Consumers that care that this came from a wrapper can
inspect `IsCommandStringWrapped` on the surfaced clauses.

A `bash` or `sh` clause whose authored arguments are dynamic, contain a decoded
`-c`, or contain a combined short option that may select command-string mode,
but that does not match the complete static wrapper production, remains visible
through its v0.2 compatibility leaf, including its direct outer source spans.
Its v0.3 command occurrence has `IsComplete=false`. Wrapper-control tokens and
the quoted body must each have literal, exactly-one outer-shell provenance;
token kind or decoded spelling alone is insufficient. A proved `--` ends this
conservative option scan. The parser does not claim to have discovered a
dynamic or otherwise unsupported command-string body.

**Recursion limit:** parse `bash -c "bash -c ..."` chains up to depth 5.
Deeper nesting → set the outer `ParsedCommand.IsUnparseable = true` with
reason `"bash -c recursion depth exceeded (>5)"` per locked interpretation
#4. (`Clause` has no `IsUnparseable` field; we surface the overflow on the
top-level ParsedCommand so consumers safe-fail per §11.)

---

## 11. Parser Anomaly Behavior

When the parser cannot produce a clean AST:

1. **Set `ParsedCommand.IsUnparseable = true`.**
2. **Set `UnparseableReason`** to a human-readable diagnostic.
3. **Return empty `Commands` and `Clauses`.** `Syntax` may retain partial
   diagnostic structure, but it is never authorization evidence. Historical
   v0.1/v0.2 parsers could retain partial clauses; v0.3 deliberately closes
   that subset-authorization hazard.
4. **Never throw** on well-formed input strings (only throw on null).

Conditions that produce `IsUnparseable = true`:

- Unbalanced quotes (`"foo` with no closing `"`).
- Unbalanced parens (`(cmd && cmd2`).
- Unrecognized control-flow keywords (`for`, `while`, `do`, `done`,
  `then`, `fi`, `case`, `esac`).
- Function definitions (`name() { ... }`).
- Process substitution (`<(cmd)`, `>(cmd)`).
- Arithmetic expansion `$((expr))` (per §1 non-goal; lexer emits an
  UNPARSEABLE_SENTINEL token; parser sets the outer flag).
- Operator-bearing parameter expansion such as `${var:-$(cmd)}` or
  `${var//pat/repl}` (per §1 non-goal; same mechanism). Only simple braced
  identifiers, positional parameters, and special parameters are accepted.
- Recursion depth exceeded on `bash -c` chains (>5 levels).

**Diagnostic precedence.** When multiple conditions could fire on a
single input (e.g. `case x in a) ;; esac` is both a control-flow
keyword AND has unbalanced parens), the parser checks them in this
order so the most informative reason wins:

1. Lexer-emitted `UnparseableSentinel` tokens (unbalanced quote /
   unterminated heredoc / arithmetic / complex parameter expansion).
2. Control-flow keyword at verb position (start of input or
   immediately after a clause separator `&&`, `||`, `;`, `|`, or
   `(`). Catches `case x in a) ;; esac` before the `)` triggers a
   paren-balance error.
3. Function definition pattern (`Word` immediately followed by `(`,
   `)`).
4. Process substitution (`<(` or `>(` adjacent).
5. Segment-split errors (unbalanced parens, unexpected operator).
6. `bash -c` recursion depth cap.

Consumers (e.g. Netclaw's gate evaluator) route unparseable commands to a
safe-fail path (prompt the user; offer only Once and Deny — no persistent
grants on shapes the parser can't model).

---

## 12. Public Examples

A handful of input/expected-AST pairs to anchor understanding. These belong
in the corpus (§13) verbatim.

### Simple verb

Input: `ls -la /tmp`

```
ParsedCommand {
  Source = "ls -la /tmp",
  IsUnparseable = false,
  Clauses = [
    Clause {
      Operator = None,
      Verb = VerbChain { Tokens = ["ls"] },
      Args = [
        Arg { Raw = "-la", IsFlag = true, Kind = Literal },
        Arg { Raw = "/tmp", IsPath = true, Resolved = "/tmp", Kind = Literal }
      ],
      Redirects = [],
      IsSubshell = false,
      IsCommandStringWrapped = false
    }
  ]
}
```

### Multi-token verb (greedy over-extraction)

Input: `git push origin main`

```
Clauses = [
  Clause {
    Verb = VerbChain { Tokens = ["git", "push", "origin", "main"] },
    Args = []
  }
]
```

The greedy heuristic absorbs `origin` and `main` because they're
syntactically indistinguishable from subcommand verbs (lowercase
identifiers, no path-shape). Consumers gating on `git push *` use
pattern-prefix length 2 — see §6.1.1.

Input: `freshdesk ticket list --status open`

```
Clauses = [
  Clause {
    Verb = VerbChain { Tokens = ["freshdesk", "ticket", "list"] },
    Args = [
      Arg { Raw = "--status", Kind = Literal, IsFlag = true },
      Arg { Raw = "open", Kind = Literal, IsPath = false }
    ]
  }
]
```

The walk stops at `--status` (a flag with no `FlagsWithValue` entry for
`freshdesk`). The full subcommand stack is captured without requiring a
curated table entry — the canonical benefit motivating the change.

### Compound with cd attribution

Input: `cd /target && cmd1 && cmd2 file.txt`

```
Clauses = [
  Clause { Verb = [cd], Args = [/target attributed-as-path], Op = None },
  Clause {
    Verb = [cmd1], Op = AndIf,
    Args = [Arg { Raw = "/target", Resolved = "/target",
                  IsPath = true, IsCwdAttribution = true }]
  },
  Clause {
    Verb = [cmd2], Op = AndIf,
    Args = [
      Arg { Raw = "file.txt", Resolved = "/target/file.txt", IsPath = true },
      Arg { Raw = "/target", Resolved = "/target",
            IsPath = true, IsCwdAttribution = true }
    ]
  }
]
```

### `git -C` flag-with-value

Input: `git -C /repo log`

```
Clauses = [
  Clause {
    Verb = VerbChain { Tokens = ["git", "log"] },
    Args = [
      Arg { Raw = "-C", IsFlag = true },
      Arg { Raw = "/repo", IsPath = true, Resolved = "/repo" }
    ],
    Elements = [
      ClauseElement { Value = "git", Role = Verb,
                      PrecedingVerbElementCount = 0 },
      ClauseElement { Value = "-C", Role = Argument,
                      PrecedingVerbElementCount = 1 },
      ClauseElement { Value = "/repo", Role = Argument,
                      PrecedingVerbElementCount = 1 },
      ClauseElement { Value = "log", Role = Verb,
                      PrecedingVerbElementCount = 1 }
    ]
  }
]
```

### Redirect

Input: `cmd > /tmp/out.txt`

```
Clauses = [
  Clause {
    Verb = [cmd],
    Args = [],
    Redirects = [Redirect { Direction = Out, Target = "/tmp/out.txt" }]
  }
]
```

### Subshell isolation

Input: `cd /a && (cd /b && cmd1) && cmd2`

```
Clauses = [
  Clause { Verb = [cd], Args = [/a], Op = None },
  Clause { Verb = [cd], Args = [/b], Op = AndIf, IsSubshell = true,
           Args = [/a attribution from outer compound] },
  Clause { Verb = [cmd1], Op = AndIf, IsSubshell = true,
           Args = [/b attribution — local to subshell] },
  Clause { Verb = [cmd2], Op = AndIf,
           Args = [/a attribution — inherited from outer cd, NOT /b] }
]
```

### Dynamic skip

Input: `rm $UNRESOLVED/foo`

```
Clauses = [
  Clause {
    Verb = [rm],
    Args = [
      Arg { Raw = "$UNRESOLVED/foo", Kind = DynamicSkip, IsPath = false,
            Resolved = null }
    ]
  }
]
```

Consumer impact: zone-gate sees zero paths to evaluate; routes to the
fallback "treat as one untrusted path = the raw token" prompt.

### Unparseable

Input: `for ((i = $(next); i < 10; i++)); do run "$i"; done`

```
ParsedCommand {
  Source = "for ((i = $(next); i < 10; i++)); do run \"$i\"; done",
  IsUnparseable = true,
  UnparseableReason = "C-style loops and arithmetic execution are unsupported",
  Syntax = ShellBlockSyntax { ... }   // optional diagnostic evidence only
  Commands = [],
  Clauses = []
}
```

---

## 13. Test Corpus Contract

The corpus is the **acceptance contract** for the parser. Implementation is
"done" when every corpus entry parses to its expected AST.

### Location

`tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` — one file per corpus
entry. File name pattern: `NN_descriptive_slug.json` where NN is a
zero-padded sequence number.

### Format

Each file:

```json
{
  "name": "Multi-token verb: git push",
  "input": "git push origin main",
  "expected": {
    "isUnparseable": false,
    "clauses": [
      {
        "operator": "None",
        "verb": ["git", "push"],
        "args": [
          { "raw": "origin", "kind": "Literal", "isPath": false },
          { "raw": "main", "kind": "Literal", "isPath": false }
        ],
        "redirects": [],
        "isSubshell": false,
        "isCommandStringWrapped": false
      }
    ]
  },
  "notes": "Optional explanation of edge case being captured."
}
```

An entry may set `bashInitialStateMode` to `Unknown` or
`IsolatedNonInteractive` when the expected result depends on the caller-proved
Bash variable-state contract. When omitted, the Bash corpus runner uses
`IsolatedNonInteractive`. The field is rejected outside the Bash corpus.

An entry may add an `elements` list to a clause to pin the complete
`Clause.Elements` projection (`raw`, `value`, `role`, `sourceStart`,
`sourceLength`, `precedingVerbElementCount`, `kind`, `isFlag`, `isPath`, and
`resolved`). The field is opt-in so older corpus entries remain readable;
issue-specific provenance entries SHALL include it.

An entry may also add both of the following v0.3 structural expectations:

- `syntax` is the complete `ParsedCommand.Syntax` tree flattened in preorder.
  Each item records `kind`, `parentIndex`, the incoming ancestry `region` and
  `childIndex`, exact-or-null `sourceStart` / `sourceLength`, and the
  kind-specific `clauseIndex`, `groupKind`, or `listOperator`. `clauseIndex`
  identifies the exact compatibility `Clause` instance owned by a
  `SimpleCommand` node; it is not a copied value comparison.
- `commands` is the complete `ParsedCommand.Commands` projection in authored
  order. Each item records its `clauseIndex`, `immediateRole`, `isComplete`,
  and outermost-to-innermost `ancestry` frames. Each frame records
  `ancestorKind`, `region`, `childIndex`, and exact-or-null source range.

These fields are independently opt-in so legacy corpus entries retain their
v0.2 shape; structural acceptance cases normally provide both. When present,
the runner compares every node, relationship, range, occurrence, role,
completeness bit, ancestry frame, and `Clause` reference. Unknown JSON members
are rejected. An unparseable result always asserts empty `Clauses` and
`Commands`, even when those arrays are omitted from the JSON.

The corpus runner also lexes every direct, parseable input and verifies that
each authored verb, argument, opaque region, and redirect token is covered by
exactly-positioned clause-element provenance. This invariant applies even when
an older entry omits the optional field, preventing silent argument loss across
the legacy corpus.

### Coverage targets for v0.1

Author at least:

- 10 simple-verb cases (ls, pwd, echo, cat, grep, etc.)
- 10 multi-token-verb cases (git push, dotnet test, docker compose up, etc.)
- 15 compound cases (`&&`, `||`, `;`, `|` combinations)
- 10 `cd`-in-compound propagation cases (single, sequential, with subshell)
- 10 quote-handling cases (single, double, escaped, mixed)
- 10 redirect cases (`>`, `>>`, `<`, `2>`, `2>>`, multiple redirects)
- 10 subshell cases (with and without isolation effects)
- 10 `bash -c` recursion cases (depth 1, 2, with inner compounds)
- 10 dynamic-skip cases (`$VAR`, `${VAR}`, `~user`, glob args)
- 10 per-verb path-rule cases (chmod, chown, find, grep, curl, git -C, etc.)
- 10 unparseable cases (unbalanced quotes, control-flow keywords, function
  definitions)

**Total minimum: 105 entries.** Strive for 150+ once seeded from sanitized
real-world commands (see §14).

### Test runner

A single xunit test method enumerates `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json`, parses
each `input`, and asserts the result matches `expected` field-by-field.
The runner emits a per-corpus-entry test name so failures point at the
specific case.

```csharp
[Theory]
[MemberData(nameof(CorpusEntries))]
public void Corpus_entry_parses_to_expected_ast(CorpusEntry entry)
{
    var parser = new BashParser();
    var actual = parser.Parse(entry.Input);
    AstAssert.Equal(entry.Expected, actual);   // structural equality
}

public static IEnumerable<object[]> CorpusEntries()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "Corpus", "bash");
    foreach (var file in Directory.GetFiles(dir, "*.json"))
    {
        var entry = JsonSerializer.Deserialize<CorpusEntry>(File.ReadAllText(file));
        yield return [entry];
    }
}
```

`AstAssert.Equal` is a helper that does structural equality with helpful
diff messages on mismatch. Implement to taste.

---

## 14. Sanitization Process

A portion of the corpus seeds from real shell commands captured from
agent dogfood logs. The seed source is a daemon log file at
`~/.netclaw/logs/daemon-2026-05-09.log` (and similar). These logs contain
PII (usernames, repo paths, channel/thread IDs) that **must not** appear
in the public corpus.

### Sanitization rules

Apply these transformations to every seeded entry **before** committing:

| Pattern | Replacement |
|---|---|
| `/home/<username>/` (any specific username) | `/home/user/` |
| `/Users/<username>/` (macOS) | `/Users/user/` |
| `~/<username>/` | `~/` |
| Specific repo paths like `/home/user/repositories/stannardlabs/<repo>` | `/home/user/repos/sample-repo` |
| Specific repo names (not in the org's public list) | `sample-repo` or `project` |
| Slack channel IDs (`D[A-Z0-9]{10}`) | `<channel>` (only if appears in command) |
| Slack thread IDs (`\d{10}\.\d{6}`) | `<thread>` |
| Internal hostnames | `internal-host.example` |
| Email addresses | `user@example.com` |
| API keys, tokens, secrets (any `[A-Za-z0-9]{20,}` that looks key-shaped) | `<redacted>` (but prefer to drop the entry entirely) |

### Workflow

1. Pull candidate commands from logs:
   ```bash
   grep -oP "command \K\{[^}]+\}" ~/.netclaw/logs/daemon-*.log \
     | jq -r .Command | sort -u > /tmp/raw-corpus.txt
   ```
2. Apply sanitization (script TBD) — for each line, walk the table above.
3. **Manual review** of each sanitized entry before committing. The script
   can miss patterns; a human (or careful agent) reviews for residual PII.
4. Drop any entry that can't be cleanly sanitized (too many specific
   identifiers; rewrite as a fully-synthetic entry instead).
5. Commit with a clear message: `chore(corpus): seed from sanitized agent
   logs (NN entries)`.

### Audit gate

Before any corpus PR merges, CI runs a regex check against the corpus
files for residual PII patterns. The check fails the build if any
sanitization-rule pattern appears in any committed corpus file. Implement
as a small `dotnet test` that scans `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` for the
forbidden patterns.

---

## 15. CI & Release Flow

The repo template already has:

- `.github/workflows/pr_validation.yml` — runs `dotnet test` on PR.
- `.github/workflows/publish_nuget.yml` — publishes to NuGet on release tag.

Adapt for ShellSyntaxTree:

- **Trigger NuGet publish on a bare SemVer tag** (for example,
  `0.3.0-alpha`). A leading `v` is invalid.
- **Test job** runs the corpus runner plus all unit tests.
- **PII audit job** runs the sanitization-pattern scan over `tests/ShellSyntaxTree.Tests/Corpus/`.

### Versioning

- **v0.1.x-alpha** — pre-release alpha cycle. Public API surface per §2 is
  locked; internal data and behavior are subject to course-correction
  while real-world feedback lands (e.g. v0.1.4-alpha replaces the
  `BashArity` static table with the greedy verb-chain heuristic per
  issue #27).
- **v0.1.0** — first publishable non-alpha cut. Bash-only.
- **v0.1.x** (post-0.1.0) — additive changes and SPEC-conformance fixes
  (more verb table entries, more corpus, bug fixes). A fix may shift the
  parsed-AST shape when the prior shape violated this SPEC — e.g. v0.1.5
  makes a bare newline a statement separator per §4. The §2 public API
  surface stays locked.
- **v0.2.0** — first PowerShell parser implementation (`PwshParser`). Adds
  the shared `ShellParserOptions` base, the additive `VerbChain.CanonicalVerb`
  / `VerbChain.IsDynamic` fields, and the breaking `Clause.IsBashCWrapped` →
  `IsCommandStringWrapped` rename. A breaking AST change on a `0.x` minor is
  permitted by Appendix A when `RELEASE_NOTES.md` carries the old→new mapping
  and Netclaw is updated in lockstep. See `SPEC.POWERSHELL.md`.
- **v1.0.0** — ready when at least one external consumer beyond Netclaw
  ships against it without finding API gaps.

### Release notes

Update `RELEASE_NOTES.md` for each tagged release. Format:

```
0.1.0-alpha YYYY-MM-DD

* First publishable cut.
* Bash parser per SPEC.md v0.1.
* Corpus: N entries.
* Public API: IShellParser, BashParser, ParsedCommand, Clause, VerbChain,
  Arg, Redirect, ArgKind, RedirectDirection, CompoundOperator.
```

---

## 16. Implementation Sequencing

A natural order for the implementer:

1. **Bootstrap projects.** Create `src/ShellSyntaxTree/ShellSyntaxTree.csproj`
   (library) and `tests/ShellSyntaxTree.Tests/ShellSyntaxTree.Tests.csproj`
   (xunit). Update `SampleSln.slnx` (rename to `ShellSyntaxTree.slnx`) and
   delete the `Akka.Console` sample.
2. **Update template defaults.** `Directory.Build.props`: replace Akka
   metadata with ShellSyntaxTree. `README.md`: real intro. `LICENSE`: keep
   Apache-2.0 (already correct). `Directory.Packages.props`: add xunit, drop
   Akka.Hosting. `Tags`: bash, shell, parser, ast.
3. **Write public API skeleton** (§2): interface + record stubs that compile
   but throw `NotImplementedException` on `Parse()`. Lock the surface
   first.
4. **Implement BashLexer** (§5). Heavy unit tests on tokenization.
5. **Implement FILE / CWD verb tables and IsVerbLikeToken predicate** (§6) as static data + helper.
6. **Implement BashParser** (§4). One production at a time; unit-test
   each.
7. **Implement Resolver** (§8). Unit-test each resolution rule.
8. **Implement per-verb path-arg rules** (§7). Unit-test per verb.
9. **Implement cd-in-compound propagation** (§9). Unit-test.
10. **Implement subshell + bash -c recursion** (§10). Unit-test.
11. **Implement parser anomaly safe-fail** (§11). Unit-test.
12. **Author corpus** (§13) — start with 105 hand-authored entries
    covering each section. Iterate parser to make all pass.
13. **Sanitize and seed from real logs** (§14) — script + manual review.
    Add 50-100 more corpus entries.
14. **Wire CI** (§15). Tag `0.1.0-alpha` when the corpus is green and the PII audit
    passes.

Estimated implementation effort: 600-800 LOC of source + 400-600 LOC of
test infrastructure + 100-150 corpus entries (~50 KB JSON).

Post-v0.1.0 increments (e.g. v0.1.5 newline-as-statement-separator) are
sequenced through `IMPLEMENTATION_PLAN.md` — §16 records the one-time
v0.1.0 build order, not the ongoing changelog.

---

## 17. Acceptance Criteria

v0.1.0-alpha ships when **all** of the following hold:

1. ✅ Public API matches §2 exactly. `dotnet pack` produces a
   ShellSyntaxTree.0.1.0-alpha.nupkg.
2. ✅ Every corpus entry in `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` parses to its
   expected AST. `dotnet test` runs them all and passes.
3. ✅ Corpus has at least 105 entries spanning the categories in §13.
4. ✅ PII audit scan over `tests/ShellSyntaxTree.Tests/Corpus/bash/*.json` finds zero hits.
5. ✅ `dotnet test` runs on PR via GitHub Actions and passes.
6. ✅ Tagging `0.1.0-alpha` triggers `publish_nuget.yml` and the package
   appears on nuget.org.
7. ✅ Netclaw can consume the package via `<PackageReference>` and the
   `IShellParser` resolves at runtime in Netclaw's DI container.
8. ✅ At least one Netclaw integration test exercises a real corpus entry
   through the live Netclaw matcher and gets the expected gate decision.

---

## 18. Out of Scope

Stable v0.3 deliberately continues to exclude:

- Windows `cmd` parsing.
- Command execution, filesystem glob enumeration, runtime variable lookup, or
  live-shell evaluation.
- Executable-specific option, operand, object, revision, or subcommand
  grammars; consumers own those semantics.
- Bash `while`, `until`, `if`, `elif`, `else`, process substitution, single-`&`
  background lists, `case`, C-style or implicit positional-parameter loops,
  arithmetic execution, functions, and definitions until each hidden-execution
  and state boundary is specified.
- PowerShell `while`, `if`, `elseif`, `else`, `do`, `switch`, functions,
  definitions, class/type bodies,
  arbitrary execution-bearing expressions, and `.ps1` file-content parsing.
- A stable serialized wire format for the polymorphic v0.3 records.
- Caller-configurable analysis limits, filesystem-dependent pattern
  expansion, or unbounded value/state alternatives.
- Full IDE-style concrete syntax mapping. Exact-or-null source ranges exist
  for security correlation, not lossless editing.
- Performance optimization beyond "fast enough" (~1ms typical).
- Extensible verb-table loading from config.

---

## Appendix A: Consumer Contract (Netclaw)

What a v0.3 Netclaw-style security consumer expects from this library:

1. `IShellParser.Parse(string)` returns one `ParsedCommand`. If
   `IsUnparseable` is true or `Commands` is empty, authorization prompts or
   denies; neither partial `Syntax` nor raw-prefix inference can authorize.
2. The consumer evaluates every `CommandOccurrence`, including condition,
   iterator, branch, body, substitution, and pipeline-stage occurrences.
   `Syntax` may group the UI but is not the command-discovery API.
3. An incomplete occurrence, dynamic authored verb, unknown or unrecognized role,
   ancestry kind, value kind, redirect kind, or policy-sensitive fact prompts
   or denies. Unknown executable operands are never dropped to reuse a broader
   approval. Ambient runtime resolution does not by itself make a static
   authored occurrence incomplete; the approval covers the command text the
   user was shown.
4. For every exact or finite effective value, the consumer reapplies the
   shell's binding rules and the complete executable-specific grammar at the
   candidate's authored position. A finite shell proof is not authorization;
   option-like candidates remain option-like.
5. Every `RedirectAnalysis` is evaluated. Static descriptor operations are
   not paths. File targets are path-relevant. Complete heredoc/here-string
   bodies remain data unless executable-specific stdin policy makes that data
   sensitive; expanding-body substitutions appear as separate commands.
6. Hard-deny and protected-path rules still precede reusable grants. Stored
   approval never bypasses a hard deny, and every command occurrence is
   evaluated.
7. A v0.2 consumer may temporarily continue reading `Clauses`. The projection
   includes every authored simple command from supported nested syntax and
   retains dynamic authored operands, but it does not expose proved effective
   loop values. Migration to `Commands` is required for bounded reuse.

The contract is extend-only — additive records and members with safe defaults
are source and binary compatible; renaming, removing, or changing signatures
is breaking. Adding members changes generated record equality, hashing,
`ToString()`, and default reflection serialization. Consumers that persist
results own a versioned DTO or serializer mapping rather than treating the
in-memory hierarchy as a stable wire union.
Before v1.0.0, while the library is in its `0.x` line, a breaking AST change
MAY ship in a minor bump (e.g. the `Clause.IsCommandStringWrapped` →
`IsCommandStringWrapped` rename in v0.2.0) provided `RELEASE_NOTES.md`
documents the old→new mapping and the consumer (Netclaw) is updated in
lockstep. From v1.0.0 onward, renaming or removing a field requires a major
version bump.

---

## Appendix B: Why not tree-sitter-bash?

OpenCode (Node) uses tree-sitter-bash. We considered porting that approach
to .NET. The packaging cost is real:

- No first-class .NET tree-sitter binding. Community bindings exist but
  vary in maintenance.
- Native dependency: ship `libtree-sitter` + `libtree-sitter-bash` per
  platform (Linux x64, Linux arm64, macOS x64, macOS arm64, Windows x64).
  Five binaries to ship and maintain, plus PowerShell would need a
  separate native lib.
- AOT-trimming compatibility is uncertain.
- We don't need IDE-grade fidelity. Fork bombs and function definitions
  legitimately confuse our parser; we want them to mark `IsUnparseable`
  so the consumer routes to safe-fail. tree-sitter would parse them and
  we'd have to teach the consumer to ignore the result anyway.

The hand-rolled approach trades a higher ceiling for control over scope,
zero native deps, and a clean upgrade path to PowerShell via the same
`IShellParser` seam. For our use case, that trade is correct.
