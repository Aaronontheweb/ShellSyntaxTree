// -----------------------------------------------------------------------
// <copyright file="BashToken.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree.Internal.Resolving;

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
    string? UnparseableReason)
{
    /// <summary>
    /// True when this is a single-quoted <see cref="BashTokenKind.QuotedString"/>.
    /// Bash semantics: contents are literal bytes — no variable expansion,
    /// no glob handling, no <c>filesystem::</c> stripping. The resolver
    /// consults this flag to bypass meta-character processing on the
    /// token's <see cref="Value"/>. Default <c>false</c> for every other
    /// kind and for double-quoted strings (which allow <c>$HOME</c>
    /// substitution per SPEC §8 step 3).
    /// </summary>
    public bool IsSingleQuoted { get; init; }

    /// <summary>
    /// Resolver-relevant decoded fragments. Null only for token kinds that
    /// never carry an argument value.
    /// </summary>
    public ShellValue? ResolverValue { get; init; }

    /// <summary>
    /// Resolver fragments from the authored heredoc body associated with a
    /// delimiter token. Null for ordinary tokens and malformed heredocs.
    /// </summary>
    public ShellValue? HeredocBodyValue { get; init; }

    /// <summary>
    /// Exclusive authored-source end of the heredoc terminator associated
    /// with a delimiter token. Null for ordinary tokens and malformed
    /// heredocs. This lets structural spans cover the body without exposing
    /// the body as an ordinary compatibility token.
    /// </summary>
    public int? HeredocSourceEnd { get; init; }

    /// <summary>
    /// True when this <see cref="BashTokenKind.Whitespace"/> token contains
    /// a newline and therefore acts as a statement separator equivalent to
    /// <c>;</c> per SPEC §4. The lexer sets this on the newline branch and
    /// on the heredoc-terminator newline; the parser retains these tokens
    /// past <c>FilterSignificant</c> and splits clauses on them. Default
    /// <c>false</c> for plain space/tab whitespace and every other kind.
    /// </summary>
    public bool IsStatementSeparator { get; init; }
}
