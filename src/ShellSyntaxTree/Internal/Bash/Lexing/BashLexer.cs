// -----------------------------------------------------------------------
// <copyright file="BashLexer.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal.Lexing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Lexing;

/// <summary>
/// Tokenizer for the bash subset described in SPEC §4 / §5. The output
/// is a flat <see cref="IReadOnlyList{T}"/> of <see cref="BashToken"/>
/// — the parser is responsible for grouping into clauses, verb chains,
/// args, and redirects.
///
/// Design notes:
/// <list type="bullet">
///   <item>The lexer never expands variables. <c>$VAR</c> and
///         <c>${VAR}</c> stay literal inside a <see cref="BashTokenKind.Word"/>
///         token; the resolver in PR 4 decides what to do with them.</item>
///   <item>Command substitutions use a Bash-specific boundary scan so line
///         comments cannot hide a closing parenthesis. Backtick regions use
///         <see cref="OpaqueRegionScanner"/>. Both are emitted as a single
///         <see cref="BashTokenKind.OpaqueSubstitution"/> token.</item>
///   <item>Constructs SPEC §1 calls non-goals — arithmetic expansion
///         <c>$((…))</c> / <c>$[…]</c> and complex parameter expansion
///         <c>${var//pat/repl}</c> — emit a
///         <see cref="BashTokenKind.UnparseableSentinel"/>. The parser
///         lifts that sentinel into <c>ParsedCommand.IsUnparseable</c>.</item>
///   <item>Heredoc bodies do not become ordinary tokens. The delimiter token
///         retains body resolver fragments so stable v0.3 can discover
///         executable substitutions without treating body data as argv.</item>
/// </list>
/// </summary>
internal static class BashLexer
{
    /// <summary>
    /// Tokenize <paramref name="input"/> per SPEC §5. Never throws on
    /// non-null input; malformed regions surface as
    /// <see cref="BashTokenKind.UnparseableSentinel"/> tokens that the
    /// parser must lift into <c>ParsedCommand.IsUnparseable</c>.
    /// </summary>
    internal static IReadOnlyList<BashToken> Tokenize(string input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (input.Length == 0)
        {
            return Array.Empty<BashToken>();
        }

        var tokens = new List<BashToken>();
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

                tokens.Add(new BashToken(
                    BashTokenKind.Whitespace, "", null, start, i - start, null));
                continue;
            }

            // ---- newline (treated as a sequence terminator analogous to ';') ----
            // Per SPEC §4, top-level newlines separate clauses just like ';'.
            // Emitting them as a Whitespace token preserves source fidelity for
            // the parser without requiring a dedicated token kind.
            if (c == '\n' || c == '\r')
            {
                var start = i;
                while (i < src.Length && (src[i] == '\n' || src[i] == '\r'))
                {
                    i++;
                }

                tokens.Add(new BashToken(
                    BashTokenKind.Whitespace, "", null, start, i - start, null)
                { IsStatementSeparator = true });
                continue;
            }

            // ---- backslash + newline = continuation (treat as whitespace) ----
            if (c == '\\' && i + 1 < src.Length && (src[i + 1] == '\n' || src[i + 1] == '\r'))
            {
                var start = i;
                i += 2;
                // Optionally consume a paired \r\n.
                if (i - start == 2 && start + 1 < src.Length && src[start + 1] == '\r'
                    && i < src.Length && src[i] == '\n')
                {
                    i++;
                }

                tokens.Add(new BashToken(
                    BashTokenKind.Continuation, "", null, start, i - start, null));
                continue;
            }

            // ---- operators (longer-match first) ----
            // Order matters: a token-boundary numeric descriptor precedes its
            // redirect, `&&` precedes `&`, `||` precedes `|`, `>>` precedes
            // `>`, and `<<<` / `<<-` precede `<<` and `<`. Bare `&`
            // background jobs remain unsupported.
            if (TryReadOperator(
                    src,
                    i,
                    CanStartNumericDescriptor(tokens),
                    out var opLen,
                    out var opText))
            {
                var operatorTok = new BashToken(
                    BashTokenKind.Operator, "", opText, i, opLen, null);
                tokens.Add(operatorTok);
                i += opLen;

                // Heredoc handling: when we just emitted `<<` or `<<-`, the
                // *next* word is the delimiter and the body that follows the
                // first newline must be skipped per SPEC §4.
                if (IsHeredocOperator(opText))
                {
                    i = ConsumeHeredoc(src, i, opText!, tokens);
                }

                continue;
            }

