## Why

Consumers need to visit every syntax node in a parsed command. Today, internal assignment nodes can own command substitutions that consumers cannot discover through public node properties.

## What Changes

- Add a `GetChildren()` method to `ShellSyntaxNode`.
- Return all direct syntax children, including children of internal node types.
- Fail explicitly when a node kind or required child is unsupported.
- Document that child traversal reports structure only and does not prove complete parsing, execution order, or authority.

## Capabilities

### New Capabilities
- `syntax-tree-children`: expose complete direct-child traversal for parser-owned syntax nodes.

### Modified Capabilities
- None.

## Impact

The change adds a public API to `ShellSyntaxTree`. It updates `SPEC.md`, the consumer guide, API snapshots, and AST traversal tests. It adds no package dependency.
