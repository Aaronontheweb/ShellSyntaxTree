// -----------------------------------------------------------------------
// <copyright file="ParsedCommand.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree;

/// <summary>
/// The top-level result of parsing. Always returned (never null).
/// </summary>
public sealed record ParsedCommand
{
    /// <summary>The original input string, verbatim.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Top-level clauses, split on compound operators
    /// (<c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>). For a simple
    /// command, exactly one clause with Operator=None.
    /// </summary>
    public IReadOnlyList<Clause> Clauses { get; init; } = Array.Empty<Clause>();

    /// <summary>
    /// True when the parser could not produce a clean AST (unbalanced
    /// quotes, unparseable construct). When true, Clauses MAY be partial
    /// or empty. Consumers should route to safe-fail.
    /// </summary>
    public bool IsUnparseable { get; init; }

    /// <summary>
    /// Human-readable diagnostic when IsUnparseable=true; null otherwise.
    /// </summary>
    public string? UnparseableReason { get; init; }
}
