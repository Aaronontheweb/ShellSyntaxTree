# v0.3 Design Corpus

This directory is a pre-implementation contract corpus for the
`v0-3-structured-shell-analysis` OpenSpec change. It does not feed the shipped
v0.2 AST runner because that schema cannot represent nested syntax, command
occurrences, effective values, or joined state.

Each case records these independent views:

1. `current`: the behavior the released flat parser produces today;
2. `syntax`: the desired authored structural nodes and their parent slots;
3. `commands`: every authored command that may execute, exactly once;
4. `effectiveValues`: bounded shell facts without executable policy claims;
5. `securityInvariants`: properties every future implementation and consumer
   must preserve.

The accompanying tests deserialize with unknown-member rejection, validate
node and command references, compare `current` with the real v0.2 parser, and
include these JSON files in the repository PII audit. A case moves into
`Corpus/<shell>/` only after the corresponding public API and parser behavior
exist and its full expected AST can pass the normal corpus runner.

The design corpus is intentionally shell-specific. Similar Bash and PowerShell
cases may share structural expectations while retaining different quoting,
expression, option-binding, object, and scope semantics.

Each command records one `immediateRole` plus its full `ancestry`. These are
deliberately separate: a pipeline stage nested inside a loop body is immediately
a pipeline stage and still carries the enclosing loop-body context. Likewise,
`isComplete` describes command discovery and structure, not value precision; a
complete occurrence may contain an `Unknown` effective value.

The v0.3 contract fixes the candidate cap at 32. Each shell has a case at the
cap and a 33-value overflow case that collapses to `Unknown` rather than
publishing a truncated finite set. Supported heredocs and Bash here strings
record data separately from executable substitutions and path-relevant
redirects. Constructs deliberately deferred beyond stable v0.3 remain in the
corpus as unparseable security boundaries.

Resolver-provenance cases additionally pin selected current and desired v0.2
compatibility leaves. Besides `Arg`, a case may assert clause argument,
redirect, and element counts plus one complete `Redirect` and `ClauseElement`
shape. This records the v0.2 false path claim until its production slice lands
and proves that a corrected redirect does not leave a suffix argument behind.
The desired effective value records the literal shell value proved by the
paired real-shell oracle. Those cases then move into the executable corpus
instead of being treated as behavior that issue #69 must preserve.

The provenance matrix deliberately separates shell value formation from path
consumer semantics. PowerShell cases use identical quoted values across native
file operands, cmdlet `Path`, and cmdlet `LiteralPath` positions to prove that
tilde, wildcard, provider, and PSDrive behavior cannot be recovered from the
decoded string alone. Runtime-variable cases remain `Unknown`; incomplete
braced interpolation makes the whole parse unparseable. Paired redirect cases
require adjacent fragments to become one redirect target rather than a target
prefix plus an unrelated argument.
