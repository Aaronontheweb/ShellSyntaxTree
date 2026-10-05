// -----------------------------------------------------------------------
// <copyright file="BashWordExpansionFacts.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Text;

namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>
/// Finds whether Bash can apply pathname expansion or field splitting to one
/// authored word at run time (#232). The scan reads the authored text, so it
/// does not depend on a proved value.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>An unquoted glob character (<c>*</c>, <c>?</c>, <c>[</c>) can
///         expand.</item>
///   <item>An unquoted parameter expansion or command substitution can
///         expand and split. The parser does not use the proved value here,
///         so the answer stays true for every such expansion. An unquoted
///         bounded arithmetic expansion can split but not expand, because its
///         integer result has no glob character.</item>
///   <item>A brace expansion can expand and split.</item>
///   <item><c>"$@"</c> and <c>"${@}"</c> can split into several words.</item>
///   <item>Single quotes, double quotes, ANSI-C quotes, and a backslash make
///         their text literal.</item>
/// </list>
/// The scan is conservative: text that it cannot read gives true.
/// </remarks>
internal static class BashWordExpansionFacts
{
    internal static (bool MayPathnameExpand, bool MayFieldSplit) Analyze(string raw)
    {
        var mayExpand = false;
        var maySplit = false;
        var active = new StringBuilder(raw.Length);
        var inDouble = false;
        var index = 0;
        while (index < raw.Length)
        {
            var c = raw[index];
            if (c == '\\')
            {
                active.Append('x');
                index += 2;
                continue;
            }

            if (inDouble)
            {
                if (c == '"')
                {
                    inDouble = false;
                    index++;
                    continue;
                }

                if (c == '$' && index + 1 < raw.Length &&
                    (raw[index + 1] == '@' ||
                     index + 3 < raw.Length && raw[index + 1] == '{' &&
                     raw[index + 2] == '@' && raw[index + 3] == '}'))
                {
                    maySplit = true;
                }

                if (!TrySkipSubstitution(raw, ref index))
                {
                    return (true, true);
                }

                active.Append('x');
                continue;
            }

            switch (c)
            {
                case '\'':
                    {
                        var close = raw.IndexOf('\'', index + 1);
                        if (close < 0)
                        {
                            return (true, true);
                        }

                        active.Append('x');
                        index = close + 1;
                        continue;
                    }
                case '"':
                    inDouble = true;
                    index++;
                    continue;
                case '$' when index + 1 < raw.Length && raw[index + 1] == '\'':
                    if (!BashAnsiCQuoting.TryFindEnd(raw.AsSpan(), index, out var ansiEnd))
                    {
                        return (true, true);
                    }

                    active.Append('x');
                    index = ansiEnd;
                    continue;
                case '$' when index + 1 < raw.Length && raw[index + 1] == '"':
                    inDouble = true;
                    index += 2;
                    continue;
                case '$' when BashArithmeticGrammar.TryScanExpansion(raw.AsSpan(), index, out var arithmeticEnd):
                    // A bounded arithmetic result is an integer. It has no
                    // glob character, but a changed IFS can split it.
                    maySplit = true;
                    active.Append('x');
                    index = arithmeticEnd;
                    continue;
                case '$' when index + 1 < raw.Length:
                case '`':
                    mayExpand = true;
                    maySplit = true;
                    if (!TrySkipSubstitution(raw, ref index))
                    {
                        return (true, true);
                    }

                    active.Append('x');
                    continue;
                case '*':
                case '?':
                case '[':
                    mayExpand = true;
                    active.Append('x');
                    index++;
                    continue;
            }

            active.Append(c);
            index++;
        }

        if (inDouble)
        {
            return (true, true);
        }

        if (BashBraceExpansion.ContainsExpansion(active.ToString()))
        {
            mayExpand = true;
            maySplit = true;
        }

        return (mayExpand, maySplit);
    }

    /// <summary>
    /// Moves past one character, or past a whole <c>$(…)</c>,
    /// <c>$((…))</c>, <c>${…}</c>, <c>$name</c>, or backtick region that starts
    /// at <paramref name="index"/>. Returns false when the region has no end.
    /// </summary>
    private static bool TrySkipSubstitution(string raw, ref int index)
    {
        var c = raw[index];
        if (c == '`')
        {
            var end = index + 1;
            while (end < raw.Length && raw[end] != '`')
            {
                end += raw[end] == '\\' ? 2 : 1;
            }

            if (end >= raw.Length)
            {
                return false;
            }

            index = end + 1;
            return true;
        }

        if (c != '$' || index + 1 >= raw.Length)
        {
            index++;
            return true;
        }

        var next = raw[index + 1];
        if (next == '(')
        {
            if (!BashLexer.TryFindCommandSubstitutionEnd(raw.AsSpan(), index + 1, out var close))
            {
                return false;
            }

            index = close + 1;
            return true;
        }

        if (next == '{')
        {
            var close = raw.IndexOf('}', index + 2);
            if (close < 0)
            {
                return false;
            }

            index = close + 1;
            return true;
        }

        // `$name` continues over name characters. `$1` and `$?` end after
        // one character.
        index++;
        if (raw[index] == '_' || char.IsLetter(raw[index]))
        {
            while (index < raw.Length && (raw[index] == '_' || char.IsLetterOrDigit(raw[index])))
            {
                index++;
            }

            return true;
        }

        index++;
        return true;
    }
}
