// -----------------------------------------------------------------------
// <copyright file="Arg.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// One argument token after the verb chain. Includes resolution state.
/// </summary>
public sealed record Arg
{
    /// <summary>Verbatim token from the source.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Resolved value for path tokens — tilde expanded, env vars
    /// substituted, normalized to absolute path against
    /// BashParserOptions.WorkingDirectory. Null when Kind is not a path
    /// (Literal non-path / Glob / DynamicSkip).
    /// </summary>
    public string? Resolved { get; init; }

    /// <summary>Token kind. See <see cref="ArgKind"/>.</summary>
    public ArgKind Kind { get; init; }

    /// <summary>
    /// True when this token starts with '-' or '--' (a flag, not a
    /// positional arg).
    /// </summary>
    // netstandard2.0 lacks string.StartsWith(char); use indexer for cross-tfm parity.
    public bool IsFlag => Raw.Length > 0 && Raw[0] == '-';

    /// <summary>
    /// True when this token is a path the clause operates on (per the
    /// per-verb pathArgs table; see SPEC §7). Set during parsing so
    /// consumers don't reapply per-verb rules.
    /// </summary>
    public bool IsPath { get; init; }

    /// <summary>
    /// True when this Arg is a synthetic attribution arg representing the
    /// working directory inherited from a preceding <c>cd</c>/<c>chdir</c>
    /// clause in the same compound. Default false. See SPEC §9.
    /// </summary>
    public bool IsCwdAttribution { get; init; }
}
