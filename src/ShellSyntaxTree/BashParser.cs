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
    /// <param name="options">Resolver and initial-state analysis options.</param>
    public BashParser(BashParserOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        Internal.Resolving.ShellLaunchFacts.ValidateForBash(options);
        _options = Internal.Resolving.ShellLaunchFacts.NormalizeHome(options);
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

    /// <summary>
    /// Proves exact directories for a bounded static compound command.
    /// Each scoped occurrence retains its parser-proved value and path facts.
    /// </summary>
    public bool TryProjectFiniteScopes(
        string command,
        out BashFiniteScopeProjection? projection)
    {
        var parsed = Parse(command);
        return BashFiniteScopeAnalyzer.TryProject(parsed, _options, out projection);
    }

    /// <summary>
    /// Builds literal twins for each command whose changeable argument words
    /// (an expansion, a tilde) have a proved finite set of values. A twin
    /// writes each such word as one literal value. The parser then parses the
    /// text of that one command in the exact directory of the command, so the
    /// twin occurrence has the facts of the literal command. Returns false,
    /// with a null projection, when no command has twins, and always under
    /// <see cref="BashInitialStateMode.Unknown"/>. A command without twins
    /// keeps the facts of <see cref="Parse(string)"/> only.
    /// </summary>
    public bool TryProjectLiteralTwins(
        string command,
        out BashLiteralTwinProjection? projection)
    {
        var parsed = Parse(command);
        return BashLiteralTwinAnalyzer.TryProject(parsed, _options, out projection);
    }
}
