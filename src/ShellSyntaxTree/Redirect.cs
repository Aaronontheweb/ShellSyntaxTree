// -----------------------------------------------------------------------
// <copyright file="Redirect.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;

namespace ShellSyntaxTree;

/// <summary>
/// One redirect operator on a clause (e.g. <c>&gt; out.txt</c>, <c>2&gt;&amp;1</c>).
/// </summary>
public sealed record Redirect
{
    /// <summary>Direction of the redirect.</summary>
    public RedirectDirection Direction { get; init; }

    /// <summary>Target path (resolved per Arg conventions).</summary>
    public string Target { get; init; } = "";

    /// <summary>True when target is a dynamic token (skip).</summary>
    public bool IsDynamicSkip { get; init; }
}
