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
                // Bash removes a line continuation, so `{a.\⏎.c}` is the
                // brace sequence `{a..c}` (#243). Any other backslash quotes
                // the next character.
                var continuation = BashLineContinuation.LengthAt(
                    raw.AsSpan(),
                    index,
                    inDouble ? BashContinuationContext.DoubleQuoted : BashContinuationContext.Unquoted);
                if (continuation == 0)
                {
                    active.Append('x');
                }

                index += continuation > 0 ? continuation : 2;
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

                if (c == '$' && IsQuotedAllPositional(raw, index))
                {
                    maySplit = true;
                }

                if (!TrySkipSubstitution(raw, ref index, BashContinuationContext.DoubleQuoted))
                {
                    return (true, true);
                }

                active.Append('x');
                continue;
            }

            // Bash removes a line continuation before it reads `$`, so
            // `$\⏎'a'` is an ANSI-C string and `$\⏎(id)` a substitution (#243).
            var dollarNext = c == '$'
                ? BashLineContinuation.Skip(raw.AsSpan(), index + 1, BashContinuationContext.Unquoted)
                : raw.Length;
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
                case '$' when dollarNext < raw.Length && raw[dollarNext] == '\'':
                    if (!BashAnsiCQuoting.TryFindEnd(raw.AsSpan(), dollarNext, out var ansiEnd))
                    {
                        return (true, true);
                    }

                    active.Append('x');
                    index = ansiEnd;
                    continue;
                case '$' when dollarNext < raw.Length && raw[dollarNext] == '"':
                    inDouble = true;
                    index = dollarNext + 1;
                    continue;
                case '$' when BashArithmeticGrammar.TryScanExpansion(raw.AsSpan(), index, out var arithmeticEnd):
                    // A bounded arithmetic result is an integer. It has no
                    // glob character, but a changed IFS can split it.
                    maySplit = true;
                    active.Append('x');
                    index = arithmeticEnd;
                    continue;
                case '$' when dollarNext < raw.Length:
                case '`':
                    mayExpand = true;
                    maySplit = true;
                    if (!TrySkipSubstitution(raw, ref index, BashContinuationContext.Unquoted))
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
    /// <summary>
    /// True for <c>$@</c> or <c>${@}</c> at <paramref name="dollar"/> in
    /// double quotes, also with line continuations in it.
    /// </summary>
    private static bool IsQuotedAllPositional(string raw, int dollar)
    {
        var next = BashLineContinuation.Skip(
            raw.AsSpan(), dollar + 1, BashContinuationContext.DoubleQuoted);
        if (next >= raw.Length)
        {
            return false;
        }

        if (raw[next] == '@')
        {
            return true;
        }

        if (raw[next] != '{')
        {
            return false;
        }

        var close = raw.IndexOf('}', next + 1);
        return close > next &&
            BashLineContinuation.Remove(
                raw.AsSpan(next + 1, close - next - 1),
                BashContinuationContext.DoubleQuoted) == "@";
    }

    private static bool TrySkipSubstitution(
        string raw,
        ref int index,
        BashContinuationContext context)
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

        var nextIndex = c == '$'
            ? BashLineContinuation.Skip(raw.AsSpan(), index + 1, context)
            : raw.Length;
        if (nextIndex >= raw.Length)
        {
            index++;
            return true;
        }

        var next = raw[nextIndex];
        if (next == '(')
        {
            if (!BashLexer.TryFindCommandSubstitutionEnd(raw.AsSpan(), nextIndex, out var close))
            {
                return false;
            }

            index = close + 1;
            return true;
        }

        if (next == '{')
        {
            var close = raw.IndexOf('}', nextIndex + 1);
            if (close < 0)
            {
                return false;
            }

            index = close + 1;
            return true;
        }

        // `$name` continues over name characters, also across a line
        // continuation. `$1` and `$?` end after one character.
        index = nextIndex + 1;
        if (next == '_' || char.IsLetter(next))
        {
            var afterContinuation = BashLineContinuation.Skip(raw.AsSpan(), index, context);
            while (afterContinuation < raw.Length &&
                   (raw[afterContinuation] == '_' || char.IsLetterOrDigit(raw[afterContinuation])))
            {
                index = afterContinuation + 1;
                afterContinuation = BashLineContinuation.Skip(raw.AsSpan(), index, context);
            }
        }

        return true;
    }
}
