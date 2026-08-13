## Context

`AnalyzedArgument.Argument.IsPath` is part of the v0.2 compatibility leaf. It
uses broad heuristics so old consumers can find likely paths. Those heuristics
also classify values such as an interpreter payload, a numeric count, a remote
endpoint, or a PowerShell filter as paths. They cannot support a new strong
local-filesystem claim.

ShellSyntaxTree 0.3.2 separately publishes `AuthoredValue` and
`AuthoredPathShape`. Neither says that a value occupies a proved local
filesystem slot. `AuthoredValue` also precedes field splitting and pathname
expansion, so bounded source words are not automatically bounded runtime path
arguments.

Both gaps appear around Netclaw evidence row D14:

```bash
for f in src/App/App.csproj src/Hosting/Hosting.csproj; do
  cat /work/$f
done
```

With `PublishAuthoredSourceFacts=true`, the parser proves two authored words
but reports an unknown effective value and `Argument.IsPath=false`. Netclaw
must not infer local path authority from `cat`, slash characters, or the broad
compatibility tables.

The public API is stable at 0.3.2. The change must be additive, preserve
fail-closed defaults, and avoid a consumer-side executable parser.

## Goals / Non-Goals

**Goals:**

- Expose bounded, resolver-normalized local filesystem values derived from an
  authored argument.
- Require an audited parser-owned local-path binder and a transform-safe value
  proof before publication.
- Let a consumer evaluate D14 through its path policy without guessing from
  command identity or lexical shape.
- Use one public shape for Bash and PowerShell.
- Preserve source and binary compatibility with 0.3.2.

**Non-Goals:**

- Strengthen or reinterpret compatibility `Arg.IsPath` or
  `ClauseElement.IsPath`.
- Claim that a path exists, is allowed, or remains within a trust zone.
- Treat a remote endpoint or non-filesystem PowerShell provider as a local
  path.
- Add a full grammar for every executable.
- Publish a path domain for field-split, glob-expanded, ambiguous, or
  unbounded authored values in this slice.
- Add a parallel authored projection for redirects; the redirect hierarchy
  already owns their path relevance.
- Relax dynamic identity, hidden execution, runtime iterators, unsupported
  control flow, or incomplete occurrences.

## Decisions

### 1. Add one bounded filesystem-value projection

The public API adds one property and reuses the existing closed value union:

```csharp
public sealed record AnalyzedArgument
{
    // Existing members remain unchanged.
    public ShellValueDomain AuthoredFileSystemValue { get; internal init; }
}
```

Parser results always set the property. `ShellValueDomain.Unknown` means the
parser supplies no bounded local-filesystem fact. The positive alternatives in
this release are `Exact` and `FiniteSet`; later support for `PathPattern` needs
a separate contract because it models pathname expansion. `IntegerRange` and
`Concatenation` are invalid for this property and fail projection closed.

An enum plus `AuthoredValue` was rejected. That shape would force every
consumer to recombine binding, field-split, glob, resolution, and value-domain
rules. A direct domain answers the consumer's actual question while it remains
a parser fact rather than an authorization result.

### 2. Separate strong binders from compatibility path heuristics

The positive projection uses a new internal audited binder catalog. It does
not copy `Arg.IsPath`, `ClauseElement.IsPath`, `FileVerbs`, or generic
path-shape output. An entry must prove that the accepted argument position is a
local filesystem path for every syntax shape that the binder accepts.

The catalog uses general binding categories, such as all non-option operands
or one exact named/positional parameter. Commands opt into a category through
data. An option with special grammar remains unknown until a reviewed binder
models that complete option shape. The initial Bash slice must cover D14's
ordinary `cat` operand. The initial PowerShell slice must cover one exact
filesystem parameter in each supported dialect.

The broad compatibility results stay unchanged:

```text
python -c 'print(1)'
test 1 -eq 1
head -n 10 README
scp user@example.test:/srv/file .
Get-ChildItem C:\work *.cs
Rename-Item C:\old new
```

Their unaudited, remote, count, payload, filter, or relational positions have
`AuthoredFileSystemValue=Unknown`, even when compatibility `IsPath` is true.
`curl --output=-` and `tar --file=-` also remain unknown because `-` denotes a
stream rather than a local file. The audited `cat` binder applies the same
boundary to `cat -` and `cat -- -`.

This is a general parser-owned catalog, not a Netclaw command parser. It may
grow only through full-shape tests and an invariant that applies to every
accepted argument for that entry.

### 3. Require transform-safe authored candidates

`PublishAuthoredSourceFacts` remains an explicit consumer assertion that
ambient Bash variable attributes and ambient `IFS` are outside the approval
claim. The new projection adds a stricter rule under that boundary: each
candidate must remain one filesystem argument under ordinary Bash field and
pathname semantics.

For an unquoted tracked expansion, each candidate must contain no default-IFS
whitespace and no active glob character. Quoted expansions may contain
whitespace because they remain one field. Any unresolved cardinality,
zero-or-many expansion, active glob, or opaque fragment produces `Unknown`.
This slice does not enumerate the filesystem.

The proof follows fragment provenance and never reparses a substituted value
as shell source. A loop binding whose literal value is `$HOME/literal` remains
a filename containing `$`; it does not expand `HOME` a second time. A quoted
`*.txt` candidate may likewise be one literal filename, while an unquoted
candidate keeps active pathname expansion and remains unknown.

