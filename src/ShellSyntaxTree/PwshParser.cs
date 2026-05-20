// -----------------------------------------------------------------------
// <copyright file="PwshParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;

namespace ShellSyntaxTree;

/// <summary>
/// PowerShell implementation of <see cref="IShellParser"/>. Parses
/// PowerShell command pipelines into the shared <see cref="ParsedCommand"/>
/// AST. See SPEC.POWERSHELL.md.
/// </summary>
public sealed class PwshParser : IShellParser
{
    private readonly PwshParserOptions _options;

    /// <summary>
    /// Create a parser with default options.
    /// </summary>
    public PwshParser() : this(new PwshParserOptions())
    {
    }

    /// <summary>
    /// Create a parser with the supplied options.
    /// </summary>
    /// <param name="options">Resolver knobs (home / working directory).</param>
    public PwshParser(PwshParserOptions options)
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

        return Internal.Pwsh.Parsing.PwshCommandParser.Parse(command, _options);
    }
}
