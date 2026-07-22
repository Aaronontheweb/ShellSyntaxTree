// -----------------------------------------------------------------------
// <copyright file="PwshTokenKind.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree.Internal.Pwsh.Lexing;

/// <summary>
/// Token classes emitted by <see cref="PwshLexer"/>. See
/// SPEC.POWERSHELL.md §5 for the canonical definitions.
/// </summary>
internal enum PwshTokenKind
{
    /// <summary>A bare token — command name, native arg, path, number,
    /// <c>$var</c>, <c>${name}</c>, <c>$env:PATH</c>, drive-qualified
    /// <c>C:\x</c>. Backtick escapes processed; simple <c>$x</c> /
    /// <c>${x}</c> absorbed.</summary>
    Word,

    /// <summary>A parameter-shaped token, including hyphenated cmdlet
    /// parameters and native options. Inline <c>:</c> or <c>=</c> text stays
    /// in <see cref="PwshToken.Value"/>; the parser interprets it according
    /// to command kind.</summary>
    Parameter,

    /// <summary>Single-quoted, double-quoted, or here-string. Delimiters
    /// stripped from the value. Carries <see cref="PwshToken.IsSingleQuoted"/>
    /// and <see cref="PwshToken.IsHereString"/>.</summary>
    QuotedString,

    /// <summary>One of <c>;</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>,
    /// <c>&amp;</c>, <c>(</c>, <c>)</c>, or a redirect operator. The literal
    /// text is in <see cref="PwshToken.OperatorText"/>.</summary>
    Operator,

    /// <summary>Spaces/tabs, or a newline run. A newline-bearing run carries
    /// <see cref="PwshToken.IsStatementSeparator"/> = true.</summary>
    Whitespace,

    /// <summary>Backtick + newline line continuation. Treated as
    /// whitespace by the parser.</summary>
    Continuation,

    /// <summary>A <c>#</c> line comment or a <c>&lt;# ... #&gt;</c> block
    /// comment. Dropped by the significant-token filter.</summary>
    Comment,

    /// <summary>A balanced <c>{ ... }</c> script-block region, emitted
    /// whole. Parser → <c>DynamicSkip</c> arg.</summary>
    ScriptBlock,

    /// <summary>A balanced <c>$( ... )</c>, <c>@( ... )</c>, or
    /// <c>@{ ... }</c> region, emitted whole. Parser → <c>DynamicSkip</c>
    /// arg.</summary>
    Subexpression,

    /// <summary><c>@identifier</c> splatting. Parser → <c>DynamicSkip</c>
    /// arg.</summary>
    Splat,

    /// <summary>The <c>--%</c> stop-parsing token; <see cref="PwshToken.Value"/>
    /// carries the verbatim line remainder. Parser → one <c>DynamicSkip</c>
    /// arg.</summary>
    StopParsing,

    /// <summary>An unbalanced region or a lex-time-detected unsupported
    /// construct. The reason is in <see cref="PwshToken.UnparseableReason"/>;
    /// the parser lifts it to <c>ParsedCommand.IsUnparseable</c>.</summary>
    UnparseableSentinel,
}
