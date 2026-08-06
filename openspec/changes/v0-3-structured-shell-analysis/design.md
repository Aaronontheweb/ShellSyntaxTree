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

### Represent simple commands with existing Clause leaves

A simple-command syntax node wraps the same `Clause` value exposed through the
occurrence and compatibility projections. Existing `VerbChain`, `Arg`,
`Redirect`, and `ClauseElement` facts are not replaced.

New structural nodes preserve their complete source range when it can be mapped
exactly. Expanded wrapper content retains the current nullable-span rule rather
than inventing offsets into escaped or encoded outer text.

### Project authored command occurrences exactly once

`Commands` contains one entry per authored simple command that may execute,
not one entry per predicted runtime iteration. Each occurrence carries its
`Clause`, structural role, ancestry suitable for diagnostics, and an explicit
completeness fact. Roles include at least ordinary, pipeline stage, condition,
iterator, loop body, branch, and substitution; the final names are locked with
the public API review.

Condition and iterator commands are never omitted. Mutually exclusive branch
commands all appear because the collection is a may-execute set. Runtime loop
counts do not duplicate occurrences; bounded variable domains describe the
possible effective values at the occurrence.

If any executable region cannot be discovered completely, the containing
`ParsedCommand` remains `IsUnparseable=true`. Partial syntax and occurrences
may be returned for diagnostics but MUST NOT be used to authorize execution.

### Preserve Clauses as a conservative flattened view

For a fully parseable result, `ParsedCommand.Clauses` contains every authored
simple command occurrence in source order, including nested iterator,
condition, branch, body, and substitution commands. It does not invent
compound operators across structural boundaries. Existing `Clause.Operator`
values are retained only for actual authored relationships.

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

Effective values are shell facts, not executable semantics. A consumer must
re-run its executable-aware option grammar for every exact or finite candidate;
for example, a loop value beginning with `-` may inject an option even if the
authored `$variable` token was not option-shaped.

### Join state rather than selecting a path

The analysis carries abstract working-directory and supported variable state
through the structure. Sequential lists propagate state. Subshell or
scope-isolated groups do not leak state. Branches join their possible exit
states; loops include the zero-iteration path unless shell semantics prove at
least one iteration.

The first implementation may collapse any differing cwd states to `Unknown`
rather than publish a finite cwd set. Selecting one branch's directory is
never allowed. A later additive version may expose bounded cwd alternatives if
the consumer contract demonstrates a need.

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
2. Extract issue #69 and other behavior-preserving shared helpers.
3. Produce `Syntax`, `Commands`, and unchanged `Clauses` for existing grammar.
4. Validate the new consumer path on existing Netclaw cases.
5. Add Bash `for ... in` with literal values first.
6. Add PowerShell `foreach` with literal arrays next.
7. Extract shared occurrence/value/state machinery proven by both slices.
8. Add bounded patterns and iterator/substitution command discovery.
9. Add condition loops and branches in separately testable shell-specific
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
- **[Partial trees invite partial authorization]** -> Keep
  `IsUnparseable=true`, mark occurrences incomplete, and state that partial
  results are diagnostic only.
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
3. Ship the new structural and occurrence API in a 0.3.0 alpha while all
   existing grammar produces byte-for-byte equivalent compatibility facts.
4. Migrate Netclaw to `Commands` and explicit redirect facts before enabling
   supported control flow for authorization reuse.
5. Add Bash and PowerShell vertical slices behind corpus and integration gates.
6. Promote 0.3.0 only after both shells, old-consumer fail-closed behavior, and
   the new Netclaw consumer path pass their acceptance matrices.

Before stable 0.3.0, a flawed new surface can be revised with prerelease
migration notes. After stable release, removals or renames follow the normal
minor-version rule for this 0.x library. `Clauses` is not removed in 0.3.

## Open Questions

1. What are the exact public names and members for the syntax root, occurrence,
   ancestry, value-domain, and redirect-detail types?
2. What fixed candidate-count and nesting limits are small enough for security
   review while useful for agent-authored loops?
3. Should an occurrence refer to the identical `Clause` instance used by the
   syntax leaf and compatibility projection, or only guarantee value equality?
4. Which initial pattern facts can safely expose a covering directory when
   Bash unmatched-glob options are unknown?
5. Is any divergent cwd domain useful in v0.3, or should every disagreement
   immediately become `Unknown`?
6. Which heredoc forms, process substitutions, and background-list forms belong
   in 0.3 rather than subsequent additive releases?

## Appendix A: Non-Normative Candidate Public API

