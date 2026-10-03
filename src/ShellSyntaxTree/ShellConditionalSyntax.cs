// -----------------------------------------------------------------------
// <copyright file="ShellConditionalSyntax.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal;

namespace ShellSyntaxTree;

/// <summary>
/// A Bash <c>while</c> or <c>until</c> loop (v0.4.0-beta.13). The condition
/// runs before each iteration. The body runs while the condition succeeds
/// (<c>while</c>) or fails (<c>until</c>).
/// </summary>
public sealed record ConditionLoopSyntax : ShellSyntaxNode
{
    internal ConditionLoopSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    /// <summary>Gets the loop keyword.</summary>
    public ConditionLoopKind LoopKind { get; internal init; }

    /// <summary>Gets the commands that decide each iteration.</summary>
    public ShellBlockSyntax Condition { get; internal init; } = new();

    /// <summary>Gets the loop body.</summary>
    public ShellBlockSyntax Body { get; internal init; } = new();
}

/// <summary>Identifies the keyword of a condition loop.</summary>
public enum ConditionLoopKind
{
    /// <summary>No loop keyword is proved.</summary>
    Unknown,

    /// <summary>The body runs while the condition succeeds.</summary>
    While,

    /// <summary>The body runs until the condition succeeds.</summary>
    Until,
}

/// <summary>
/// A Bash <c>if</c> statement (v0.4.0-beta.13). The parser tries each branch
/// condition in order and runs the body of the first one that succeeds. When
/// no condition succeeds, the parser runs <see cref="Else"/>.
/// </summary>
public sealed record ConditionalSyntax : ShellSyntaxNode
{
    private IReadOnlyList<ConditionalBranchSyntax> _branches =
        Array.Empty<ConditionalBranchSyntax>();

    internal ConditionalSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    /// <summary>Gets the <c>if</c> branch, then each <c>elif</c> branch.</summary>
    public IReadOnlyList<ConditionalBranchSyntax> Branches
    {
        get => _branches;
        internal init => _branches = PublicCollection.Copy(value);
    }

    /// <summary>Gets the <c>else</c> body, or null when there is none.</summary>
    public ShellBlockSyntax? Else { get; internal init; }
}

/// <summary>One condition and its body in a <see cref="ConditionalSyntax"/>.</summary>
public sealed record ConditionalBranchSyntax : ShellSyntaxNode
{
    internal ConditionalBranchSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    /// <summary>Gets the condition commands.</summary>
    public ShellBlockSyntax Condition { get; internal init; } = new();

    /// <summary>Gets the commands that run when the condition succeeds.</summary>
    public ShellBlockSyntax Body { get; internal init; } = new();
}

/// <summary>
/// A Bash <c>case</c> statement (v0.4.0-beta.13). The shell runs the body of
/// the first item with a pattern that matches the subject word.
/// </summary>
public sealed record CaseSyntax : ShellSyntaxNode
{
    private IReadOnlyList<CaseItemSyntax> _items = Array.Empty<CaseItemSyntax>();

    internal CaseSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    /// <summary>Gets the authored subject word.</summary>
    public ShellSourceFragment Subject { get; internal init; } = new();

    /// <summary>Gets the items in source order.</summary>
    public IReadOnlyList<CaseItemSyntax> Items
    {
        get => _items;
        internal init => _items = PublicCollection.Copy(value);
    }
}

/// <summary>One item of a <see cref="CaseSyntax"/>.</summary>
public sealed record CaseItemSyntax : ShellSyntaxNode
{
    private IReadOnlyList<ShellSourceFragment> _patterns =
        Array.Empty<ShellSourceFragment>();

    internal CaseItemSyntax()
    {
    }

    private protected override object LibraryOwnership => this;

    /// <summary>Gets the authored patterns in source order.</summary>
    public IReadOnlyList<ShellSourceFragment> Patterns
    {
        get => _patterns;
        internal init => _patterns = PublicCollection.Copy(value);
    }

    /// <summary>Gets the commands that run when a pattern matches.</summary>
    public ShellBlockSyntax Body { get; internal init; } = new();
}
