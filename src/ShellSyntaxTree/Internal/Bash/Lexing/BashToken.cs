// -----------------------------------------------------------------------
// <copyright file="BashToken.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>
/// One token emitted by <see cref="BashLexer"/>.
/// </summary>
/// <param name="Kind">Token class — see <see cref="BashTokenKind"/>.</param>
/// <param name="Value">
/// The token's logical content, with quote delimiters stripped for
/// <see cref="BashTokenKind.QuotedString"/> per SPEC §5. For
/// <see cref="BashTokenKind.Word"/> this is the literal text after escape
/// processing. For <see cref="BashTokenKind.OpaqueSubstitution"/> this is
/// the full source slice including delimiters (e.g. <c>$(echo foo)</c>).
/// For <see cref="BashTokenKind.UnparseableSentinel"/> this is the source
/// slice that triggered the sentinel. Empty string for kinds where it
/// carries no information (Operator, Whitespace, Continuation).
/// </param>
/// <param name="OperatorText">For <see cref="BashTokenKind.Operator"/>,
/// the literal operator text (<c>"&amp;&amp;"</c>, <c>"&lt;&lt;-"</c>,
/// etc.). Null for every other kind.</param>
/// <param name="SourceStart">0-based index into the original input where
/// this token starts.</param>
/// <param name="SourceLength">Length of the original-input slice this
/// token covers, in chars.</param>
/// <param name="UnparseableReason">For
/// <see cref="BashTokenKind.UnparseableSentinel"/>, the human-readable
/// reason (e.g. <c>"unbalanced quote at position 4"</c>). Null for every
/// other kind.</param>
internal readonly record struct BashToken(
    BashTokenKind Kind,
    string Value,
    string? OperatorText,
    int SourceStart,
    int SourceLength,
    string? UnparseableReason);
