// -----------------------------------------------------------------------
// <copyright file="ShellConditionalSyntax.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree;

internal sealed record ConditionLoopSyntax : ShellSyntaxNode
{
    private protected override object LibraryOwnership => this;

    internal ConditionLoopKind LoopKind { get; init; }

    internal ShellBlockSyntax Condition { get; init; } = new();

    internal ShellBlockSyntax Body { get; init; } = new();
}

internal enum ConditionLoopKind
{
    Unknown,
    While,
    Until,
}

internal sealed record ConditionalSyntax : ShellSyntaxNode
{
    private protected override object LibraryOwnership => this;

    internal IReadOnlyList<ConditionalBranchSyntax> Branches { get; init; } =
        Array.Empty<ConditionalBranchSyntax>();

    internal ShellBlockSyntax? Else { get; init; }
}

internal sealed record ConditionalBranchSyntax : ShellSyntaxNode
{
    private protected override object LibraryOwnership => this;

    internal ShellBlockSyntax Condition { get; init; } = new();

    internal ShellBlockSyntax Body { get; init; } = new();
}
