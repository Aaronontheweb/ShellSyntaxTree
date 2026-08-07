// -----------------------------------------------------------------------
// <copyright file="CommandOccurrence.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree;

/// <summary>One authored simple command that may execute.</summary>
public sealed record CommandOccurrence
{
    /// <summary>Gets the shared compatibility leaf.</summary>
    public Clause Clause { get; init; } = new();

    /// <summary>Gets the nearest structural execution role.</summary>
    public CommandOccurrenceRole ImmediateRole { get; init; }

    /// <summary>Gets ancestry ordered from outermost to innermost.</summary>
    public IReadOnlyList<CommandAncestryFrame> Ancestry { get; init; } =
        Array.Empty<CommandAncestryFrame>();

    /// <summary>Gets bounded effective values at authored argument coordinates.</summary>
    public IReadOnlyList<EffectiveArgument> EffectiveArguments { get; init; } =
        Array.Empty<EffectiveArgument>();

    /// <summary>Gets the effective working-directory proof.</summary>
    public ShellValueDomain WorkingDirectory { get; init; } = ShellValueDomain.Unknown;

    /// <summary>Gets explicit redirect analysis in compatibility redirect order.</summary>
    public IReadOnlyList<RedirectAnalysis> Redirects { get; init; } =
        Array.Empty<RedirectAnalysis>();

    /// <summary>
    /// Gets whether command identity, ancestry, and parser-owned shell analysis
    /// are structurally complete.
    /// </summary>
    public bool IsComplete { get; init; }
}

/// <summary>Identifies the nearest structural role of a command occurrence.</summary>
public enum CommandOccurrenceRole
{
    /// <summary>The role is unknown.</summary>
    Unknown,
    /// <summary>An ordinary command.</summary>
    Ordinary,
    /// <summary>A pipeline stage.</summary>
    PipelineStage,
    /// <summary>A condition command.</summary>
    Condition,
    /// <summary>An iterator-producing command.</summary>
    Iterator,
    /// <summary>A loop-body command.</summary>
    LoopBody,
    /// <summary>A conditional-branch command.</summary>
    Branch,
    /// <summary>A substitution command.</summary>
    Substitution,
    /// <summary>A command inside an execution-bearing region.</summary>
    ExecutionRegion,
}

/// <summary>One compositional structural ancestor of a command occurrence.</summary>
public sealed record CommandAncestryFrame
{
    /// <summary>Gets the ancestor's syntax kind.</summary>
    public ShellSyntaxKind AncestorKind { get; init; }

    /// <summary>Gets the occurrence's region within the ancestor.</summary>
    public CommandAncestryRegion Region { get; init; }

    /// <summary>Gets the child index for repeated regions, when applicable.</summary>
    public int? ChildIndex { get; init; }

    /// <summary>Gets the ancestor source start, when exact.</summary>
    public int? SourceStart { get; init; }

    /// <summary>Gets the ancestor source length, when exact.</summary>
    public int? SourceLength { get; init; }
}

/// <summary>Identifies an occurrence's region within a structural ancestor.</summary>
public enum CommandAncestryRegion
{
    /// <summary>The region is unknown.</summary>
    Unknown,
    /// <summary>The root region.</summary>
    Root,
    /// <summary>An ordinary statement region.</summary>
    Statement,
    /// <summary>A pipeline-stage region.</summary>
    PipelineStage,
    /// <summary>A group body.</summary>
    GroupBody,
    /// <summary>An iterator expression.</summary>
    Iterator,
    /// <summary>A loop body.</summary>
    LoopBody,
    /// <summary>A condition region.</summary>
    Condition,
    /// <summary>A conditional branch.</summary>
    Branch,
    /// <summary>A command substitution.</summary>
    Substitution,
    /// <summary>An execution-bearing region.</summary>
    ExecutionRegion,
}

/// <summary>A bounded effective value at one authored clause-element coordinate.</summary>
public sealed record EffectiveArgument
{
    /// <summary>Gets the index into <see cref="Clause.Elements"/>.</summary>
    public int ClauseElementIndex { get; init; } = -1;

    /// <summary>Gets the effective value proof.</summary>
    public ShellValueDomain Value { get; init; } = ShellValueDomain.Unknown;
}

/// <summary>A bounded, non-executing proof for a shell value.</summary>
public sealed record ShellValueDomain
{
    /// <summary>Gets the shared empty unknown-domain value.</summary>
    public static ShellValueDomain Unknown { get; } = new();

    /// <summary>Gets the proof kind.</summary>
    public ShellValueDomainKind Kind { get; init; }

    /// <summary>Gets exact or finite values.</summary>
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();

    /// <summary>Gets a bounded symbolic pattern.</summary>
    public string? Pattern { get; init; }

    /// <summary>Gets the pattern's conservative covering directory.</summary>
    public string? CoveringDirectory { get; init; }
}

/// <summary>Identifies the strength and shape of a shell-value proof.</summary>
public enum ShellValueDomainKind
{
    /// <summary>No bounded value is proved.</summary>
    Unknown,
    /// <summary>Exactly one value is proved.</summary>
    Exact,
    /// <summary>Two through 32 distinct values are proved.</summary>
    FiniteSet,
    /// <summary>A pattern and conservative covering directory are proved.</summary>
    Pattern,
}
