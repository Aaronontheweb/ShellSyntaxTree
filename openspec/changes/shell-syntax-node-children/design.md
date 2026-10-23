## Context

See `proposal.md` for the need and `specs/syntax-tree-children/spec.md` for the observable contract. `ShellSyntaxProjection` already walks syntax nodes, but it validates parser shapes and assigns command roles. It is not a general child enumerator.

## Goals / Non-Goals

**Goals:**
- Expose each parser-owned direct syntax child through one additive method.
- Include children of internal syntax nodes.
- Keep returned collections read-only and preserve child object identity.

**Non-Goals:**
- Define execution order, authorization meaning, or parse completeness.
- Change command projection or parser error behavior.
- Add a visitor framework or cache child lists on syntax nodes.

## Decisions

- Add `ShellSyntaxNode.GetChildren()` as a public method that returns `IReadOnlyList<ShellSyntaxNode>`.
- Use a type switch over the library-owned node hierarchy. Reuse existing read-only child collections when one collection contains all direct children.
- Copy combined child collections and internal assignment substitution arrays through `PublicCollection.Copy`.
- Return the original child node references. Do not store a second child graph.
- Throw `NotSupportedException` for an unknown node type. Throw `InvalidOperationException` when a required child or child collection is missing.
- Return `Array.Empty<ShellSyntaxNode>()` for known leaf nodes.
- Keep `GetChildren()` as a method. A public `Children` property could enter automatic JSON serialization and record `PrintMembers` output. The method avoids those cross-boundary changes.
- Keep `ShellSyntaxProjection` role assignment and structural validation separate. `GetChildren()` reports reachability, not policy evidence.
- A diagnostic partial tree can expose present children. Documentation will require consumers to check `ParsedCommand.IsUnparseable`; the method will not make a partial tree complete.

## Risks / Trade-offs

- A new node type can be omitted from the switch. A default branch will throw, and traversal tests will cover every current node type.
- Combined lists allocate on each call. The method reuses existing read-only collections where possible and avoids cached duplicate state.
- Consumers can mistake structural reachability for execution semantics. XML documentation and the consumer guide state this limit.