            if (c == '&')
            {
                if (TryConsumeFileDescriptorTarget(src, i, tokens, out var afterTarget))
                {
                    i = afterTarget;
                    continue;
                }

                tokens.Add(new BashToken(
                    BashTokenKind.UnparseableSentinel,
                    src.Slice(i).ToString(),
                    null,
                    i,
                    src.Length - i,
                    "single-'&' background lists are not supported"));
                return tokens;
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

            // ---- $( ... ) command substitution / $((expr)) / ${var//...} ----
            if (c == '$' && i + 1 < src.Length)
            {
                var next = src[i + 1];
                if (next is '\'' or '"')
                {
                    tokens.Add(new BashToken(
                        BashTokenKind.UnparseableSentinel,
                        src.Slice(i).ToString(),
                        null,
                        i,
                        src.Length - i,
                        next == '\''
                            ? "ANSI-C quoted strings are not supported"
                            : "localized quoted strings are not supported"));
                    return tokens;
                }

                if (next == '(')
                {
                    // $(( -> arithmetic, unparseable. Detect before $(.
                    if (i + 2 < src.Length && src[i + 2] == '(')
                    {
                        i = ConsumeArithmetic(src, i, tokens);
                        continue;
                    }

                    i = ConsumeCommandSubstitution(src, i, tokens);
                    continue;
                }

                if (next == '[')
                {
                    i = ConsumeObsoleteArithmetic(src, i, tokens);
                    continue;
                }

                if (next == '{')
                {
                    if (TryConsumeComplexParamExpansion(src, i, tokens, out var afterBrace))
                    {
                        i = afterBrace;
                        continue;
                    }

                    // Simple ${VAR}: fall through to word reader so the whole
                    // thing (and any adjacent text) becomes one Word token.
                }
            }

            // ---- backtick command substitution ----
            if (c == '`')
            {
                i = ConsumeBacktickSubstitution(src, i, tokens);
                continue;
            }

            // ---- line comment ----
            // Reaching this branch implies a word boundary (quotes,
            // operators, and opaque regions are dispatched above), so `#`
            // here starts a comment to EOL. Mid-word `#` is consumed by
            // ReadWord, and `\#` by its escape handling — neither reaches
            // this point. SPEC §5.
            if (c == '#')
            {
                i = ConsumeLineComment(src, i, tokens);
                continue;
            }

            // ---- word ----
            i = ReadWord(src, i, tokens);
        }

