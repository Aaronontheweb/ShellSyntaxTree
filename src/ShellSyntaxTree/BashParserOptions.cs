// -----------------------------------------------------------------------
// <copyright file="BashParserOptions.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>Configuration knobs for <see cref="BashParser"/>.</summary>
public sealed record BashParserOptions
{
    /// <summary>
    /// User home directory used to expand <c>~</c> and <c>$HOME</c> tokens
    /// during resolution. Defaults to
    /// <see cref="System.Environment.SpecialFolder.UserProfile"/>.
    /// </summary>
    public string? HomeDirectory { get; init; }

    /// <summary>
    /// Working directory used to resolve relative path tokens during
    /// resolution. Defaults to the daemon-process cwd.
    /// </summary>
    public string? WorkingDirectory { get; init; }
}
