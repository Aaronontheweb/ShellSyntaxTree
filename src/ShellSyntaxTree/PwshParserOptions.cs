// -----------------------------------------------------------------------
// <copyright file="PwshParserOptions.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>Declares which ambient PowerShell runspace facts the caller can prove.</summary>
public enum PwshInitialStateMode
{
    /// <summary>No safe assumption is made about ambient PowerShell runspace state.</summary>
    Unknown,

    /// <summary>
    /// The source runs in a new non-interactive PowerShell process with
    /// profiles, startup state, and module discovery constrained as specified.
    /// </summary>
    IsolatedNonInteractiveNoProfile,
}

/// <summary>
/// Configuration knobs for <see cref="PwshParser"/>. The resolver knobs live
/// on the shared <see cref="ShellParserOptions"/> base.
/// </summary>
public sealed record PwshParserOptions : ShellParserOptions
{
    /// <summary>
    /// Gets the caller-proved initial PowerShell runspace-state contract. The
    /// default fails bounded loop-variable analysis closed.
    /// </summary>
    public PwshInitialStateMode InitialStateMode { get; init; }
}
