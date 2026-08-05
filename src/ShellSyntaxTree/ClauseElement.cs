// -----------------------------------------------------------------------
// <copyright file="ClauseElement.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// One significant source-authored verb, argument, or redirect in a
/// <see cref="Clause"/>. Elements preserve cross-projection source order;
/// executable-specific meaning remains the consumer's responsibility.
/// </summary>
public sealed record ClauseElement
{
    /// <summary>Exact authored source slice, including quote delimiters.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Lexer-decoded logical value. For a redirect this is its decoded target.
    /// For an inline binding, this remains the complete decoded source token.
    /// </summary>
    public string Value { get; init; } = "";

    /// <summary>The semantic projection represented by this element.</summary>
    public ClauseElementRole Role { get; init; }

    /// <summary>
    /// Zero-based start in <see cref="ParsedCommand.Source"/>. Null when a
    /// command-string wrapper cannot be mapped exactly into the outer source.
    /// </summary>
    public int? SourceStart { get; init; }

    /// <summary>
    /// Length of the exact source slice. Null whenever <see cref="SourceStart"/>
    /// is null.
    /// </summary>
    public int? SourceLength { get; init; }

    /// <summary>
    /// Number of parser-classified verb elements authored before this element
    /// in its clause. This is an AST coordinate, not an executable-specific
    /// semantic boundary.
    /// For a verb element, this is its zero-based
    /// <see cref="VerbChain.Tokens"/> index.
    /// </summary>
    public int PrecedingVerbElementCount { get; init; }

    /// <summary>
    /// Classification of this token, inline bound value, or redirect target.
    /// </summary>
    public ArgKind Kind { get; init; }

    /// <summary>True when the authored argument token is option-shaped.</summary>
    public bool IsFlag { get; init; }

    /// <summary>True when this element carries a path-shaped operand.</summary>
    public bool IsPath { get; init; }

    /// <summary>Resolved path when static resolution succeeded; otherwise null.</summary>
    public string? Resolved { get; init; }
}
