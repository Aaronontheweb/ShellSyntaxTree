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

    /// <summary>Convenience: tokens joined with spaces.</summary>
    public string Joined => string.Join(" ", Tokens);
}
