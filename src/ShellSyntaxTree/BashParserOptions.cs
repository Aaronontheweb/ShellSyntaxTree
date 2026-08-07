// -----------------------------------------------------------------------
// <copyright file="BashParserOptions.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>Declares which ambient Bash variable facts the caller can prove.</summary>
public enum BashInitialStateMode
{
    /// <summary>No safe assumption is made about ambient Bash variable state.</summary>
    Unknown,

    /// <summary>
    /// The source runs in a new non-interactive Bash process without startup
    /// content or an inherited environment entry for a loop-bound name.
    /// </summary>
    IsolatedNonInteractive,
}

/// <summary>
/// Configuration knobs for <see cref="BashParser"/>. The resolver knobs
/// (<see cref="ShellParserOptions.HomeDirectory"/> /
/// <see cref="ShellParserOptions.WorkingDirectory"/>) live on the shared
/// <see cref="ShellParserOptions"/> base; the shape stays source-compatible
/// with v0.1 — <c>new BashParserOptions { HomeDirectory = ... }</c> still
/// compiles. See SPEC.POWERSHELL.md §2.
/// </summary>
public sealed record BashParserOptions : ShellParserOptions
{
    /// <summary>
    /// Gets the caller-proved initial Bash shell-state contract. The default
    /// fails bounded loop-variable analysis closed.
    /// </summary>
    public BashInitialStateMode InitialStateMode { get; init; }
}
