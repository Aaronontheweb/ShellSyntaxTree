// -----------------------------------------------------------------------
// <copyright file="BashParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;

namespace ShellSyntaxTree;

/// <summary>Bash implementation of <see cref="IShellParser"/>.</summary>
public sealed class BashParser : IShellParser
{
    // Stored for use by the lexer/parser passes landing in PRs 2-5; the v0.1.0-alpha
    // skeleton only locks the public API surface — see SPEC §17 acceptance criteria.
#pragma warning disable IDE0052, CA1823 // intentionally retained until PR 2 wires this in
    private readonly BashParserOptions _options;
#pragma warning restore IDE0052, CA1823

    /// <summary>
    /// Create a parser with default options.
    /// </summary>
    public BashParser() : this(new BashParserOptions())
    {
    }

    /// <summary>
    /// Create a parser with the supplied options.
    /// </summary>
    /// <param name="options">Resolver knobs (home / working directory).</param>
    public BashParser(BashParserOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _options = options;
    }

    /// <inheritdoc />
    public ParsedCommand Parse(string command)
    {
        // Cross-tfm null-check: ArgumentNullException.ThrowIfNull is net6+ only.
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        throw new NotImplementedException(
            "BashParser.Parse will be implemented in PR 2-5; this stub locks the public API surface.");
    }
}
