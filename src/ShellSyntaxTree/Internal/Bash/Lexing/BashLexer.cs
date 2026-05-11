// -----------------------------------------------------------------------
// <copyright file="BashLexer.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal.Lexing;

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
///   <item>Opaque regions (<c>$(…)</c> and backtick <c>`…`</c>) are
///         consumed by <see cref="OpaqueRegionScanner"/> and emitted as
///         a single <see cref="BashTokenKind.OpaqueSubstitution"/> token
///         per the v0.1 locked interpretation.</item>
///   <item>Constructs SPEC §1 calls non-goals — arithmetic expansion
///         <c>$((…))</c> and complex parameter expansion
///         <c>${var//pat/repl}</c> — emit a
///         <see cref="BashTokenKind.UnparseableSentinel"/>. The parser
///         lifts that sentinel into <c>ParsedCommand.IsUnparseable</c>.</item>
///   <item>Heredoc bodies are dropped per SPEC §4. Only the
///         <c>&lt;&lt;</c>/<c>&lt;&lt;-</c> operator and the delimiter
///         word make it into the token stream.</item>
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
                    BashTokenKind.Whitespace, "", null, start, i - start, null));
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
            // Order matters: `&&` before `&`, `||` before `|`, `>>` before `>`,
            // `2>>` before `2>`, `<<-` before `<<`, `<<` before `<`. We don't
            // recognize a bare `&` in v0.1 (no background-job support; SPEC §1).
            if (TryReadOperator(src, i, out var opLen, out var opText))
            {
                var operatorTok = new BashToken(
                    BashTokenKind.Operator, "", opText, i, opLen, null);
                tokens.Add(operatorTok);
                i += opLen;

                // Heredoc handling: when we just emitted `<<` or `<<-`, the
                // *next* word is the delimiter and the body that follows the
                // first newline must be skipped per SPEC §4.
                if (opText == "<<" || opText == "<<-")
                {
                    i = ConsumeHeredoc(src, i, opText, tokens);
                }

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

            // ---- $( ... ) command substitution / $((expr)) / ${var//...} ----
            if (c == '$' && i + 1 < src.Length)
            {
                var next = src[i + 1];
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

            // ---- word ----
            i = ReadWord(src, i, tokens);
        }

        return tokens;
    }

    // ---------------------------------------------------------------- operators

    private static bool TryReadOperator(
        ReadOnlySpan<char> src, int i, out int length, out string? text)
    {
        // Multi-char operators first.
        if (i + 1 < src.Length)
        {
            var c0 = src[i];
            var c1 = src[i + 1];
            if (c0 == '&' && c1 == '&') { length = 2; text = "&&"; return true; }
            if (c0 == '|' && c1 == '|') { length = 2; text = "||"; return true; }
            if (c0 == '>' && c1 == '>') { length = 2; text = ">>"; return true; }
            if (c0 == '<' && c1 == '<')
            {
                if (i + 2 < src.Length && src[i + 2] == '-')
                {
                    length = 3; text = "<<-"; return true;
                }

                length = 2; text = "<<"; return true;
            }

            if (c0 == '2' && c1 == '>')
            {
                if (i + 2 < src.Length && src[i + 2] == '>')
                {
                    length = 3; text = "2>>"; return true;
                }

                length = 2; text = "2>"; return true;
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
        tokens.Add(new BashToken(
            BashTokenKind.QuotedString, inner, null, start, (i - start) + 1, null)
            { IsSingleQuoted = true });
        return i + 1;
    }

    private static int ReadDoubleQuoted(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
    {
        // Double quotes preserve whitespace but recognize \", \\, \$, and
        // \\+newline as escape sequences (SPEC §5). Other backslashes are
        // preserved literally. $VAR / ${VAR} are *not* expanded — kept literal.
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '"')
            {
                tokens.Add(new BashToken(
                    BashTokenKind.QuotedString, sb.ToString(), null,
                    start, (i - start) + 1, null));
                return i + 1;
            }

            if (c == '\\' && i + 1 < src.Length)
            {
                var n = src[i + 1];
                if (n == '"' || n == '\\' || n == '$' || n == '`')
                {
                    sb.Append(n);
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
                sb.Append(c);
                i++;
                continue;
            }

            sb.Append(c);
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
        var scan = OpaqueRegionScanner.Scan(src, openParen, '(', ')');
        if (!scan.Closed)
        {
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                src.Slice(start).ToString(),
                null,
                start,
                src.Length - start,
                "unbalanced '$(' command substitution"));
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
            null));
        return start + length;
    }

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
            null));
        return start + length;
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

    private static bool TryConsumeComplexParamExpansion(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens, out int afterBrace)
    {
        // src[start] = '$', src[start+1] = '{'. We need to find the matching
        // '}' and decide: simple ${VAR} -> false (let word reader take it);
        // ${...//...} or any other "complex" form -> emit UnparseableSentinel.
        //
        // For v0.1 we treat the presence of a slash inside the braces as the
        // single signal of "complex param expansion" (per the locked
        // interpretation #2 in the OpenSpec change). Other operators inside
        // ${...} (like ${X-default}, ${X#prefix}) fall through to the word
        // reader; a future PR can tighten this if needed.
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
        var hasSlash = false;
        for (var k = bodyStart; k < bodyEnd; k++)
        {
            if (src[k] == '/')
            {
                hasSlash = true;
                break;
            }
        }

        if (!hasSlash)
        {
            // Simple ${VAR} (or ${X-default} etc.). Caller will fall through
            // to the word reader and absorb it as part of a Word token.
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
            "complex parameter expansion '${var//pat/repl}' not supported in v0.1"));
        afterBrace = start + length;
        return true;
    }

    // ---------------------------------------------------------------- words

    private static int ReadWord(
        ReadOnlySpan<char> src, int start, List<BashToken> tokens)
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
        var sb = new StringBuilder();
        var i = start;
        while (i < src.Length)
        {
            var c = src[i];

            // Stop conditions.
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') break;
            if (c == '\'' || c == '"' || c == '`') break;
            if (IsOperatorStart(src, i)) break;

            // Backslash escapes the next character (outside quotes).
            if (c == '\\')
            {
                if (i + 1 >= src.Length)
                {
                    // Trailing lone backslash — preserve it as literal.
                    sb.Append('\\');
                    i++;
                    break;
                }

                var n = src[i + 1];
                if (n == '\n' || n == '\r')
                {
                    // Continuation: terminate the current word; the outer
                    // loop will pick up the continuation token.
                    break;
                }

                sb.Append(n);
                i += 2;
                continue;
            }

            // $( and ${...//...} terminate the word — they emit their own
            // tokens. Simple ${VAR}, $VAR, $$ etc. are absorbed.
            if (c == '$' && i + 1 < src.Length)
            {
                var next = src[i + 1];
                if (next == '(') break;
                if (next == '{')
                {
                    // Decide complex vs simple by looking for a '/' in the body.
                    var openBrace = i + 1;
                    var scan = OpaqueRegionScanner.Scan(src, openBrace, '{', '}');
                    if (!scan.Closed) break; // let outer loop emit the sentinel

                    var bodyHasSlash = false;
                    for (var k = openBrace + 1; k < scan.EndIndex; k++)
                    {
                        if (src[k] == '/') { bodyHasSlash = true; break; }
                    }

                    if (bodyHasSlash) break;

                    // Simple form — absorb whole ${...} verbatim.
                    // StringBuilder.Append(ReadOnlySpan<char>) is net6+
                    // only; spell out the loop for netstandard2.0 parity.
                    var braceLen = scan.EndIndex - i + 1;
                    for (var k = 0; k < braceLen; k++)
                    {
                        sb.Append(src[i + k]);
                    }

                    i += braceLen;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        if (sb.Length == 0)
        {
            // Defensive: caller should not invoke ReadWord on a position
            // that produces no chars (would loop forever). Advance one
            // char to make progress; this should not trigger in practice
            // because the dispatch in Tokenize covers every printable
            // case.
            return start + 1;
        }

        tokens.Add(new BashToken(
            BashTokenKind.Word, sb.ToString(), null, start, i - start, null));
        return i;
    }

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
                // Only `&&` is an operator in v0.1; bare `&` is unsupported
                // background-job syntax. Treat `&` not followed by `&` as a
                // word char to avoid silently splitting; consumers will see
                // it in the Raw value.
                return i + 1 < src.Length && src[i + 1] == '&';
            case '2':
                // `2>` and `2>>` start with '2' — only treat them as operator
                // starts when the immediate next char is '>'.
                return i + 1 < src.Length && src[i + 1] == '>';
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

        // Read the delimiter as a word (no quote/escape unwrapping handling
        // beyond what ReadWord does — bash supports `<<'EOF'` for
        // unexpanded bodies, but we skip the body either way so the value
        // doesn't matter). Capture the delimiter text from the freshly
        // appended token.
        var beforeDelim = tokens.Count;
        var afterDelim = ReadWord(src, i, tokens);
        if (tokens.Count == beforeDelim)
        {
            // ReadWord didn't produce a token (delimiter started with an
            // operator/quote we don't unwrap here). Treat as malformed.
            tokens.Add(new BashToken(
                BashTokenKind.UnparseableSentinel,
                "",
                null,
                i,
                0,
                "heredoc operator '" + opText + "' missing delimiter"));
            return afterDelim;
        }

        var delimToken = tokens[tokens.Count - 1];
        var delim = delimToken.Value;

        // Skip the heredoc body: from the next newline to the line that
        // contains only `delim` (or, for `<<-`, optional leading tabs +
        // delim). On unterminated body, emit a sentinel and stop.
        var j = afterDelim;
        // Find the first newline that opens the body.
        while (j < src.Length && src[j] != '\n')
        {
            j++;
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

        j++; // step past the opening newline; body now starts at j.
        var bodyStart = j;

        var stripTabs = opText == "<<-";
        while (j <= src.Length)
        {
            // Read the next line: from j to the next '\n' or end-of-input.
            var lineStart = j;
            while (j < src.Length && src[j] != '\n')
            {
                j++;
            }

            var lineEnd = j; // exclusive

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
                        BashTokenKind.Whitespace, "", null, j, 1, null));
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
}
