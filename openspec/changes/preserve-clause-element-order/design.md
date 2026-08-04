## Context

Both parsers already retain exact lexer source spans and decoded token values.
Their verb passes then record verb token positions while their argument and
redirect passes build separate public projections. Once those lists are
finished, cross-list order cannot be reconstructed reliably.

Issue #62 requires shell syntax provenance, not Git semantics. The parser must
preserve where `-c` / `-C` occurred in the authored element stream. A Git-aware
consumer decides which element is the semantic subcommand and whether an
option changes cwd, overrides configuration, or reuses a commit message.

## Goals / Non-Goals

**Goals:**

- Preserve source order across verbs, arguments, and redirects.
- Identify each element's coordinate relative to parser-classified verb
  elements without presenting that coordinate as executable semantics.
- Preserve exact spelling and decoded value without consumer tokenization.
- Carry the existing argument/path facts needed by security policy.
- Keep existing public projections and cwd behavior source-compatible.
- Use one shell-neutral contract for Bash and PowerShell.

**Non-Goals:**

- Encode Git or another executable's option grammar.
- Replace the existing projections.
- Publish whitespace, comments, compound operators, grouping delimiters, or
  PowerShell's call operator as clause elements.
- Introduce recursive syntax nodes or composable public subtrees.
- Map decoded command-string payload characters back through quoting, escaping,
  or base64 into an outer wrapper source.

## Decisions

### Add an ordered leaf projection, not a recursive tree

`Clause.Elements` is an ordered list of significant source-authored leaves.
It solves the observed ambiguity without committing the v0.x API to a
general-purpose shell tree. A future tree can reuse this list as the leaf order
of a simple-command node. Authored order is authoritative; executable-specific
semantic interpretation remains consumer-owned.

### Use one verb-relative coordinate

Every element carries `PrecedingVerbElementCount`, the number of elements with
`Role=Verb` that appeared before it in the clause. For a verb element, that
value is also its zero-based index in `Clause.Verb.Tokens`.

`Role` mirrors the parser's existing projections, including the greedy
`Clause.Verb` heuristic. Therefore the count is an AST coordinate, not an
executable-specific subcommand boundary. For example, an unrecognized global
option may stop the greedy walk even though a later element is a Git
subcommand. A Git-aware consumer uses the complete ordered list, not this
count alone, to interpret the command.

This avoids redundant `VerbIndex` and `AfterVerbIndex` fields whose invariants
could drift.

### Match native option spelling case-sensitively

Native executables receive option spelling unchanged under both Bash and
PowerShell. Their option tables therefore use ordinal matching even though
PowerShell cmdlet parameter tables remain case-insensitive. Git explicitly
lists both `-c` (configuration value, not a path) and `-C` (directory path in
the generic global-option table).

This corrects pre-existing metadata drift. It does not make the shared parser
Git-semantic: a Git-aware consumer must still reinterpret `git commit -c/-C`
operands as revisions using authored order.

### Exclude synthetic cwd attribution

`Clause.Elements` contains authored syntax only. A cwd attribution appended to
`Clause.Args` has no source position in that clause and is therefore excluded.
Existing consumers continue to find it through `Arg.IsCwdAttribution`.

### Preserve source spans only when exact outer mapping exists

For an ordinary clause, `SourceStart` and `SourceLength` index the returned
`ParsedCommand.Source`. Clauses expanded from `bash -c`, `pwsh -Command`, or
`pwsh -EncodedCommand` retain their inner `Raw` and `Value`, but their spans are
null after expansion because escaping and decoding prevent a generally exact
mapping into the outer source.

Nullable spans are an uncertainty signal; the parser does not guess offsets.

### Model a redirect as one ordered semantic element

One redirect element spans its operator and target and occupies the same
ordinal among redirect elements as its corresponding `Clause.Redirects`
entry. `Raw` is the complete source slice, `Value` is the decoded target, and
the argument-like fields describe the target. A stream-merge redirect has
`Kind=DynamicSkip` and `IsPath=false`.

This preserves placement without adding redirect-operator and redirect-target
roles that do not exist in the current semantic projection.

### Keep inline binding forms as one source element

Forms such as `--work-tree=../repo` and `-Path:C:\repo` are one lexer token
and therefore one clause element. Their `Raw` and `Value` describe the full
token; `IsPath`, `Kind`, and `Resolved` describe the bound value when the
parser can classify it. Existing `Args` may continue splitting such a token
into multiple semantic arguments.

### Build elements in the classified token walk

The argument/redirect scan emits an element when it encounters each verb
position, argument token, or redirect. It must not zip or search the completed
`Verb`, `Args`, and `Redirects` lists. Repeated values and interleaved flags
remain deterministic because lexer positions are still present at emission.

## Risks / Trade-offs

- The element record duplicates some `Arg` facts. This is deliberate: inline
  forms are not always one-to-one with `Args`, and consumers should not need to
  correlate by string value.
- Nullable wrapped-command spans provide less provenance than direct clauses,
  but are safer than approximate outer offsets.
- Redirect `Raw` may contain whitespace between operator and target. That is
  the exact authored region and is suitable for display.
- `Clause` is a record, so adding `Elements` changes its generated value
  equality and hashing. Generated `ToString()` and default JSON serialization
  also include the new projection. The surface is source- and binary-additive,
  but it is behaviorally observable to consumers of those generated forms.
- Adding public types before stable `0.2.0` expands the API contract. Public API
  snapshot tests and both SPECs lock the shape.

## Migration Plan

The change is source- and binary-additive. Existing consumers continue using
`Verb`, `Args`, and `Redirects` unchanged. Position-sensitive consumers opt
into `Elements`; consumers relying on record equality, hashing, textual
snapshots, or default JSON output must account for the new projection.

A revert can remove the new projection before stable `0.2.0`; after release,
the normal public API compatibility rules apply.
