## Context

`CommandOccurrence.WorkingDirectory` is an incoming-state fact. It can say that
`head file` starts in `/tmp`, but it cannot say whether an earlier occurrence
preserves or changes that directory. A consumer that wants to reason about
`cd /tmp && inspect; head file` currently has two bad choices: keep the whole
call strict, or maintain its own list of shell builtins.

The latter fails for `pushd`, `popd`, `command cd`, `builtin cd`, PowerShell
location cmdlets, current-scope executable regions, and future grammar. It also
crosses the repository boundary: ShellSyntaxTree owns syntax and abstract shell
state; Netclaw owns authorization.

Both abstract-state analyzers already retain incoming state and separate normal
success and failure flows. They do not preserve the relational reason why two
unknown output states are equal. A public output-domain pair would therefore
conflate “proved unchanged” with “may have changed to an unknown directory.”

The v0.3.3 public API is stable. This slice must be extend-only and fail closed.
It must also correct one v0.3 occurrence-analysis error: Bash has no `chdir`
builtin. The v0.2 compatibility leaf may retain its legacy attribution, but
the v0.3 occurrence flow must not claim that Bash `chdir` changes its parent
shell directory.

## Goals / Non-Goals

**Goals:**

- Publish a relational, parser-owned cwd effect for every command occurrence.
- Distinguish proved preservation from a proved success-only transfer and an
  unknown effect.
- Preserve bounded targets through loop visits and shell-specific resolution.
- Cover Bash and native PowerShell with one public vocabulary.
- Let security consumers reject unmodeled mutations without command-name
  parsing.
- Preserve all v0.3.3 public signatures and compatibility leaf shapes.

**Non-Goals:**

- Authorize commands, directories, redirects, or trust zones.
- Predict ambient aliases, functions, modules, profiles, `PATH`, or executable
  implementation behavior.
- Model the Bash or PowerShell directory stack in this slice.
- Prove that a transfer succeeds at runtime or that its target exists.
- Replace `CommandOccurrence.WorkingDirectory` or legacy cwd attribution.
- Infer effects for incomplete commands, hidden executable regions, or
  unsupported control flow.

## Decisions

### 1. Add a closed relational effect family

The public API gains one property and one library-owned record family:

```csharp
public sealed record CommandOccurrence
{
    // Existing members remain unchanged.
    public ShellWorkingDirectoryEffect WorkingDirectoryEffect
        { get; internal init; } = new ShellWorkingDirectoryEffect.Unknown();
}

public abstract record ShellWorkingDirectoryEffect
{
    private protected ShellWorkingDirectoryEffect() { }
    private protected abstract object LibraryOwnership { get; }

    public sealed record Unknown : ShellWorkingDirectoryEffect
    {
        internal Unknown() { }
        private protected override object LibraryOwnership => this;
    }

    public sealed record Unchanged : ShellWorkingDirectoryEffect
    {
        internal Unchanged() { }
        private protected override object LibraryOwnership => this;
    }

    public sealed record ChangesOnSuccess : ShellWorkingDirectoryEffect
    {
        internal ChangesOnSuccess(ShellValueDomain target) { ... }
        private protected override object LibraryOwnership => this;
        public ShellValueDomain Target { get; }
    }
}
```

`Unchanged` means every parser-modeled normal success and failure exit retains
the occurrence's incoming shell-scope directory. `ChangesOnSuccess` means every
modeled failure exit retains the incoming directory and every modeled success
exit takes `Target`. `Unknown` means the parser cannot prove either relation.

This is more truthful than exposing only `OnSuccess` and `OnFailure` domains.
When the incoming cwd is unknown, two output `Unknown` domains do not prove
preservation. An enum plus a nullable target was rejected because invalid
state combinations would be publicly representable. A closed record family
also gives consumers a default fail-closed arm for later alternatives.

### 2. Keep the effect authored and scope-relative

The fact describes the effect of authored shell syntax under the selected
grammar, dialect, parse options, and modeled source-level state. It does not
claim which ambient function, alias, module, profile, or executable the runtime
selects. This matches `CommandOccurrence.IsComplete` and the existing v0.3
threat-model boundary.

