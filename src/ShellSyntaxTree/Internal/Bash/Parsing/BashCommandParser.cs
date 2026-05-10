// -----------------------------------------------------------------------
// <copyright file="BashCommandParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Bash.Verbs;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Translates a <see cref="BashLexer"/> token stream into the public
/// <see cref="ParsedCommand"/> AST. The parser is intentionally narrow:
/// PR 3 wires verb chains, args, redirects, compound splitting, and the
/// safe-fail anomaly behavior in SPEC §11. It does <em>not</em> apply
/// per-verb path classification or path resolution (PR 4) and stops short
/// of the full subshell / <c>bash -c</c> recursion treatment described in
/// SPEC §10 (PR 5 lands that surface flattening + IsBashCWrapped /
/// IsSubshell attribution + cd-in-compound propagation).
/// </summary>
/// <remarks>
/// PR 3 scope: subshell + <c>bash -c</c> framework only — see comments
/// next to <see cref="ParseClauseSegment"/> and the segment splitter for
/// where PR 5 will land real attribution and inner-string flattening.
/// </remarks>
internal static class BashCommandParser
{
    /// <summary>
    /// Parse the input string into a <see cref="ParsedCommand"/>. Never
    /// throws on well-formed input; safe-fails to <c>IsUnparseable=true</c>
    /// for SPEC §11 anomalies.
    /// </summary>
    internal static ParsedCommand Parse(string source, BashParserOptions options)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        // options is currently unused by PR 3 — the resolver lands in PR 4
        // and consumes HomeDirectory / WorkingDirectory. Discarding here
        // keeps the call sites stable for the full pipeline.
        _ = options;

        if (source.Length == 0)
        {
            return new ParsedCommand
            {
                Source = source,
                Clauses = Array.Empty<Clause>(),
                IsUnparseable = false,
            };
        }

        var tokens = BashLexer.Tokenize(source);

