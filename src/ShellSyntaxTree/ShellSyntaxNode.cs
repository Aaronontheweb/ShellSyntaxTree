// -----------------------------------------------------------------------
// <copyright file="ShellSyntaxNode.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal;

namespace ShellSyntaxTree;

/// <summary>Base record for the library-owned authored syntax hierarchy.</summary>
public abstract record ShellSyntaxNode
{
    private protected ShellSyntaxNode()
    {
    }

    private protected abstract object LibraryOwnership { get; }

    internal ShellSyntaxKind Kind => this switch
    {
        ShellBlockSyntax => ShellSyntaxKind.Block,
        SimpleCommandSyntax => ShellSyntaxKind.SimpleCommand,
        PipelineSyntax => ShellSyntaxKind.Pipeline,
        CommandListSyntax => ShellSyntaxKind.CommandList,
        GroupSyntax => ShellSyntaxKind.Group,
        ForEachSyntax => ShellSyntaxKind.ForEach,
        CommandSubstitutionSyntax => ShellSyntaxKind.CommandSubstitution,
        ExecutionRegionSyntax => ShellSyntaxKind.ExecutionRegion,
        _ => ShellSyntaxKind.Unknown,
    };

    public int? SourceStart { get; internal init; }

    public int? SourceLength { get; internal init; }
}

internal enum ShellSyntaxKind
{
    Unknown,
    Block,
    SimpleCommand,
    Pipeline,
    CommandList,
    Group,
    ForEach,
    CommandSubstitution,
    ExecutionRegion,
}

/// <summary>An ordered block of authored statements.</summary>
public sealed record ShellBlockSyntax : ShellSyntaxNode
{
    private IReadOnlyList<ShellSyntaxNode> _statements = Array.Empty<ShellSyntaxNode>();

    internal ShellBlockSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    public IReadOnlyList<ShellSyntaxNode> Statements
    {
        get => _statements;
        internal init => _statements = PublicCollection.Copy(value);
    }
}

/// <summary>A simple command backed by the existing compatibility leaf.</summary>
public sealed record SimpleCommandSyntax : ShellSyntaxNode
{
    private IReadOnlyList<CommandSubstitutionSyntax> _substitutions =
        Array.Empty<CommandSubstitutionSyntax>();
    private IReadOnlyList<ExecutionRegionSyntax> _executionRegions =
        Array.Empty<ExecutionRegionSyntax>();

    internal SimpleCommandSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    public Clause Clause { get; internal init; } = new();

    public IReadOnlyList<CommandSubstitutionSyntax> Substitutions
    {
        get => _substitutions;
        internal init => _substitutions = PublicCollection.Copy(value);
    }

    public IReadOnlyList<ExecutionRegionSyntax> ExecutionRegions
    {
        get => _executionRegions;
        internal init => _executionRegions = PublicCollection.Copy(value);
    }
}

/// <summary>An ordered pipeline.</summary>
public sealed record PipelineSyntax : ShellSyntaxNode
{
    private IReadOnlyList<ShellSyntaxNode> _stages = Array.Empty<ShellSyntaxNode>();

    internal PipelineSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    public IReadOnlyList<ShellSyntaxNode> Stages
    {
        get => _stages;
        internal init => _stages = PublicCollection.Copy(value);
    }
}

/// <summary>A command list preserving authored operator relationships.</summary>
public sealed record CommandListSyntax : ShellSyntaxNode
{
    private IReadOnlyList<CommandListItemSyntax> _items =
        Array.Empty<CommandListItemSyntax>();

    internal CommandListSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    public IReadOnlyList<CommandListItemSyntax> Items
    {
        get => _items;
        internal init => _items = PublicCollection.Copy(value);
    }
}

/// <summary>One command-list item and its preceding authored operator.</summary>
public sealed record CommandListItemSyntax
{
    internal CommandListItemSyntax()
    {
    }

    public CompoundOperator Operator { get; internal init; }

    public ShellSyntaxNode Command { get; internal init; } = null!;
}

/// <summary>A grouped command region.</summary>
public sealed record GroupSyntax : ShellSyntaxNode
{
    internal GroupSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    public ShellGroupKind GroupKind { get; internal init; }

    public ShellBlockSyntax Body { get; internal init; } = null!;
}

public enum ShellGroupKind
{
    Unknown,
    CurrentScope,
    IsolatedScope,
}

/// <summary>A foreach-style loop with a shell-specific iterable spelling.</summary>
public sealed record ForEachSyntax : ShellSyntaxNode
{
    private LoopBindingSyntax _binding = new();

    internal ForEachSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    public string BindingName
    {
        get => _binding.Name;
        internal init => _binding = _binding with { Name = value };
    }

    public ShellSourceFragment BindingSource
    {
        get => _binding.Source;
        internal init => _binding = _binding with { Source = value };
    }

    internal LoopBindingSyntax Binding
    {
        get => _binding;
        init => _binding = value;
    }

    public ShellSourceFragment Iterable { get; internal init; } = null!;

    public ShellBlockSyntax IteratorCommands { get; internal init; } = null!;

    public ShellBlockSyntax Body { get; internal init; } = null!;
}

internal sealed record LoopBindingSyntax
{
    internal string Name { get; init; } = "";

    internal ShellSourceFragment Source { get; init; } = new();
}

/// <summary>An authored source fragment with an exact-or-unavailable outer range.</summary>
public sealed record ShellSourceFragment
{
    internal ShellSourceFragment()
    {
    }

    public string Raw { get; internal init; } = "";

    public int? SourceStart { get; internal init; }

    public int? SourceLength { get; internal init; }
}

/// <summary>A command substitution whose produced value is analyzed separately.</summary>
public sealed record CommandSubstitutionSyntax : ShellSyntaxNode
{
    internal CommandSubstitutionSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    public ShellBlockSyntax Body { get; internal init; } = null!;
}

/// <summary>An authored body whose activation is described by shell semantics.</summary>
public sealed record ExecutionRegionSyntax : ShellSyntaxNode
{
    internal ExecutionRegionSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    public ExecutionRegionOrigin Origin { get; internal init; }

    public ClauseElement? HostArgument { get; internal init; }

    internal int? HostClauseElementIndex { get; init; }

    public ExecutionRegionPhase Phase { get; internal init; }

    public ExecutionRegionTiming Timing { get; internal init; }

    public ExecutionRegionCardinality Cardinality { get; internal init; }

    public ShellBlockSyntax Body { get; internal init; } = null!;
}

public enum ExecutionRegionOrigin
{
    Unknown,
    DirectCall,
    DotSource,
    CommandArgument,
}

public enum ExecutionRegionPhase
{
    Unknown,
    Main,
    Initialization,
    Begin,
    Process,
    End,
    Filter,
    Action,
    Completion,
}

public enum ExecutionRegionTiming
{
    Unknown,
    Synchronous,
    Concurrent,
    Deferred,
}

public enum ExecutionRegionCardinality
{
    Unknown,
    Once,
    OncePerInputObject,
    ZeroOrMore,
}
