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
    private readonly BashParserOptions _options;

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

        return Internal.Bash.Parsing.BashCommandParser.Parse(command, _options);
    }
}