The following sketches make the design review concrete. They are deliberately
non-normative: task group 1 must reconcile names, default values, XML
documentation, serialization behavior, and the exact member set with the five
capability specifications before any public type is implemented.

### Structural node family

```csharp
namespace ShellSyntaxTree;

public abstract record ShellSyntaxNode
{
    // Prevent consumers from extending the parser-owned node family.
    private protected ShellSyntaxNode() { }

    public int? SourceStart { get; init; }
    public int? SourceLength { get; init; }
}

public sealed record ShellBlockSyntax : ShellSyntaxNode
{
    public IReadOnlyList<ShellSyntaxNode> Statements { get; init; } = [];
}

public sealed record SimpleCommandSyntax : ShellSyntaxNode
{
    public Clause Clause { get; init; } = new();
}

public sealed record PipelineSyntax : ShellSyntaxNode
{
    public IReadOnlyList<ShellSyntaxNode> Stages { get; init; } = [];
}

public sealed record CommandListSyntax : ShellSyntaxNode
{
    public IReadOnlyList<CommandListItemSyntax> Items { get; init; } = [];
}

public sealed record CommandListItemSyntax
{
    public CompoundOperator Operator { get; init; }
    public ShellSyntaxNode Command { get; init; } = new ShellBlockSyntax();
}

public sealed record GroupSyntax : ShellSyntaxNode
{
    public ShellGroupKind Kind { get; init; }
    public ShellBlockSyntax Body { get; init; } = new();
}

public sealed record ForEachSyntax : ShellSyntaxNode
{
    public LoopBindingSyntax Binding { get; init; } = new();
    public ShellValueExpressionSyntax Iterable { get; init; } = new();
    public ShellBlockSyntax IteratorCommands { get; init; } = new();
    public ShellBlockSyntax Body { get; init; } = new();
}

public sealed record ConditionLoopSyntax : ShellSyntaxNode
{
    public ConditionLoopKind Kind { get; init; }
    public ShellBlockSyntax Condition { get; init; } = new();
    public ShellBlockSyntax Body { get; init; } = new();
}

public sealed record ConditionalSyntax : ShellSyntaxNode
{
    public ShellBlockSyntax Condition { get; init; } = new();
    public ShellBlockSyntax Then { get; init; } = new();
    public IReadOnlyList<ConditionalBranchSyntax> ElseIf { get; init; } = [];
    public ShellBlockSyntax? Else { get; init; }
}
```

`ForEachSyntax` is shown as a shared execution-structure node, not a claim that
Bash words and PowerShell expressions share a grammar. If the contract review
shows that their iterable or binding facts cannot coexist without optional
members or semantic ambiguity, the public family should instead use
`BashForEachSyntax` and `PwshForEachSyntax` derived from a smaller common loop
base.

The same rule applies to `ConditionalSyntax`: share the shape only when the
public fields preserve every material shell distinction. Internal parse nodes
remain shell-specific regardless of the eventual public choice.

### Command occurrence and bounded values

```csharp
public sealed record CommandOccurrence
{
    public Clause Clause { get; init; } = new();
    public CommandOccurrenceRole Role { get; init; }
    public IReadOnlyList<CommandAncestryFrame> Ancestry { get; init; } = [];
    public IReadOnlyList<EffectiveArgument> EffectiveArguments { get; init; } = [];
    public ShellValueDomain WorkingDirectory { get; init; } = ShellValueDomain.Unknown;
    public IReadOnlyList<RedirectAnalysis> Redirects { get; init; } = [];
    public bool IsComplete { get; init; }
}

public enum CommandOccurrenceRole
{
    Ordinary,
    PipelineStage,
    Condition,
    Iterator,
    LoopBody,
    Branch,
    Substitution,
}

public sealed record EffectiveArgument
{
    // A stable authored coordinate is safer than correlating by string value.
    public int ClauseElementIndex { get; init; }
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
```

The candidate uses a source-authored element coordinate rather than attaching
derived values directly to `Arg`. This prevents a loop iteration from mutating
the compatibility leaf and provides a place for one authored token to have
multiple possible effective values. Contract review must still account for
redirect operands, inline option bindings, shell expansions that create more
than one argument, and occurrences that do not have an exact outer source span.

### Explicit redirect facts

