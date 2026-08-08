// -----------------------------------------------------------------------
// <copyright file="PwshLexer.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal.Lexing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Pwsh.Lexing;

/// <summary>
/// Tokenizer for the PowerShell subset described in SPEC.POWERSHELL.md §4 /
/// §5. Emits a flat list of <see cref="PwshToken"/>; the parser groups them
/// into statements, pipelines, clauses, verb chains, args, and redirects.
///
/// Design notes:
/// <list type="bullet">
///   <item>The lexer never expands variables. <c>$var</c>, <c>${name}</c>,
///         and <c>$env:NAME</c> stay literal inside a Word token; the
///         resolver classifies them.</item>
///   <item><c>$( … )</c> uses a PowerShell-specific boundary scan that
///         honors comments and nested interpolation. Other opaque regions —
///         <c>@( … )</c>, <c>@{ … }</c>, and <c>{ … }</c> — use
///         <see cref="OpaqueRegionScanner"/> in backtick-escape mode.</item>
///   <item>Malformed regions surface as
///         <see cref="PwshTokenKind.UnparseableSentinel"/> tokens the
///         parser lifts into <c>ParsedCommand.IsUnparseable</c>.</item>
/// </list>
/// </summary>
internal static class PwshLexer
{
    /// <summary>Tokenize <paramref name="input"/> per SPEC.POWERSHELL.md §5.</summary>
    internal static IReadOnlyList<PwshToken> Tokenize(string input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (input.Length == 0)
        {
            return Array.Empty<PwshToken>();
        }

        var tokens = new List<PwshToken>();
        var src = input.AsSpan();
        var i = 0;

        while (i < src.Length)
        {
            var c = src[i];

            if (c == '\0')
            {
                tokens.Add(new PwshToken(
                    PwshTokenKind.UnparseableSentinel,
                    "", null, i, 1,
                    $"NUL character is not supported at position {i}"));
                return tokens;
            }

            // ---- whitespace ----
            if (IsInlineWhitespace(c))
            {
                var start = i;
                while (i < src.Length && IsInlineWhitespace(src[i]))
                {
                    i++;
                }

                tokens.Add(new PwshToken(
                    PwshTokenKind.Whitespace, "", null, start, i - start, null));
                continue;
            }

            // ---- newline run (statement separator, SPEC §4) ----
            if (c == '\n' || c == '\r')
            {
                var start = i;
                while (i < src.Length && (src[i] == '\n' || src[i] == '\r'))
                {
                    i++;
                }

                tokens.Add(new PwshToken(
                    PwshTokenKind.Whitespace, "", null, start, i - start, null)
                { IsStatementSeparator = true });
                continue;
            }

            // ---- backtick + newline = line continuation ----
            if (c == '`' && i + 1 < src.Length && (src[i + 1] == '\n' || src[i + 1] == '\r'))
            {
                var start = i;
                i += 2;
                if (i - start == 2 && start + 1 < src.Length && src[start + 1] == '\r'
                    && i < src.Length && src[i] == '\n')
                {
                    i++;
                }

                tokens.Add(new PwshToken(
                    PwshTokenKind.Continuation, "", null, start, i - start, null));
                continue;
            }

            // ---- block comment <# ... #> ---- (before the '<' redirect check)
            if (c == '<' && i + 1 < src.Length && src[i + 1] == '#')
            {
                i = ConsumeBlockComment(src, i, tokens);
                continue;
            }

            // ---- line comment ---- (reaching here implies a token boundary)
            if (c == '#')
            {
                i = ConsumeLineComment(src, i, tokens);
                continue;
            }

            // ---- --% stop-parsing token ----
            if (c == '-' && IsStopParsingToken(src, i))
            {
                i = ConsumeStopParsing(src, i, tokens);
                continue;
            }

            // ---- operators (incl. redirects) ----
            if (TryReadOperator(src, i, out var opLen, out var opText))
            {
                tokens.Add(new PwshToken(
                    PwshTokenKind.Operator, "", opText, i, opLen, null));
                i += opLen;
                continue;
            }

            // ---- quoted strings ----
            if (c == '\'')
            {
                i = ReadSingleQuoted(src, i, tokens);
                continue;
            }

            if (c == '"')
            {
                i = ReadDoubleQuoted(src, i, tokens);
                continue;
            }

            // ---- @ dispatch: here-strings, @( @{ subexpressions, splat ----
            if (c == '@')
            {
                var afterAt = TryReadAtConstruct(src, i, tokens);
                if (afterAt >= 0)
                {
                    i = afterAt;
                    continue;
                }

                // Bare '@' — fall through to the word reader.
            }

            // ---- $( ... ) subexpression ----
            if (c == '$' && i + 1 < src.Length && src[i + 1] == '(')
            {
                i = ConsumeBalancedRegion(
                    src, i, openAt: i + 1, '(', ')', PwshTokenKind.Subexpression,
                    "unbalanced '$(' subexpression", tokens);
                continue;
            }

            // ---- { ... } script block ----
            if (c == '{')
            {
                i = ConsumeBalancedRegion(
                    src, i, openAt: i, '{', '}', PwshTokenKind.ScriptBlock,
                    "unbalanced '{' script block", tokens);
                continue;
            }

            // ---- stray closing brace ----
            if (c == '}')
            {
                tokens.Add(new PwshToken(
                    PwshTokenKind.UnparseableSentinel,
                    src.Slice(i).ToString(),
                    null,
                    i,
                    src.Length - i,
                    $"unbalanced '}}' at position {i}"));
                return tokens;
            }

            // ---- parameter -Name ----
            if (IsAlternateParameterDash(c) && IsParameterStart(src, i))
            {
                tokens.Add(new PwshToken(
                    PwshTokenKind.UnparseableSentinel,
                    src.Slice(i).ToString(),
                    null,
                    i,
                    src.Length - i,
                    $"PowerShell parameter dash U+{(int)c:X4} is not supported at position {i}"));
                return tokens;
            }

            if (c == '-' && IsParameterStart(src, i))
            {
                i = ReadParameter(src, i, tokens);
                continue;
            }

            // ---- word ----
            i = ReadWord(src, i, tokens);
        }

        return tokens;
    }

