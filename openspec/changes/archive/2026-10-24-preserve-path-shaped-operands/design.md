## Context

The parser builds a greedy native verb chain from lowercase word tokens. A lowercase filename can therefore leave no argument or path metadata.

Three users define the required behavior:

- A security-policy author needs a file operand and its directory scope before a persistent grant.
- An audit-tool author needs the parser's path result without a duplicate extension table.
- A private-CLI author needs unknown lowercase subcommand chains to remain intact.

The public API is locked. Both shell parsers use the same greedy native-command policy.

## Goals / Non-Goals

**Goals:**

- Preserve a path-shaped token as an argument after the command token.
- Use the existing path-shape rules as the canonical boundary.
- Keep Bash and PowerShell native-command results equivalent.
- Preserve plain lowercase subcommand chains.

**Non-Goals:**

- Define the grammar of Git or another command.
- Add source positions or an ordered public token list.
- Solve the relative-position problem from issue #62.
- Expand the file-extension table.

## Decisions

### Use the existing path-shape classifier as a verb boundary

The greedy pass will stop before a token when `BashResolver.LooksLikePath` returns `true`.

This rule applies only after the first command token. A path-shaped command name remains the command name.

The argument pass already calls the same classifier. It will therefore emit the token with `IsPath` and `Resolved` metadata.

### Apply the rule to both native-command parsers

The Bash parser and the PowerShell native-command path must make the same boundary decision.

Both paths will call the same `BashResolver.LooksLikePath` method. This choice prevents extension-table drift.

### Keep the public AST unchanged

Issue #64 does not require a new public type. The current `Arg` record already carries the required path result.

An ordered public token API remains a possible answer for issue #62. That larger API is outside this change.

### Preserve the existing greedy default

Plain lowercase words still extend the verb chain. Commands such as `freshdesk ticket list` keep their current result.

The parser will not use a command dictionary. The existing path evidence supplies the only new boundary.

## Risks / Trade-offs

- A legitimate subcommand can have a known file suffix. The curated path table limits this case, and path evidence wins for security consumers.
- The parsed verb becomes shorter for affected commands. Regression tests will lock the intentional result.
- The shared rule can change PowerShell native-command output. Equivalent tests will cover both shell parsers.

## Migration Plan

This change requires no consumer API migration. Consumers receive richer argument data after a package update.

A revert restores the prior parser behavior if the rule causes an unexpected regression.

## Open Questions

Issue #62 can later add lexical provenance. That proposal must remain compatible with this parser boundary.
