## Why

`Clause.Verb`, `Clause.Args`, and `Clause.Redirects` retain order within each
projection but lose how those projections were interleaved in the command.
That makes position-sensitive command policy impossible without re-tokenizing
`ParsedCommand.Source`. Issue #62 demonstrates this with Git global options
before a subcommand and similarly spelled options after it.

## What Changes

- Add a source-ordered `Clause.Elements` public projection.
- Add `ClauseElement` and `ClauseElementRole` public types.
- Preserve exact source spelling, decoded values, source spans when available,
  parser-verb-relative position, and argument/path classification on each
  element without claiming executable-specific semantics.
- Populate the ordered view directly while walking classified parser tokens.
- Keep `Verb`, `Args`, `Redirects`, and synthetic cwd attribution unchanged for
  existing consumers.
- Apply the same contract to Bash and PowerShell.
- Match native option spelling case-sensitively in both shells so distinct
  options such as Git `-c` and `-C` do not share path metadata; explicitly
  model case-colliding curl and Wget bindings rather than relying on comparer
  collisions.

## Capabilities

### New Capabilities

- `clause-element-provenance`: Preserve cross-projection source order so a
  command-aware consumer can interpret argument placement without duplicating
  shell tokenization.

### Modified Capabilities

None.

## Impact

This is a source- and binary-additive public-API change affecting `Clause`, the
Bash and PowerShell command parsers, public API snapshot tests, parser tests,
the shared SPEC, the PowerShell delta SPEC, and the consumer guide. Generated
record equality, hashing, `ToString()`, and default JSON output observe the new
property. It adds no dependency; the native-option comparer correction may
change path classification where options differ only by case.
