// -----------------------------------------------------------------------
// <copyright file="ArgKind.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// Classification for an argument token after parsing.
/// </summary>
public enum ArgKind
{
    /// <summary>Literal value (string, number, flag).</summary>
    Literal,

    /// <summary>Token containing an unresolved env var reference.</summary>
    EnvVar,

    /// <summary>Token containing glob metachars (* ? [).</summary>
    Glob,

    /// <summary>Token starting with ~ (tilde).</summary>
    Tilde,

    /// <summary>
    /// Token whose value cannot be safely resolved (unresolved env var,
    /// unexpandable glob). Consumers SHALL treat as "no value extracted"
    /// rather than using Raw as a literal path.
    /// </summary>
    DynamicSkip
}
