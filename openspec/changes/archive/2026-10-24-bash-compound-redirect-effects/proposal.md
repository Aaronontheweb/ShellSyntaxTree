## Why

Bash permits redirects on supported compound commands, but ShellSyntaxTree rejects them. This gap blocks exact facts for a redirect on a loop, branch, or subshell.

The parser must expose that effect without letting older consumers ignore it. Existing `CommandOccurrence` facts do not own a redirect that applies to a whole compound command.

## What Changes

- Add an opt-in syntax wrapper for a supported compound command with ordered redirect facts and exact redirect source spans.
- Reuse the existing `RedirectAnalysis` alternatives and target domains.
- Keep the default parser behavior unchanged so old consumers still receive an unparseable result.
- Keep unsupported target values unresolved and keep brace groups outside this change.
- Keep compound redirects in decoded `bash -c` source unparseable because child spans do not map to the caller.
- Preserve the pre-compound state when an outer redirect fails to open.
- Add direct parser tests and Bash syntax-only controls for loops, branches, and subshells.
- Update the canonical `SPEC.md` and consumer guide.
- Bump the prerelease line to a deliberate `0.5.0-beta.1` API release after validation.

## Capabilities

### New Capabilities

- `compound-shell-redirects`: Publish redirects that apply to supported Bash compound commands while preserving source ownership and fail-closed consumer behavior.

### Modified Capabilities

None.

## Impact

- Public API: add a compound redirect wrapper and fact record; do not change existing public signatures.
- Parser: support redirects after `for`, `while`, `until`, `if`, and subshell commands.
- Compatibility: `BashParserOptions.ParseCompoundRedirects` defaults to false. A consumer must opt in only after it handles the new wrapper.
- Documentation and tests: update `SPEC.md`, the consumer guide, parser tests, and the implementation plan.
- Package: use the `0.5.0-beta.1` prerelease line; do not publish or tag in this change.
