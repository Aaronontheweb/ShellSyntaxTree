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

    /// <summary>
    /// The first <c>=</c> is quoted, escaped, or part of an expansion or a
    /// substitution. Bash reads it as plain text.
    /// </summary>
    Quoted,
}

/// <summary>
/// The "unquoted <c>=</c>" test of Bash words. In Bash, only an <c>=</c>
/// outside quotes can make a <c>name=value</c> word: <c>a'='b</c> and
/// <c>a\=b</c> are not assignments. The parser splits an inline option value
/// (<c>--name=value</c>) with the same test, so <c>-F'x=y'</c> stays one
/// argument <c>-Fx=y</c>. The assignment-word tilde rule
/// (<see cref="BashAssignmentWordTilde"/>) counts only unquoted, unescaped
/// characters, so it applies the same test.
/// </summary>
/// <remarks>Owner: the parser, call-local.</remarks>
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

        // Quoted strings, ANSI-C strings, and substitutions are never plain.
        if (token.Kind != BashTokenKind.Word || token.ResolverValue is null)
        {
            return BashFirstEquals.Quoted;
        }

        // A lexer word holds only unquoted text. The lexer keeps an escaped
        // character (`\=`) in its own fragment with a two-character source
        // span, so a plain `=` is a literal fragment whose source text is
        // exactly its value.
        foreach (var fragment in token.ResolverValue.Fragments)
        {
            if (fragment.Value.IndexOf('=') < 0)
            {
                continue;
            }

            return fragment.Kind == ShellValueFragmentKind.Literal &&
                   fragment.SourceLength == fragment.Value.Length
                ? BashFirstEquals.Plain
                : BashFirstEquals.Quoted;
        }

        return BashFirstEquals.Quoted;
    }

    /// <summary>The answer for a word joined from <paramref name="first"/> and <paramref name="second"/>.</summary>
    internal static BashFirstEquals Join(BashToken first, BashToken second)
    {
        var head = Classify(first);
        return head != BashFirstEquals.None ? head : Classify(second);
    }
}
