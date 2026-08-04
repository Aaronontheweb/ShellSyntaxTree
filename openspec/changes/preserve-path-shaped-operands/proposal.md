## Why

The greedy verb pass can consume a lowercase filename as a verb token. Security consumers then lose the path and its directory scope.

## What Changes

- Stop a native verb chain before a token that matches the existing path-shape rules.
- Return that token as an argument with the existing path metadata.
- Apply the same native-command rule to Bash and PowerShell.
- Preserve greedy verb chains for lowercase words that do not have a path shape.
- Add no command dictionary and make no public API change.

## Capabilities

### New Capabilities

- `path-shaped-operands`: Preserve lowercase file operands and their resolved directory scope after multi-token commands.

### Modified Capabilities

None.

## Impact

The change affects the Bash and PowerShell native verb passes. It also affects their unit tests, corpora, and parser specifications.

The public AST remains unchanged. The change adds no dependency and no native library.
