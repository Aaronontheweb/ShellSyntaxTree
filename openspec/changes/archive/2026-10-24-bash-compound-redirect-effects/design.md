## Context

See `proposal.md` for the problem. The parser already owns redirect analysis for simple commands. Its syntax tree already owns `for`, condition loops, branches, and subshell groups.

`RedirectAnalysis.Authored` points to the compatibility `Redirect` leaf. `CommandOccurrence.Redirects` represents redirects on one simple command. It does not represent one redirect on an enclosing compound command.

ShellSyntaxTree 0.4.0 feeds Netclaw 0.28.0-beta.2. That consumer reads `ParsedCommand.Commands` and does not read a compound redirect field. It fails closed today because the parser marks these inputs unparseable. New parser behavior must remain opt-in until Netclaw handles the compound owner.

## Goals / Non-Goals

**Goals:**

- Preserve a compound redirect as an effect of its compound command.
- Reuse existing redirect operation and target analysis.
- Keep exact spans for the outer command and each redirect.
- Keep default parsing behavior unchanged for older consumers.
- Let a consumer opt in only after it reads the new syntax node.

**Non-Goals:**

- Add filesystem authority or command policy to ShellSyntaxTree.
- Copy a compound redirect onto child `CommandOccurrence` records.
- Resolve values that the caller does not prove.
- Add brace-group parsing or process substitution support.
- Add here-document or here-string ownership to a compound command.
- Enable the new grammar by default in this prerelease.

## Decisions

### Add an explicit compound wrapper

Add `RedirectedCompoundSyntax : ShellSyntaxNode`. It holds the original supported compound node in `Command`, an ordered list of outer redirect facts in `Redirects`, and the `WorkingDirectory` at the compound command's entry point.

Each `CompoundRedirect` fact holds one existing `RedirectAnalysis`, plus `SourceStart` and `SourceLength` for the exact operator-to-target slice. The wrapper source span covers the compound command and all trailing redirects.

This keeps the redirect owner and its path base visible in the tree. It also avoids adding a redirect property to every compound syntax type. A separate wrapper gives consumers one explicit shape to inspect.

The `RedirectAnalysis.Authored` contract will identify the exact source redirect leaf. A simple-command fact keeps its leaf in `Clause.Redirects`. A compound fact owns its leaf in `CompoundRedirect.Analysis`.

### Gate the grammar with a default-off option

Add `BashParserOptions.ParseCompoundRedirects`, with the default value `false`. When false, the parser keeps its current unparseable result. When true, the parser may emit `RedirectedCompoundSyntax` for supported forms.

This option is a consumer capability boundary. It is not an authority grant. Netclaw must enable it only in code that reads and checks every compound redirect.

The option is separate from `PublishAuthoredSourceFacts`. That setting controls source-value facts. It does not declare that the consumer handles outer redirect effects.

### Keep occurrence completeness truthful

Do not copy outer redirects to child occurrences. Do not set `CommandOccurrence.IsComplete` false only to force an older consumer to prompt. That boolean can already represent other missing parser proof.

The default-off option preserves the old safety behavior. An opted-in consumer must inspect both the wrapper and each child occurrence. A compound redirect fact does not prove that its target is allowed.

### Reuse existing redirect analysis

The parser will use the existing redirect alternatives and value domains for each supported outer redirect. It will preserve source order and exact source spans. An unknown or incomplete target remains unknown or incomplete. The wrapper's `WorkingDirectory` records the cwd at the compound invocation, before the body can change shell state.

The parser will not read the process filesystem to resolve a redirect. Existing launch facts can prove values such as `HOME` when the caller supplies them. Without such proof, the value stays unknown.

The parser will reject compound command substitutions, here-documents, and here-strings. This slice does not classify nested executable occurrences or own text input. It keeps the whole parse unparseable and exposes no partial command occurrences.

The parser will also keep compound redirects inside decoded `bash -c` source unparseable. The child spans do not map to the caller's source.

### Keep support bounded

The first scope covers `for`, `while`, `until`, `if`, and subshell constructs that the parser already models. Brace groups remain unparseable because their structural analysis is a separate gap.

After a compound terminator, the parser will consume only redirect syntax that the existing redirect analyzer can classify. A malformed or unsupported redirect keeps the whole parse unparseable. Redirect-target analysis must use the compound entry state, not a state that the loop or branch body changes.

The analyzer includes the compound entry state as a possible failure result. Bash skips the body when an outer redirect fails to open. A later sequence joins that path with the body result. An `||` fallback can receive the entry state. An `&&` command can receive only the body success state.

The analyzer will not prove a compound redirect value from shell assignments in this change. It will keep those targets unknown unless existing launch facts prove them.

### Version the additive public API

The current stable release is `0.4.0`. The specification makes the stable 0.x minor version the API boundary. This public API addition will target `0.5.0-beta.1` after tests pass.

Prepare a local source handoff and package only. Do not tag or publish the package.

## Risks / Trade-offs

- [Risk] An older consumer may ignore a new syntax node. → Keep the option false by default.
- [Risk] A consumer may treat a known target as permitted. → State in the API docs that syntax facts grant no authority.
- [Risk] A compound effect may lose its exact source owner. → Test node identity, ordered facts, and exact source slices.
- [Risk] A wider redirect grammar may surface an unmodeled operation. → Reuse the existing closed analysis family and fail closed on unsupported forms.
- [Trade-off] The default parser does not reduce prompts for this syntax. → The owning consumer opts in only after it adds full compound-effect checks.

## Migration Plan

1. Ship the additive API in the `0.5.0-beta.1` prerelease only after local validation.
2. Keep consumers on the default option until they handle `RedirectedCompoundSyntax`.
3. Update Netclaw to opt in and pass every compound file redirect through its existing path and read-operation checks.
4. Keep the default false until a later compatibility decision.

## Open Questions

None. The maintainer approved the API and option names before parser changes.
