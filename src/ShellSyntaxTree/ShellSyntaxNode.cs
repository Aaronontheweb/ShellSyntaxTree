// -----------------------------------------------------------------------
// <copyright file="ShellSyntaxNode.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree;

/// <summary>
/// Base record for the library-owned authored syntax hierarchy.
/// </summary>
public abstract record ShellSyntaxNode
{
    private protected ShellSyntaxNode()
    {
    }

    private protected abstract bool IsLibraryOwnedNode { get; }

    /// <summary>Gets the stable syntax discriminant.</summary>
    public abstract ShellSyntaxKind Kind { get; }

    /// <summary>Gets the zero-based start in <see cref="ParsedCommand.Source"/>, when exact.</summary>
    public int? SourceStart { get; init; }

    /// <summary>Gets the source length, when exact.</summary>
    public int? SourceLength { get; init; }
}

/// <summary>Identifies a library-owned syntax-node shape.</summary>
public enum ShellSyntaxKind
{
    /// <summary>The kind is unknown to the consumer.</summary>
    Unknown,
    /// <summary>An ordered statement block.</summary>
    Block,
    /// <summary>An existing simple-command leaf.</summary>
    SimpleCommand,
    /// <summary>An ordered pipeline.</summary>
    Pipeline,
    /// <summary>A command list with authored operators.</summary>
    CommandList,
    /// <summary>A grouped command region.</summary>
    Group,
    /// <summary>A foreach-style loop.</summary>
    ForEach,
    /// <summary>A condition-controlled loop.</summary>
    ConditionLoop,
    /// <summary>A conditional statement.</summary>
    Conditional,
    /// <summary>One conditional branch.</summary>
    ConditionalBranch,
    /// <summary>A command substitution.</summary>
    CommandSubstitution,
    /// <summary>An authored body activated by shell or command semantics.</summary>
    ExecutionRegion,
}

/// <summary>An ordered block of authored statements.</summary>
public sealed record ShellBlockSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.Block;

    /// <summary>Gets the statements in authored order.</summary>
    public IReadOnlyList<ShellSyntaxNode> Statements { get; init; } =
        Array.Empty<ShellSyntaxNode>();
}

/// <summary>A simple command backed by the existing compatibility leaf.</summary>
public sealed record SimpleCommandSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.SimpleCommand;

    /// <summary>Gets the shared simple-command leaf.</summary>
    public Clause Clause { get; init; } = new();

    /// <summary>
    /// Gets executable command substitutions evaluated for this command's
    /// authored words and redirects, in source order.
    /// </summary>
    public IReadOnlyList<CommandSubstitutionSyntax> Substitutions { get; init; } =
        Array.Empty<CommandSubstitutionSyntax>();

    /// <summary>
    /// Gets execution-bearing regions bound to this command's arguments, in
    /// authored order.
    /// </summary>
    public IReadOnlyList<ExecutionRegionSyntax> ExecutionRegions { get; init; } =
        Array.Empty<ExecutionRegionSyntax>();
}

/// <summary>An ordered pipeline.</summary>
public sealed record PipelineSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.Pipeline;

    /// <summary>Gets the stages in authored order.</summary>
    public IReadOnlyList<ShellSyntaxNode> Stages { get; init; } =
        Array.Empty<ShellSyntaxNode>();
}

/// <summary>A command list preserving authored operator relationships.</summary>
public sealed record CommandListSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.CommandList;

    /// <summary>Gets the list items in authored order.</summary>
    public IReadOnlyList<CommandListItemSyntax> Items { get; init; } =
        Array.Empty<CommandListItemSyntax>();
}

/// <summary>One command-list item and its preceding authored operator.</summary>
public sealed record CommandListItemSyntax
{
    /// <summary>Gets the authored relationship to the preceding item.</summary>
    public CompoundOperator Operator { get; init; }

    /// <summary>Gets the command structure for this item.</summary>
    public ShellSyntaxNode Command { get; init; } = new ShellBlockSyntax();
}

/// <summary>A grouped command region.</summary>
public sealed record GroupSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.Group;

    /// <summary>Gets the group's shell-state behavior.</summary>
    public ShellGroupKind GroupKind { get; init; }

    /// <summary>Gets the grouped body.</summary>
    public ShellBlockSyntax Body { get; init; } = new();
}

/// <summary>Identifies whether a group shares or isolates shell state.</summary>
public enum ShellGroupKind
{
    /// <summary>The state behavior is unknown.</summary>
    Unknown,
    /// <summary>The group executes in the current shell scope.</summary>
    CurrentScope,
    /// <summary>The group executes in an isolated shell scope.</summary>
    IsolatedScope,
}

/// <summary>A foreach-style loop with a shell-specific iterable spelling.</summary>
public sealed record ForEachSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.ForEach;

    /// <summary>Gets the loop binding.</summary>
    public LoopBindingSyntax Binding { get; init; } = new();

    /// <summary>Gets the shell-specific authored iterable.</summary>
    public ShellSourceFragment Iterable { get; init; } = new();

    /// <summary>Gets commands discovered while producing iterator values.</summary>
    public ShellBlockSyntax IteratorCommands { get; init; } = new();

    /// <summary>Gets the loop body.</summary>
    public ShellBlockSyntax Body { get; init; } = new();
}

/// <summary>A normalized loop binding and its authored source.</summary>
public sealed record LoopBindingSyntax
{
    /// <summary>Gets the normalized binding name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets the authored binding source.</summary>
    public ShellSourceFragment Source { get; init; } = new();
}

