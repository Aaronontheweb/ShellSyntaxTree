## Why

`CommandOccurrence.WorkingDirectory` describes where a command starts, but it
does not describe whether that command can change the current shell scope.
Security consumers therefore cannot distinguish an ordinary command from
`cd`, `pushd`, `popd`, or another parser-known location mutation without
reimplementing shell grammar.

## What Changes

- Add a parser-owned, closed working-directory-effect family to each command
  occurrence.
- Distinguish a proved unchanged directory, a change on successful completion,
  and an unknown effect.
- Publish the successful target as a bounded `ShellValueDomain` when the shell
  grammar proves one.
- Derive the fact from the existing success/failure abstract-state transfer for
  Bash and native PowerShell.
- Keep ambient functions, aliases, profiles, modules, executable behavior, and
  other runtime resolution outside the authored-syntax fact.
- Update the shared and PowerShell contracts, consumer guide, public API
  snapshots, and sanitized regression corpus.

## Capabilities

### New Capabilities

- `working-directory-effects`: Defines the additive public fact, its
  success/failure semantics, shell-specific derivation, and fail-closed
  consumer boundary.

### Modified Capabilities

None.

## Impact

- Adds one property to `CommandOccurrence` and one closed public record family.
- Extends Bash and PowerShell abstract-state projection without changing the
  accepted grammar or compatibility `Clauses` projection.
- Changes record equality, hashing, and diagnostic rendering for
  `CommandOccurrence`; the new library-owned default is `Unknown`.
- Enables Netclaw to consume a general parser fact instead of maintaining a
  command-name list for causal working-directory approval.
- Requires the next additive v0.x package release; no existing public member is
  removed or changed.