The relation is local to the occurrence's execution scope. A Bash `cd` inside
a subshell can be `ChangesOnSuccess` for that subshell while ancestry proves
that the subshell does not mutate the parent. A PowerShell in-process execution
region can affect its containing runspace. Consumers must evaluate ancestry,
region timing, and completeness together with the effect.

`AuthoredWorkingDirectoryEffect` was considered as the type name. The shorter
name is preferred because all `CommandOccurrence` facts already share the
authored-syntax boundary. XML documentation and the consumer guide state the
boundary explicitly.

### 3. Reuse `ShellValueDomain` for successful targets

`ChangesOnSuccess.Target` uses the existing value family. This release permits
only `Unknown`, `Exact`, and `FiniteSet` targets:

- `Exact` is one normalized absolute directory.
- `FiniteSet` is a bounded set of normalized absolute directories across
  abstract visits.
- `Unknown` proves a success-only directory mutation but not its destination.

`IntegerRange`, `Concatenation`, `PathPattern`, null, or an unrecognized domain
make the entire effect `Unknown`. A target remains a syntax fact; it does not
prove existence, accessibility, or runtime success.

### 4. Derive effects from per-occurrence transfer facts

Each language analyzer records an internal effect while it computes a simple
command's success and failure flow. It must not reconstruct the relation later
by comparing output cwd strings. The internal join is:

```text
Unchanged + Unchanged                         -> Unchanged
ChangesOnSuccess(A) + ChangesOnSuccess(B)     -> ChangesOnSuccess(join(A, B))
Unknown + anything                            -> Unknown
Unchanged + ChangesOnSuccess(...)             -> Unknown
invalid or over-limit target join             -> Unknown
```

The analyzer copies the joined internal fact into `CommandOccurrenceFacts`.
The public projector validates it and returns `Unknown` for any invalid or
missing combination. Default structural facts are `Unknown`, never
`Unchanged`.

This preserves correlation across loop visits. It also avoids a false
`Unchanged` result when both the incoming and outgoing domains happen to be
unknown.

### 5. Define the Bash boundary from Bash grammar

The Bash analyzer publishes:

- `Unchanged` for complete ordinary simple commands that contain no
  parser-known current-scope cwd mutation;
- `ChangesOnSuccess(Exact|FiniteSet)` for a modeled `cd` target;
- `ChangesOnSuccess(Unknown)` for a modeled `cd` whose successful destination
  is not bounded;
- `Unknown` for `pushd`, `popd`, current-scope hidden execution, or another
  accepted effect the grammar cannot bound.

`source`, `.`, `eval`, and the existing execution-bearing builtin family keep
their locked atomic parse failure. They produce no command occurrence and do
not gain a working-directory effect.

Exact `command cd` and `builtin cd` use the same cwd-transfer grammar as `cd`.
Invalid static `cd` argument shapes that cannot have a successful transfer and
retain cwd on failure are `Unchanged`.

Bash `chdir` is an ordinary command name, not a builtin. The v0.3 occurrence
flow and new effect treat it as `Unchanged`. The compatibility `Clauses`
projection may retain its historical synthetic attribution for v0.2 consumers;
the guide labels that leaf unsuitable for v0.3 authorization.

### 6. Define the PowerShell boundary from selected-dialect grammar

The native PowerShell analyzer publishes:

- `Unchanged` for a complete command with no parser-known current-runspace
  location mutation;
- `ChangesOnSuccess(Exact|FiniteSet)` for a bounded `Set-Location` target;
- `ChangesOnSuccess(Unknown)` for a modeled `Set-Location` with an unbounded
  successful target;
- `Unchanged` for a statically invalid `Set-Location` parameter or operand
  shape whose only modeled exit is an unchanged failure;
- `Unknown` for `Push-Location`, `Pop-Location`, an unmodeled script or
  execution-region effect, provider ambiguity, or invalidated command state.