        // Step 1: lift any UnparseableSentinel to the outer ParsedCommand.
        // SPEC §11 step 3 says we may also return whatever clauses were
        // parsed up to that point — for PR 3 we keep it strictly safe-fail
        // (empty Clauses) so consumers don't get a half-built AST whose
        // shape changes when PR 5 wires recursion. The reason text comes
        // straight from the lexer.
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == BashTokenKind.UnparseableSentinel)
            {
                return new ParsedCommand
                {
                    Source = source,
                    Clauses = Array.Empty<Clause>(),
                    IsUnparseable = true,
                    UnparseableReason = t.UnparseableReason,
                };
            }
        }

        // Step 2: split the (filtered, non-whitespace) token stream into
        // clause segments at top-level &&, ||, ;, and |. Subshell and
        // bash -c boundaries are recognized as framework only — see
        // SplitIntoSegments.
        var significant = FilterSignificant(tokens);

        // Step 3: detect anomalies that map straight to outer IsUnparseable.
        if (TryDetectAnomaly(significant, out var anomalyReason))
        {
            return new ParsedCommand
            {
                Source = source,
                Clauses = Array.Empty<Clause>(),
                IsUnparseable = true,
                UnparseableReason = anomalyReason,
            };
        }

        var segments = SplitIntoSegments(significant, source, out var splitError);
        if (splitError is not null)
        {
            return new ParsedCommand
            {
                Source = source,
                Clauses = Array.Empty<Clause>(),
                IsUnparseable = true,
                UnparseableReason = splitError,
            };
        }

        // Step 4: parse each segment into a Clause. Any per-clause anomaly
        // (control-flow keyword as the verb, function definition shape,
        // process substitution) flips the outer IsUnparseable.
        var clauses = new List<Clause>(segments.Count);
        foreach (var segment in segments)
        {
            var clauseOrError = ParseClauseSegment(segment, source);
            if (clauseOrError.Error is not null)
            {
                return new ParsedCommand
                {
                    Source = source,
                    Clauses = Array.Empty<Clause>(),
                    IsUnparseable = true,
                    UnparseableReason = clauseOrError.Error,
                };
            }

            // ParseClauseSegment produces a list because subshell framework
            // emits one Clause per inner segment; see comments inside.
            foreach (var clause in clauseOrError.Clauses)
            {
                clauses.Add(clause);
            }
        }

        return new ParsedCommand
        {
            Source = source,
            Clauses = clauses,
            IsUnparseable = false,
        };
    }

    // ---------------------------------------------------------------- token filtering

    private static List<BashToken> FilterSignificant(IReadOnlyList<BashToken> tokens)
    {
        // Whitespace and Continuation are source-fidelity tokens — irrelevant
        // for parser logic. Drop them. The remaining list is what every
        // subsequent step walks.
        var filtered = new List<BashToken>(tokens.Count);
        foreach (var t in tokens)
        {
            if (t.Kind == BashTokenKind.Whitespace || t.Kind == BashTokenKind.Continuation)
            {
                continue;
            }

            filtered.Add(t);
        }

        return filtered;
    }

    // ---------------------------------------------------------------- anomaly detection

    private static bool TryDetectAnomaly(IReadOnlyList<BashToken> tokens, out string? reason)
    {
        // Function definition: `name() { ... }`. Trigger = a Word followed
        // by an immediately-adjacent `(` and `)`. SPEC §11 + locked
        // interpretation: outer IsUnparseable.
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            var a = tokens[i];
            var b = tokens[i + 1];
            var c = tokens[i + 2];
            if (a.Kind == BashTokenKind.Word
                && b.Kind == BashTokenKind.Operator && b.OperatorText == "("
                && c.Kind == BashTokenKind.Operator && c.OperatorText == ")"
                && b.SourceStart == a.SourceStart + a.SourceLength
                && c.SourceStart == b.SourceStart + b.SourceLength)
            {
                reason = "function definition is not supported in v0.1";
                return true;
            }
        }

        // Process substitution: `<(cmd)` or `>(cmd)`. The lexer doesn't
        // emit a dedicated token for these; they show up as `<` or `>`
        // operators followed *immediately* by `(` (no whitespace between).
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var a = tokens[i];
            var b = tokens[i + 1];
            if (a.Kind == BashTokenKind.Operator
                && (a.OperatorText == "<" || a.OperatorText == ">")
                && b.Kind == BashTokenKind.Operator && b.OperatorText == "("
                && b.SourceStart == a.SourceStart + a.SourceLength)
            {
                reason = "process substitution is not supported in v0.1";
                return true;
            }
        }

        reason = null;
        return false;
    }

    // ---------------------------------------------------------------- segment split

    /// <summary>
    /// One contiguous piece of significant tokens that becomes a single
    /// <see cref="Clause"/>. Carries the operator that <em>preceded</em>
    /// the segment in the source, plus a flag for the subshell-framework
    /// pass-through (see <see cref="ParseClauseSegment"/>).
    /// </summary>
    private sealed class Segment
    {
        public CompoundOperator PrecedingOperator { get; init; }

        public List<BashToken> Tokens { get; init; } = new();

        /// <summary>
        /// True when this segment came from inside a subshell <c>(...)</c>
        /// region. PR 3 surfaces inner clauses without setting
        /// <c>IsSubshell</c> per the explicit note in the task — that's
        /// PR 5's job. The flag lives on the segment for forward-compat.
        /// </summary>
        public bool FromSubshell { get; init; }
    }

    /// <summary>
    /// Walk the significant token list and split on top-level compound
    /// operators. Tracks paren depth so operators inside a subshell stay
    /// part of the inner segments. PR 3: subshell inner clauses surface
    /// inline (no IsSubshell flag); PR 5 will land real attribution +
    /// IsBashCWrapped / IsSubshell flag flipping + bash -c recursion.
    /// </summary>
    private static List<Segment> SplitIntoSegments(
        IReadOnlyList<BashToken> tokens,
        string source,
        out string? error)
    {
        var segments = new List<Segment>();
        var current = new Segment { PrecedingOperator = CompoundOperator.None };

        var depth = 0;
        // SubshellRegion stack tracks "we entered a subshell" to mark inner
        // segments' FromSubshell. Empty when at top level.
        var subshellDepthStack = new Stack<int>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (t.Kind == BashTokenKind.Operator)
            {
                var op = t.OperatorText;

                if (op == "(")
                {
                    // Open subshell: flush current segment if it has content,
                    // then mark the new context as "inside a subshell."
                    if (current.Tokens.Count > 0)
                    {
                        segments.Add(current);
                        current = new Segment { PrecedingOperator = CompoundOperator.Sequence, FromSubshell = subshellDepthStack.Count > 0 };
                    }
                    else
                    {
                        // Inherit FromSubshell from the new (deeper) context.
                        current = new Segment { PrecedingOperator = current.PrecedingOperator, FromSubshell = true };
                    }

                    subshellDepthStack.Push(depth);
                    depth++;
                    // The opening paren itself is not part of any clause's
                    // tokens. Continue.
                    continue;
                }

                if (op == ")")
                {
                    if (depth == 0)
                    {
                        error = $"unbalanced parens at position {t.SourceStart}";
                        return segments;
                    }

                    depth--;
                    if (subshellDepthStack.Count > 0)
                    {
                        subshellDepthStack.Pop();
                    }

                    // Flush the inner segment and start a new top-level (or
                    // deeper-but-still-subshell) segment.
                    if (current.Tokens.Count > 0)
                    {
                        segments.Add(current);
                    }

                    // After the close paren, the next operator/token decides
                    // the next segment's preceding operator. We default to
                    // None and let the operator dispatch below overwrite it.
                    current = new Segment
                    {
                        PrecedingOperator = CompoundOperator.None,
                        FromSubshell = subshellDepthStack.Count > 0,
                    };
                    continue;
                }

                // Compound operators only split when at top level relative to
                // the current "shell" — a subshell is its own scope, but its
                // operators still split the inner segments. Concretely: any
                // depth (including inside subshells) treats &&/||/;/| as a
                // splitter for the segment they live in.
                if (op == "&&" || op == "||" || op == ";" || op == "|")
                {
                    if (current.Tokens.Count == 0 && current.PrecedingOperator != CompoundOperator.None)
                    {
                        // Two operators in a row, e.g. `&& &&`. Treat as
                        // unparseable.
                        error = $"unexpected operator '{op}' at position {t.SourceStart}";
                        return segments;
                    }

                    if (current.Tokens.Count > 0)
                    {
                        segments.Add(current);
                    }

                    current = new Segment
                    {
                        PrecedingOperator = MapOperator(op),
                        FromSubshell = subshellDepthStack.Count > 0,
                    };
                    continue;
                }

                // Redirect operators stay inside the current segment.
                current.Tokens.Add(t);
                continue;
            }

            current.Tokens.Add(t);
        }

        if (depth != 0)
        {
            // Find the position of the unmatched '(' for a useful diagnostic.
            // We don't track it precisely; use the source length as a fallback.
            error = $"unbalanced parens at position {source.Length}";
            return segments;
        }

        if (current.Tokens.Count > 0)
        {
            segments.Add(current);
        }

        error = null;
        return segments;
    }

    private static CompoundOperator MapOperator(string? op) => op switch
    {
        "&&" => CompoundOperator.AndIf,
        "||" => CompoundOperator.OrIf,
        ";" => CompoundOperator.Sequence,
        "|" => CompoundOperator.Pipe,
        _ => CompoundOperator.None,
    };

    // ---------------------------------------------------------------- clause parse

    /// <summary>
    /// Either a list of clauses (success) or an error reason (failure).
    /// Multi-clause results are reserved for the subshell framework — a
    /// single segment can produce one clause for non-subshell input.
    /// </summary>
    private readonly struct ClauseResult
    {
        public IReadOnlyList<Clause> Clauses { get; }

        public string? Error { get; }

        public ClauseResult(IReadOnlyList<Clause> clauses, string? error)
        {
            Clauses = clauses;
            Error = error;
        }

        public static ClauseResult Empty() => new(Array.Empty<Clause>(), null);

        public static ClauseResult Ok(Clause c) => new(new[] { c }, null);

        public static ClauseResult Fail(string reason) => new(Array.Empty<Clause>(), reason);
    }

    private static ClauseResult ParseClauseSegment(Segment segment, string source)
    {
        // Empty segment (e.g. trailing `;`): just drop it. We materialize
        // an empty clause only when the segment carries a preceding
        // operator AND tokens; the splitter already prevents the "operator
        // with no tokens" case via the `unexpected operator` error.
        if (segment.Tokens.Count == 0)
        {
            return ClauseResult.Empty();
        }

        // Verb chain extraction. Probe the first 1–3 verb-eligible tokens
        // against BashVerbs.BashArity. PR 3 keeps things simple: only
        // consecutive Word/QuotedString/OpaqueSubstitution tokens at the
        // very start qualify as verb candidates. Redirect operators or any
        // other operator immediately end the verb chain.
        var verbCandidateValues = new List<string>(3);
        var verbCandidateIndices = new List<int>(3);
        for (var i = 0; i < segment.Tokens.Count && verbCandidateValues.Count < 3; i++)
        {
            var t = segment.Tokens[i];
            if (t.Kind == BashTokenKind.Word || t.Kind == BashTokenKind.QuotedString)
            {
                // A flag-like word stops verb-chain probing — flags belong
                // to args, never to verbs.
                if (IsFlagWord(t))
                {
                    break;
                }

                verbCandidateValues.Add(t.Value);
                verbCandidateIndices.Add(i);
                continue;
            }

            break;
        }

        if (verbCandidateValues.Count == 0)
        {
            // No verb tokens at all. Could be a redirect-only clause
            // (`> /tmp/out` is rare but technically valid bash). Return an
            // empty-verb clause; consumers can detect this with
            // `Verb.Tokens.Count == 0`.
            var redirectsOnly = ExtractRedirectsAndArgs(
                segment.Tokens, 0, source, out var emptyArgs, out var emptyRedirects, out var redirectError);
            if (redirectError is not null)
            {
                return ClauseResult.Fail(redirectError);
            }

            _ = redirectsOnly;
            return ClauseResult.Ok(new Clause
            {
                Operator = segment.PrecedingOperator,
                Verb = new VerbChain(),
                Args = emptyArgs,
                Redirects = emptyRedirects,
                IsSubshell = false, // PR 5 will set this for FromSubshell segments.
                IsBashCWrapped = false, // PR 5.
            });
        }

        // Anomaly: control-flow keyword as the leading verb.
        var firstVerb = verbCandidateValues[0];
        if (BashVerbs.ControlFlowKeywords.Contains(firstVerb))
        {
            return ClauseResult.Fail(
                $"control-flow keyword '{firstVerb}' is not supported in v0.1");
        }

        var arity = BashVerbs.ProbeArity(verbCandidateValues, 0);
        if (arity <= 0)
        {
            arity = 1;
        }

        // Build the verb chain.
        var verbTokens = new List<string>(arity);
        for (var k = 0; k < arity; k++)
        {
            verbTokens.Add(verbCandidateValues[k]);
        }

        // Position in segment.Tokens immediately after the verb chain.
        var argStart = verbCandidateIndices[arity - 1] + 1;

        // Determine the verb name we use to drive flag-with-value lookups.
        // SPEC §7's table is keyed on the *first* token (`git`, `docker`,
        // `tar`, ...). Multi-token verbs share the first-token's table.
        var verbKeyForFlags = verbTokens[0];

        var argsAndRedirects = ExtractRedirectsAndArgs(
            segment.Tokens,
            argStart,
            source,
            out var args,
            out var redirects,
            out var argError);
        if (argError is not null)
        {
            return ClauseResult.Fail(argError);
        }

        _ = argsAndRedirects;

        // Apply flag-with-value pairing. Per SPEC §7 the *value* arg's
        // IsPath classification lands in PR 4; for PR 3 both flag and
        // value remain in Args with default-literal kind.
        args = ApplyFlagsWithValue(verbKeyForFlags, args);

        var clause = new Clause
        {
            Operator = segment.PrecedingOperator,
            Verb = new VerbChain { Tokens = verbTokens },
            Args = args,
            Redirects = redirects,
            IsSubshell = false, // PR 3: framework only; PR 5 sets this for FromSubshell segments.
            IsBashCWrapped = false, // PR 3: framework only; PR 5 lands real bash -c recursion.
        };

        return ClauseResult.Ok(clause);
    }

    private static bool IsFlagWord(BashToken token)
    {
        // A leading '-' marks a flag. QuotedString tokens are never flags
        // — quoting a leading dash is the user's signal "treat as literal."
        if (token.Kind != BashTokenKind.Word)
        {
            return false;
        }

        return token.Value.Length > 0 && token.Value[0] == '-';
    }

    // ---------------------------------------------------------------- args + redirects

    private static int ExtractRedirectsAndArgs(
        IReadOnlyList<BashToken> segmentTokens,
        int start,
        string source,
        out IReadOnlyList<Arg> args,
        out IReadOnlyList<Redirect> redirects,
        out string? error)
    {
        var argList = new List<Arg>();
        var redirectList = new List<Redirect>();
        var i = start;
        while (i < segmentTokens.Count)
        {
            var t = segmentTokens[i];
            if (t.Kind == BashTokenKind.Operator)
            {
                if (TryMapRedirect(t.OperatorText, out var dir))
                {
                    // Heredoc operators come through as a redirect operator
                    // followed by a Word delimiter. PR 3 emits the redirect
                    // as Direction=In, Target=<delim> with no special flag —
                    // sufficient to keep clause boundaries while we ship
                    // the rest of the parser.
                    if (i + 1 >= segmentTokens.Count)
                    {
                        error = $"redirect operator '{t.OperatorText}' missing target at position {t.SourceStart}";
                        args = argList;
                        redirects = redirectList;
                        return i;
                    }

                    var target = segmentTokens[i + 1];
                    if (target.Kind == BashTokenKind.Operator)
                    {
                        error = $"redirect operator '{t.OperatorText}' missing target at position {t.SourceStart}";
                        args = argList;
                        redirects = redirectList;
                        return i;
                    }

                    var isDynamic = target.Kind == BashTokenKind.OpaqueSubstitution;
                    var redirectTarget = target.Kind == BashTokenKind.OpaqueSubstitution
                        ? target.Value
                        : SourceSlice(source, target);

                    redirectList.Add(new Redirect
                    {
                        Direction = dir,
                        Target = redirectTarget,
                        IsDynamicSkip = isDynamic,
                    });

                    i += 2;
                    continue;
                }

                // Heredoc operators show up as `<<` / `<<-` from the lexer.
                // PR 3 treats them like the In redirect for the purpose of
                // pinning a placeholder; the body is already dropped by the
                // lexer so the next token is the delimiter Word.
                if (t.OperatorText == "<<" || t.OperatorText == "<<-")
                {
                    if (i + 1 >= segmentTokens.Count)
                    {
                        error = $"heredoc operator '{t.OperatorText}' missing delimiter at position {t.SourceStart}";
                        args = argList;
                        redirects = redirectList;
                        return i;
                    }

                    var delim = segmentTokens[i + 1];
                    redirectList.Add(new Redirect
                    {
                        Direction = RedirectDirection.In,
                        Target = "<<" + delim.Value + ">",
                        IsDynamicSkip = false,
                    });

                    i += 2;
                    continue;
                }

                // Any other operator inside a clause segment is unexpected.
                error = $"unexpected operator '{t.OperatorText}' at position {t.SourceStart}";
                args = argList;
                redirects = redirectList;
                return i;
            }

            // Args.
            switch (t.Kind)
            {
                case BashTokenKind.Word:
                {
                    // Equals-form flag-with-value: `--output=file` splits on
                    // the first `=`. Both halves enter Args. The path-shape
                    // classification lives in PR 4.
                    var raw = SourceSlice(source, t);
                    if (TrySplitEqualsFlag(t.Value, out var flagPart, out var valuePart))
                    {
                        argList.Add(new Arg
                        {
                            Raw = flagPart,
                            Resolved = null,
                            Kind = ArgKind.Literal,
                            IsPath = false,
                        });
                        argList.Add(new Arg
                        {
                            Raw = valuePart,
                            Resolved = null,
                            Kind = ArgKind.Literal,
                            IsPath = false,
                        });
                    }
                    else
                    {
                        argList.Add(new Arg
                        {
                            Raw = raw,
                            Resolved = null,
                            Kind = ArgKind.Literal,
                            IsPath = false,
                        });
                    }

                    break;
                }

                case BashTokenKind.QuotedString:
                {
                    argList.Add(new Arg
                    {
                        Raw = SourceSlice(source, t),
                        Resolved = null,
                        Kind = ArgKind.Literal,
                        IsPath = false,
                    });
                    break;
                }

                case BashTokenKind.OpaqueSubstitution:
                {
                    // Locked interpretation #2: opaque region collapses to
                    // a single DynamicSkip arg; the surrounding clause
                    // continues to parse normally.
                    argList.Add(new Arg
                    {
                        Raw = t.Value,
                        Resolved = null,
                        Kind = ArgKind.DynamicSkip,
                        IsPath = false,
                    });
                    break;
                }

                default:
                    // UnparseableSentinel is filtered earlier; Whitespace /
                    // Continuation are filtered in FilterSignificant. Any
                    // other kind would be a parser bug, but stay quiet —
                    // dropping unknown kinds is safer than crashing.
                    break;
            }

            i++;
        }

        args = argList;
        redirects = redirectList;
        error = null;
        return i;
    }

    private static bool TryMapRedirect(string? op, out RedirectDirection direction)
    {
        switch (op)
        {
            case ">":
                direction = RedirectDirection.Out;
                return true;
            case ">>":
                direction = RedirectDirection.Append;
                return true;
            case "<":
                direction = RedirectDirection.In;
                return true;
            case "2>":
                direction = RedirectDirection.ErrOut;
                return true;
            case "2>>":
                direction = RedirectDirection.ErrAppend;
                return true;
            default:
                direction = default;
                return false;
        }
    }

    private static bool TrySplitEqualsFlag(string raw, out string flagPart, out string valuePart)
    {
        // Only split when the leading character is '-' (so `KEY=value`
        // stays a single arg, but `--output=file` becomes two). The split
        // happens at the *first* '=' to preserve values that contain '='.
        if (raw.Length < 2 || raw[0] != '-')
        {
            flagPart = "";
            valuePart = "";
            return false;
        }

        var eq = raw.IndexOf('=');
        if (eq <= 0 || eq == raw.Length - 1)
        {
            // No '=' or trailing '=' (no value to split off).
            flagPart = "";
            valuePart = "";
            return false;
        }

        flagPart = raw.Substring(0, eq);
        valuePart = raw.Substring(eq + 1);
        return true;
    }

    private static IReadOnlyList<Arg> ApplyFlagsWithValue(string verbKey, IReadOnlyList<Arg> args)
    {
        if (!BashVerbs.FlagsWithValue.TryGetValue(verbKey, out var flagsWithValue))
        {
            return args;
        }

        // PR 3 doesn't change Arg shape based on the pairing — both flag
        // and value remain in Args with default-literal kind. The pairing
        // matters in PR 4 for IsPath classification. We still walk the
        // list so the structural shape stays identical with what PR 4 will
        // produce; the assignment is currently a no-op but locks the loop
        // in place.
        var unused = flagsWithValue;
        _ = unused;
        return args;
    }

    private static string SourceSlice(string source, BashToken token)
    {
        if (token.SourceStart < 0 || token.SourceStart >= source.Length)
        {
            return token.Value;
        }

        var len = token.SourceLength;
        if (token.SourceStart + len > source.Length)
        {
            len = source.Length - token.SourceStart;
        }

        return source.Substring(token.SourceStart, len);
    }
}
