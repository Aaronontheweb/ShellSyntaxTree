// -----------------------------------------------------------------------
// <copyright file="BashTokenKind.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>
/// Token classes emitted by <see cref="BashLexer"/>. See SPEC §5 for the
/// canonical definitions; the enum mirrors that table 1:1, plus two
/// additional kinds the parser uses to honor the locked interpretation
/// for opaque regions and for unparseable bash constructs.
/// </summary>
internal enum BashTokenKind
{
    /// <summary>Non-whitespace, non-operator, non-quote chars. Examples:
    /// <c>git</c>, <c>/etc/foo</c>, <c>--force</c>, <c>~/path</c>,
    /// <c>$VAR</c>, <c>${VAR}</c>.</summary>
    Word,

    /// <summary>Single- or double-quoted string. The lexer strips the
    /// outer quote delimiters from <see cref="BashToken.Value"/>.
    /// SPEC §5.</summary>
    QuotedString,

    /// <summary>One of the bash operators recognized in v0.1: <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>;</c>, <c>|</c>, <c>&gt;</c>, <c>&gt;&gt;</c>,
    /// <c>&lt;</c>, <c>2&gt;</c>, <c>2&gt;&gt;</c>, <c>(</c>, <c>)</c>,
    /// <c>&lt;&lt;</c>, <c>&lt;&lt;-</c>. The literal text is in
    /// <see cref="BashToken.OperatorText"/>.</summary>
    Operator,

    /// <summary>Run of spaces/tabs. Emitted for source-fidelity; the
    /// parser typically filters these out.</summary>
    Whitespace,

    /// <summary>Backslash + newline line continuation. Treated as
    /// whitespace by the parser. SPEC §5.</summary>
    Continuation,

    /// <summary>A bash line comment — <c>#</c> at a word boundary
    /// through end-of-line (the terminating newline is preserved as
    /// a separate <see cref="Whitespace"/> token so statement
    /// boundaries are unaffected). Emitted for source fidelity; the
    /// parser drops these in <c>FilterSignificant</c> alongside
    /// <see cref="Whitespace"/> and <see cref="Continuation"/>.
    /// SPEC §5.</summary>
    Comment,

    /// <summary>An opaque region — <c>$(…)</c> or backtick-quoted
    /// <c>`…`</c>. The parser consumes one of these as a single
    /// <c>Arg{ Kind = DynamicSkip, IsPath = false }</c> per the v0.1
    /// locked interpretation. <see cref="BashToken.Value"/> contains the
    /// region's full source slice (delimiters included).</summary>
    OpaqueSubstitution,

    /// <summary>An unparseable construct that should set
    /// <c>ParsedCommand.IsUnparseable = true</c> on the outer parsed
    /// command — e.g. <c>$((…))</c> arithmetic expansion, complex
    /// parameter expansion <c>${var//pat/repl}</c>, or an unbalanced
    /// quote / unterminated heredoc. The reason text is in
    /// <see cref="BashToken.UnparseableReason"/>.</summary>
    UnparseableSentinel,
}
