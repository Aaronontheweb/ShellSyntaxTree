// -----------------------------------------------------------------------
// <copyright file="BashBraceExpansion.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>
/// Finds Bash brace expansion in a word.
/// </summary>
/// <remarks>
/// <para>
/// Bash expands an unquoted <c>{…}</c> that holds a top-level comma, or a
/// sequence <c>{x..y}</c> or <c>{x..y..step}</c> of integers or single
/// letters, before every other expansion. One word then becomes several
/// words: <c>cat {a,b}</c> runs <c>cat a b</c>. Brace expansion is on in a
/// non-interactive shell.
/// </para>
/// <para>
/// The parser does not expand the word. It marks each unquoted part of the
/// word as an opaque region, so the word has no exact value, no resolved
/// path, and no command word. A quoted or escaped brace, <c>{}</c>,
/// <c>{a}</c>, and the <c>{</c> of <c>${…}</c> stay literal, as in Bash.
/// </para>
/// </remarks>
internal static class BashBraceExpansion
{
    // A sequence has at most three parts of a few characters each. A longer
    // body cannot be a sequence, so the check stays linear.
    private const int MaxSequenceLength = 64;

    /// <summary>
    /// Marks every word of <paramref name="tokens"/> that has a brace
    /// expansion. A word is a run of adjacent word, quoted, and substitution
    /// tokens, also across a line continuation.
    /// </summary>
    internal static void MarkWords(List<BashToken> tokens)
    {
        var index = 0;
        while (index < tokens.Count)
        {
            if (!IsWordPart(tokens[index]))
            {
                index++;
                continue;
            }

            var end = index + 1;
            var wordEnd = tokens[index].SourceStart + tokens[index].SourceLength;
            while (end < tokens.Count)
            {
                var next = tokens[end];
                if (IsWordPart(next) && next.SourceStart == wordEnd)
                {
                    wordEnd = next.SourceStart + next.SourceLength;
                    end++;
                    continue;
                }

                if (next.Kind == BashTokenKind.Continuation &&
                    next.SourceStart == wordEnd &&
                    end + 1 < tokens.Count &&
                    IsWordPart(tokens[end + 1]) &&
                    tokens[end + 1].SourceStart == next.SourceStart + next.SourceLength)
                {
                    wordEnd = next.SourceStart + next.SourceLength;
                    end++;
                    continue;
                }

                break;
            }

            if (HasBraceExpansion(tokens, index, end))
            {
                for (var part = index; part < end; part++)
                {
                    var token = tokens[part];
                    if (token.Kind == BashTokenKind.Word)
                    {
                        tokens[part] = token with
                        {
                            ResolverValue = ShellValue.Opaque(
                                token.Value,
                                ShellOpaqueCause.BraceExpansion,
                                token.SourceStart,
                                token.SourceLength),
                        };
                    }
                }
            }

            index = end;
        }
    }

    private static bool IsWordPart(BashToken token) =>
        token.Kind is BashTokenKind.Word or BashTokenKind.QuotedString or
            BashTokenKind.OpaqueSubstitution;

    private static bool HasBraceExpansion(List<BashToken> tokens, int start, int end)
    {
        // Only unquoted, unescaped literal characters can form a brace
        // expansion. Every other part becomes an inert placeholder.
        var active = new StringBuilder();
        var hasOpenBrace = false;
        for (var index = start; index < end; index++)
        {
            var token = tokens[index];
            if (token.Kind != BashTokenKind.Word || token.ResolverValue is not ShellValue value)
            {
                active.Append('x');
                continue;
            }

            foreach (var fragment in value.Fragments)
            {
                var isActiveLiteral =
                    fragment.Kind == ShellValueFragmentKind.Literal &&
                    fragment.Expansion is null &&
                    fragment.SourceLength == fragment.Value.Length;
                if (!isActiveLiteral)
                {
                    active.Append('x');
                    continue;
                }

                active.Append(fragment.Value);
                hasOpenBrace |= fragment.Value.IndexOf('{') >= 0;
            }
        }

        return hasOpenBrace && ContainsExpansion(active.ToString());
    }

    /// <summary>
    /// True when <paramref name="word"/>, in which every character is
    /// unquoted, has a brace pair with a top-level comma or a sequence body.
    /// </summary>
    internal static bool ContainsExpansion(string word)
    {
        var open = new Stack<(int Start, bool HasComma)>();
        for (var index = 0; index < word.Length; index++)
        {
            var c = word[index];
            if (c == '{')
            {
                // The `{` of `${name}` is part of an expansion fragment, so it
                // is a placeholder here and never reaches this point.
                open.Push((index, false));
                continue;
            }

            if (c == ',' && open.Count > 0)
            {
                var top = open.Pop();
                open.Push((top.Start, true));
                continue;
            }

            if (c != '}' || open.Count == 0)
            {
                continue;
            }

            var pair = open.Pop();
            if (pair.HasComma ||
                IsSequence(word, pair.Start + 1, index - pair.Start - 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSequence(string word, int start, int length)
    {
        if (length < 4 || length > MaxSequenceLength)
        {
            return false;
        }

        var parts = word.Substring(start, length).Split(new[] { ".." }, StringSplitOptions.None);
        if (parts.Length is not (2 or 3) ||
            parts.Length == 3 && !IsInteger(parts[2]))
        {
            return false;
        }

        return IsInteger(parts[0]) && IsInteger(parts[1]) ||
            IsLetter(parts[0]) && IsLetter(parts[1]);
    }

    private static bool IsInteger(string value)
    {
        var start = value.Length > 0 && value[0] is '+' or '-' ? 1 : 0;
        if (start == value.Length)
        {
            return false;
        }

        for (var index = start; index < value.Length; index++)
        {
            if (value[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLetter(string value) =>
        value.Length == 1 && value[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
