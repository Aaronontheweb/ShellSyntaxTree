// -----------------------------------------------------------------------
// <copyright file="ClauseElementRole.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// The semantic projection to which a source-authored clause element belongs.
/// </summary>
public enum ClauseElementRole
{
    /// <summary>A token represented in <see cref="Clause.Verb"/>.</summary>
    Verb,

    /// <summary>An authored argument token.</summary>
    Argument,

    /// <summary>A redirect operator and its target.</summary>
    Redirect,
}
