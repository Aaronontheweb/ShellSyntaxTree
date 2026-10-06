// -----------------------------------------------------------------------
// <copyright file="BashLineContinuation.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Text;

namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>
/// The quoting context that decides whether Bash removes a backslash-newline
/// pair (a line continuation).
/// </summary>
internal enum BashContinuationContext
{
    /// <summary>
    /// Bash keeps the pair: single quotes, <c>$'…'</c>, a comment, and the
    /// body of a heredoc. An expanding heredoc body fails closed on a pair
    /// in its own scan, because Bash also joins its lines before it looks
    /// for the delimiter.
    /// </summary>
    None,

    /// <summary>
    /// Unquoted text, also inside <c>$( )</c>, <c>${ }</c>, and backticks.
    /// The parser also accepts a carriage return before the newline here.
    /// </summary>
    Unquoted,

    /// <summary>Double-quoted text. Only backslash + LF is a pair.</summary>
    DoubleQuoted,
}

/// <summary>
/// Bash removes a line continuation before it splits the input into tokens
/// (#243). Thus <c>$\⏎(id)</c> is the command substitution <c>$(id)</c>,
/// <c>$n\⏎dir</c> reads the variable <c>ndir</c>, and <c>&amp;\⏎&amp;</c> is
/// <c>&amp;&amp;</c>. A scan that reads the second character of a
/// multi-character construct must skip the pairs with this type. Otherwise
/// the construct reads as literal text and a command can hide in it.
/// </summary>
internal static class BashLineContinuation
{
    /// <summary>
    /// The length of the line continuation at <paramref name="index"/>, or 0.
    /// </summary>
    internal static int LengthAt(
        ReadOnlySpan<char> source,
        int index,
        BashContinuationContext context)
    {
        if (context == BashContinuationContext.None ||
            index < 0 || index + 1 >= source.Length || source[index] != '\\')
        {
            return 0;
        }

        var next = source[index + 1];
        if (next == '\n')
        {
            return 2;
        }

        // The unquoted lexer has always read `\` + CR (+ LF) as a
        // continuation. Double quotes keep `\` + CR as text, as Bash does.
        if (next == '\r' && context == BashContinuationContext.Unquoted)
        {
            return index + 2 < source.Length && source[index + 2] == '\n' ? 3 : 2;
        }

        return 0;
    }

    /// <summary>
    /// The first index at or after <paramref name="index"/> that does not
    /// start a line continuation.
    /// </summary>
    internal static int Skip(
        ReadOnlySpan<char> source,
        int index,
        BashContinuationContext context)
    {
        while (true)
        {
            var length = LengthAt(source, index, context);
            if (length == 0)
            {
                return index;
            }

            index += length;
        }
    }

    /// <summary>
    /// Returns <paramref name="text"/> without its line continuations. Use it
    /// only for text that one context covers, such as a parameter name or an
    /// unquoted word. A backslash escapes the next character, so in
    /// <c>\\</c> + newline the newline stays.
    /// </summary>
    internal static string Remove(ReadOnlySpan<char> text, BashContinuationContext context)
    {
        StringBuilder? builder = null;
        var copied = 0;
        var index = 0;
        while (index < text.Length)
        {
            var length = LengthAt(text, index, context);
            if (length == 0)
            {
                index += text[index] == '\\' && context != BashContinuationContext.None ? 2 : 1;
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text.Slice(copied, index - copied).ToString());
            index += length;
            copied = index;
        }

        if (builder is null)
        {
            return text.ToString();
        }

        builder.Append(text.Slice(copied).ToString());
        return builder.ToString();
    }

    /// <summary>
    /// Matches <paramref name="expected"/> at <paramref name="index"/>, with
    /// line continuations allowed between its characters. On success,
    /// <paramref name="end"/> is the index after the last matched character.
    /// </summary>
    internal static bool TryMatch(
        ReadOnlySpan<char> source,
        int index,
        string expected,
        BashContinuationContext context,
        out int end)
    {
        end = index;
        for (var position = 0; position < expected.Length; position++)
        {
            if (position > 0)
            {
                end = Skip(source, end, context);
            }

            if (end >= source.Length || source[end] != expected[position])
            {
                end = index;
                return false;
            }

            end++;
        }

        return true;
    }
}