    // ---------------------------------------------------------------- operators

    private static bool TryReadOperator(
        ReadOnlySpan<char> src, int i, out int length, out string? text)
    {
        var c0 = src[i];

        // Pipeline-chain operators (longest match first).
        if (i + 1 < src.Length)
        {
            var c1 = src[i + 1];
            if (c0 == '&' && c1 == '&') { length = 2; text = "&&"; return true; }
            if (c0 == '|' && c1 == '|') { length = 2; text = "||"; return true; }
        }

        // Redirects (incl. stream-prefixed and merge forms).
        if (TryReadRedirect(src, i, out length, out text))
        {
            return true;
        }

        switch (c0)
        {
            case '|': length = 1; text = "|"; return true;
            case '&': length = 1; text = "&"; return true;
            case ';': length = 1; text = ";"; return true;
            case '(': length = 1; text = "("; return true;
            case ')': length = 1; text = ")"; return true;
            default:
                length = 0; text = null; return false;
        }
    }

    /// <summary>
    /// SPEC.POWERSHELL.md §5: recognize redirect operators, longest-match
    /// first — <c>&gt;</c>, <c>&gt;&gt;</c>; <c>N&gt;</c> /
    /// <c>N&gt;&gt;</c> for stream N in {1..6}; <c>*&gt;</c> / <c>*&gt;&gt;</c>;
    /// and the stream-merge forms <c>N&gt;&amp;1</c> for N in {2..6} and
    /// <c>*&gt;&amp;1</c>.
    /// </summary>
    private static bool TryReadRedirect(
        ReadOnlySpan<char> src, int i, out int length, out string? text)
    {
        length = 0;
        text = null;

        var prefix = '\0';
        var p = i;
        if (src[i] == '*' || (src[i] >= '1' && src[i] <= '6'))
        {
            if (i + 1 < src.Length && src[i + 1] == '>')
            {
                prefix = src[i];
                p = i + 1;
            }
            else
            {
                // A bare digit / '*' not followed by '>' is not a redirect.
                return false;
            }
        }

        if (p >= src.Length)
        {
            return false;
        }

        var op = src[p];
        if (op == '<')
        {
            // '<' takes no stream prefix.
            if (prefix != '\0')
            {
                return false;
            }

            length = 1;
            text = "<";
            return true;
        }

        if (op != '>')
        {
            return false;
        }

        // PowerShell only permits non-success streams (or all streams) to
        // merge into success stream 1.
        if (p + 2 < src.Length && src[p + 1] == '&'
            && src[p + 2] == '1'
            && (prefix == '*' || prefix is >= '2' and <= '6'))
        {
            length = (p + 3) - i;
            text = src.Slice(i, length).ToString();
            return true;
        }

        // Append: '>>'.
        if (p + 1 < src.Length && src[p + 1] == '>')
        {
            length = (p + 2) - i;
            text = src.Slice(i, length).ToString();
            return true;
        }

        // Plain '>'.
        length = (p + 1) - i;
        text = src.Slice(i, length).ToString();
        return true;
    }

    // ---------------------------------------------------------------- quoted

