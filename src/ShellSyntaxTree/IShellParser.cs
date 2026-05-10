// -----------------------------------------------------------------------
// <copyright file="IShellParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// Parses shell command strings into structured ASTs.
/// </summary>
public interface IShellParser
{
    /// <summary>
    /// Parse the command. Always returns a ParsedCommand; sets
    /// <see cref="ParsedCommand.IsUnparseable"/> when the input cannot
    /// be tokenized (unbalanced quotes, etc.). Never throws on
    /// well-formed strings; throws ArgumentNullException on null input.
    /// </summary>
    ParsedCommand Parse(string command);
}