        return tokens;
    }

    private static bool TryConsumeFileDescriptorTarget(
        ReadOnlySpan<char> src,
        int start,
        List<BashToken> tokens,
        out int afterTarget)
    {
        afterTarget = start;
        var previousIndex = tokens.Count - 1;
        while (previousIndex >= 0)
        {
            var previous = tokens[previousIndex];
            if (previous.Kind == BashTokenKind.Whitespace)
            {
                if (previous.IsStatementSeparator)
                {
                    return false;
                }

                previousIndex--;
                continue;
            }

            if (previous.Kind is BashTokenKind.Continuation or BashTokenKind.Comment)
            {
                previousIndex--;
                continue;
            }

            break;
        }

        if (previousIndex < 0 || tokens[previousIndex].Kind != BashTokenKind.Operator ||
            !CanTakeDescriptorTarget(tokens[previousIndex].OperatorText))
        {
            return false;
        }

        var end = start + 1;
        if (end < src.Length && src[end] == '$')
        {
            afterTarget = ReadWord(src, start, tokens, allowLeadingAmpersand: true);
            return afterTarget > start + 1;
        }

        if (end < src.Length && src[end] == '-')
        {
            end++;
        }
        else
        {
            var digitStart = end;
            while (end < src.Length && src[end] is >= '0' and <= '9')
            {
                end++;
            }

            if (end == digitStart)
            {
                return false;
            }

            if (end < src.Length && src[end] == '-')
            {
                end++;
            }
        }

        if (end < src.Length && !char.IsWhiteSpace(src[end]) && !IsOperatorStart(src, end))
        {
            return false;
        }

        var raw = src.Slice(start, end - start).ToString();
        tokens.Add(new BashToken(
            BashTokenKind.Word,
            raw,
            null,
            start,
            end - start,
            null)
        {
            ResolverValue = ShellValue.Literal(raw, start, end - start),
        });
        afterTarget = end;
        return true;
    }

    private static bool CanTakeDescriptorTarget(string? operatorText)
    {
        if (string.IsNullOrEmpty(operatorText))
        {
            return false;
        }

        var operatorStart = 0;
        while (operatorStart < operatorText!.Length &&
               operatorText[operatorStart] is >= '0' and <= '9')
        {
            operatorStart++;
        }

        var redirect = operatorText.Substring(operatorStart);
        return redirect is ">" or ">>" or "<";
    }

    // ---------------------------------------------------------------- operators

    private static bool TryReadOperator(
        ReadOnlySpan<char> src,
        int i,
        bool canStartNumericDescriptor,
        out int length,
        out string? text)
    {
        var descriptor = new StringBuilder();
        var descriptorEnd = i;
        while (descriptorEnd < src.Length)
        {
            if (src[descriptorEnd] is >= '0' and <= '9')
            {
                descriptor.Append(src[descriptorEnd]);
                descriptorEnd++;
                continue;
            }

            if (TrySkipLineContinuation(src, descriptorEnd, out var afterContinuation))
            {
                descriptorEnd = afterContinuation;
                continue;
            }

            break;
        }

        if (canStartNumericDescriptor &&
            descriptor.Length > 0 &&
            descriptorEnd < src.Length)
        {
            if (src[descriptorEnd] == '>')
            {
                var append = descriptorEnd + 1 < src.Length &&
                    src[descriptorEnd + 1] == '>';
                length = descriptorEnd - i + (append ? 2 : 1);
                text = descriptor.ToString() + (append ? ">>" : ">");
                return true;
            }

            if (src[descriptorEnd] == '<')
            {
                var redirectLength = 1;
                if (descriptorEnd + 1 < src.Length && src[descriptorEnd + 1] == '<')
                {
                    redirectLength = descriptorEnd + 2 < src.Length &&
                        src[descriptorEnd + 2] is '<' or '-'
                            ? 3
                            : 2;
                }

                length = descriptorEnd - i + redirectLength;
                text = descriptor.ToString() +
                    (redirectLength == 3
                        ? src[descriptorEnd + 2] == '<' ? "<<<" : "<<-"
                        : redirectLength == 2 ? "<<" : "<");
                return true;
            }
        }

        // Multi-char operators first.
        if (i + 1 < src.Length)
        {
            var c0 = src[i];
            var c1 = src[i + 1];
            if (c0 == '&' && c1 == '>')
            {
                if (i + 2 < src.Length && src[i + 2] == '>')
                {
                    length = 3; text = "&>>"; return true;
                }

                length = 2; text = "&>"; return true;
            }

            if (c0 == '&' && c1 == '&') { length = 2; text = "&&"; return true; }
            if (c0 == '|' && c1 == '|') { length = 2; text = "||"; return true; }
            if (c0 == '>' && c1 == '>') { length = 2; text = ">>"; return true; }
            if (c0 == '<' && c1 == '<')
            {
                if (i + 2 < src.Length && src[i + 2] is '<' or '-')
                {
                    length = 3;
                    text = src[i + 2] == '<' ? "<<<" : "<<-";
                    return true;
                }

                length = 2; text = "<<"; return true;
            }

        }

        // Single-char operators.
        switch (src[i])
        {
            case ';': length = 1; text = ";"; return true;
            case '|': length = 1; text = "|"; return true;
            case '>': length = 1; text = ">"; return true;
            case '<': length = 1; text = "<"; return true;
            case '(': length = 1; text = "("; return true;
            case ')': length = 1; text = ")"; return true;
            default:
                length = 0; text = null; return false;
        }
    }

    private static bool TrySkipLineContinuation(
        ReadOnlySpan<char> source,
        int index,
        out int afterContinuation)
    {
        afterContinuation = index;
        if (index + 1 >= source.Length || source[index] != '\\' ||
            source[index + 1] is not ('\n' or '\r'))
        {
            return false;
        }

        afterContinuation = source[index + 1] == '\r' &&
            index + 2 < source.Length && source[index + 2] == '\n'
            ? index + 3
            : index + 2;
        return true;
    }

    internal static bool IsHeredocOperator(string? operatorText)
    {
        if (string.IsNullOrEmpty(operatorText))
        {
            return false;
        }

        var operatorStart = 0;
        while (operatorStart < operatorText!.Length &&
               operatorText[operatorStart] is >= '0' and <= '9')
        {
            operatorStart++;
        }

        var remaining = operatorText.Length - operatorStart;
        return remaining >= 2 &&
               operatorText[operatorStart] == '<' &&
               operatorText[operatorStart + 1] == '<' &&
               (remaining == 2 || remaining == 3 && operatorText[operatorStart + 2] == '-');
    }

    internal static bool IsHereStringOperator(string? operatorText)
    {
        if (string.IsNullOrEmpty(operatorText))
        {
            return false;
        }

        var operatorStart = 0;
        while (operatorStart < operatorText!.Length &&
               operatorText[operatorStart] is >= '0' and <= '9')
        {
            operatorStart++;
        }

        return operatorText.AsSpan(operatorStart).SequenceEqual("<<<".AsSpan());
    }

    private static bool CanStartNumericDescriptor(IReadOnlyList<BashToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return true;
        }

        var index = tokens.Count - 1;
        while (index >= 0 && tokens[index].Kind == BashTokenKind.Continuation)
        {
            index--;
        }

        if (index < 0)
        {
            return true;
        }

        return tokens[index].Kind is
            BashTokenKind.Whitespace or
            BashTokenKind.Operator or
            BashTokenKind.Comment;
    }

    // ---------------------------------------------------------------- quoted

    private static int ReadSingleQuoted(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
    {
        // Single quotes preserve bytes literally per SPEC §5 — no escape
        // processing, no variable expansion. Find the next ' and we're done.
        var i = start + 1;
        while (i < src.Length && src[i] != '\'')
        {
            i++;
        }

        if (i >= src.Length)
        {
            // Unbalanced — emit a sentinel covering the rest of the input
            // and stop. The parser will lift this to ParsedCommand.IsUnparseable.
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                src.Slice(start).ToString(),
                null,
                start,
                src.Length - start,
                $"unbalanced quote at position {start}"));
            return src.Length;
        }

        // src[start]   = opening '
        // src[i]       = closing '
        // Strip the delimiters from the value per SPEC §5.
        var inner = src.Slice(start + 1, i - start - 1).ToString();
        var boundary = new ShellValueBuilder();
        boundary.AppendBoundary(start + 1);
        if (inner.Length > 0)
        {
            boundary.AppendLiteral(inner, start + 1, i - start - 1);
        }

        var resolverValue = boundary.Build();

        tokens.Add(new BashToken(
            BashTokenKind.QuotedString, inner, null, start, (i - start) + 1, null)
        {
            IsSingleQuoted = true,
            ResolverValue = resolverValue,
        });
        return i + 1;
    }

    private static int ReadDoubleQuoted(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
    {
        // Double quotes preserve whitespace but recognize \", \\, \$, and
        // \\+newline as escape sequences (SPEC §5). Other backslashes are
        // preserved literally. Expansion spelling stays decoded while its
        // typed resolver provenance remains attached to the token.
        var value = new ShellValueBuilder();
        value.AppendBoundary(start + 1);
        var i = start + 1;
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '"')
            {
                var resolverValue = value.Build();
                tokens.Add(new BashToken(
                    BashTokenKind.QuotedString, resolverValue.Decoded, null,
                    start, (i - start) + 1, null)
                { ResolverValue = resolverValue });
                return i + 1;
            }

            if (c == '\\' && i + 1 < src.Length)
            {
                var n = src[i + 1];
                if (n == '"' || n == '\\' || n == '$' || n == '`')
                {
                    value.AppendLiteral(n, i, 2);
                    i += 2;
                    continue;
                }

                if (n == '\n')
                {
                    // Line continuation inside double quotes: drop both.
                    i += 2;
                    continue;
                }

                // Other backslashes preserved literally per SPEC §5.
                value.AppendLiteral(c, i, 1);
                i++;
                continue;
            }

            if (c == '$'
                && TryAppendBashExpansion(
                    src, ref i, value, allowFieldSplit: false, out var error))
            {
                if (error is not null)
                {
                    tokens.Add(new BashToken(
                        BashTokenKind.UnparseableSentinel,
                        src.Slice(start).ToString(),
                        null,
                        start,
                        src.Length - start,
                        error));
                    return src.Length;
                }

                continue;
            }

            if (c == '`')
            {
                var scan = OpaqueRegionScanner.ScanSymmetric(src, i, '`');
                if (!scan.Closed)
                {
                    tokens.Add(new BashToken(
                        BashTokenKind.UnparseableSentinel,
                        src.Slice(start).ToString(),
                        null,
                        start,
                        src.Length - start,
                        "unbalanced backtick command substitution"));
                    return src.Length;
                }

                var length = scan.EndIndex - i + 1;
                value.AppendOpaque(
                    src.Slice(i, length).ToString(),
                    ShellOpaqueCause.CommandSubstitution,
                    i,
                    length);
                i += length;
                continue;
            }

            value.AppendLiteral(c, i, 1);
            i++;
        }

        // Unbalanced double quote.
        tokens.Add(new BashToken(
            BashTokenKind.UnparseableSentinel,
            src.Slice(start).ToString(),
            null,
            start,
            src.Length - start,
            $"unbalanced quote at position {start}"));
        return src.Length;
    }

    // ---------------------------------------------------------------- substitutions

    private static int ConsumeCommandSubstitution(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
    {
        // src[start] = '$', src[start+1] = '('
        var openParen = start + 1;
        var scan = ScanCommandSubstitution(src, openParen);
        if (!scan.Closed)
        {
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                src.Slice(start).ToString(),
                null,
                start,
                src.Length - start,
                scan.Error ?? "unbalanced '$(' command substitution"));
            return src.Length;
        }

        // EndIndex is the closing ')' (inclusive). The opaque region runs
        // from start ($) through EndIndex (closing paren) inclusive.
        var length = scan.EndIndex - start + 1;
        tokens.Add(new BashToken(
            BashTokenKind.OpaqueSubstitution,
            src.Slice(start, length).ToString(),
            null,
            start,
            length,
            null)
        {
            ResolverValue = ShellValue.Opaque(
                    src.Slice(start, length).ToString(),
                    ShellOpaqueCause.CommandSubstitution,
                    start,
                    length),
        });
        return start + length;
    }

    private static CommandSubstitutionScan ScanCommandSubstitution(
        ReadOnlySpan<char> src,
        int openParen)
    {
        if (openParen < 0 || openParen >= src.Length || src[openParen] != '(')
        {
            return new CommandSubstitutionScan(src.Length, false, null);
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
                if (c == '\\' && i + 1 < src.Length)
                {
                    if (src[i + 1] == '\r' && i + 2 < src.Length && src[i + 2] == '\n')
                    {
                        i += 3;
                        continue;
                    }

                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    inDoubleQuote = false;
                    i++;
                    continue;
                }

                if (c == '`')
                {
                    return new CommandSubstitutionScan(
                        src.Length,
                        false,
                        "legacy backtick command substitution is not supported");
                }

                if (c == '$' && i + 1 < src.Length && src[i + 1] == '(')
                {
                    if (resumeDoubleQuote.Count >= ShellAnalysisLimits.MaxStructuralNesting)
                    {
                        return StructuralNestingOverflow(src.Length);
                    }

                    resumeDoubleQuote.Push(true);
                    inDoubleQuote = false;
                    atWordBoundary = true;
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '\\' && i + 1 < src.Length)
            {
                if (src[i + 1] is '\n' or '\r')
                {
                    i += src[i + 1] == '\r' && i + 2 < src.Length && src[i + 2] == '\n'
                        ? 3
                        : 2;
                    continue;
                }

                atWordBoundary = false;
                i += 2;
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < src.Length && src[i] != '\'')
                {
                    i++;
                }

                if (i >= src.Length)
                {
                    return new CommandSubstitutionScan(src.Length, false, null);
                }

                atWordBoundary = false;
                i++;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                atWordBoundary = false;
                i++;
                continue;
            }

            if (c == '`')
            {
                return new CommandSubstitutionScan(
                    src.Length,
                    false,
                    "legacy backtick command substitution is not supported");
            }

            if (c == '#' && atWordBoundary)
            {
                while (i < src.Length && src[i] is not '\n' and not '\r')
                {
                    i++;
                }

                atWordBoundary = true;
                continue;
            }

            if (c == '<' && i + 1 < src.Length && src[i + 1] == '<')
            {
                return new CommandSubstitutionScan(
                    src.Length,
                    false,
                    "heredocs inside command substitution are not supported");
            }

            if (c == '$' && i + 1 < src.Length && src[i + 1] == '(')
            {
                if (resumeDoubleQuote.Count >= ShellAnalysisLimits.MaxStructuralNesting)
                {
                    return StructuralNestingOverflow(src.Length);
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
                    return StructuralNestingOverflow(src.Length);
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
                    return new CommandSubstitutionScan(i, true, null);
                }

                inDoubleQuote = restoreDoubleQuote;
                atWordBoundary = false;
                i++;
                continue;
            }

            atWordBoundary = char.IsWhiteSpace(c) || c is ';' or '|' or '&' or '<' or '>';
            i++;
        }

        return new CommandSubstitutionScan(src.Length, false, null);
    }

    private static CommandSubstitutionScan StructuralNestingOverflow(int endIndex) =>
        new(
            endIndex,
            false,
            $"Bash structural nesting depth exceeded (>{ShellAnalysisLimits.MaxStructuralNesting})");

    private readonly record struct CommandSubstitutionScan(
        int EndIndex,
        bool Closed,
        string? Error);

    private static int ConsumeBacktickSubstitution(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
    {
        var scan = OpaqueRegionScanner.ScanSymmetric(src, start, '`');
        if (!scan.Closed)
        {
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                src.Slice(start).ToString(),
                null,
                start,
                src.Length - start,
                "unbalanced backtick command substitution"));
            return src.Length;
        }

        var length = scan.EndIndex - start + 1;
        tokens.Add(new BashToken(
            BashTokenKind.OpaqueSubstitution,
            src.Slice(start, length).ToString(),
            null,
            start,
            length,
            null)
        {
            ResolverValue = ShellValue.Opaque(
                    src.Slice(start, length).ToString(),
                    ShellOpaqueCause.CommandSubstitution,
                    start,
                    length),
        });
        return start + length;
    }

    // Consume `#` through (but not including) the next newline. The
    // terminating newline stays in the stream so the outer loop emits it
    // as a Whitespace token, preserving SPEC §4 clause-boundary
    // semantics. Value is "" to match Whitespace/Continuation — callers
    // that need the literal text can slice the source via
    // SourceStart/SourceLength.
    private static int ConsumeLineComment(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
    {
        var i = start;
        while (i < src.Length && src[i] != '\n' && src[i] != '\r')
        {
            i++;
        }

        tokens.Add(new BashToken(
            BashTokenKind.Comment, "", null, start, i - start, null));
        return i;
    }

    private static int ConsumeArithmetic(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
    {
        // src[start] = '$', src[start+1] = '(', src[start+2] = '(' — and we
        // need to skip past matching '))'. Use the opaque scanner anchored
        // at the outer '(' so we count the correct depth: the inner '(' is
        // its own sub-region for the scanner.
        var outerParen = start + 1;
        var scan = OpaqueRegionScanner.Scan(src, outerParen, '(', ')');
        // We want to land *one* paren past EndIndex (the inner ))) close).
        // OpaqueRegionScanner's depth counting closes when the *outer* paren
        // balances; for $(( the inner '(' bumps depth back to 1 when it sees
        // the first ')', then to 0 when it sees the second ')'. So EndIndex
        // already points at the *second* ')' — i.e. the bash arithmetic close.
        int endInclusive;
        if (scan.Closed)
        {
            endInclusive = scan.EndIndex;
        }
        else
        {
            // Unterminated: still consume the rest so the lexer can move on,
            // but mark the token's reason accordingly.
            endInclusive = src.Length - 1;
        }

        var length = endInclusive - start + 1;
        var reason = scan.Closed
            ? "arithmetic expansion '$((…))' not supported in v0.1"
            : "unterminated arithmetic expansion '$((…))' (also not supported in v0.1)";
        tokens.Add(new BashToken(
            BashTokenKind.UnparseableSentinel,
            src.Slice(start, length).ToString(),
            null,
            start,
            length,
            reason));
        return start + length;
    }

    private static int ConsumeObsoleteArithmetic(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
    {
        var scan = OpaqueRegionScanner.Scan(src, start + 1, '[', ']');
        var endInclusive = scan.Closed ? scan.EndIndex : src.Length - 1;
        var length = endInclusive - start + 1;
        var reason = scan.Closed
            ? "obsolete arithmetic expansion '$[…]': not supported in v0.3"
            : "unterminated obsolete arithmetic expansion '$[…]': not supported in v0.3";
        tokens.Add(new BashToken(
            BashTokenKind.UnparseableSentinel,
            src.Slice(start, length).ToString(),
            null,
            start,
            length,
            reason));
        return start + length;
    }

    private static bool TryConsumeComplexParamExpansion(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens, out int afterBrace)
    {
        // src[start] = '$', src[start+1] = '{'. We need to find the matching
        // '}' and decide: a simple variable, positional, or special parameter
        // falls through to the word reader. Operators can themselves contain
        // executable substitutions, so every other body fails closed.
        var openBrace = start + 1;
        var scan = OpaqueRegionScanner.Scan(src, openBrace, '{', '}');
        if (!scan.Closed)
        {
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                src.Slice(start).ToString(),
                null,
                start,
                src.Length - start,
                "unbalanced '${' parameter expansion"));
            afterBrace = src.Length;
            return true;
        }

        var endInclusive = scan.EndIndex;
        var bodyStart = openBrace + 1;
        var bodyEnd = endInclusive; // exclusive of '}'
        var body = src.Slice(bodyStart, bodyEnd - bodyStart);
        if (IsSimpleBracedParameterName(body))
        {
            afterBrace = -1;
            return false;
        }

        var length = endInclusive - start + 1;
        tokens.Add(new BashToken(
            BashTokenKind.UnparseableSentinel,
            src.Slice(start, length).ToString(),
            null,
            start,
            length,
            "complex parameter expansion is not supported in v0.3"));
        afterBrace = start + length;
        return true;
    }

    // ---------------------------------------------------------------- words

    private static int ReadWord(
        ReadOnlySpan<char> src,
        int start,
        List<BashToken> tokens,
        bool allowLeadingAmpersand = false)
    {
        // A word continues until we hit whitespace, an operator boundary,
        // a newline, a quote, a backtick, or the start of an opaque region
        // ($( or $((  or ${...//...} that we'd lift to a sentinel). We
        // honor `\X` escapes by consuming both the backslash and X
        // verbatim — the resulting Word value contains X literally,
        // which preserves SPEC §5's "echo \$HOME → token whose value is
        // $HOME" behavior. (We can't distinguish escaped-$ from
        // literal-$ in the value alone; the parser doesn't need to —
        // unescaped $VAR/${VAR} stayed in the source verbatim, escaped
        // ones came out the same way after escape collapse, and the
        // resolver classifies based on the original source positions if
        // necessary. SPEC §5 explicitly says `echo \$HOME` produces a
        // Literal token.)
        var value = new ShellValueBuilder();
        var i = start;
        while (i < src.Length)
        {
            var c = src[i];

            // Stop conditions.
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') break;
            if (c == '\'' || c == '"' || c == '`') break;
            if (IsOperatorStart(src, i)
                && !(allowLeadingAmpersand && i == start && c == '&')) break;

            // Backslash escapes the next character (outside quotes).
            if (c == '\\')
            {
                if (i + 1 >= src.Length)
                {
                    // Trailing lone backslash — preserve it as literal.
                    value.AppendLiteral('\\', i, 1);
                    i++;
                    break;
                }

                var n = src[i + 1];
                if (n == '\n' || n == '\r')
                {
                    // Bash removes a continuation before word-boundary
                    // analysis, so adjacent fragments remain one word.
                    i += n == '\r' && i + 2 < src.Length && src[i + 2] == '\n'
                        ? 3
                        : 2;
                    continue;
                }

                value.AppendLiteral(n, i, 2);
                i += 2;
                continue;
            }

            // Unsupported expansion forms terminate the word so the outer
            // tokenizer can emit one fail-closed sentinel at their exact
            // authored boundary. Simple ${VAR}, $VAR, $$ etc. are absorbed.
            if (c == '$' && i + 1 < src.Length)
            {
                var next = src[i + 1];
                if (next is '(' or '[') break;
                if (next == '{')
                {
                    var openBrace = i + 1;
                    var scan = OpaqueRegionScanner.Scan(src, openBrace, '{', '}');
                    if (!scan.Closed) break; // let outer loop emit the sentinel

                    var body = src.Slice(openBrace + 1, scan.EndIndex - openBrace - 1);
                    if (!IsSimpleBracedParameterName(body)) break;
                }

                if (TryAppendBashExpansion(
                        src, ref i, value, allowFieldSplit: true, out var error))
                {
                    if (error is not null) break;
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
            else
            {
                value.AppendLiteral(c, i, 1);
            }

            i++;
        }

        var resolverValue = value.Build();
        if (resolverValue.Decoded.Length == 0)
        {
            // Defensive: caller should not invoke ReadWord on a position
            // that produces no chars (would loop forever). Advance one
            // char to make progress; this should not trigger in practice
            // because the dispatch in Tokenize covers every printable
            // case.
            return start + 1;
        }

        tokens.Add(new BashToken(
            BashTokenKind.Word, resolverValue.Decoded, null, start, i - start, null)
        { ResolverValue = resolverValue });
        return i;
    }

    private static bool TryAppendBashExpansion(
        ReadOnlySpan<char> src,
        ref int index,
        ShellValueBuilder value,
        bool allowFieldSplit,
        out string? error)
    {
        error = null;
        var start = index;
        if (start + 1 >= src.Length || src[start] != '$')
        {
            return false;
        }

        var next = src[start + 1];
        if (next == '[')
        {
            error = "obsolete arithmetic expansion '$[…]': not supported in v0.3";
            index = src.Length;
            return true;
        }

        if (next == '(')
        {
            if (start + 2 < src.Length && src[start + 2] == '(')
            {
                error = "arithmetic expansion '$((…))' not supported in v0.1";
                index = src.Length;
                return true;
            }

            var scan = ScanCommandSubstitution(src, start + 1);
            if (!scan.Closed)
            {
                error = scan.Error ?? "unbalanced '$(' command substitution";
                index = src.Length;
                return true;
            }

            var length = scan.EndIndex - start + 1;
            value.AppendOpaque(
                src.Slice(start, length).ToString(),
                ShellOpaqueCause.CommandSubstitution,
                start,
                length);
            index += length;
            return true;
        }

        string name;
        int expansionLength;
        if (next == '{')
        {
            var scan = OpaqueRegionScanner.Scan(src, start + 1, '{', '}');
            if (!scan.Closed)
            {
                error = "unbalanced '${' parameter expansion";
                index = src.Length;
                return true;
            }

            expansionLength = scan.EndIndex - start + 1;
            name = src.Slice(start + 2, expansionLength - 3).ToString();
            if (!IsSimpleBracedParameterName(name.AsSpan()))
            {
                error = "complex parameter expansion is not supported in v0.3";
                index += expansionLength;
                return true;
            }
        }
        else if (IsBashIdentifierStart(next))
        {
            var end = start + 2;
            while (end < src.Length && IsBashIdentifierContinuation(src[end]))
            {
                end++;
            }

            expansionLength = end - start;
            name = src.Slice(start + 1, expansionLength - 1).ToString();
        }
        else if (next is '?' or '$' or '#' or '-' or '!' or '@' or '*'
            || next is >= '0' and <= '9')
        {
            expansionLength = 2;
            name = next.ToString();
        }
        else
        {
            return false;
        }

        var kind = IsAllAsciiDigits(name)
            ? ShellExpansionKind.PositionalParameter
            : name.Length == 1 && name[0] is '?' or '$' or '#' or '-' or '!' or '@' or '*'
                ? ShellExpansionKind.SpecialParameter
                : ShellExpansionKind.Variable;
        var cardinality = name == "@" || (name == "*" && allowFieldSplit)
            ? ShellValueCardinality.ZeroOrMore
            : ShellValueCardinality.ExactlyOne;
        var transforms = ShellLexicalTransform.Variable;
        if (allowFieldSplit)
        {
            transforms |= ShellLexicalTransform.FieldSplit;
        }

        value.AppendExpansion(
            src.Slice(start, expansionLength).ToString(),
            transforms,
            new ShellExpansionReference(kind, name),
            cardinality,
            start,
            expansionLength);
        index += expansionLength;
        return true;
    }

    private static bool IsAllAsciiDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSimpleBracedParameterName(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (value.Length == 1 &&
            value[0] is '?' or '$' or '#' or '-' or '!' or '@' or '*')
        {
            return true;
        }

        var allDigits = true;
        for (var index = 0; index < value.Length; index++)
        {
            allDigits &= value[index] is >= '0' and <= '9';
        }

        if (allDigits)
        {
            return true;
        }

        if (!IsBashIdentifierStart(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsBashIdentifierContinuation(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBashIdentifierStart(char value) =>
        value == '_' || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsBashIdentifierContinuation(char value) =>
        IsBashIdentifierStart(value) || value is >= '0' and <= '9';

    private static bool IsOperatorStart(ReadOnlySpan<char> src, int i)
    {
        var c = src[i];
        switch (c)
        {
            case ';':
            case '|':
            case '<':
            case '>':
            case '(':
            case ')':
                return true;
            case '&':
                // Both `&&` and unsupported bare `&` terminate a word. The
                // tokenizer emits a sentinel for the latter on its next pass.
                return true;
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- heredoc

    private static int ConsumeHeredoc(
        ReadOnlySpan<char> src, int i, string opText, List<BashToken> tokens)
    {
        // Skip any whitespace between `<<` and the delimiter word.
        while (i < src.Length && (src[i] == ' ' || src[i] == '\t'))
        {
            tokens.Add(new BashToken(
                BashTokenKind.Whitespace, "", null, i, 1, null));
            i++;
        }

        if (i >= src.Length)
        {
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                "",
                null,
                i,
                0,
                "heredoc operator '" + opText + "' missing delimiter"));
            return i;
        }

        if (!TryReadHeredocDelimiter(
                src,
                i,
                out var afterDelim,
                out var delim,
                out var delimiterQuoted,
                out var delimiterError))
        {
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                src.Slice(i, afterDelim - i).ToString(),
                null,
                i,
                afterDelim - i,
                delimiterError ?? "heredoc operator '" + opText + "' missing delimiter"));
            return afterDelim;
        }

        var delimiterIndex = tokens.Count;
        tokens.Add(new BashToken(
            BashTokenKind.Word,
            delim,
            null,
            i,
            afterDelim - i,
            null)
        {
            ResolverValue = ShellValue.Literal(delim, i, afterDelim - i),
        });

        // Skip the heredoc body: from the next newline to the line that
        // contains only `delim` (or, for `<<-`, optional leading tabs +
        // delim). On unterminated body, emit a sentinel and stop.
        var j = afterDelim;
        while (j < src.Length && src[j] is ' ' or '\t')
        {
            j++;
        }

        if (j < src.Length && src[j] == '#')
        {
            while (j < src.Length && src[j] is not '\n' and not '\r')
            {
                j++;
            }
        }

        if (j < src.Length && src[j] is not '\n' and not '\r')
        {
            var headerEnd = j;
            while (headerEnd < src.Length && src[headerEnd] is not '\n' and not '\r')
            {
                headerEnd++;
            }

            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                src.Slice(j, headerEnd - j).ToString(),
                null,
                j,
                headerEnd - j,
                "tokens after a heredoc delimiter are not supported"));
            return src.Length;
        }

        if (j >= src.Length)
        {
            // No newline at all after the delimiter — unterminated heredoc.
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                "",
                null,
                afterDelim,
                src.Length - afterDelim,
                $"heredoc body not terminated (delimiter '{delim}' not found)"));
            return src.Length;
        }

        if (src[j] == '\r' && j + 1 < src.Length && src[j + 1] == '\n')
        {
            j += 2;
        }
        else
        {
            j++;
        }

        var bodyStart = j;

        var stripTabs = opText.EndsWith("<<-", StringComparison.Ordinal);
        while (j <= src.Length)
        {
            // Read the next line: from j to the next '\n' or end-of-input.
            var lineStart = j;
            while (j < src.Length && src[j] != '\n')
            {
                j++;
            }

            var lineEnd = j;
            if (lineEnd > lineStart && src[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            // For <<-, optional leading tabs are stripped before comparing.
            var compareStart = lineStart;
            if (stripTabs)
            {
                while (compareStart < lineEnd && src[compareStart] == '\t')
                {
                    compareStart++;
                }
            }

            var lineSlice = src.Slice(compareStart, lineEnd - compareStart);
            if (lineSlice.SequenceEqual(delim.AsSpan()))
            {
                var bodyLength = lineStart - bodyStart;
                ShellValue bodyValue;
                if (delimiterQuoted)
                {
                    bodyValue = ShellValue.Literal(
                        src.Slice(bodyStart, bodyLength).ToString(),
                        bodyStart,
                        bodyLength);
                }
                else if (!TryBuildExpandingHeredocBodyValue(
                             src,
                             bodyStart,
                             bodyLength,
                             out bodyValue,
                             out var bodyError))
                {
                    tokens.Add(new BashToken(
                        BashTokenKind.UnparseableSentinel,
                        src.Slice(bodyStart, bodyLength).ToString(),
                        null,
                        bodyStart,
                        bodyLength,
                        bodyError));
                    return src.Length;
                }

                tokens[delimiterIndex] = tokens[delimiterIndex] with
                {
                    HeredocBodyValue = bodyValue,
                    HeredocBodyStart = bodyStart,
                    HeredocBodyLength = bodyLength,
                    IsHeredocDelimiterQuoted = delimiterQuoted,
                    HeredocSourceEnd = lineEnd,
                };

                // Found terminator. Skip the body silently — no token is
                // emitted for the body itself — but emit a single
                // Whitespace token covering the terminator's trailing
                // newline (if any) so the parser still sees a clause
                // boundary between the heredoc-using clause and whatever
                // follows. Without this, `cmd <<EOF\nbody\nEOF\nrest` would
                // collapse into one clause stream with no separator
                // between `cmd` and `rest`.
                _ = bodyStart; // bodyStart retained for future debug telemetry.
                if (j < src.Length && src[j] == '\n')
                {
                    tokens.Add(new BashToken(
                        BashTokenKind.Whitespace, "", null, j, 1, null)
                    { IsStatementSeparator = true });
                    return j + 1;
                }

                return j;
            }

            if (j >= src.Length)
            {
                // End of input without terminator.
                tokens.Add(new BashToken(
                    BashTokenKind.UnparseableSentinel,
                    "",
                    null,
                    bodyStart,
                    src.Length - bodyStart,
                    $"heredoc body not terminated (delimiter '{delim}' not found)"));
                return src.Length;
            }

            j++; // step over the '\n'.
        }

        return j;
    }

    private static bool TryReadHeredocDelimiter(
        ReadOnlySpan<char> src,
        int start,
        out int end,
        out string delimiter,
        out bool quoted,
        out string? error)
    {
        var decoded = new StringBuilder();
        quoted = false;
        error = null;
        var index = start;
        while (index < src.Length && !char.IsWhiteSpace(src[index]) &&
               src[index] is not ';' and not '|' and not '&' and not '<' and not '>')
        {
            var character = src[index];
            if (character == '\\')
            {
                quoted = true;
                if (index + 1 >= src.Length || src[index + 1] is '\n' or '\r')
                {
                    end = Math.Min(index + 1, src.Length);
                    delimiter = decoded.ToString();
                    error = "heredoc delimiter has an unsupported continuation";
                    return false;
                }

                decoded.Append(src[index + 1]);
                index += 2;
                continue;
            }

            if (character is '\'' or '"')
            {
                quoted = true;
                var quote = character;
                index++;
                var closed = false;
                while (index < src.Length)
                {
                    character = src[index];
                    if (character == quote)
                    {
                        index++;
                        closed = true;
                        break;
                    }

                    if (quote == '"' && character == '\\' && index + 1 < src.Length &&
                        src[index + 1] is '$' or '`' or '"' or '\\')
                    {
                        decoded.Append(src[index + 1]);
                        index += 2;
                        continue;
                    }

                    decoded.Append(character);
                    index++;
                }

                if (!closed)
                {
                    end = index;
                    delimiter = decoded.ToString();
                    error = "unterminated quote in heredoc delimiter";
                    return false;
                }

                continue;
            }

            decoded.Append(character);
            index++;
        }

        end = index;
        delimiter = decoded.ToString();
        if (delimiter.Length == 0)
        {
            return false;
        }

        return true;
    }

    private static bool TryBuildExpandingHeredocBodyValue(
        ReadOnlySpan<char> src,
        int bodyStart,
        int bodyLength,
        out ShellValue value,
        out string? error)
    {
        var bodyEnd = bodyStart + bodyLength;
        var builder = new ShellValueBuilder();
        builder.AppendBoundary(bodyStart);
        var index = bodyStart;
        while (index < bodyEnd)
        {
            var character = src[index];
            if (character == '\\' && index + 1 < bodyEnd)
            {
                var escaped = src[index + 1];
                if (escaped is '\n' or '\r')
                {
                    value = builder.Build();
                    error = "continuations inside expanding heredoc bodies are not supported";
                    return false;
                }

                if (escaped is '$' or '`' or '\\')
                {
                    builder.AppendLiteral(escaped, index, 2);
                    index += 2;
                    continue;
                }
            }

            if (character == '`')
            {
                value = builder.Build();
                error = "legacy backtick command substitution is not supported";
                return false;
            }

            if (character == '$')
            {
                var afterExpansion = index;
                if (TryAppendBashExpansion(
                        src.Slice(0, bodyEnd),
                        ref afterExpansion,
                        builder,
                        allowFieldSplit: false,
                        out var expansionError))
                {
                    if (expansionError is not null)
                    {
                        value = builder.Build();
                        error = expansionError;
                        return false;
                    }

                    index = afterExpansion;
                    continue;
                }
            }

            builder.AppendLiteral(character, index, 1);
            index++;
        }

        value = builder.Build();
        error = null;
        return true;
    }
}
