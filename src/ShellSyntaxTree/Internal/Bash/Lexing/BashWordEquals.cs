// -----------------------------------------------------------------------
// <copyright file="BashWordEquals.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>Where the first <c>=</c> of a word's decoded text comes from.</summary>
internal enum BashFirstEquals
{
    /// <summary>The decoded text has no <c>=</c>.</summary>
    None,

    /// <summary>The first <c>=</c> is unquoted and unescaped word text.</summary>
    Plain,

    /// <summary>The first <c>=</c> is quoted or escaped word text.</summary>
    Quoted,

    /// <summary>
    /// The first <c>=</c> of the decoded spelling is part of an expansion or a
    /// substitution. Bash gives its value only at run time.
    /// </summary>
    Expanded,
}

/// <summary>
/// Finds where the first <c>=</c> of a Bash word comes from.
/// </summary>
/// <remarks>
/// <para>
/// A program never sees shell quotes. It reads <c>--file\=/x</c>,
/// <c>--file'='/x</c>, and <c>"--file=/x"</c> as <c>--file=/x</c>, and splits
/// the word at its first <c>=</c>. So the parser splits an inline option
/// value at the first <c>=</c> when that <c>=</c> is word text, quoted or not
/// (<see cref="BashFirstEquals.Plain"/> or <see cref="BashFirstEquals.Quoted"/>),
/// and the value gets the same facts as in the unquoted form. When the first
/// <c>=</c> of the spelling is in an expansion (<c>--$(echo a=b)</c>), the split
/// point is not known, and the word stays one argument.
/// </para>
/// <para>
/// Only an unquoted <c>=</c> makes a Bash <c>name=value</c> word
/// (<c>a'='b</c> is not an assignment). The assignment-word tilde rule
/// (<see cref="BashAssignmentWordTilde"/>) counts only unquoted, unescaped
/// characters, which is the <see cref="BashFirstEquals.Plain"/> test.
/// </para>
/// <para>Owner: the lexer and the parser, call-local.</para>
/// </remarks>
internal static class BashWordEquals
{
    internal static BashFirstEquals Classify(BashToken token)
    {
        // A lexer word, and a word that the parser joined from adjacent
        // parts, carry the answer.
        if (token.FirstEquals is { } known)
        {
            return known;
        }

        if (token.Value.IndexOf('=') < 0)
        {
            return BashFirstEquals.None;
        }

        if (token.ResolverValue is null ||
            token.Kind is not (BashTokenKind.Word or BashTokenKind.QuotedString))
        {
            return BashFirstEquals.Expanded;
        }

        // A lexer word holds only unquoted text. The lexer keeps an escaped
        // character (`\=`) in its own fragment with a two-character source
        // span, so a plain `=` is a literal fragment whose source text is
        // exactly its value. Every literal fragment of a quoted string is
        // quoted.
        foreach (var fragment in token.ResolverValue.Fragments)
        {
            if (fragment.Value.IndexOf('=') < 0)
            {
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Literal)
            {
                return BashFirstEquals.Expanded;
            }

            return token.Kind == BashTokenKind.Word &&
                   fragment.SourceLength == fragment.Value.Length
                ? BashFirstEquals.Plain
                : BashFirstEquals.Quoted;
        }

        return BashFirstEquals.Expanded;
    }

    /// <summary>The answer for a word joined from <paramref name="first"/> and <paramref name="second"/>.</summary>
    internal static BashFirstEquals Join(BashToken first, BashToken second)
    {
        var head = Classify(first);
        return head != BashFirstEquals.None ? head : Classify(second);
    }

    /// <summary>
    /// True when the parser splits the word at its first decoded <c>=</c>:
    /// that <c>=</c> is word text, quoted or not.
    /// </summary>
    internal static bool SplitsInlineValue(BashToken token) =>
        Classify(token) is BashFirstEquals.Plain or BashFirstEquals.Quoted;
}
