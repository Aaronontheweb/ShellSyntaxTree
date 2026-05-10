// -----------------------------------------------------------------------
// <copyright file="OpaqueRegionScanner.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;

namespace ShellSyntaxTree.Internal.Lexing;

/// <summary>
/// Grammar-agnostic boundary scanner for "opaque regions" — input slices
/// that the lexer must skip over verbatim because their interior follows
/// rules the outer lexer does not model. In v0.1 this is bash's
/// <c>$(…)</c> command substitution and backtick <c>`…`</c>; in v0.2 it
/// will additionally serve PowerShell's <c>$( … )</c> and <c>@( … )</c>.
///
/// The scanner is intentionally permissive about interior content — it
/// does not parse the inside, it only finds the matching close. The
/// outer parser treats the whole region as a single
/// <c>Arg{ Kind = DynamicSkip, IsPath = false }</c> per the locked
/// interpretation in the v0.1 OpenSpec change
/// (proposal §"2. Command substitution + arithmetic + complex param
/// expansion").
/// </summary>
internal static class OpaqueRegionScanner
{
    /// <summary>
    /// Result of a scan. <see cref="EndIndex"/> is the index of the
    /// closing delimiter character (inclusive) when <see cref="Closed"/>
    /// is <c>true</c>; when <see cref="Closed"/> is <c>false</c> the
    /// scanner ran off the end of the input and <see cref="EndIndex"/>
    /// equals the input length.
    /// </summary>
    internal readonly record struct ScanResult(int EndIndex, bool Closed);

    /// <summary>
    /// Find the matching close delimiter for an asymmetric opaque region
    /// (e.g. <c>(</c>/<c>)</c>) starting at <paramref name="startIndex"/>
    /// — which must point at the opening delimiter character. Handles:
    ///
    /// <list type="bullet">
    ///   <item>nested same-kind regions (depth tracking),</item>
    ///   <item>single- and double-quoted nested strings (delimiters
    ///         inside quotes don't count),</item>
    ///   <item><c>\X</c> escapes outside of single quotes (the next char
    ///         is consumed verbatim).</item>
    /// </list>
    /// </summary>
    internal static ScanResult Scan(
        ReadOnlySpan<char> input,
        int startIndex,
        char openChar,
        char closeChar)
    {
        // Caller contract: startIndex points at the opening delimiter.
        // We start scanning at startIndex+1 with depth=1 already counted.
        if (startIndex < 0 || startIndex >= input.Length || input[startIndex] != openChar)
        {
            return new ScanResult(input.Length, false);
        }

        var depth = 1;
        var i = startIndex + 1;
        while (i < input.Length)
        {
            var c = input[i];

            // Backslash escapes the next character (outside single quotes).
            // The single-quote branch below short-circuits before this code
            // ever runs while inside '...'.
            if (c == '\\' && i + 1 < input.Length)
            {
                i += 2;
                continue;
            }

            if (c == '\'')
            {
                // Single-quoted: bytes are literal, no escapes recognized.
                i = SkipSingleQuoted(input, i + 1);
                continue;
            }

            if (c == '"')
            {
                i = SkipDoubleQuoted(input, i + 1);
                continue;
            }

            if (c == openChar)
            {
                depth++;
            }
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0)
                {
                    return new ScanResult(i, true);
                }
            }

            i++;
        }

        return new ScanResult(input.Length, false);
    }

    /// <summary>
    /// Variant for symmetric opaque regions (e.g. backtick-quoted command
    /// substitution) where the open and close delimiters are the same
    /// character. <paramref name="startIndex"/> points at the opening
    /// delimiter; the scan returns at the next unescaped occurrence of
    /// <paramref name="delimiter"/>. There is no nesting — the next
    /// unescaped match wins.
    /// </summary>
    internal static ScanResult ScanSymmetric(
        ReadOnlySpan<char> input,
        int startIndex,
        char delimiter)
    {
        if (startIndex < 0 || startIndex >= input.Length || input[startIndex] != delimiter)
        {
            return new ScanResult(input.Length, false);
        }

        var i = startIndex + 1;
        while (i < input.Length)
        {
            var c = input[i];

            if (c == '\\' && i + 1 < input.Length)
            {
                // Escape: skip backslash + next char verbatim. Bash treats
                // \` inside a backtick context as a literal backtick — the
                // skip is the right behavior either way.
                i += 2;
                continue;
            }

            if (c == delimiter)
            {
                return new ScanResult(i, true);
            }

            i++;
        }

        return new ScanResult(input.Length, false);
    }

    /// <summary>
    /// Skip past a single-quoted string. <paramref name="i"/> points at
    /// the first char after the opening quote. Returns the index of the
    /// char after the closing quote, or <c>input.Length</c> if no close
    /// was found. Single-quoted strings preserve bytes literally — no
    /// escape processing per SPEC §5.
    /// </summary>
    private static int SkipSingleQuoted(ReadOnlySpan<char> input, int i)
    {
        while (i < input.Length)
        {
            if (input[i] == '\'')
            {
                return i + 1;
            }

            i++;
        }

        return input.Length;
    }

    /// <summary>
    /// Skip past a double-quoted string. <paramref name="i"/> points at
    /// the first char after the opening quote. Backslash escapes the
    /// next character (covers <c>\"</c> in particular). Returns the
    /// index of the char after the closing quote, or <c>input.Length</c>
    /// if no close was found.
    /// </summary>
    private static int SkipDoubleQuoted(ReadOnlySpan<char> input, int i)
    {
        while (i < input.Length)
        {
            var c = input[i];
            if (c == '\\' && i + 1 < input.Length)
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                return i + 1;
            }

            i++;
        }

        return input.Length;
    }
}