```csharp
public sealed record RedirectAnalysis
{
    public int RedirectIndex { get; init; }
    public int? SourceDescriptor { get; init; }
    public RedirectOperation Operation { get; init; }
    public int? TargetDescriptor { get; init; }
    public ShellValueDomain Target { get; init; } = ShellValueDomain.Unknown;
    public bool IsPathRelevant { get; init; }
    public bool IsComplete { get; init; }
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

This sketch places occurrence-specific redirect analysis on
`CommandOccurrence` and leaves the existing `Redirect` record untouched. An
alternative is an additive `Redirect.Analysis` property. The contract review
should prefer the shape that avoids duplicated facts while preserving v0.2
equality and serialization expectations as far as an additive record change
allows.

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

The intended security-consumer shape is therefore:

```csharp
var parsed = parser.Parse(source);
if (parsed.IsUnparseable || parsed.Commands.Count == 0)
{
    return Prompt(parsed.UnparseableReason ?? "no complete command occurrences");
}

foreach (var occurrence in parsed.Commands)
{
    if (!occurrence.IsComplete || occurrence.Clause.Verb.IsDynamic)
    {
        return Prompt("command execution is not statically bounded");
    }

    var interpreted = executableGrammar.Interpret(occurrence);
    if (!interpreted.IsComplete)
    {
        return Prompt("executable arguments are ambiguous");
    }

    EvaluateEveryCandidate(interpreted);
}
```

## Appendix B: Non-Normative Grammar and Parser Mocks

These sketches show how the existing flat parsers can evolve. They are not a
replacement for the normative BNF that task group 1 adds to `SPEC.md` and
`SPEC.POWERSHELL.md`.

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

### Candidate Bash grammar delta

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
                      | bash_group
                      | bash_subshell
                      | bash_c_wrapper
                      | bash_simple_command

// Initial v0.3 tracer bullet: explicit `in` form only.
bash_for_in          := "for" binding_name "in" iterable_word*
                        list_terminator "do"
                        bash_script(stop = "done")
                        "done"

list_sep             := ";" | NEWLINE
list_terminator      := ";" | NEWLINE+
binding_name         := shell_identifier
iterable_word        := word | quoted_string | supported_substitution
```

Later Bash deltas add `while` / `until`, `if` / `elif` / `else`, and `case`
using explicit stop-keyword sets. C-style `for ((...))`, implicit `for name`
iteration over positional parameters, arithmetic expansion, and substitutions
whose inner commands cannot be discovered remain rejected until separately
specified.

The current Bash lexer already emits `for`, `in`, `do`, and `done` as `Word`
tokens. The first slice therefore does not require dedicated keyword token
kinds. The structural parser interprets them contextually and preserves the
existing lexer values and spans.

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

All `Expect*` failures return one outer unparseable result. They do not skip to
`done` and return a partial tree that could be mistaken for authorization
evidence.

### Candidate PowerShell grammar delta

PowerShell retains its statement-versus-pipeline distinction and contextual
keyword rules. In particular, `foreach` is a language keyword only at a
statement position when followed by `(`; `Get-ChildItem | foreach { ... }`
continues to treat `foreach` as command or alias syntax.

```text
pwsh_script(stop)    := pwsh_statement (statement_sep pwsh_statement)*
pwsh_statement       := pwsh_foreach
                      | pwsh_pipeline

pwsh_foreach         := "foreach" "(" variable "in" foreach_expression ")"
                        script_block_body

// Initial v0.3 tracer bullet only.
foreach_expression   := literal_value
                      | literal_array
                      | pipeline_expression
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

internal abstract record PwshNode(SourceRange Range);
internal sealed record PwshForEachNode(
    PwshBinding Binding,
    PwshExpression Iterable,
    PwshBlockNode Body,
    SourceRange Range) : PwshNode(Range);
```

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
| Missing Bash `do` or `done` | Parse failure with the offending range | `IsUnparseable=true`; partial nodes diagnostic only |
| Bash keyword used as an argument | Existing simple-command leaf | No false control-flow node |
| Unsupported Bash substitution in an iterable | Inner commands surfaced only if completely parsed | Otherwise the entire result is unparseable |
| PowerShell `foreach` at statement position followed by `(` | `PwshForEachNode` | Iterator and body occurrences exposed |
| PowerShell `foreach` in a pipeline command slot | Existing pipeline/simple-command path | Alias or command semantics preserved |
| PowerShell statement body `ScriptBlock` | Interior recursively parsed with adjusted spans | Every body command exposed |
| PowerShell script block used as an ordinary argument | Existing opaque argument | `DynamicSkip`; contents are not invented as executed commands |
| Candidate cap or state-join overflow | Structure remains parseable | Affected effective fact becomes `Unknown` |
| Any executable region is skipped or cannot be delimited | Partial diagnostic tree allowed | `IsUnparseable=true`; no authorization from the subset |