Nested occurrences inside a fully decoded current-runspace execution region
retain their own effects. The host occurrence is `Unknown` when any reachable
nested effect can change location. The host cannot copy a nested
`ChangesOnSuccess` relation because nested code can change location and then
make the host fail. A host whose receiver and all reachable nested effects are
proved `Unchanged` may remain `Unchanged`.

Aliases canonicalized by the selected dialect (`cd`, `chdir`, and `sl`) share
the `Set-Location` fact. One parameter-binding pass produces both flow and the
relation. It recognizes selected-dialect common parameters, aliases, and their
unambiguous prefixes. Bash does not inherit these PowerShell aliases.

### 7. Keep authorization and causal fallback in Netclaw

The parser does not decide that a transition or later action is safe. A
Netclaw-style consumer may use the fact only after it:

1. requires a complete parse and complete occurrences;
2. checks the effect and target with a default-deny switch;
3. evaluates every prerequisite occurrence and redirect;
4. evaluates the intended target and every real fallback directory through
   path policy;
5. requires ancestry that preserves the relevant shell scope; and
6. applies one-time, session, stored, or reviewed-safe authority independently.

For example, `cd /tmp && inspect; head private.log` has a possible original-cwd
fallback when `inspect` or `cd` does not establish the intended causal path.
ShellSyntaxTree supplies effects and occurrence flow. Netclaw remains
responsible for checking all reachable scopes before authorization.

### 8. Preserve bounded loop effects only under a proved initial state

An isolated non-interactive Bash analysis may join exact `cd` targets across
finite loop visits. Authored-only analysis under an unknown initial state keeps
a tracked loop-derived cwd target unbounded. This slice does not add a second
authored transfer projection or repeat field-splitting, globbing, variable-
attribute, and environment proofs already owned by authored filesystem facts.

Thus a finite `cd "$d"` loop is positive under
`BashInitialStateMode.IsolatedNonInteractive`. The same loop under default
unknown initial state with only `PublishAuthoredSourceFacts=true` publishes a
top-level unknown effect.

### 9. Ship as an additive v0.x release

No existing public member changes. The new property participates in record
equality, hashing, `ToString()`, reflection, and default serializer output.
Parser results are still an in-memory typed API, not a stable wire format.

The API snapshot, package diff, `SPEC.md`, `SPEC.POWERSHELL.md`, consumer guide,
and corpus all change together. Netclaw upgrades only after the public package
passes its causal-directory regressions.

## Risks / Trade-offs

- **Risk: equality changes surprise consumers.** → Document the additive
  record member and compare public APIs against 0.3.3.
- **Risk: an unknown incoming cwd looks unchanged.** → Track the relational
  fact directly; never infer it from domain equality.
- **Risk: ambient functions can mutate cwd.** → Keep ambient runtime resolution
  outside the authored-syntax contract and require consumers to accept that
  existing boundary explicitly.
- **Risk: a hidden region mutates cwd.** → Publish `Unknown` unless its current-
  scope transfer is fully modeled.
- **Risk: Bash and PowerShell aliases leak across languages.** → Derive through
  the selected grammar and dialect, with cross-language negative tests.
- **Trade-off: directory-stack commands remain unknown.** → This is safe and
  general. A later stack model may refine them without consumer changes.
- **Trade-off: legacy Bash `chdir` attribution remains in `Clauses`.** → Preserve
  v0.2 compatibility while correcting the v0.3 security projection and guide.

## Migration Plan

1. Lock the additive API and semantic tables in the shared and PowerShell
   specifications.
2. Add internal effect facts and fail-closed public projection.
3. Extend Bash and PowerShell transfer analysis and joins.
4. Add unit, corpus, public API, consumer, and adversarial regressions.
5. Run Release build, full tests, headers, package, API diff, and native CI.
6. Publish the next additive v0.x package.
7. Replace Netclaw's paused command-name logic with the parser-owned fact.

Rollback requires no data migration. Consumers can ignore the property or pin
0.3.3. Netclaw must retain strict causal behavior until it upgrades.

## Open Questions

None. Directory-stack refinement and richer outcome relations are later,
separately specified capabilities.
