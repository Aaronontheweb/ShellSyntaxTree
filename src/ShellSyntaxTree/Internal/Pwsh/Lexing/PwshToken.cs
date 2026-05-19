// -----------------------------------------------------------------------
// <copyright file="PwshToken.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree.Internal.Pwsh.Lexing;

/// <summary>
/// One token emitted by <see cref="PwshLexer"/>.
/// </summary>
/// <param name="Kind">Token class — see <see cref="PwshTokenKind"/>.</param>
/// <param name="Value">
/// The token's logical content, with quote delimiters stripped for
/// <see cref="PwshTokenKind.QuotedString"/>. For <see cref="PwshTokenKind.Word"/>
/// this is the text after backtick-escape processing. For
/// <see cref="PwshTokenKind.Parameter"/> this is the verbatim <c>-Name</c>
/// or <c>-Name:value</c> text. For <see cref="PwshTokenKind.ScriptBlock"/>,
/// <see cref="PwshTokenKind.Subexpression"/>, <see cref="PwshTokenKind.Splat"/>,
/// and <see cref="PwshTokenKind.StopParsing"/> this is the full verbatim
/// source slice. Empty for kinds that carry no content.
/// </param>
/// <param name="OperatorText">For <see cref="PwshTokenKind.Operator"/>, the
/// literal operator text. Null otherwise.</param>
/// <param name="SourceStart">0-based index into the original input where
/// this token starts.</param>
/// <param name="SourceLength">Length of the original-input slice this token
/// covers, in chars.</param>
/// <param name="UnparseableReason">For
/// <see cref="PwshTokenKind.UnparseableSentinel"/>, the human-readable
/// reason. Null otherwise.</param>
internal readonly record struct PwshToken(
    PwshTokenKind Kind,
    string Value,
    string? OperatorText,
    int SourceStart,
    int SourceLength,
    string? UnparseableReason)
{
    /// <summary>
    /// True when this is a single-quoted <see cref="PwshTokenKind.QuotedString"/>
    /// or a literal (<c>@'...'@</c>) here-string. PowerShell semantics:
    /// contents are literal bytes — no variable expansion, no escape
    /// processing. The resolver consults this to bypass meta-character
    /// handling (SPEC.POWERSHELL.md §8 step 0).
    /// </summary>
    public bool IsSingleQuoted { get; init; }

    /// <summary>
    /// True when this <see cref="PwshTokenKind.QuotedString"/> came from a
    /// here-string (<c>@" ... "@</c> or <c>@' ... '@</c>).
    /// </summary>
    public bool IsHereString { get; init; }

    /// <summary>
    /// True when this <see cref="PwshTokenKind.Whitespace"/> token contains
    /// a newline and therefore acts as a statement separator equivalent to
    /// <c>;</c> (SPEC.POWERSHELL.md §4). The parser retains these past the
    /// significant-token filter and splits statements on them.
    /// </summary>
    public bool IsStatementSeparator { get; init; }
}
