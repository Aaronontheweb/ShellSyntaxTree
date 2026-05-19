// -----------------------------------------------------------------------
// <copyright file="ShellParserOptions.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// Shell-neutral configuration shared by every <see cref="IShellParser"/>
/// implementation. Carries the resolver knobs used to expand and normalize
/// path tokens. See SPEC.POWERSHELL.md §2.
/// </summary>
public abstract record ShellParserOptions
{
    /// <summary>
    /// User home directory used to expand <c>~</c>, <c>$HOME</c>, and
    /// <c>$env:USERPROFILE</c> tokens during resolution. Defaults to
    /// <see cref="System.Environment.SpecialFolder.UserProfile"/>.
    /// </summary>
    public string? HomeDirectory { get; init; }

    /// <summary>
    /// Working directory used to resolve relative path tokens during
    /// resolution. Defaults to the daemon-process cwd.
    /// </summary>
    public string? WorkingDirectory { get; init; }
}
