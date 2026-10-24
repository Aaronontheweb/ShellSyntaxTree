## Why

The PowerShell parser exposes code hidden by `pwsh -Command` and
`-EncodedCommand`, but leaves equivalent static code behind
`Invoke-Expression` / `iex` opaque. Security consumers can therefore approve
the wrapper without evaluating the command it executes.

## What Changes

- Recurse into `Invoke-Expression` and `iex` only when the complete payload is
  provably a static string.
- Surface computed payloads as `DynamicSkip`; safe-fail pipeline-only,
  missing, and ambiguous payloads rather than returning a clean approval
  shape.
- Preserve the caller's PowerShell location while parsing static payloads and
  propagate location changes made by the payload back to following clauses.
- Share the existing 64 KiB input cap and depth-five recursion budget across
  `Invoke-Expression`, `pwsh -Command`, and `pwsh -EncodedCommand`.
- Preserve the current public API.

## Capabilities

### New Capabilities

- `invoke-expression-recursion`: Safely expose static PowerShell expression
  strings while preserving current-scope location and safe-failing computed
  payloads.

### Modified Capabilities

None.

## Impact

The change affects the PowerShell lexer and command parser, PowerShell unit
tests and corpus entries, `SPEC.POWERSHELL.md`, shared wrapper documentation
in `SPEC.md`, release notes, and the implementation plan. It adds no public
API, package dependency, or native dependency.