    private static int ReadSingleQuoted(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        // Single quotes preserve bytes literally (SPEC §5). A doubled '' is
        // an escaped single quote.
        var value = new ShellValueBuilder();
        value.AppendBoundary(start + 1);
        var i = start + 1;
        while (i < src.Length)
        {
            if (src[i] == '\'')
            {
                if (i + 1 < src.Length && src[i + 1] == '\'')
                {
                    value.AppendLiteral('\'', i, 2);
                    i += 2;
                    continue;
                }

                var resolverValue = value.Build();
                tokens.Add(new PwshToken(
                    PwshTokenKind.QuotedString, resolverValue.Decoded, null,
                    start, (i - start) + 1, null)
                {
                    IsSingleQuoted = true,
                    ResolverValue = resolverValue,
                });
                return i + 1;
            }

            value.AppendLiteral(src[i], i, 1);
            i++;
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.UnparseableSentinel,
            src.Slice(start).ToString(), null, start, src.Length - start,
            $"unbalanced single quote at position {start}"));
        return src.Length;
    }

    private static int ReadDoubleQuoted(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        // Double quotes allow backtick escapes and recognize $var / $(...)
        // interpolation. The lexer does not evaluate it; the typed region is
        // retained for the resolver.
        var value = new ShellValueBuilder();
        value.AppendBoundary(start + 1);
        var hasInterpolation = false;
        var i = start + 1;
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '"')
            {
                // A doubled "" is an escaped double quote.
                if (i + 1 < src.Length && src[i + 1] == '"')
                {
                    value.AppendLiteral('"', i, 2);
                    i += 2;
                    continue;
                }

                var resolverValue = value.Build();
                tokens.Add(new PwshToken(
                    PwshTokenKind.QuotedString, resolverValue.Decoded, null,
                    start, (i - start) + 1, null)
                {
                    HasInterpolation = hasInterpolation,
                    ResolverValue = resolverValue,
                });
                return i + 1;
            }

            if (c == '`' && i + 1 < src.Length)
            {
                if (IsMalformedUnicodeEscape(src, i))
                {
                    tokens.Add(InvalidUnicodeEscapeToken(src, i));
                    return src.Length;
                }

                i = AppendBacktickEscapeFragment(src, i, value, 0);
                continue;
            }

            if (c == '$' && StartsInterpolation(src, i))
            {
                hasInterpolation = true;
                if (TryAppendPwshExpansion(src, ref i, value, out var error))
                {
                    if (error is not null)
                    {
                        tokens.Add(new PwshToken(
                            PwshTokenKind.UnparseableSentinel,
                            src.Slice(start).ToString(),
                            null,
                            start,
                            src.Length - start,
                            error));
                        return src.Length;
                    }

                    continue;
                }
            }

            value.AppendLiteral(c, i, 1);
            i++;
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.UnparseableSentinel,
            src.Slice(start).ToString(), null, start, src.Length - start,
            $"unbalanced double quote at position {start}"));
        return src.Length;
    }

    // ---------------------------------------------------------------- @ constructs

    /// <summary>
    /// Handle a token starting with <c>@</c>: here-strings (<c>@" … "@</c> /
    /// <c>@' … '@</c>), array/hash subexpressions (<c>@( … )</c> /
    /// <c>@{ … }</c>), and splatting (<c>@identifier</c>). Returns the new
    /// scan index, or -1 when the <c>@</c> is a bare word character.
    /// </summary>
    private static int TryReadAtConstruct(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        if (start + 1 >= src.Length)
        {
            return -1;
        }

        var next = src[start + 1];

        // Here-strings: @" or @' followed (after optional ws) by a newline.
        if (next == '"' || next == '\'')
        {
            var afterHere = TryReadHereString(src, start, next, tokens);
            return afterHere; // -1 when not a here-string.
        }

        // Array / hash subexpression.
        if (next == '(')
        {
            return ConsumeBalancedRegion(
                src, start, openAt: start + 1, '(', ')', PwshTokenKind.Subexpression,
                "unbalanced '@(' array subexpression", tokens);
        }

        if (next == '{')
        {
            return ConsumeBalancedRegion(
                src, start, openAt: start + 1, '{', '}', PwshTokenKind.Subexpression,
                "unbalanced '@{' hash literal", tokens);
        }

        // Splatting: @identifier.
        if (IsIdentifierStart(next))
        {
            var i = start + 1;
            while (i < src.Length && IsIdentifierContinuation(src[i]))
            {
                i++;
            }

            tokens.Add(new PwshToken(
                PwshTokenKind.Splat, src.Slice(start, i - start).ToString(),
                null, start, i - start, null)
            {
                ResolverValue = ShellValue.Opaque(
                        src.Slice(start, i - start).ToString(),
                        ShellOpaqueCause.Splat,
                        start,
                        i - start),
            });
            return i;
        }

        return -1;
    }

    private static int TryReadHereString(
        ReadOnlySpan<char> src, int start, char quote, List<PwshToken> tokens)
    {
        // start points at '@'; src[start+1] is the quote. After the quote
        // only whitespace is permitted before the newline.
        var j = start + 2;
        while (j < src.Length && (src[j] == ' ' || src[j] == '\t'))
        {
            j++;
        }

        if (j >= src.Length || (src[j] != '\n' && src[j] != '\r'))
        {
            return -1; // Not a here-string.
        }

        // Skip the opening newline.
        if (src[j] == '\r' && j + 1 < src.Length && src[j + 1] == '\n')
        {
            j += 2;
        }
        else
        {
            j += 1;
        }

        var bodyStart = j;

        // Find a line whose first two chars are quote + '@'.
        var k = j;
        while (k < src.Length)
        {
            var atLineStart = k == bodyStart || src[k - 1] == '\n'
                || (src[k - 1] == '\r' && (k < 2 || src[k - 2] != '\n'));
            if (atLineStart && k + 1 < src.Length && src[k] == quote && src[k + 1] == '@')
            {
                // Body ends at the newline preceding this terminator line.
                var bodyEnd = k;
                if (bodyEnd > bodyStart && src[bodyEnd - 1] == '\n')
                {
                    bodyEnd--;
                }

                if (bodyEnd > bodyStart && src[bodyEnd - 1] == '\r')
                {
                    bodyEnd--;
                }

                var bodySpan = bodyEnd >= bodyStart
                    ? src.Slice(bodyStart, bodyEnd - bodyStart)
                    : ReadOnlySpan<char>.Empty;
                var hasInterpolation = false;
                var invalidUnicodeAt = -1;
                string? interpolationError = null;
                var resolverValue = quote == '"'
                    ? DecodeExpandableValue(
                        bodySpan,
                        bodyStart,
                        out hasInterpolation,
                        out invalidUnicodeAt,
                        out interpolationError)
                    : ShellValue.Literal(
                        bodySpan.ToString(), bodyStart, bodySpan.Length);
                if (quote == '"' && invalidUnicodeAt >= 0)
                {
                    var sourcePosition = bodyStart + invalidUnicodeAt;
                    tokens.Add(InvalidUnicodeEscapeToken(src, sourcePosition));
                    return src.Length;
                }

                if (interpolationError is not null)
                {
                    tokens.Add(new PwshToken(
                        PwshTokenKind.UnparseableSentinel,
                        src.Slice(start).ToString(),
                        null,
                        start,
                        src.Length - start,
                        interpolationError));
                    return src.Length;
                }

                var end = k + 2; // past quote + '@'
                tokens.Add(new PwshToken(
                    PwshTokenKind.QuotedString, resolverValue.Decoded, null,
                    start, end - start, null)
                {
                    IsHereString = true,
                    IsSingleQuoted = quote == '\'',
                    HasInterpolation = hasInterpolation,
                    ResolverValue = resolverValue,
                });
                return end;
            }

            k++;
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.UnparseableSentinel,
            src.Slice(start).ToString(), null, start, src.Length - start,
            $"unterminated here-string at position {start}"));
        return src.Length;
    }

    private static ShellValue DecodeExpandableValue(
        ReadOnlySpan<char> value,
        int? sourceStart,
        out bool hasInterpolation,
        out int invalidUnicodeAt,
        out string? interpolationError)
    {
        var decoded = new ShellValueBuilder();
        decoded.AppendBoundary(sourceStart);
        hasInterpolation = false;
        invalidUnicodeAt = -1;
        interpolationError = null;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '`' && i + 1 < value.Length)
            {
                if (IsMalformedUnicodeEscape(value, i))
                {
                    invalidUnicodeAt = i;
                    return decoded.Build();
                }

                i = AppendBacktickEscapeFragment(value, i, decoded, sourceStart) - 1;
                continue;
            }

            if (value[i] == '$' && StartsInterpolation(value, i))
            {
                hasInterpolation = true;
                var expansionIndex = i;
                if (TryAppendPwshExpansion(
                        value,
                        ref expansionIndex,
                        decoded,
                        out interpolationError,
                        sourceStart))
                {
                    if (interpolationError is not null)
                    {
                        return decoded.Build();
                    }

                    i = expansionIndex - 1;
                    continue;
                }
            }

            decoded.AppendLiteral(value[i], sourceStart + i, 1);
        }

        return decoded.Build();
    }

    internal static bool TryDecodeExpandableValue(
        string value, out string decoded, out bool hasInterpolation)
    {
        var resolverValue = DecodeExpandableValue(
            value.AsSpan(),
            null,
            out hasInterpolation,
            out var invalidUnicodeAt,
            out var interpolationError);
        decoded = resolverValue.Decoded;
        return invalidUnicodeAt < 0 && interpolationError is null;
    }

    private static bool StartsInterpolation(ReadOnlySpan<char> value, int dollarIndex)
    {
        if (dollarIndex + 1 >= value.Length)
        {
            return false;
        }

        var next = value[dollarIndex + 1];
        return next is '(' or '{' or '?' or '^' or '$' or '_' or ':'
            || char.IsLetterOrDigit(next);
    }

    private static bool TryAppendPwshExpansion(
        ReadOnlySpan<char> value,
        ref int index,
        ShellValueBuilder target,
        out string? error,
        int? sourceOffset = 0)
    {
        error = null;
        var start = index;
        if (start + 1 >= value.Length || value[start] != '$')
        {
            return false;
        }

        var next = value[start + 1];
        if (next == '(')
        {
            var scan = ScanCommandSubexpression(
                value,
                start + 1,
                allowComments: false);
            if (!scan.Closed)
            {
                error = scan.Error ?? "unbalanced '$(' subexpression";
                index = value.Length;
                return true;
            }

            var subexpressionLength = scan.EndIndex - start + 1;
            target.AppendOpaque(
                value.Slice(start, subexpressionLength).ToString(),
                ShellOpaqueCause.PowerShellSubexpression,
                sourceOffset + start,
                subexpressionLength);
            index += subexpressionLength;
            return true;
        }

        string name;
        int length;
        if (next == '{')
        {
            var scan = OpaqueRegionScanner.Scan(
                value,
                start + 1,
                '{',
                '}',
                OpaqueRegionScanner.PwshEscape);
            if (!scan.Closed)
            {
                error = "unbalanced '${' variable interpolation";
                index = value.Length;
                return true;
            }

            length = scan.EndIndex - start + 1;
            name = value.Slice(start + 2, length - 3).ToString();
            if (name.Length == 0)
            {
                error = "empty '${}' variable interpolation";
                index += length;
                return true;
            }
        }
        else if (next is '?' or '^' or '$')
        {
            length = 2;
            name = next.ToString();
        }
        else if (next == '_' || char.IsLetterOrDigit(next))
        {
            var end = start + 2;
            while (end < value.Length
                && (value[end] == '_' || char.IsLetterOrDigit(value[end])))
            {
                end++;
            }

            if (end < value.Length && value[end] == ':')
            {
                end++;
                while (end < value.Length
                    && (value[end] == '_' || char.IsLetterOrDigit(value[end])))
                {
                    end++;
                }
            }

            length = end - start;
            name = value.Slice(start + 1, length - 1).ToString();
        }
        else
        {
            return false;
        }

        var kind = name is "?" or "^" or "$"
            ? ShellExpansionKind.SpecialParameter
            : ShellExpansionKind.Variable;
        target.AppendExpansion(
            value.Slice(start, length).ToString(),
            ShellLexicalTransform.Variable,
            new ShellExpansionReference(kind, name),
            ShellValueCardinality.ExactlyOne,
            sourceOffset + start,
            length);
        index += length;
        return true;
    }

    private static int AppendBacktickEscapeFragment(
        ReadOnlySpan<char> value,
        int backtickIndex,
        ShellValueBuilder target,
        int? sourceStart)
    {
        var decoded = new StringBuilder();
        var end = AppendBacktickEscape(value, backtickIndex, decoded);
        target.AppendLiteral(
            decoded.ToString(),
            sourceStart + backtickIndex,
            end - backtickIndex);
        return end;
    }

    private static int AppendBacktickEscape(
        ReadOnlySpan<char> value, int backtickIndex, StringBuilder target)
    {
        var escaped = value[backtickIndex + 1];
        if (escaped == 'u' && TryReadUnicodeEscape(
            value, backtickIndex, out var scalar, out var endIndex))
        {
            if (scalar <= char.MaxValue)
            {
                target.Append((char)scalar);
            }
            else
            {
                target.Append(char.ConvertFromUtf32(scalar));
            }

            return endIndex;
        }

        target.Append(escaped switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '0' => '\0',
            'a' => '\a',
            'b' => '\b',
            'e' => '\u001b',
            'f' => '\f',
            'v' => '\v',
            _ => escaped,
        });
        return backtickIndex + 2;
    }

    private static bool TryReadUnicodeEscape(
        ReadOnlySpan<char> value, int backtickIndex, out int scalar, out int endIndex)
    {
        scalar = 0;
        endIndex = backtickIndex + 2;
        var openBrace = backtickIndex + 2;
        if (openBrace >= value.Length || value[openBrace] != '{')
        {
            return false;
        }

        var i = openBrace + 1;
        var digits = 0;
        while (i < value.Length && digits < 6 && TryHexValue(value[i], out var hex))
        {
            scalar = (scalar * 16) + hex;
            digits++;
            i++;
        }

        if (digits == 0 || i >= value.Length || value[i] != '}'
            || scalar > 0x10FFFF)
        {
            scalar = 0;
            return false;
        }

        endIndex = i + 1;
        return true;
    }

    private static bool IsMalformedUnicodeEscape(
        ReadOnlySpan<char> value, int backtickIndex)
    {
        return backtickIndex + 2 < value.Length
            && value[backtickIndex + 1] == 'u'
            && value[backtickIndex + 2] == '{'
            && !TryReadUnicodeEscape(value, backtickIndex, out _, out _);
    }

    private static PwshToken InvalidUnicodeEscapeToken(
        ReadOnlySpan<char> source, int position) => new(
        PwshTokenKind.UnparseableSentinel,
        source.Slice(position).ToString(), null,
        position, source.Length - position,
        $"invalid PowerShell Unicode escape at position {position}");

    private static bool TryHexValue(char value, out int hex)
    {
        if (value >= '0' && value <= '9')
        {
            hex = value - '0';
            return true;
        }

        if (value >= 'a' && value <= 'f')
        {
            hex = value - 'a' + 10;
            return true;
        }

        if (value >= 'A' && value <= 'F')
        {
            hex = value - 'A' + 10;
            return true;
        }

        hex = 0;
        return false;
    }

    // ---------------------------------------------------------------- regions

    /// <summary>
    /// Consume a balanced opaque region (<c>$( )</c>, <c>@( )</c>,
    /// <c>@{ }</c>, or <c>{ }</c>) and emit a single token of
    /// <paramref name="kind"/>. <paramref name="start"/> is where the token
    /// begins (the <c>$</c> / <c>@</c> / <c>{</c>); <paramref name="openAt"/>
    /// is the opening bracket.
    /// </summary>
    private static int ConsumeBalancedRegion(
        ReadOnlySpan<char> src, int start, int openAt,
        char openChar, char closeChar, PwshTokenKind kind,
        string unbalancedReason, List<PwshToken> tokens)
    {
        var scan = start < src.Length && src[start] == '$' && openChar == '('
            ? ScanCommandSubexpression(src, openAt, allowComments: true)
            : ScanOpaqueRegion(src, openAt, openChar, closeChar);
        if (!scan.Closed)
        {
            tokens.Add(new PwshToken(
                PwshTokenKind.UnparseableSentinel,
                src.Slice(start).ToString(), null, start, src.Length - start,
                scan.Error ?? unbalancedReason));
            return src.Length;
        }

        var length = scan.EndIndex - start + 1;
        tokens.Add(new PwshToken(
            kind, src.Slice(start, length).ToString(), null, start, length, null)
        {
            ResolverValue = ShellValue.Opaque(
                    src.Slice(start, length).ToString(),
                    kind == PwshTokenKind.Subexpression
                        ? ShellOpaqueCause.PowerShellSubexpression
                        : ShellOpaqueCause.Unsupported,
                    start,
                    length),
        });
        return start + length;
    }

    private static CommandSubexpressionScan ScanOpaqueRegion(
        ReadOnlySpan<char> src,
        int openAt,
        char openChar,
        char closeChar)
    {
        var scan = OpaqueRegionScanner.Scan(
            src, openAt, openChar, closeChar, OpaqueRegionScanner.PwshEscape);
        return new CommandSubexpressionScan(scan.EndIndex, scan.Closed, null);
    }

    private static CommandSubexpressionScan ScanCommandSubexpression(
        ReadOnlySpan<char> src,
        int openParen,
        bool allowComments)
    {
        if (openParen < 0 || openParen >= src.Length || src[openParen] != '(')
        {
            return new CommandSubexpressionScan(src.Length, false, null);
        }

        var resumeDoubleQuote = new Stack<bool>();
        resumeDoubleQuote.Push(false);
        var inDoubleQuote = false;
        var atWordBoundary = true;
        var i = openParen + 1;
        while (i < src.Length)
        {
            var c = src[i];
            if (inDoubleQuote)
            {
                if (c == '`' && i + 1 < src.Length)
                {
                    i += src[i + 1] == '\r' && i + 2 < src.Length && src[i + 2] == '\n'
                        ? 3
                        : 2;
                    continue;
                }

                if (c == '"')
                {
                    inDoubleQuote = false;
                    atWordBoundary = true;
                    i++;
                    continue;
                }

                if (c == '$' && i + 1 < src.Length && src[i + 1] == '(')
                {
                    if (resumeDoubleQuote.Count >= ShellAnalysisLimits.MaxStructuralNesting)
                    {
                        return PowerShellNestingOverflow(src.Length);
                    }

                    resumeDoubleQuote.Push(true);
                    inDoubleQuote = false;
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '`' && i + 1 < src.Length)
            {
                if (src[i + 1] is not '\n' and not '\r')
                {
                    atWordBoundary = false;
                }

                i += src[i + 1] == '\r' && i + 2 < src.Length && src[i + 2] == '\n'
                    ? 3
                    : 2;
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < src.Length)
                {
                    if (src[i] != '\'')
                    {
                        i++;
                        continue;
                    }

                    if (i + 1 < src.Length && src[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                if (i >= src.Length && (src.Length == 0 || src[src.Length - 1] != '\''))
                {
                    return new CommandSubexpressionScan(src.Length, false, null);
                }

                atWordBoundary = true;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                atWordBoundary = false;
                i++;
                continue;
            }

            if (c == '#' && atWordBoundary)
            {
                if (!allowComments || resumeDoubleQuote.Peek())
                {
                    return new CommandSubexpressionScan(
                        src.Length,
                        false,
                        "comments inside expandable PowerShell subexpressions are not supported");
                }

                while (i < src.Length && src[i] is not '\n' and not '\r')
                {
                    i++;
                }

                atWordBoundary = true;
                continue;
            }

            if (c == '<' && i + 1 < src.Length && src[i + 1] == '#')
            {
                if (!allowComments || resumeDoubleQuote.Peek())
                {
                    return new CommandSubexpressionScan(
                        src.Length,
                        false,
                        "comments inside expandable PowerShell subexpressions are not supported");
                }

                i += 2;
                while (i + 1 < src.Length && (src[i] != '#' || src[i + 1] != '>'))
                {
                    i++;
                }

                if (i + 1 >= src.Length)
                {
                    return new CommandSubexpressionScan(src.Length, false, null);
                }

                i += 2;
                atWordBoundary = true;
                continue;
            }

            if (c == '@' && i + 1 < src.Length && src[i + 1] is '\'' or '"' &&
                StartsHereString(src, i))
            {
                return new CommandSubexpressionScan(
                    src.Length,
                    false,
                    "here-strings inside PowerShell subexpressions are not supported");
            }

            if (c == '$' && i + 1 < src.Length && src[i + 1] == '(')
            {
                if (resumeDoubleQuote.Count >= ShellAnalysisLimits.MaxStructuralNesting)
                {
                    return PowerShellNestingOverflow(src.Length);
                }

                resumeDoubleQuote.Push(false);
                atWordBoundary = true;
                i += 2;
                continue;
            }

            if (c == '(')
            {
                if (resumeDoubleQuote.Count >= ShellAnalysisLimits.MaxStructuralNesting)
                {
                    return PowerShellNestingOverflow(src.Length);
                }

                resumeDoubleQuote.Push(false);
                atWordBoundary = true;
                i++;
                continue;
            }

            if (c == ')')
            {
                var restoreDoubleQuote = resumeDoubleQuote.Pop();
                if (resumeDoubleQuote.Count == 0)
                {
                    return new CommandSubexpressionScan(i, true, null);
                }

                inDoubleQuote = restoreDoubleQuote;
                atWordBoundary = true;
                i++;
                continue;
            }

            atWordBoundary = char.IsWhiteSpace(c) ||
                c is ';' or '|' or '&' or '<' or '>' or '{' or '}';
            i++;
        }

        return new CommandSubexpressionScan(src.Length, false, null);
    }

    private static bool StartsHereString(ReadOnlySpan<char> src, int start)
    {
        var index = start + 2;
        while (index < src.Length && IsInlineWhitespace(src[index]))
        {
            index++;
        }

        return index < src.Length && src[index] is '\n' or '\r';
    }

    private static CommandSubexpressionScan PowerShellNestingOverflow(int endIndex) =>
        new(
            endIndex,
            false,
            $"PowerShell structural nesting depth exceeded (>{ShellAnalysisLimits.MaxStructuralNesting})");

    private readonly record struct CommandSubexpressionScan(
        int EndIndex,
        bool Closed,
        string? Error);

    // ---------------------------------------------------------------- comments

    private static int ConsumeLineComment(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        var i = start;
        while (i < src.Length && src[i] != '\n' && src[i] != '\r')
        {
            i++;
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.Comment, "", null, start, i - start, null));
        return i;
    }

    private static int ConsumeBlockComment(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        // start points at '<', src[start+1] == '#'. Scan for '#>'.
        var i = start + 2;
        while (i + 1 < src.Length)
        {
            if (src[i] == '#' && src[i + 1] == '>')
            {
                var length = (i + 2) - start;
                tokens.Add(new PwshToken(
                    PwshTokenKind.Comment, "", null, start, length, null));
                return i + 2;
            }

            i++;
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.UnparseableSentinel,
            src.Slice(start).ToString(), null, start, src.Length - start,
            $"unterminated block comment '<# … #>' at position {start}"));
        return src.Length;
    }

    // ---------------------------------------------------------------- --%

    private static bool IsStopParsingToken(ReadOnlySpan<char> src, int i)
    {
        if (i + 2 >= src.Length || src[i] != '-' || src[i + 1] != '-' || src[i + 2] != '%')
        {
            return false;
        }

        // `--%` must be a standalone token: followed by whitespace, newline,
        // or end of input.
        if (i + 3 >= src.Length)
        {
            return true;
        }

        var after = src[i + 3];
        return after == ' ' || after == '\t' || after == '\n' || after == '\r';
    }

    private static int ConsumeStopParsing(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        // SPEC §4 / §10: the remainder of the line — including any |, ;, &&,
        // || — becomes one opaque token.
        var i = start;
        while (i < src.Length && src[i] != '\n' && src[i] != '\r')
        {
            i++;
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.StopParsing,
            src.Slice(start, i - start).ToString(), null, start, i - start, null));
        return i;
    }

    // ---------------------------------------------------------------- parameters

    /// <summary>
    /// True when the <c>-</c> at <paramref name="i"/> begins a
    /// <c>-Name</c> parameter: after one or two dashes there is an ASCII
    /// letter, <c>_</c>, or <c>?</c>. A bare <c>-</c> / <c>--</c> and a
    /// negative number (<c>-5</c>) are words, not parameters.
    /// </summary>
    private static bool IsParameterStart(ReadOnlySpan<char> src, int i)
    {
        var p = i + 1;
        if (p < src.Length && src[p] == '-')
        {
            p++;
        }

        if (p >= src.Length)
        {
            return false;
        }

        var c = src[p];
        return IsAsciiLetter(c) || c == '_' || c == '?';
    }

    private static bool IsAlternateParameterDash(char c) =>
        c is '\u2013' or '\u2014' or '\u2015';

    private static int ReadParameter(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        var i = start + 1;
        if (i < src.Length && src[i] == '-')
        {
            i++;
        }

        // Parameter / native-option name. Hyphens after the first name
        // character are significant (`-Name-Part`, `--work-tree`) and `?` is
        // a name character in its own right (`-?`, `-Ba?r`) — both must stay
        // in the same token; the identifier predicate is deliberately not
        // widened because it also governs splat names.
        while (i < src.Length && IsParameterNameContinuation(src[i]))
        {
            i++;
        }

        // Keep an unquoted inline value attached to its source token. The
        // parser interprets ':' only for cmdlet-style parameters and '='
        // only for native options.
        var valueStart = -1;
        if (i < src.Length && (src[i] == ':' || src[i] == '='))
        {
            i++;
            valueStart = i;
            i = ScanWordRun(src, i, out var invalidUnicodeAt);
            if (invalidUnicodeAt >= 0)
            {
                tokens.Add(InvalidUnicodeEscapeToken(src, invalidUnicodeAt));
                return src.Length;
            }
        }

        var resolverValue = ShellValue.Literal(
            src.Slice(start, i - start).ToString(),
            start,
            i - start);
        var hasInterpolation = false;
        if (valueStart >= 0)
        {
            var valueBuilder = new ShellValueBuilder();
            valueBuilder.AppendLiteral(
                src.Slice(start, valueStart - start).ToString(),
                start,
                valueStart - start);
            var valueIndex = valueStart;
            while (valueIndex < i)
            {
                var character = src[valueIndex];
                if (character == '`' && valueIndex + 1 < i)
                {
                    valueIndex = AppendBacktickEscapeFragment(
                        src,
                        valueIndex,
                        valueBuilder,
                        0);
                    continue;
                }

                if (character == '$' && StartsInterpolation(src, valueIndex))
                {
                    hasInterpolation = true;
                    if (TryAppendPwshExpansion(
                        src,
                        ref valueIndex,
                        valueBuilder,
                        out var interpolationError))
                    {
                        if (interpolationError is not null)
                        {
                            resolverValue = ShellValue.Opaque(
                                src.Slice(start, i - start).ToString(),
                                ShellOpaqueCause.Unsupported,
                                start,
                                i - start);
                            break;
                        }

                        if (IsPowerShellExpressionSuffix(src, valueIndex, i))
                        {
                            valueBuilder.AppendOpaque(
                                src.Slice(valueIndex, i - valueIndex).ToString(),
                                ShellOpaqueCause.PowerShellExpressionSuffix,
                                valueIndex,
                                i - valueIndex);
                            valueIndex = i;
                        }

                        continue;
                    }
                }

                if (character == '~' && valueIndex == valueStart)
                {
                    valueBuilder.AppendExpansion(
                        "~",
                        ShellLexicalTransform.Tilde,
                        new ShellExpansionReference(ShellExpansionKind.Tilde, null),
                        ShellValueCardinality.ExactlyOne,
                        valueIndex,
                        1);
                }
                else if (character is '*' or '?' or '[')
                {
                    valueBuilder.AppendExpansion(
                        character.ToString(),
                        ShellLexicalTransform.Glob,
                        new ShellExpansionReference(ShellExpansionKind.Glob, null),
                        ShellValueCardinality.ZeroOrMore,
                        valueIndex,
                        1);
                }
                else if (character == ',')
                {
                    valueBuilder.AppendExpansion(
                        ",",
                        ShellLexicalTransform.FieldSplit,
                        new ShellExpansionReference(ShellExpansionKind.ArraySeparator, null),
                        ShellValueCardinality.ZeroOrMore,
                        valueIndex,
                        1);
                }
                else
                {
                    valueBuilder.AppendLiteral(character, valueIndex, 1);
                }

                valueIndex++;
            }

            if (resolverValue.Fragments.Count == 1
                && resolverValue.Fragments[0].Kind == ShellValueFragmentKind.Literal)
            {
                resolverValue = valueBuilder.Build();
            }
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.Parameter, src.Slice(start, i - start).ToString(),
            null, start, i - start, null)
        {
            HasInterpolation = hasInterpolation,
            ResolverValue = resolverValue,
        });
        return i;
    }

    // ---------------------------------------------------------------- words

    private static int ReadWord(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        var value = new ShellValueBuilder();
        var hasInterpolation = false;
        var i = start;
        while (i < src.Length)
        {
            var c = src[i];

            if (IsWordBoundary(c))
            {
                break;
            }

            // Backtick escape outside quotes: `X takes X literally.
            if (c == '`')
            {
                if (i + 1 >= src.Length)
                {
                    value.AppendLiteral('`', i, 1);
                    i++;
                    break;
                }

                var next = src[i + 1];
                if (next == '\n' || next == '\r')
                {
                    break; // line continuation — handled by the outer loop
                }

                if (IsMalformedUnicodeEscape(src, i))
                {
                    tokens.Add(InvalidUnicodeEscapeToken(src, i));
                    return src.Length;
                }

                i = AppendBacktickEscapeFragment(src, i, value, 0);
                continue;
            }

            // $( terminates the word (subexpression is its own token).
            if (c == '$' && i + 1 < src.Length && src[i + 1] == '(')
            {
                break;
            }

            // ${name} is absorbed verbatim into the word.
            if (c == '$' && i + 1 < src.Length && src[i + 1] == '{')
            {
                hasInterpolation = true;
                var scan = OpaqueRegionScanner.Scan(
                    src, i + 1, '{', '}', OpaqueRegionScanner.PwshEscape);
                if (!scan.Closed)
                {
                    tokens.Add(new PwshToken(
                        PwshTokenKind.UnparseableSentinel,
                        src.Slice(i).ToString(),
                        null,
                        i,
                        src.Length - i,
                        "unbalanced '${' variable interpolation"));
                    return src.Length;
                }

                if (TryAppendPwshExpansion(src, ref i, value, out var error))
                {
                    if (error is not null)
                    {
                        tokens.Add(new PwshToken(
                            PwshTokenKind.UnparseableSentinel,
                            src.Slice(start).ToString(),
                            null,
                            start,
                            src.Length - start,
                            error));
                        return src.Length;
                    }

                    if (IsPowerShellExpressionSuffix(src, i, src.Length))
                    {
                        var suffixEnd = i;
                        while (suffixEnd < src.Length && !IsWordBoundary(src[suffixEnd]))
                        {
                            suffixEnd++;
                        }

                        value.AppendOpaque(
                            src.Slice(i, suffixEnd - i).ToString(),
                            ShellOpaqueCause.PowerShellExpressionSuffix,
                            i,
                            suffixEnd - i);
                        i = suffixEnd;
                    }

                    continue;
                }
            }

            if (c == '$' && StartsInterpolation(src, i))
            {
                hasInterpolation = true;
                if (TryAppendPwshExpansion(src, ref i, value, out var error))
                {
                    if (error is not null)
                    {
                        tokens.Add(new PwshToken(
                            PwshTokenKind.UnparseableSentinel,
                            src.Slice(start).ToString(),
                            null,
                            start,
                            src.Length - start,
                            error));
                        return src.Length;
                    }

                    if (IsPowerShellExpressionSuffix(src, i, src.Length))
                    {
                        var suffixEnd = i;
                        while (suffixEnd < src.Length && !IsWordBoundary(src[suffixEnd]))
                        {
                            suffixEnd++;
                        }

                        value.AppendOpaque(
                            src.Slice(i, suffixEnd - i).ToString(),
                            ShellOpaqueCause.PowerShellExpressionSuffix,
                            i,
                            suffixEnd - i);
                        i = suffixEnd;
                    }

                    continue;
                }
            }

            if (c == '~' && i == start)
            {
                value.AppendExpansion(
                    "~",
                    ShellLexicalTransform.Tilde,
                    new ShellExpansionReference(ShellExpansionKind.Tilde, null),
                    ShellValueCardinality.ExactlyOne,
                    i,
                    1);
            }
            else if (c is '*' or '?' or '[')
            {
                value.AppendExpansion(
                    c.ToString(),
                    ShellLexicalTransform.Glob,
                    new ShellExpansionReference(ShellExpansionKind.Glob, null),
                    ShellValueCardinality.ZeroOrMore,
                    i,
                    1);
            }
            else if (c == ',')
            {
                value.AppendExpansion(
                    ",",
                    ShellLexicalTransform.FieldSplit,
                    new ShellExpansionReference(ShellExpansionKind.ArraySeparator, null),
                    ShellValueCardinality.ZeroOrMore,
                    i,
                    1);
            }
            else
            {
                value.AppendLiteral(c, i, 1);
            }

            i++;
        }

        var resolverValue = value.Build();
        if (resolverValue.Decoded.Length == 0)
        {
            // Defensive: make progress on a char the dispatcher missed.
            return start + 1;
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.Word, resolverValue.Decoded, null, start, i - start, null)
        {
            HasInterpolation = hasInterpolation,
            ResolverValue = resolverValue,
        });
        return i;
    }

    /// <summary>
    /// Scan a word-style run (used for the colon-form parameter value),
    /// returning the index just past the run. Honors backtick escapes and
    /// <c>${name}</c> absorption; stops at a word boundary.
    /// </summary>
    private static int ScanWordRun(
        ReadOnlySpan<char> src, int i, out int invalidUnicodeAt)
    {
        invalidUnicodeAt = -1;
        var start = i;
        while (i < src.Length)
        {
            var c = src[i];
            if (IsWordBoundary(c) || (i == start && c == '#'))
            {
                break;
            }

            if (c == '`')
            {
                if (i + 1 >= src.Length)
                {
                    i++;
                    break;
                }

                if (src[i + 1] == '\n' || src[i + 1] == '\r')
                {
                    break;
                }

                if (src[i + 1] == 'u' && TryReadUnicodeEscape(
                    src, i, out _, out var unicodeEnd))
                {
                    i = unicodeEnd;
                    continue;
                }

                if (IsMalformedUnicodeEscape(src, i))
                {
                    invalidUnicodeAt = i;
                    return src.Length;
                }

                i += 2;
                continue;
            }

            if (c == '$' && i + 1 < src.Length && src[i + 1] == '(')
            {
                break;
            }

            if (c == '$' && i + 1 < src.Length && src[i + 1] == '{')
            {
                var scan = OpaqueRegionScanner.Scan(
                    src, i + 1, '{', '}', OpaqueRegionScanner.PwshEscape);
                if (scan.Closed)
                {
                    i = scan.EndIndex + 1;
                    continue;
                }
            }

            i++;
        }

        return i;
    }

    private static bool IsPowerShellExpressionSuffix(
        ReadOnlySpan<char> source, int index, int end) =>
        index < end && (source[index] == '['
            || (source[index] == '.'
                && index + 1 < end
                && (source[index + 1] == '_' || char.IsLetter(source[index + 1]))));

    private static bool IsWordBoundary(char c)
    {
        switch (c)
        {
            case ' ':
            case '\t':
            case '\n':
            case '\r':
            case '\'':
            case '"':
            case '{':
            case '}':
            case '(':
            case ')':
            case ';':
            case '|':
            case '&':
            case '<':
            case '>':
                return true;
            default:
                return c == '\0' || IsInlineWhitespace(c);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsAsciiLetter(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsInlineWhitespace(char c) =>
        c != '\r' && c != '\n' && char.IsWhiteSpace(c);

    private static bool IsIdentifierStart(char c) =>
        IsAsciiLetter(c) || c == '_';

    private static bool IsIdentifierContinuation(char c) =>
        IsAsciiLetter(c) || (c >= '0' && c <= '9') || c == '_';

    // Mirrors IsParameterStart, which already admits '?' — without it
    // `Get-Help -?` lexed as a bare `-` flag plus a `?` glob arg.
    private static bool IsParameterNameContinuation(char c) =>
        IsIdentifierContinuation(c) || c == '-' || c == '?';
}
