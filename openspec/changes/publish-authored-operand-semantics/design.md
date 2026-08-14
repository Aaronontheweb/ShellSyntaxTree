## Context

`AuthoredPathShape` is deliberately lexical. It classifies `\n` as a
Windows-shaped word even when Bash passes that word to `tr` as translation
data. Compatibility `Arg.IsPath` has the same false positive because `tr`
currently falls through to the generic path heuristic.

ShellSyntaxTree 0.3.4 can publish a strong positive
`AuthoredFileSystemValue`, but `Unknown` combines two different states:
unaudited semantics and audited non-filesystem semantics. Netclaw cannot safely
distinguish them. Its reviewed-safe policy must therefore prompt for the live
`tr -d '\n'` case.

The parser already owns a small audited operand-binding subsystem. This change
extends that boundary instead of adding executable grammar to Netclaw.

## Goals / Non-Goals

**Goals:**

- Publish a bounded value only when an audited binding proves that the
  argument is not a local-filesystem operand.
- Preserve lexical path shape as independent evidence.
- Correct compatibility path classification for audited `tr` operands.
- Let Netclaw ignore only the contradicted lexical path fact.
- Keep the public addition and implementation small.

**Non-Goals:**

- Declare a command safe or read-only.
- Suppress redirect, hidden-execution, dynamic-identity, or completeness
  checks.
- Add a complete `tr`, `grep`, or native-option grammar.
- Treat every compatibility `IsPath=false` argument as audited data.
- Publish an exact value after unbounded splitting or pathname expansion.
- Add a positive PowerShell binder in this slice.

## Decisions

### 1. Publish one direct non-filesystem value domain

Add one property to `AnalyzedArgument`:

```csharp
public ShellValueDomain AuthoredNonFileSystemValue { get; internal init; }
```

The default is `ShellValueDomain.Unknown`. The positive alternatives in this
slice are `Exact` and `FiniteSet`.

This mirrors `AuthoredFileSystemValue` and reuses the closed value family. It
avoids a new public enum and avoids making consumers recombine a role with
`AuthoredValue` transformation rules.

A boolean was rejected because it would not bind the proof to the bounded
value. A three-state enum was rejected because it would duplicate the positive
filesystem state already carried by `AuthoredFileSystemValue`. Reinterpreting
`Arg.IsPath=false` was rejected because that compatibility value also covers
unaudited and unknown executables.

### 2. Extend the audited operand-binding subsystem

Rename the internal filesystem-only catalog and projector to describe audited
operand semantics. Each catalog entry selects:

- a shell and canonical command identity;
- a reusable binding category;
- either local-filesystem or non-filesystem projection; and
- the complete accepted argument shape.

The existing Bash `cat` and PowerShell `Get-Content -LiteralPath` entries keep
their exact filesystem behavior. The initial non-filesystem entry is Bash
`tr`, whose arguments are translation sets or options and never name files
opened by `tr`.

The `tr` entry classifies the command as a single-token identity and all
bounded authored arguments as non-filesystem. The greedy verb walk consumes
that same catalog fact, so ordinary `tr abc def` retains two analyzed
arguments. Redirects remain separate occurrence facts. Command substitutions
remain separate command occurrences. Unknown command identity, unbounded
values, active pathname expansion, or incomplete provenance produce `Unknown`.

The positive filesystem and non-filesystem domains are mutually exclusive for
one argument. Projection validation fails closed on an impossible pairing.

### 3. Correct the compatibility leaf through parser-owned data

Use the audited `tr` entry to stop verb extraction after the command name and
classify every positional as non-path data. This makes both `tr abc def` and
`tr -d '\n'` retain `IsPath=false` and `Resolved=null` for their translation
operands.

The generic heuristic remains unchanged. An unknown command receiving `\n`
continues to classify it as path-shaped. Netclaw therefore receives the
compatibility correction only from parser-owned command knowledge.

### 4. Keep lexical shape independent

`AuthoredPathShape` remains `Windows` for the exact `\n` word. That fact is
truthful lexical evidence and can still matter to consumers without audited
semantic binding.

Netclaw may omit broad compatibility `IsPath` and authored lexical-path facts
only when the same analyzed argument has a positive
`AuthoredNonFileSystemValue`. It must not remove positive
`AuthoredFileSystemValue` facts, redirect facts, or the raw protected-path
defense scan.

### 5. Keep authorization and execution checks unchanged

The new property is not an allow decision. A consumer still requires a
complete occurrence, approved command effects, safe cwd, and independent
coverage for every command and redirect.

Therefore `tr -d '\n' > /outside/result` remains strict because of the output
redirect. `tr "$(tool)"` still exposes and evaluates the substitution. An
unknown or unrecognized property/domain shape remains strict.

### 6. Pin both the parser fact and the observed consumer outcome

ShellSyntaxTree receives direct and executable-corpus cases for ordinary
translation sets, the exact backslash data shape, path-looking translation
data, active globs, redirects, unknown commands, and the
positive-filesystem mutual-exclusion invariant.

Netclaw receives the sanitized full diagnostic loop from the live session. It
must derive the real project cwd, produce no `/n` candidate scope, and allow
the reviewed `tr` phrase only after every other command and redirect is
covered.

The shell-write observations remain an agent-guidance corpus category. This
change does not authorize `cat >`, heredocs, `tee`, or other file-writing
shell shapes.

## Risks / Trade-offs

- **Risk: an overbroad data entry hides a filesystem operand.** → Require an
  invariant over every accepted argument shape and adversarial option tests.
- **Risk: consumers treat data as globally safe.** → The contract permits
  skipping only local-path interpretation for that argument.
- **Risk: two authored domains disagree.** → Validate mutual exclusion before
  publishing the occurrence.
- **Risk: the catalog grows into executable grammars.** → Keep reusable
  binding categories small and add entries only from observed, tested cases.
- **Trade-off: dynamic `tr` values may still prompt.** → Unknown remains strict
  until bounded transformation proof exists.

## Migration Plan

1. Publish the additive ShellSyntaxTree package after API, corpus, Linux, and
   native Windows validation.
2. Upgrade Netclaw and consume the new fact in typed path projection only.
3. Run the exact live-derived approval fixture and full policy matrix.
4. Binary-swap the merged Netclaw build and harvest another traffic window.

Rollback removes the Netclaw consumer branch first, then restores the prior
package. Existing consumers ignore the additive property.

## Open Questions

- Which later observed commands justify additional audited non-filesystem
  entries? No command beyond `tr` is required for this slice.
