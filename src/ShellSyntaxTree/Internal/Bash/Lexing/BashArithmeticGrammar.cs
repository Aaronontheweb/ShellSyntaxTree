// -----------------------------------------------------------------------
// <copyright file="BashArithmeticGrammar.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>
/// One variable read in a bounded arithmetic expansion. A special
/// parameter is <c>?</c>, <c>#</c>, or <c>$</c>. Every other read names a
/// shell variable.
/// </summary>
internal readonly record struct BashArithmeticRead(string Name, bool IsSpecialParameter);

/// <summary>
/// The bounded grammar of a Bash arithmetic expansion <c>$((…))</c> (#227).
/// </summary>
/// <remarks>
/// <para>
/// Bash can run code and change state in an arithmetic context. An array
/// subscript expands command substitutions, and the assignment, increment,
/// and decrement operators change variables. The grammar accepts only
/// tokens that cannot do either:
/// </para>
/// <list type="bullet">
///   <item>numeric constants: decimal, <c>0x</c> hexadecimal, and
///         <c>base#digits</c>;</item>
///   <item>variable reads: a bare name, <c>$name</c>, <c>${name}</c>, and
///         the special parameters <c>$?</c>, <c>$#</c>, and <c>$$</c>;</item>
///   <item>a nested bounded <c>$((…))</c>;</item>
///   <item>the operators <c>+ - * / % ** &lt;&lt; &gt;&gt; &amp; | ^ ~ !
///         &lt; &gt; &lt;= &gt;= == != &amp;&amp; || ? :</c>, parentheses,
///         spaces, and tabs.</item>
/// </list>
/// <para>
/// Every other character fails closed: <c>=</c> and the compound
/// assignment operators, <c>++</c> and <c>--</c>, the comma operator,
/// <c>[</c> and <c>]</c>, quotes, a backslash, a backtick, a command
/// substitution, a complex parameter expansion, and a line break.
/// </para>
/// <para>
/// Bash also evaluates the value of each variable as an arithmetic
/// expression. A value with a subscript can run code. The grammar only
/// finds the reads. The state pass proves that each value is an integer.
/// </para>
/// </remarks>
internal static class BashArithmeticGrammar
{
    /// <summary>
    /// Scans a bounded <c>$((…))</c> that starts at <paramref name="start"/>.
    /// <paramref name="endExclusive"/> receives the index after the closing
    /// <c>))</c>. Returns false when the text is not a bounded arithmetic
    /// expansion.
    /// </summary>
    internal static bool TryScanExpansion(
        ReadOnlySpan<char> src,
        int start,
        out int endExclusive) =>
        TryScanExpansion(src, start, reads: null, nesting: 0, out endExclusive, out _);

    /// <summary>
    /// Scans like <see cref="TryScanExpansion(ReadOnlySpan{char}, int, out int)"/>.
    /// <paramref name="rejection"/> receives the reason for a rejected
    /// expansion, or null when the expansion has no end.
    /// </summary>
    internal static bool TryScanExpansion(
        ReadOnlySpan<char> src,
        int start,
        out int endExclusive,
        out string? rejection) =>
        TryScanExpansion(src, start, reads: null, nesting: 0, out endExclusive, out rejection);

    /// <summary>
    /// Gets the variable reads of one bounded expansion, which is the whole
    /// of <paramref name="expansion"/>. The reads of nested expansions are
    /// included.
    /// </summary>
    internal static bool TryGetReads(
        string expansion,
        out IReadOnlyList<BashArithmeticRead> reads)
    {
        var found = new List<BashArithmeticRead>();
        if (!TryScanExpansion(expansion.AsSpan(), 0, found, nesting: 0, out var end, out _) ||
            end != expansion.Length)
        {
            reads = Array.Empty<BashArithmeticRead>();
            return false;
        }

        reads = found;
        return true;
    }

