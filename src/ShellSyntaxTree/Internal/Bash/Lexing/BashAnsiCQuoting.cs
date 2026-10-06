// -----------------------------------------------------------------------
// <copyright file="BashAnsiCQuoting.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>
/// Decodes a Bash ANSI-C quoted string <c>$'…'</c> (#232).
/// </summary>
/// <remarks>
/// <para>
/// Bash decodes the backslash escapes in the string and then treats the
/// result as quoted text. <c>$'\x6beys'</c> is the word <c>keys</c>. Before
/// 0.4.0-beta.19 the parser read a <c>$'…'</c> after other text as a literal
/// <c>$</c> and single-quoted text, so it published a wrong exact value.
/// </para>
/// <para>
/// The decoder accepts only escapes with one exact ASCII result:
/// <c>\a \b \e \E \f \n \r \t \v \\ \' \" \?</c>, octal <c>\N</c> to
/// <c>\NNN</c>, and hexadecimal <c>\xH</c> or <c>\xHH</c>, with a value from 1
/// to 127. It rejects <c>\u</c> and <c>\U</c> (the result depends on the
/// locale), <c>\c</c>, a NUL (Bash ends the string there), a byte above 127,
/// <c>\x</c> without a digit, and every other escape. A rejected string makes
/// the source unparseable, so the parser never publishes a wrong value.
/// </para>
/// </remarks>
internal static class BashAnsiCQuoting
{
    /// <summary>
    /// Decodes the string whose opening quote is at
    /// <paramref name="openQuote"/> into <paramref name="value"/>. A line
    /// continuation can come between the <c>$</c> and the quote (#243).
    /// <paramref name="endExclusive"/> receives the index after the closing
    /// quote. On failure, <paramref name="error"/> gives the reason.
    /// </summary>
    internal static bool TryDecode(
        ReadOnlySpan<char> src,
        int openQuote,
        ShellValueBuilder value,
        out int endExclusive,
        out string? error)
    {
        endExclusive = src.Length;
        error = null;
        value.AppendBoundary(openQuote + 1);
        var index = openQuote + 1;
        while (index < src.Length)
        {
            var c = src[index];
            if (c == '\'')
            {
                endExclusive = index + 1;
                return true;
            }

            if (c != '\\')
            {
                value.AppendLiteral(c, index, 1);
                index++;
                continue;
            }

            if (!TryDecodeEscape(src, index, out var decoded, out var length))
            {
                error = "an ANSI-C escape that the parser cannot decode exactly is not supported";
                return false;
            }

            value.AppendLiteral(decoded, index, length);
            index += length;
        }

        error = $"unbalanced quote at position {openQuote}";
        return false;
    }

    /// <summary>
    /// Finds the end of a <c>$'…'</c> string, whose opening quote is at
    /// <paramref name="openQuote"/>, without decoding it. A backslash
    /// escapes the next character, so <c>\'</c> does not end the string.
    /// </summary>
    internal static bool TryFindEnd(ReadOnlySpan<char> src, int openQuote, out int endExclusive)
    {
        var index = openQuote + 1;
        while (index < src.Length)
        {
            if (src[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (src[index] == '\'')
            {
                endExclusive = index + 1;
                return true;
            }

            index++;
        }

        endExclusive = src.Length;
        return false;
    }

    private static bool TryDecodeEscape(
        ReadOnlySpan<char> src,
        int backslash,
        out char decoded,
        out int length)
    {
        decoded = '\0';
        length = 2;
        if (backslash + 1 >= src.Length)
        {
            return false;
        }

        var escape = src[backslash + 1];
        switch (escape)
        {
            case 'a': decoded = '\a'; return true;
            case 'b': decoded = '\b'; return true;
            case 'e':
            case 'E': decoded = '\u001b'; return true;
            case 'f': decoded = '\f'; return true;
            case 'n': decoded = '\n'; return true;
            case 'r': decoded = '\r'; return true;
            case 't': decoded = '\t'; return true;
            case 'v': decoded = '\v'; return true;
            case '\\':
            case '\'':
            case '"':
            case '?': decoded = escape; return true;
        }

        var code = 0;
        var digits = 0;
        if (escape is >= '0' and <= '7')
        {
            while (digits < 3 &&
                   backslash + 1 + digits < src.Length &&
                   src[backslash + 1 + digits] is >= '0' and <= '7')
            {
                code = code * 8 + (src[backslash + 1 + digits] - '0');
                digits++;
            }

            length = 1 + digits;
        }
        else if (escape == 'x')
        {
            while (digits < 2 &&
                   backslash + 2 + digits < src.Length &&
                   HexValue(src[backslash + 2 + digits]) is int digit)
            {
                code = code * 16 + digit;
                digits++;
            }

            length = 2 + digits;
        }

        if (digits == 0 || code is < 1 or > 127)
        {
            return false;
        }

        decoded = (char)code;
        return true;
    }

    private static int? HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => null,
    };
}
