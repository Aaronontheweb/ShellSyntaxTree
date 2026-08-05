// -----------------------------------------------------------------------
// <copyright file="Clause.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree;

/// <summary>
/// One logical command within a compound. Each clause has its own verb
/// chain, args, redirects, and the operator that joined it to the previous
/// clause.
/// </summary>
public sealed record Clause
{
    /// <summary>
    /// The operator joining this clause to the previous one. The first
    /// clause in a ParsedCommand has Operator=None. Subsequent clauses
    /// carry the operator that preceded them in the source
    /// (e.g. <c>a &amp;&amp; b</c> produces clauses [{None,a}, {AndIf,b}]).
    /// </summary>
    public CompoundOperator Operator { get; init; }

    /// <summary>The verb chain (see SPEC §3.3 and §6).</summary>
    public VerbChain Verb { get; init; } = new();

    /// <summary>
    /// All argument tokens after the verb chain, in source order. Includes
    /// flags and positional args. See <see cref="Arg.Kind"/> for token kind.
    /// </summary>
    public IReadOnlyList<Arg> Args { get; init; } = Array.Empty<Arg>();

    /// <summary>
    /// Redirect operators on this clause (<c>&gt;</c>, <c>&gt;&gt;</c>,
    /// <c>&lt;</c>, <c>2&gt;</c>, <c>2&gt;&gt;</c>). Each entry includes
    /// direction and target path.
    /// </summary>
    public IReadOnlyList<Redirect> Redirects { get; init; } = Array.Empty<Redirect>();

    /// <summary>
    /// Significant source-authored verbs, arguments, and redirects in source
    /// order. Synthetic cwd attribution is intentionally excluded. See
    /// SPEC §3 <c>ClauseElement</c>.
    /// </summary>
    public IReadOnlyList<ClauseElement> Elements { get; init; } = Array.Empty<ClauseElement>();

    /// <summary>
    /// True when this clause is wrapped in a subshell (parens). Subshells
    /// isolate cd state — see SPEC §9.
    /// </summary>
    public bool IsSubshell { get; init; }

    /// <summary>
    /// True when this clause is the result of recursing into a
    /// command-string wrapper — bash <c>bash -c "..."</c> / <c>sh -c "..."</c>,
    /// or PowerShell <c>pwsh -Command "..."</c> / <c>pwsh -c "..."</c> /
    /// <c>pwsh -EncodedCommand ...</c> / static
    /// <c>Invoke-Expression '...'</c>. Useful for consumers that want to
    /// surface "this came from a wrapped invocation" in UI. See
    /// SPEC.POWERSHELL.md §3 / §10.
    /// </summary>
    public bool IsCommandStringWrapped { get; init; }
}