/// <summary>An authored source fragment with an exact-or-unavailable outer range.</summary>
public sealed record ShellSourceFragment
{
    /// <summary>Gets the authored raw spelling.</summary>
    public string Raw { get; init; } = "";

    /// <summary>Gets the zero-based outer source start, when exact.</summary>
    public int? SourceStart { get; init; }

    /// <summary>Gets the outer source length, when exact.</summary>
    public int? SourceLength { get; init; }
}

/// <summary>A while- or until-style loop.</summary>
public sealed record ConditionLoopSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.ConditionLoop;

    /// <summary>Gets the loop kind.</summary>
    public ConditionLoopKind LoopKind { get; init; }

    /// <summary>Gets the executable condition region.</summary>
    public ShellBlockSyntax Condition { get; init; } = new();

    /// <summary>Gets the loop body.</summary>
    public ShellBlockSyntax Body { get; init; } = new();
}

/// <summary>Identifies the condition-loop behavior.</summary>
public enum ConditionLoopKind
{
    /// <summary>The loop kind is unknown.</summary>
    Unknown,
    /// <summary>The loop continues while its condition succeeds.</summary>
    While,
    /// <summary>The loop continues until its condition succeeds.</summary>
    Until,
}

/// <summary>A conditional statement with authored branches.</summary>
public sealed record ConditionalSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.Conditional;

    /// <summary>Gets the conditional branches in authored order.</summary>
    public IReadOnlyList<ConditionalBranchSyntax> Branches { get; init; } =
        Array.Empty<ConditionalBranchSyntax>();

    /// <summary>Gets the optional else body.</summary>
    public ShellBlockSyntax? Else { get; init; }
}

/// <summary>One conditional condition and body pair.</summary>
public sealed record ConditionalBranchSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.ConditionalBranch;

    /// <summary>Gets the executable condition region.</summary>
    public ShellBlockSyntax Condition { get; init; } = new();

    /// <summary>Gets the branch body.</summary>
    public ShellBlockSyntax Body { get; init; } = new();
}

/// <summary>A command substitution whose produced value is analyzed separately.</summary>
public sealed record CommandSubstitutionSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.CommandSubstitution;

    /// <summary>Gets the commands inside the substitution.</summary>
    public ShellBlockSyntax Body { get; init; } = new();
}

/// <summary>An authored body whose activation is described by shell semantics.</summary>
public sealed record ExecutionRegionSyntax : ShellSyntaxNode
{
    private protected override bool IsLibraryOwnedNode => true;

    /// <inheritdoc />
    public override ShellSyntaxKind Kind => ShellSyntaxKind.ExecutionRegion;

    /// <summary>Gets the syntax or binding that introduces the region.</summary>
    public ExecutionRegionOrigin Origin { get; init; }

    /// <summary>
    /// Gets the index of the bound script-block token in the owning command's
    /// <see cref="Clause.Elements"/>, or null for a direct shell invocation.
    /// </summary>
    public int? HostClauseElementIndex { get; init; }

    /// <summary>Gets the semantic activation phase.</summary>
    public ExecutionRegionPhase Phase { get; init; }

    /// <summary>Gets the relationship between registration and execution time.</summary>
    public ExecutionRegionTiming Timing { get; init; }

    /// <summary>Gets how activations relate to one authored region.</summary>
    public ExecutionRegionCardinality Cardinality { get; init; }

    /// <summary>Gets the recursively parsed region body.</summary>
    public ShellBlockSyntax Body { get; init; } = new();
}

/// <summary>Identifies how an authored execution region is introduced.</summary>
public enum ExecutionRegionOrigin
{
    /// <summary>The introducing syntax or binding is unknown.</summary>
    Unknown,
    /// <summary>A direct PowerShell call-operator script block: <c>&amp; { ... }</c>.</summary>
    DirectCall,
    /// <summary>A direct PowerShell dot-source script block: <c>. { ... }</c>.</summary>
    DotSource,
    /// <summary>A script block bound to an argument of a host command.</summary>
    CommandArgument,
}

/// <summary>Identifies an execution region's semantic phase.</summary>
public enum ExecutionRegionPhase
{
    /// <summary>The phase is unknown.</summary>
    Unknown,
    /// <summary>The primary body of an invocation.</summary>
    Main,
    /// <summary>An initialization body.</summary>
    Initialization,
    /// <summary>A pipeline begin body.</summary>
    Begin,
    /// <summary>A pipeline process body.</summary>
    Process,
    /// <summary>A pipeline end body.</summary>
    End,
    /// <summary>A per-input filter body.</summary>
    Filter,
    /// <summary>An externally triggered action body.</summary>
    Action,
    /// <summary>An argument-completion body.</summary>
    Completion,
}

/// <summary>Identifies when an execution region may run.</summary>
public enum ExecutionRegionTiming
{
    /// <summary>The timing is unknown.</summary>
    Unknown,
    /// <summary>The body completes as part of the containing invocation.</summary>
    Synchronous,
    /// <summary>Instances or the containing continuation may overlap.</summary>
    Concurrent,
    /// <summary>Registration and a later trigger are distinct.</summary>
    Deferred,
}

/// <summary>Identifies how often one authored execution region may activate.</summary>
public enum ExecutionRegionCardinality
{
    /// <summary>The activation cardinality is unknown.</summary>
    Unknown,
    /// <summary>The body activates once per proved host invocation.</summary>
    Once,
    /// <summary>The body activates once per input object.</summary>
    OncePerInputObject,
    /// <summary>The body may activate zero or more times.</summary>
    ZeroOrMore,
}
