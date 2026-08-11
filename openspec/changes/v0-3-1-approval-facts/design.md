## Context

The sanitized harvest is stored in `evidence/approval-matrix.json`. D02 and D10
contain an embedded status expansion, not a standalone numeric argument. D14
contains a static loop whose complete path word is finite from authored source.

ShellSyntaxTree 0.3.0 already produces a finite effective value for D14-like
input when the caller proves `IsolatedNonInteractive`. Netclaw cannot make that
runtime assertion because it inherits ambient process state. Default `Unknown`
therefore withholds the effective value. Relabeling the source result as an
effective value would be false in the presence of ambient Bash nameref or
integer attributes.

The contract needs two independent axes:

1. the bounded value language; and
2. the provenance of the proof.

## Goals / Non-Goals

**Goals:**

- Represent canonical bounded integers and bounded string concatenation.
- Keep the existing effective-value contract unchanged.
- Expose a separate authored word value before field splitting.
- Expose lexical path shape without claiming executable operand semantics.
- Preserve conservative effective behavior for identities, redirects,
  substitutions, unquoted field splitting, ambient variables, and explicit
  state mutation.
- Keep the public API additive and closed to consumer construction.
- Pin exact sanitized evidence and adversarial boundaries.

**Non-Goals:**

- Assert that authored-source values are runtime-effective values.
- Change default parser behavior for existing consumers.
- Parse an executable's private options or operands.
- Reimplement finite loop-fragment composition already shipped in 0.3.0.
- Execute commands, inspect ambient variables, or enumerate the filesystem.
- Add Bash `if`, `while`, `case`, implicit loops, or process substitution.
- Change PowerShell language semantics.

## Decisions

### 1. Extend the bounded value language

The exact additive API is:

```csharp
public abstract record ShellValueDomain
{
    private protected ShellValueDomain() { }
    private protected abstract object LibraryOwnership { get; }

    public sealed record IntegerRange : ShellValueDomain
    {
        internal IntegerRange(long minimumInclusive, long maximumInclusive)
        {
            if (minimumInclusive > maximumInclusive)
                throw new ArgumentOutOfRangeException(nameof(minimumInclusive));
            MinimumInclusive = minimumInclusive;
            MaximumInclusive = maximumInclusive;
        }
        private protected override object LibraryOwnership => this;
        internal override ShellValueDomainKind InternalKind =>
            ShellValueDomainKind.IntegerRange;
        public long MinimumInclusive { get; }
        public long MaximumInclusive { get; }
    }

    public sealed record Concatenation : ShellValueDomain
    {
        internal Concatenation(IReadOnlyList<ShellValueDomain> parts) =>
            Parts = PublicCollection.Copy(parts);
        private protected override object LibraryOwnership => this;
        internal override ShellValueDomainKind InternalKind =>
            ShellValueDomainKind.Concatenation;
        public IReadOnlyList<ShellValueDomain> Parts { get; }
    }
}
```

The internal `ShellValueDomainKind` appends `IntegerRange` and `Concatenation`.
The internal factories enforce all part-shape and normalization rules before
they call these constructors. The public surface has no consumer constructor or
setter.

`IntegerRange` is inclusive. Every represented value uses canonical signed
ASCII decimal: `0`; a nonzero digit followed by zero or more digits; or `-`
followed by a nonzero digit and zero or more digits. A plus sign and leading
zeros are not represented. Bash status is the nonnegative `0..255` subset.

`Concatenation` is a bounded string-language upper bound. It contains two
through 16 normalized parts. Parts may be `Exact`, `FiniteSet`, or
`IntegerRange`. `Unknown`, `PathPattern`, and nested `Concatenation` are
rejected. Construction removes empty exact parts, merges adjacent exact parts,
and collapses an all-exact result to `Exact`. If normalization leaves one
non-exact part, the factory returns that part instead of `Concatenation`. More
than 16 normalized parts produces `Unknown`; it never truncates. Repeated
dynamic parts are conservatively independent. The language can include values
that Bash cannot produce, but it never omits an actual value.

The existing 32-member finite-set cap still applies to `FiniteSet`. A
concatenation is bounded by its typed representation rather than by enumerating
its Cartesian product.

### 2. Publish only quoted Bash status values

Double-quoted `$?` is one shell field and has `IntegerRange(0, 255)`. Literal
prefixes and suffixes create `Concatenation`.

For example, `echo "---EXIT $?---"` produces:

```text
Concatenation([
  Exact("---EXIT "),
  IntegerRange(0, 255),
  Exact("---")
])
```

Unquoted `$?` remains unknown because ambient `IFS` can split its decimal
characters into multiple fields. Status in command identity or redirect target
position remains strict. Single-quoted `$?` is the exact literal `$?`.

### 3. Separate effective values from authored word values

`AnalyzedArgument.Value` retains its released meaning: effective shell-value
proof under the selected initial-state contract.

Two additive members are appended:

```csharp
public sealed record AnalyzedArgument
{
    internal AnalyzedArgument() { }
    public Arg Argument { get; internal init; } = null!;
    public ClauseElement Element { get; internal init; } = null!;
    public ShellValueDomain Value { get; internal init; } = null!;
    public ShellValueDomain AuthoredValue { get; internal init; } = null!;
    public ShellPathShape AuthoredPathShape { get; internal init; }
}

public enum ShellPathShape
{
    Unknown = 0,
    Posix = 1,
    Windows = 2,
}
```

`AuthoredValue` is the bounded shell word that source proves before field
splitting and pathname expansion. It applies quote removal and bounded source
bindings. It does not apply unobserved ambient variable attributes or ambient
`IFS`. It is not an argv claim or a runtime-value prediction. For arguments
where this distinction does not matter, `AuthoredValue` equals `Value`.

