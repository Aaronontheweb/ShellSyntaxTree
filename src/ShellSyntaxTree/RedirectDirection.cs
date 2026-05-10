// -----------------------------------------------------------------------
// <copyright file="RedirectDirection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// Direction of a clause redirect operator.
/// </summary>
public enum RedirectDirection
{
    /// <summary><c>&lt;</c> — input redirect.</summary>
    In,

    /// <summary><c>&gt;</c> — stdout redirect (truncate).</summary>
    Out,

    /// <summary><c>&gt;&gt;</c> — stdout append.</summary>
    Append,

    /// <summary><c>2&gt;</c> — stderr redirect (truncate).</summary>
    ErrOut,

    /// <summary><c>2&gt;&gt;</c> — stderr append.</summary>
    ErrAppend
}