    private static bool TryScanExpansion(
        ReadOnlySpan<char> src,
        int start,
        List<BashArithmeticRead>? reads,
        int nesting,
        out int endExclusive,
        out string? rejection)
    {
        endExclusive = start;
        rejection = null;
        if (start + 2 >= src.Length ||
            src[start] != '$' || src[start + 1] != '(' || src[start + 2] != '(')
        {
            return false;
        }

        if (nesting >= ShellAnalysisLimits.MaxStructuralNesting)
        {
            rejection = NestingRejection;
            return false;
        }

        var depth = 0;
        var index = start + 3;
        while (index < src.Length)
        {
            var c = src[index];
            if (c is ' ' or '\t')
            {
                index++;
                continue;
            }

            if (c is >= '0' and <= '9')
            {
                if (!TryScanNumber(src, ref index))
                {
                    rejection = TextRejection;
                    return false;
                }

                continue;
            }

            if (IsNameStart(c))
            {
                var nameStart = index;
                while (index < src.Length && IsNameContinuation(src[index]))
                {
                    index++;
                }

                reads?.Add(new BashArithmeticRead(
                    src.Slice(nameStart, index - nameStart).ToString(),
                    IsSpecialParameter: false));
                continue;
            }

            if (c == '$')
            {
                if (index + 2 < src.Length && src[index + 1] == '(' && src[index + 2] == '(')
                {
                    if (!TryScanExpansion(src, index, reads, nesting + 1, out var nestedEnd, out rejection))
                    {
                        return false;
                    }

                    index = nestedEnd;
                    continue;
                }

                if (index + 1 < src.Length && src[index + 1] == '(')
                {
                    rejection = SubstitutionRejection;
                    return false;
                }

                if (!TryScanParameter(src, ref index, reads))
                {
                    rejection = ParameterRejection;
                    return false;
                }

                continue;
            }

            if (c == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (c == ')')
            {
                if (depth > 0)
                {
                    depth--;
                    index++;
                    continue;
                }

                // Bash ends the expansion at the first `))` outside the
                // inner parentheses. A lone `)` is not arithmetic.
                if (index + 1 < src.Length && src[index + 1] == ')')
                {
                    endExclusive = index + 2;
                    return true;
                }

                // At the end of the input the expansion is unterminated.
                rejection = index + 1 < src.Length ? TextRejection : null;
                return false;
            }

            if (!TryScanOperator(src, ref index))
            {
                rejection = c switch
                {
                    '[' or ']' => SubscriptRejection,
                    ',' => CommaRejection,
                    '=' or '+' or '-' or '*' or '/' or '%' or '^' or '~' or '?' or ':' or
                        '<' or '>' or '&' or '|' or '!' => AssignmentRejection,
                    _ => TextRejection,
                };
                return false;
            }
        }

        // No closing `))`. The caller reports an unterminated expansion.
        return false;
    }

    private const string SubstitutionRejection =
        "a command substitution inside a Bash arithmetic expansion is not supported: " +
        "Bash evaluates its output as an arithmetic expression, which can run code";

    private const string SubscriptRejection =
        "an array subscript inside a Bash arithmetic expansion is not supported: " +
        "Bash can run code through a subscript";

    private const string AssignmentRejection =
        "an assignment, increment, or decrement inside a Bash arithmetic expansion is not supported";

    private const string CommaRejection =
        "the comma operator inside a Bash arithmetic expansion is not supported";

    private const string ParameterRejection =
        "a Bash arithmetic expansion can read only a name, ${name}, $?, $#, or $$";

    private const string NestingRejection =
        "Bash arithmetic expansion nesting depth exceeded (>16)";

    private const string TextRejection =
        "a Bash arithmetic expansion contains text outside the bounded grammar";

    private static bool TryScanNumber(ReadOnlySpan<char> src, ref int index)
    {
        var start = index;
        while (index < src.Length && (IsNameContinuation(src[index]) || src[index] is '@' or '#'))
        {
            index++;
        }

        var number = src.Slice(start, index - start);
        var hash = number.IndexOf('#');
        if (hash >= 0)
        {
            // `base#digits`. The digits can use letters, `@`, and `_`.
            return hash > 0 &&
                hash < number.Length - 1 &&
                AreDecimalDigits(number.Slice(0, hash)) &&
                number.Slice(hash + 1).IndexOf('#') < 0;
        }

        if (number.Length > 2 && number[0] == '0' && number[1] is 'x' or 'X')
        {
            foreach (var digit in number.Slice(2))
            {
                if (!IsHexDigit(digit))
                {
                    return false;
                }
            }

            return true;
        }

        return AreDecimalDigits(number);
    }

    private static bool TryScanParameter(
        ReadOnlySpan<char> src,
        ref int index,
        List<BashArithmeticRead>? reads)
    {
        // src[index] is '$'.
        if (index + 1 >= src.Length)
        {
            return false;
        }

        var next = src[index + 1];
        if (next is '?' or '#' or '$')
        {
            reads?.Add(new BashArithmeticRead(next.ToString(), IsSpecialParameter: true));
            index += 2;
            return true;
        }

        if (IsNameStart(next))
        {
            var nameStart = index + 1;
            var end = nameStart;
            while (end < src.Length && IsNameContinuation(src[end]))
            {
                end++;
            }

            reads?.Add(new BashArithmeticRead(
                src.Slice(nameStart, end - nameStart).ToString(),
                IsSpecialParameter: false));
            index = end;
            return true;
        }

        if (next == '{' && index + 2 < src.Length && IsNameStart(src[index + 2]))
        {
            var nameStart = index + 2;
            var end = nameStart;
            while (end < src.Length && IsNameContinuation(src[end]))
            {
                end++;
            }

            // Only the simple `${name}` form. Every operator form fails closed.
            if (end >= src.Length || src[end] != '}')
            {
                return false;
            }

            reads?.Add(new BashArithmeticRead(
                src.Slice(nameStart, end - nameStart).ToString(),
                IsSpecialParameter: false));
            index = end + 1;
            return true;
        }

        return false;
    }

    private static bool TryScanOperator(ReadOnlySpan<char> src, ref int index)
    {
        var c = src[index];
        var next = index + 1 < src.Length ? src[index + 1] : '\0';
        var length = 0;
        switch (c)
        {
            case '+':
            case '-':
                // `++` and `--` change a variable.
                length = next == c ? 0 : 1;
                break;
            case '*':
                length = next == '*' ? 2 : 1;
                break;
            case '/':
            case '%':
            case '^':
            case '~':
            case '?':
            case ':':
                length = 1;
                break;
            case '<':
            case '>':
                length = next == c || next == '=' ? 2 : 1;
                break;
            case '&':
            case '|':
                length = next == c ? 2 : 1;
                break;
            case '=':
                length = next == '=' ? 2 : 0;
                break;
            case '!':
                length = next == '=' ? 2 : 1;
                break;
        }

        if (length == 0)
        {
            return false;
        }

        // A compound assignment such as `+=`, `*=`, or `<<=` leaves a lone
        // `=` as the next token, which the `=` case rejects. The comparisons
        // `<=`, `>=`, `==`, and `!=` consume their `=`.
        index += length;
        return true;
    }

    private static bool AreDecimalDigits(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var digit in value)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsNameStart(char value) =>
        value is '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsNameContinuation(char value) =>
        IsNameStart(value) || value is >= '0' and <= '9';
}