`AuthoredPathShape` describes lexical form only. Classification precedence for
each represented word is deterministic:

1. a URI-shaped word matching `^[A-Za-z][A-Za-z0-9+.-]*://` is `Unknown`;
2. a drive, UNC, or backslash-bearing form is `Windows`;
3. an absolute, `./`, `../`, eligible tilde, or slash-bearing form is `Posix`;
4. every other form is `Unknown`.

Thus `C:/work/a` is `Windows`, not `Posix`. A domain publishes a known shape
only when every word it represents proves the same known shape. Mixed shapes,
any partially unknown member, or a symbolic concatenation for which one shape
cannot be proved produces `Unknown`. For example, `Exact("/tmp/")` followed by
an integer range is provably `Posix`; a finite set containing `/tmp/a` and
`C:\\work\\b` is `Unknown`.

ShellSyntaxTree does not claim that an executable treats the word as a
filesystem operand. A repository slug, container image, API route, or
slash-bearing data can have a path shape. `Unknown` means no uniform lexical
path-shape proof. Netclaw can use shape to require more path review, but never
as filesystem authority.

### 4. Make authored-source publication an explicit opt-in

Append this option with a default of `false`:

```csharp
public sealed record BashParserOptions : ShellParserOptions
{
    public BashInitialStateMode InitialStateMode { get; init; }
    public bool PublishAuthoredSourceFacts { get; init; }
}
```

With the default `false`, static loops under `BashInitialStateMode.Unknown`
retain the released `IsUnparseable=true`, empty `Commands`, and empty `Clauses`
result. Existing consumers see no behavior change.

When `PublishAuthoredSourceFacts=true`, a supported static loop that fails only
the ambient attribute or field-splitting proof can publish:

```text
Value         = Unknown
AuthoredValue = FiniteSet(...)
```

The occurrence is structurally complete only for authored source. The option
does not assert runtime isolation. `IsolatedNonInteractive` continues to
publish the finite effective set and does not require the option.

Explicit source-level nameref/integer declarations, `eval`, `source`, dynamic
identity, command substitution, and unsupported control flow keep the affected
region strict. An unassigned ambient name has `Unknown` for both properties.

This removes the proposed `AuthoredCommand` mode. The option requests an
additional conservative source projection. It does not select a policy or
relabel the result as effective. Netclaw decides whether it can consume that
projection.

### 5. Reuse shipped finite composition

The implementation reuses the existing per-visit correlation and fragment
composition. It evaluates the authored word before field splitting when the
new option is enabled. It does not add a second composition engine.

The valid tilde example is `cat ~/"repo/$f"`. `cat "~/repo/$f"` keeps a literal
tilde, matching native Bash.

### 6. Preserve the consumer boundary

ShellSyntaxTree reports structure, value domains, proof provenance, lexical
path shape, redirects, and completeness. It never labels an argument safe
or grants authority. Netclaw decides whether its approval model accepts an
authored-source proof, then applies hard deny, protected paths, stored grants,
safe policy, and execution containment.

An old consumer's default switch arm fails closed on the new domain records.
An existing consumer sees no loop-admission relaxation unless it sets the new
option.

PowerShell has exact compatibility behavior in 0.3.1. Literal, dynamic, path,
and provider arguments set `AuthoredValue` equal to their existing `Value`.
`AuthoredPathShape` is `Unknown` for all PowerShell arguments in this release.

### 7. Use the exact sanitized catalog

`evidence/approval-matrix.json` is byte-identical to the paired Netclaw
artifact. It contains all 18 deduplicated prompts, exact sanitized source,
observed response class, owner, and expected result. D02, D10, and D14 become
ShellSyntaxTree regression inputs. The other rows remain cross-repository
classification evidence.

## Risks / Trade-offs

- **A consumer mistakes authored source for runtime truth.** The API uses a
  separate property; `Value` remains unchanged; the guide requires an explicit
  policy decision before consuming `AuthoredValue`.
- **Concatenation can denote many strings.** The representation is capped,
  immutable, nonrecursive, and never silently enumerated as a finite set.
- **Path shape is not operand semantics.** Netclaw uses it only to add strict
  path review. It never creates filesystem authority.
- **Opt-in loop admission changes three released signals.** The guide lists
  `IsUnparseable`, `Commands`, and `IsComplete`. Default options retain all
  released results.
- **Additive records affect reflection and serialization.** Results are not a
  stable wire format; API snapshots and release notes make the change explicit.

## Migration Plan

1. Approve the public names, opt-in behavior, and Netclaw's use of authored
   pre-field-splitting word facts.
2. Update `SPEC.md` and API snapshots before production implementation.
3. Add value-domain projection and quoted-status tests.
4. Reuse loop analysis to publish `AuthoredValue` and lexical path shape.
5. Add exact sanitized corpus and native Bash oracle cases.
6. Update the consumer guide and release notes.
7. Build, test, pack, PII-audit, and publish 0.3.1 by tag.
8. Upgrade Netclaw only after its paired policy change is ready.

Rollback restores 0.3.0. No persisted format contains these parser records.

## Open Questions

- Maintainer approval is required for `AuthoredValue`, `ShellPathShape`,
  `AuthoredPathShape`, `PublishAuthoredSourceFacts`, `IntegerRange`, and
  `Concatenation` before implementation.
- Maintainer approval is required for Netclaw to use a pre-field-splitting word
  fact. That approval boundary excludes ambient attributes, ambient `IFS`, and
  field splitting while effective facts remain strict for execution and deny.