Therefore the positive D14 values are supported, while this value is not:

```bash
for f in 'src/A.cs /etc/passwd'; do cat /work/$f; done
```

The unquoted expansion can produce `/work/src/A.cs` and `/etc/passwd` under
ordinary splitting, so `AuthoredFileSystemValue` is `Unknown`. The property
does not reinterpret `AuthoredValue`; it is a separate derived fact.

### 4. Normalize only with exact local context

Positive `Exact` and `FiniteSet` values contain absolute local filesystem
paths. The parser uses its existing shell-specific resolver and the
occurrence's exact working directory. An unknown cwd, remote endpoint,
provider ambiguity, invalid path, or platform-style conflict produces
`Unknown`.

Lexical `AuthoredPathShape` can reject a mismatch but cannot create the
projection. A path-shaped API route remains unknown. A bare `cat README` can
produce an exact local path when the binder and cwd are both proved, even
though its lexical path shape is unknown.

### 5. Preserve argument coordinates and join rules

One authored element may project a flag plus its bound value. Only the value
receives a positive filesystem domain. The flag remains unknown.

Abstract-state analysis recomputes the complete argument vector for each
reachable loop visit. The joined filesystem domain is positive only when every
visit binds the same analyzed argument to a proved local-path slot and each
visit has transform-safe candidates. A binding conflict, missing visit fact, or
over-limit union becomes `Unknown`.

The exact audited adversarial join is:

```bash
for f in -n README; do cat "$f"; done
```

The expansion is an option in one visit and an audited `cat` filesystem
operand in the other, so its joined projection is unknown. The paired
all-filesystem `cat` loop remains finite and positive. PowerShell's
`Get-Content -LiteralPath:C:\work\a.txt` separately proves that inline
projection assigns the positive domain to the value argument, not the flag.

An unaudited `curl --output=/work/out` / `curl --data=/api/v1` loop remains a
useful compatibility negative, but cannot by itself prove join behavior because
both alternatives are already unknown at the audited-binder gate.

### 6. Apply one public contract to Bash and PowerShell

Bash uses the audited binder, shell-value provenance, ordinary transform
proof, and its local path resolver.

PowerShell uses an audited dialect-aware cmdlet/native binder and its resolver.
An exact filesystem-qualified target can be positive. A non-filesystem
provider, an unproved provider, a filter, a rename fragment, a remote native
endpoint, or dialect-unknown parameter binding remains unknown. PowerShell
effective and authored value semantics do not otherwise change.

### 7. Keep authorization in the consumer

A positive `AuthoredFileSystemValue` is not an allow decision. A security
consumer must still:

1. require a complete parse and occurrence;
2. explicitly accept the authored-source threat-model boundary;
3. accept only the documented domain alternatives;
4. evaluate each absolute path through its own path and trust-zone policy;
5. evaluate redirects and every other command occurrence independently; and
6. fail closed on `Unknown` or an unrecognized domain type.

The consumer may not use `AuthoredValue`, `AuthoredPathShape`, or compatibility
`IsPath` as a substitute for the new projection.

### 8. Preserve compatibility through an additive release

No existing public member or signature changes. The property uses a closed
type that 0.3.2 consumers already understand. As with existing record members,
it participates in generated equality, hashing, `ToString()`, reflection, and
default JSON shape. Parser results remain an in-memory API, not a stable wire
format.

The implementation adds exact public API snapshots and compares both target
frameworks against public 0.3.2. The intended release is 0.3.3.

## Risks / Trade-offs

- **Risk: compatibility heuristics leak into the strong projection.** → The
  new binder catalog is separate and false-positive compatibility cases stay
  unknown.
- **Risk: an authored word splits into an external path.** → Positive domains
  require one-field candidates under ordinary transforms; exact whitespace and
  glob counterexamples remain unknown.
- **Risk: a remote or provider path is treated as local.** → Local resolver,
  provider, remote-endpoint, and dialect checks precede publication.
- **Risk: loop visits disagree about option binding.** → Whole-vector replay
  joins every visit and returns unknown on any conflict.
- **Risk: consumers treat the domain as authorization.** → The guide and tests
  require independent path policy and complete-call checks.
- **Trade-off: the initial audited catalog is small.** → False negatives are
  recoverable. Each later catalog entry needs full-shape positive and negative
  evidence.

## Migration Plan

1. Add the property with a parser-emitted unknown default and public API
   snapshots.
2. Add internal audited Bash and PowerShell local-path binders.
3. Add transform and local-resolution projection after authored-value
   analysis.
4. Add direct, corpus, oracle, consumer-guide, and adversarial coverage.
5. Validate source and binary compatibility against public 0.3.2.
6. Publish 0.3.3 after Linux and native Windows CI pass.
7. Let Netclaw opt into the property in a separate package-upgrade change.

Rollback needs no data migration. Netclaw can ignore the property or pin
0.3.2. Existing consumers continue to compile and run.

## Open Questions

None. Implementation must stop for design revision if the positive catalog
needs executable-private control flow rather than bounded table data, or if
PowerShell cannot prove a local target from existing dialect metadata.
