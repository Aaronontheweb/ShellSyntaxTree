// -----------------------------------------------------------------------
// <copyright file="CompoundOperator.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// Operator joining a clause to its predecessor in a compound command.
/// </summary>
public enum CompoundOperator
{
    /// <summary>First clause; no prior operator.</summary>
    None,

    /// <summary><c>&amp;&amp;</c> — short-circuit AND.</summary>
    AndIf,

    /// <summary><c>||</c> — short-circuit OR.</summary>
    OrIf,

    /// <summary><c>;</c> — sequence.</summary>
    Sequence,

    /// <summary><c>|</c> — pipe.</summary>
    Pipe
}
