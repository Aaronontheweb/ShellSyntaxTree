// -----------------------------------------------------------------------
// <copyright file="VerbChain.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree;

/// <summary>
/// The verb of a clause. Multi-token to handle commands like
/// <c>git push</c>, <c>docker compose up</c>, <c>bun run</c>,
/// <c>dotnet test</c>. Length determined by the BashArity table (see SPEC §6).
/// </summary>
public sealed record VerbChain
{
    /// <summary>
    /// Verb tokens in source order. Empty when the clause has no verb
    /// (e.g. clause is just a redirect or an empty fragment).
    /// </summary>
    public IReadOnlyList<string> Tokens { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The canonical, alias-resolved verb identity, set when the parser
    /// resolved the first token of <see cref="Tokens"/> from a shell
    /// built-in alias — e.g. <c>ls</c> / <c>gci</c> / <c>dir</c> resolve to
    /// <c>Get-ChildItem</c>; <c>rm</c> / <c>del</c> resolve to
    /// <c>Remove-Item</c>. <see cref="Tokens"/> always keeps the verbatim
    /// token the user typed; this field carries the resolved name so a
    /// consumer can gate on canonical identity without re-implementing the
    /// alias table.
    ///
    /// Null when no alias resolution applied: every bash clause, and every
    /// PowerShell clause whose verb is already a canonical cmdlet or an
    /// unknown command. Consumers SHOULD use
    /// <c>CanonicalVerb ?? Tokens[0]</c> as the gate key. See
    /// SPEC.POWERSHELL.md §3.
    /// </summary>
    public string? CanonicalVerb { get; init; }

    /// <summary>
    /// True when the clause's command name is a dynamic token the parser
    /// cannot statically identify — a variable (<c>&amp; $exe</c>), an
    /// interpolated name (<c>&amp; "tool-$name"</c>), a subexpression
    /// (<c>&amp; $(Get-Thing)</c>), or another supported value expression at
    /// verb position. <see cref="Tokens"/> still carries the verbatim token;
    /// <see cref="CanonicalVerb"/> is null. An unsupported executable
    /// identity expression makes the whole result unparseable instead.
    ///
    /// A consumer MUST treat a clause with <c>IsDynamic=true</c> as "the
    /// command being run is unknown" and route to safe-fail. Always false
    /// for bash clauses and for PowerShell clauses with a literal command
    /// name. See SPEC.POWERSHELL.md §3.
    /// </summary>
    public bool IsDynamic { get; init; }

    /// <summary>Convenience: tokens joined with spaces.</summary>
    public string Joined => string.Join(" ", Tokens);
}
