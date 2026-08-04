// -----------------------------------------------------------------------
// <copyright file="PwshLexer.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal.Lexing;

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
///   <item>Opaque regions — <c>$( … )</c>, <c>@( … )</c>, <c>@{ … }</c>,
///         <c>{ … }</c> — are bounded by <see cref="OpaqueRegionScanner"/>
///         (in PowerShell backtick-escape mode) and emitted whole.</item>
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

            // ---- whitespace ----
            if (c == ' ' || c == '\t')
            {
                var start = i;
                while (i < src.Length && (src[i] == ' ' || src[i] == '\t'))
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
    /// first — <c>&gt;</c>, <c>&gt;&gt;</c>, <c>&lt;</c>; <c>N&gt;</c> /
    /// <c>N&gt;&gt;</c> for stream N in {1..6}; <c>*&gt;</c> / <c>*&gt;&gt;</c>;
    /// and the stream-merge form <c>N&gt;&amp;M</c>.
    /// </summary>
    private static bool TryReadRedirect(
        ReadOnlySpan<char> src, int i, out int length, out string? text)
    {
        length = 0;
        text = null;

        var prefixed = false;
        var p = i;
        if (src[i] == '*' || (src[i] >= '1' && src[i] <= '6'))
        {
            if (i + 1 < src.Length && src[i + 1] == '>')
            {
                prefixed = true;
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
            if (prefixed)
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

        // Stream-merge: '>' '&' digit.
        if (p + 2 < src.Length && src[p + 1] == '&'
            && src[p + 2] >= '1' && src[p + 2] <= '6')
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
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < src.Length)
        {
            if (src[i] == '\'')
            {
                if (i + 1 < src.Length && src[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }

                tokens.Add(new PwshToken(
                    PwshTokenKind.QuotedString, sb.ToString(), null,
                    start, (i - start) + 1, null) { IsSingleQuoted = true });
                return i + 1;
            }

            sb.Append(src[i]);
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
        // interpolation, but the parser does NOT expand — $var stays literal
        // in the value (SPEC §5).
        var sb = new StringBuilder();
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
                    sb.Append('"');
                    i += 2;
                    continue;
                }

                tokens.Add(new PwshToken(
                    PwshTokenKind.QuotedString, sb.ToString(), null,
                    start, (i - start) + 1, null)
                    { HasInterpolation = hasInterpolation });
                return i + 1;
            }

            if (c == '`' && i + 1 < src.Length)
            {
                var n = src[i + 1];
                sb.Append(n switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    _ => n,
                });
                i += 2;
                continue;
            }

            if (c == '$' && StartsInterpolation(src, i))
            {
                hasInterpolation = true;
            }

            sb.Append(c);
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
                null, start, i - start, null));
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

                var body = bodyEnd >= bodyStart
                    ? src.Slice(bodyStart, bodyEnd - bodyStart).ToString()
                    : string.Empty;
                var hasInterpolation = quote == '"'
                    && ContainsInterpolation(src.Slice(bodyStart, bodyEnd - bodyStart));
                var end = k + 2; // past quote + '@'
                tokens.Add(new PwshToken(
                    PwshTokenKind.QuotedString, body, null,
                    start, end - start, null)
                    {
                        IsHereString = true,
                        IsSingleQuoted = quote == '\'',
                        HasInterpolation = hasInterpolation,
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

    private static bool ContainsInterpolation(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '`' && i + 1 < value.Length)
            {
                i++;
                continue;
            }

            if (value[i] == '$' && StartsInterpolation(value, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsInterpolation(ReadOnlySpan<char> value, int dollarIndex)
    {
        if (dollarIndex + 1 >= value.Length)
        {
            return false;
        }

        var next = value[dollarIndex + 1];
        return next is '(' or '{' or '?' or '^' or '$' or '_'
            || char.IsLetterOrDigit(next);
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
        var scan = OpaqueRegionScanner.Scan(
            src, openAt, openChar, closeChar, OpaqueRegionScanner.PwshEscape);
        if (!scan.Closed)
        {
            tokens.Add(new PwshToken(
                PwshTokenKind.UnparseableSentinel,
                src.Slice(start).ToString(), null, start, src.Length - start,
                unbalancedReason));
            return src.Length;
        }

        var length = scan.EndIndex - start + 1;
        tokens.Add(new PwshToken(
            kind, src.Slice(start, length).ToString(), null, start, length, null));
        return start + length;
    }

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
        if (i < src.Length && (src[i] == ':' || src[i] == '='))
        {
            i++;
            i = ScanWordRun(src, i);
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.Parameter, src.Slice(start, i - start).ToString(),
            null, start, i - start, null));
        return i;
    }

    // ---------------------------------------------------------------- words

    private static int ReadWord(
        ReadOnlySpan<char> src, int start, List<PwshToken> tokens)
    {
        var sb = new StringBuilder();
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
                    sb.Append('`');
                    i++;
                    break;
                }

                var n = src[i + 1];
                if (n == '\n' || n == '\r')
                {
                    break; // line continuation — handled by the outer loop
                }

                sb.Append(n);
                i += 2;
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
                    // Unbalanced — emit the $ literally and let the outer
                    // loop reach the '{' and produce a sentinel.
                    sb.Append('$');
                    i++;
                    continue;
                }

                var braceLen = scan.EndIndex - i + 1;
                for (var k = 0; k < braceLen; k++)
                {
                    sb.Append(src[i + k]);
                }

                i += braceLen;
                continue;
            }

            if (c == '$' && StartsInterpolation(src, i))
            {
                hasInterpolation = true;
            }

            sb.Append(c);
            i++;
        }

        if (sb.Length == 0)
        {
            // Defensive: make progress on a char the dispatcher missed.
            return start + 1;
        }

        tokens.Add(new PwshToken(
            PwshTokenKind.Word, sb.ToString(), null, start, i - start, null)
            { HasInterpolation = hasInterpolation });
        return i;
    }

    /// <summary>
    /// Scan a word-style run (used for the colon-form parameter value),
    /// returning the index just past the run. Honors backtick escapes and
    /// <c>${name}</c> absorption; stops at a word boundary.
    /// </summary>
    private static int ScanWordRun(ReadOnlySpan<char> src, int i)
    {
        while (i < src.Length)
        {
            var c = src[i];
            if (IsWordBoundary(c))
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
                return false;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsAsciiLetter(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsIdentifierStart(char c) =>
        IsAsciiLetter(c) || c == '_';

    private static bool IsIdentifierContinuation(char c) =>
        IsAsciiLetter(c) || (c >= '0' && c <= '9') || c == '_';

    // Mirrors IsParameterStart, which already admits '?' — without it
    // `Get-Help -?` lexed as a bare `-` flag plus a `?` glob arg.
    private static bool IsParameterNameContinuation(char c) =>
        IsIdentifierContinuation(c) || c == '-' || c == '?';
}
