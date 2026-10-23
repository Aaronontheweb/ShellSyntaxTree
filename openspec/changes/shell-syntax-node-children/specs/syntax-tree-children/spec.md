## Purpose

This capability lets consumers visit every parser-owned syntax node, including nodes that the public type surface does not expose directly. It reports structural reachability only.

## ADDED Requirements

### Requirement: Enumerate direct syntax children
`ShellSyntaxNode.GetChildren()` SHALL return each direct syntax child as the same parser-owned node reference. The returned collection SHALL be read-only. It SHALL include children held by internal node types, including command substitutions on assignment-only nodes. The method SHALL report structural reachability only. It SHALL NOT assert execution order, parse completeness, or authority.

#### Scenario: Assignment substitution is reachable
- **WHEN** a parsed assignment-only node contains a command substitution
- **THEN** `GetChildren()` returns the substitution node, and traversal from the root can reach its command body

#### Scenario: Composite nodes expose their direct children
- **WHEN** a parsed node has children through ordered collections or nested structural records
- **THEN** `GetChildren()` returns its direct syntax children, including commands inside command-list items

#### Scenario: Leaf node has no children
- **WHEN** a valid leaf node calls `GetChildren()`
- **THEN** the method returns an empty read-only collection

### Requirement: Fail on unsupported or invalid node shape
`ShellSyntaxNode.GetChildren()` SHALL fail explicitly when the node kind is unknown or when a required child collection or child node is missing. It SHALL NOT treat an unsupported or invalid node as a leaf.

#### Scenario: Unknown node kind
- **WHEN** a future or unsupported `ShellSyntaxNode` kind calls `GetChildren()`
- **THEN** the method throws `NotSupportedException`

#### Scenario: Required child is missing
- **WHEN** a node that requires a child has no child value
- **THEN** the method throws `InvalidOperationException`

### Requirement: Preserve diagnostic partial-tree limits
`ShellSyntaxNode.GetChildren()` SHALL return structural children that exist on a diagnostic partial tree. It SHALL NOT mark that tree complete or usable as authorization evidence. Consumers SHALL check the owning parse result's `IsUnparseable` value before they use syntax as policy evidence.

#### Scenario: Unparseable result retains diagnostic structure
- **WHEN** an unparseable parse result retains a partial syntax tree
- **THEN** consumers can traverse present children for diagnostics, but the result remains unparseable and supplies no authorization proof
