## 1. Public Traversal API

- [x] 1.1 Add `ShellSyntaxNode.GetChildren()` and verify it returns all direct children for each node type.
- [x] 1.2 Verify internal assignment substitutions and command-list item commands are reachable by recursive traversal.
- [x] 1.3 Verify leaf, missing-child, read-only collection, and child-identity behavior; inspect the explicit unknown-node failure path.

## 2. Contract and Evidence

- [x] 2.1 Update `SPEC.md`, the consumer guide, and public API snapshots; verify diagnostic partial-tree limits and method shape.
- [x] 2.2 Run focused traversal tests and OpenSpec validation; verify all traversal scenarios pass.
- [x] 2.3 Run the Release build, full tests, header check, Slopwatch, and diff check; verify the final source commit is clean before packing.
