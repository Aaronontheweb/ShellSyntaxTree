## Context

`PwshCommandParser` already expands `pwsh -Command` and
`pwsh -EncodedCommand`, marks surfaced clauses as command-string wrapped, and
enforces a 64 KiB input cap plus a depth-five recursion cap.
`Invoke-Expression` currently parses as an ordinary cmdlet or alias clause.
That leaves a static destructive payload hidden from verb-based security
policy and permits a persistent approval for the wrapper to authorize later
payloads.

Unlike a child `pwsh` process, `Invoke-Expression` executes in the caller's
scope. Its payload inherits the current PowerShell location, and a
`Set-Location` inside the payload affects commands that follow outside it.
The public AST is locked, so the implementation must express uncertainty
through the existing `DynamicSkip` and `IsUnparseable` signals.

## Goals / Non-Goals

**Goals:**

- Surface clauses from provably static `Invoke-Expression` and `iex`
  payloads.
- Prevent variables, interpolation, concatenation, subexpressions, pipeline
  input, and other computed expressions from producing a clean approval
  shape.
- Preserve location state into and out of static expression recursion.
- Reuse one input-size and recursion-depth budget for every PowerShell
  command-string execution construct.
- Keep the public API unchanged.

**Non-Goals:**

- Evaluate PowerShell expressions, variables, concatenation, arrays, or
  pipeline values.
- Parse script blocks passed to `Invoke-Expression` as static strings.
- Change the fresh-process scope rules for `pwsh -Command` or
  `pwsh -EncodedCommand`.
- Add source positions or a new public uncertainty type.

## Decisions

### Recognize the canonical command identity

The parser will identify the full `Invoke-Expression` cmdlet name and the
`iex` alias through one canonical-verb check. The behavior will not depend on
the spelling used by the caller.

### Require one statically provable scalar string

A payload is static only when binding produces exactly one scalar string
token and the token needs no PowerShell expression evaluation. Accepted
forms are a single-quoted string, a literal here-string, a bare non-dynamic
word, or an expandable string/here-string for which the lexer observed no
unescaped interpolation.

Multiple tokens, script blocks, subexpressions, splats, arrays, and operators
are computed expressions even when their parts happen to be literals. The
parser will not concatenate or otherwise evaluate them.

The lexer will add internal interpolation provenance to quoted-string tokens.
This distinguishes ``"`$name"`` from `"$name"` after escape processing.
Searching the processed token value for `$` was rejected because it would
misclassify escaped literal dollar signs and lose the proof the lexer already
has while scanning source text.

### Use DynamicSkip when the dynamic source is observable

For a direct computed payload, the parser will preserve the outer
`Invoke-Expression` verb and collapse the complete payload source slice into
one `Arg { Kind = DynamicSkip, IsPath = false, Resolved = null }`. Returning
the current collection of literal-looking expression fragments was rejected
because a consumer could mistake it for a stable approval shape.

Computed code can call `Set-Location` in the current scope. The parser will
therefore mark the shared location context dynamic after a computed payload,
preventing later relative paths from resolving against stale attribution.

Pipeline-only, missing, or ambiguously bound payloads will mark the outer
`ParsedCommand` unparseable. A synthetic `<pipeline-input>` argument was
rejected because `Arg.Raw` promises a verbatim source token.

Any incoming pipeline makes the expression invocation dynamic, even when an
explicit literal argument is also present, because runtime pipeline values
can contribute additional invocations.

### Expand static payloads in place

For a static payload, the outer expression clause is consumed and the inner
clauses are inserted in its place. Every inner clause has
`IsCommandStringWrapped = true`; the first inner clause inherits the operator
that preceded the outer clause. This matches the existing command-string
visibility contract without adding an expression-specific AST property.

### Share location context for Invoke-Expression only

`ParseInternal` will accept an internal location context. Top-level parsing
creates it, `Invoke-Expression` passes the same instance into the inner parse,
and child `pwsh` recursion continues to create an isolated context.

Sharing the context gives static expression payloads both required
directions of current-scope behavior:

- Relative paths inside the payload resolve against the caller's effective
  location.
- A `Set-Location` inside the payload updates attribution for following outer
  clauses.

The surfaced inner clauses receive attribution while they are parsed; the
outer expansion must not attach it a second time.

### Share one recursion counter and input cap

`Invoke-Expression`, `pwsh -Command`, and `pwsh -EncodedCommand` will all
increment the existing PowerShell command-string recursion counter. A mixed
chain therefore cannot bypass the depth-five cap by alternating wrapper
types. Every static payload is parsed through `ParseInternal`, which applies
the existing 64 KiB character cap before lexing.

## Risks / Trade-offs

- **Bare words can differ from quoted strings under full PowerShell binding.**
  The static form is limited to one non-dynamic token; any multi-token or
  operator-bearing shape safe-fails.
- **Current-scope location mutation is more coupled than child-process
  recursion.** The shared context is internal and used only for
  `Invoke-Expression`; tests will pin isolation for child `pwsh` recursion.
- **Conservative rejection can cause additional prompts.** This is the
  preferred failure mode for a security parser; false negatives are
  recoverable and false approvals are not.
- **The outer `Invoke-Expression` verb disappears for static payloads.**
  `IsCommandStringWrapped` preserves wrapper provenance, while consumers see
  the executable inner verbs they need to gate.

## Migration Plan

No consumer migration is required. The change is additive parser behavior
within the existing AST. A package rollback restores the prior behavior.

## Open Questions

None. The safe-fail representation, static proof boundary, location scope,
and shared resource limits are locked by this change.
