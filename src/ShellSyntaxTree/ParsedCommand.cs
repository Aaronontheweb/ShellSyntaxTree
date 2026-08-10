// -----------------------------------------------------------------------
// <copyright file="ParsedCommand.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal;

namespace ShellSyntaxTree;

/// <summary>
/// The top-level result of parsing. Always returned (never null).
/// </summary>
public sealed record ParsedCommand
{
    private IReadOnlyList<CommandOccurrence> _commands = Array.Empty<CommandOccurrence>();

    /// <summary>The original input string, verbatim.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Canonical authored nested structure. Direct-source nodes have exact
    /// source ranges; decoded wrapper nodes use unavailable ranges unless an
    /// exact outer mapping exists.
    /// </summary>
    public ShellBlockSyntax Syntax { get; internal init; } = new();

    /// <summary>
    /// Canonical authorization projection containing every authored simple
    /// command that may execute exactly once in deterministic source order.
    /// </summary>
    public IReadOnlyList<CommandOccurrence> Commands
    {
        get => _commands;
        internal init => _commands = PublicCollection.Copy(value);
    }

    /// <summary>
    /// Conservative v0.2 compatibility projection. Existing simple-command
    /// behavior remains available; v0.3 security consumers use
    /// <see cref="Commands"/>.
    /// </summary>
    public IReadOnlyList<Clause> Clauses { get; init; } = Array.Empty<Clause>();

    /// <summary>
    /// True when the parser could not account for every executable region.
    /// When true, <see cref="Commands"/> and <see cref="Clauses"/> are empty;
    /// <see cref="Syntax"/> may contain partial diagnostic evidence only.
    /// </summary>
    public bool IsUnparseable { get; init; }

    /// <summary>
    /// Human-readable diagnostic when IsUnparseable=true; null otherwise.
    /// </summary>
    public string? UnparseableReason { get; init; }
}
