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

    /// <summary>
    /// Launcher-proved environment facts for the new shell process (#200).
    /// Null, the default, keeps the earlier behavior. The parser uses the
    /// facts only under an initial-state mode that excludes startup content.
    /// A supplied Bash <c>HOME</c> must not be empty, and it must agree with
    /// <see cref="HomeDirectory"/> when both are set. A relative Bash
    /// <c>cd</c> resolves only when <see cref="WorkingDirectory"/> is set and
    /// these facts prove that <c>CDPATH</c> is unset.
    /// </summary>
    public ShellLaunchEnvironment? LaunchEnvironment { get; init; }
}
