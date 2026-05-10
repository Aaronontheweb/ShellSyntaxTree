// -----------------------------------------------------------------------
// <copyright file="BashCommandParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Bash.Verbs;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Translates a <see cref="BashLexer"/> token stream into the public
/// <see cref="ParsedCommand"/> AST. PR 4 wires per-verb path classification
/// (SPEC §7), the resolver (SPEC §8), and the flag-with-value-aware verb-
/// chain probe on top of the PR 3 core. PR 5 will land subshell flag
/// flipping, <c>bash -c</c> recursion, and cd-in-compound propagation.
/// </summary>
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

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

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
        // parsed up to that point — we keep it strictly safe-fail (empty
        // Clauses) so consumers don't get a half-built AST whose shape
        // changes when PR 5 wires recursion. The reason text comes
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
        // clause segments at top-level &&, ||, ;, and |.
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

        // Step 4: parse each segment into a Clause.
        var clauses = new List<Clause>(segments.Count);
        foreach (var segment in segments)
        {
            var clauseOrError = ParseClauseSegment(segment, source, options);
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
        // by an immediately-adjacent `(` and `)`.
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

        // Process substitution: `<(cmd)` or `>(cmd)`.
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

    private sealed class Segment
    {
        public CompoundOperator PrecedingOperator { get; init; }

        public List<BashToken> Tokens { get; init; } = new();

        public bool FromSubshell { get; init; }
    }

    private static List<Segment> SplitIntoSegments(
        IReadOnlyList<BashToken> tokens,
        string source,
        out string? error)
    {
        var segments = new List<Segment>();
        var current = new Segment { PrecedingOperator = CompoundOperator.None };

        var depth = 0;
        var subshellDepthStack = new Stack<int>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (t.Kind == BashTokenKind.Operator)
            {
                var op = t.OperatorText;

                if (op == "(")
                {
                    if (current.Tokens.Count > 0)
                    {
                        segments.Add(current);
                        current = new Segment { PrecedingOperator = CompoundOperator.Sequence, FromSubshell = subshellDepthStack.Count > 0 };
                    }
                    else
                    {
                        current = new Segment { PrecedingOperator = current.PrecedingOperator, FromSubshell = true };
                    }

                    subshellDepthStack.Push(depth);
                    depth++;
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

                    if (current.Tokens.Count > 0)
                    {
                        segments.Add(current);
                    }

                    current = new Segment
                    {
                        PrecedingOperator = CompoundOperator.None,
                        FromSubshell = subshellDepthStack.Count > 0,
                    };
                    continue;
                }

                if (op == "&&" || op == "||" || op == ";" || op == "|")
                {
                    if (current.Tokens.Count == 0 && current.PrecedingOperator != CompoundOperator.None)
                    {
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

    private static ClauseResult ParseClauseSegment(Segment segment, string source, BashParserOptions options)
    {
        if (segment.Tokens.Count == 0)
        {
            return ClauseResult.Empty();
        }

        // ---- Verb-chain extraction with flag-with-value awareness ----
        //
        // PR 4 follow-up to the PR 3 probe: a token like `git -C /repo log`
        // shouldn't truncate the verb chain at `-C`. We greedily consume
        // any flag-with-value pair owned by the tentative first verb
        // (`tokens[0]`) before probing arity. The consumed flag + value
        // pair stays in the segment for arg-extraction; only the verb
        // probe sees a "compressed" view of the segment.
        //
        // Locked interpretation #8 / SPEC §12: `git -C /repo log` →
        // Verb=["git", "log"], Args=[-C, /repo]. The flag and its value
        // appear in source order in the Args list, with /repo carrying
        // IsPath=true via the FlagValueIsPath table.
        var verbCandidateValues = new List<string>(3);
        var verbCandidateIndices = new List<int>(3);
        var consumedFlagValueIndices = new HashSet<int>();

        // Look up the would-be verb so we know which flags are
        // "owned" by it. We only honor the flag-with-value skip when the
        // first token is a known Word verb — quoted strings and opaque
        // substitutions don't carry verb identity.
        string? tentativeVerb = null;
        if (segment.Tokens.Count > 0
            && segment.Tokens[0].Kind == BashTokenKind.Word
            && !IsFlagWord(segment.Tokens[0]))
        {
            tentativeVerb = segment.Tokens[0].Value;
        }

        var hasFlagsTable = tentativeVerb is not null
            && BashVerbs.FlagsWithValue.TryGetValue(tentativeVerb, out _);

        for (var i = 0; i < segment.Tokens.Count && verbCandidateValues.Count < 3; i++)
        {
            var t = segment.Tokens[i];
            if (t.Kind == BashTokenKind.Word || t.Kind == BashTokenKind.QuotedString)
            {
                if (IsFlagWord(t))
                {
                    // Skip-through case: this is a flag-with-value pair owned
                    // by the tentative verb. Skip both the flag and its
                    // immediate value and keep probing arity. Only Word
                    // tokens qualify as flags (quoted "-x" stays literal).
                    if (hasFlagsTable
                        && tentativeVerb is not null
                        && BashVerbs.FlagsWithValue[tentativeVerb].Contains(StripEqualsValue(t.Value))
                        && i + 1 < segment.Tokens.Count
                        && (segment.Tokens[i + 1].Kind == BashTokenKind.Word
                            || segment.Tokens[i + 1].Kind == BashTokenKind.QuotedString))
                    {
                        // The two-token `-C /repo` form. Equals-form
                        // `--git-dir=/repo` is a single token and never enters
                        // this branch — but the verb-probe still ends at it
                        // (next iteration sees IsFlagWord and breaks below).
                        if (HasInlineEqualsValue(t.Value))
                        {
                            // `--flag=value` — single token. Don't consume
                            // the next, and let the normal arg-extraction
                            // path split on `=`. End the verb-probe here.
                            break;
                        }

                        consumedFlagValueIndices.Add(i);
                        consumedFlagValueIndices.Add(i + 1);
                        i++; // skip the value too on the next loop step
                        continue;
                    }

                    // Plain flag with no path-value to skip → stops the verb probe.
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
            // Redirect-only clause.
            ExtractRedirectsAndArgs(
                segment.Tokens,
                0,
                source,
                options,
                verb: new VerbChain(),
                consumedFlagValueIndices: consumedFlagValueIndices,
                out var emptyArgs,
                out var emptyRedirects,
                out var redirectError);
            if (redirectError is not null)
            {
                return ClauseResult.Fail(redirectError);
            }

            return ClauseResult.Ok(new Clause
            {
                Operator = segment.PrecedingOperator,
                Verb = new VerbChain(),
                Args = emptyArgs,
                Redirects = emptyRedirects,
                IsSubshell = false,
                IsBashCWrapped = false,
            });
        }

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

        var verbTokens = new List<string>(arity);
        for (var k = 0; k < arity; k++)
        {
            verbTokens.Add(verbCandidateValues[k]);
        }

        var verbChain = new VerbChain { Tokens = verbTokens };

        // The arg-extraction starts immediately after the last verb-chain
        // *position* in the original segment, so the consumed flag-value
        // pair (which sits *before* that position when it precedes the
        // verb-chain extension) still gets emitted as Args in source order.
        // Concretely: for `git -C /repo log`, the verb-chain positions are
        // 0 and 3; we walk all of segment.Tokens from position 0 and emit
        // -C, /repo as args while skipping the verb-position tokens.
        var argStart = 0;
        var verbPositions = new HashSet<int>(verbCandidateIndices.GetRange(0, arity));

        ExtractRedirectsAndArgs(
            segment.Tokens,
            argStart,
            source,
            options,
            verb: verbChain,
            consumedFlagValueIndices: consumedFlagValueIndices,
            skipIndices: verbPositions,
            verbKeyForFlagValuePaths: verbTokens[0],
            out var args,
            out var redirects,
            out var argError);
        if (argError is not null)
        {
            return ClauseResult.Fail(argError);
        }

        var clause = new Clause
        {
            Operator = segment.PrecedingOperator,
            Verb = verbChain,
            Args = args,
            Redirects = redirects,
            IsSubshell = false,
            IsBashCWrapped = false,
        };

        return ClauseResult.Ok(clause);
    }

    private static bool IsFlagWord(BashToken token)
    {
        if (token.Kind != BashTokenKind.Word)
        {
            return false;
        }

        return token.Value.Length > 0 && token.Value[0] == '-';
    }

    /// <summary>
    /// For an equals-form flag like <c>--output=file.txt</c>, return the
    /// flag portion (<c>--output</c>) so the FlagsWithValue table lookup
    /// matches. For plain flags returns the input unchanged.
    /// </summary>
    private static string StripEqualsValue(string flag)
    {
        var eq = flag.IndexOf('=');
        return eq > 0 ? flag.Substring(0, eq) : flag;
    }

    private static bool HasInlineEqualsValue(string flag) =>
        flag.IndexOf('=') > 0;

    // ---------------------------------------------------------------- args + redirects

    /// <summary>
    /// Extract args and redirects from <paramref name="segmentTokens"/>
    /// starting at <paramref name="start"/>. Honors:
    /// <list type="bullet">
    ///   <item>SPEC §7 per-verb path-arg classification via <see cref="BashPerVerbRules.IsPositionalPathArg"/>.</item>
    ///   <item>SPEC §8 path resolution via <see cref="BashResolver.Resolve"/>.</item>
    ///   <item>The flag-with-value table to decide whether a consumed value is a path.</item>
    /// </list>
    /// </summary>
    private static void ExtractRedirectsAndArgs(
        IReadOnlyList<BashToken> segmentTokens,
        int start,
        string source,
        BashParserOptions options,
        VerbChain verb,
        HashSet<int> consumedFlagValueIndices,
        out IReadOnlyList<Arg> args,
        out IReadOnlyList<Redirect> redirects,
        out string? error)
    {
        ExtractRedirectsAndArgs(
            segmentTokens,
            start,
            source,
            options,
            verb,
            consumedFlagValueIndices,
            skipIndices: null,
            verbKeyForFlagValuePaths: verb.Tokens is null || verb.Tokens.Count == 0 ? null : verb.Tokens[0],
            out args,
            out redirects,
            out error);
    }

    private static void ExtractRedirectsAndArgs(
        IReadOnlyList<BashToken> segmentTokens,
        int start,
        string source,
        BashParserOptions options,
        VerbChain verb,
        HashSet<int> consumedFlagValueIndices,
        HashSet<int>? skipIndices,
        string? verbKeyForFlagValuePaths,
        out IReadOnlyList<Arg> args,
        out IReadOnlyList<Redirect> redirects,
        out string? error)
    {
        var argList = new List<Arg>();
        var redirectList = new List<Redirect>();
        var positionalIndex = 0;
        var i = start;

        // Tracks "next non-flag arg is the value of this flag" — used to
        // attribute path-classification to the value of a flag-with-value
        // pair (e.g. `curl -o /tmp/out https://x` → /tmp/out gets IsPath).
        string? pendingFlagForValue = null;

        while (i < segmentTokens.Count)
        {
            // Skip verb-chain positions when the caller asked us to (the
            // flag-with-value-aware verb-chain probe leaves the verb tokens
            // interleaved with consumed flag-value pairs).
            if (skipIndices is not null && skipIndices.Contains(i))
            {
                i++;
                continue;
            }

            var t = segmentTokens[i];
            if (t.Kind == BashTokenKind.Operator)
            {
                if (TryMapRedirect(t.OperatorText, out var dir))
                {
                    if (i + 1 >= segmentTokens.Count)
                    {
                        error = $"redirect operator '{t.OperatorText}' missing target at position {t.SourceStart}";
                        args = argList;
                        redirects = redirectList;
                        return;
                    }

                    var target = segmentTokens[i + 1];
                    if (target.Kind == BashTokenKind.Operator)
                    {
                        error = $"redirect operator '{t.OperatorText}' missing target at position {t.SourceStart}";
                        args = argList;
                        redirects = redirectList;
                        return;
                    }

                    BuildRedirect(dir, target, source, options, redirectList);
                    i += 2;
                    continue;
                }

                if (t.OperatorText == "<<" || t.OperatorText == "<<-")
                {
                    if (i + 1 >= segmentTokens.Count)
                    {
                        error = $"heredoc operator '{t.OperatorText}' missing delimiter at position {t.SourceStart}";
                        args = argList;
                        redirects = redirectList;
                        return;
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

                error = $"unexpected operator '{t.OperatorText}' at position {t.SourceStart}";
                args = argList;
                redirects = redirectList;
                return;
            }

            // Tokens pre-consumed by the verb-chain probe as part of a
            // flag-with-value pair still pass through this loop and surface
            // as args in source order. The pending-flag state machine
            // attributes their path classification correctly without
            // requiring a special branch here.
            _ = consumedFlagValueIndices;

            switch (t.Kind)
            {
                case BashTokenKind.Word:
                {
                    var sourceRaw = SourceSlice(source, t);

                    // Equals-form flag-with-value: `--output=file.txt`. The
                    // flag half is a Literal arg with IsFlag=true (Raw
                    // starts with '-'); the value half is classified per
                    // the flag-value path rule.
                    if (TrySplitEqualsFlag(t.Value, out var flagPart, out var valuePart))
                    {
                        // Flag arg.
                        argList.Add(new Arg
                        {
                            Raw = flagPart,
                            Resolved = null,
                            Kind = ArgKind.Literal,
                            IsPath = false,
                        });

                        // Value arg — classify via FlagValueIsPath if the
                        // verb owns the flag, otherwise fall back to plain
                        // literal (the equals-form is its own visible split,
                        // so we don't apply LooksLikePath here).
                        var valueIsPath = verbKeyForFlagValuePaths is not null
                            && BashPerVerbRules.ValueOfFlagIsPath(verbKeyForFlagValuePaths, flagPart);
                        var (vKind, vResolved, vIsPath) = BashResolver.Resolve(valuePart, valueIsPath, options);
                        argList.Add(new Arg
                        {
                            Raw = valuePart,
                            Resolved = vResolved,
                            Kind = vKind,
                            IsPath = vIsPath,
                        });

                        // The split form doesn't propagate to a "next-arg is
                        // the value" pending-state — the value already
                        // landed in argList.
                        break;
                    }

                    if (IsFlag(sourceRaw))
                    {
                        // Plain flag arg. Don't bump positionalIndex.
                        argList.Add(new Arg
                        {
                            Raw = sourceRaw,
                            Resolved = null,
                            Kind = ArgKind.Literal,
                            IsPath = false,
                        });

                        // If this flag takes a value (per the verb's table),
                        // mark the *next* non-flag arg as that value. We
                        // do this whether or not the verb-chain probe
                        // pre-consumed it; pre-consumed pairs are also
                        // routed through this branch, so the pending state
                        // attributes correctly.
                        if (verbKeyForFlagValuePaths is not null
                            && BashVerbs.FlagsWithValue.TryGetValue(verbKeyForFlagValuePaths, out var flagsTable)
                            && flagsTable.Contains(sourceRaw))
                        {
                            pendingFlagForValue = sourceRaw;
                        }
                        else
                        {
                            pendingFlagForValue = null;
                        }

                        break;
                    }

                    // Non-flag positional. Classify path / resolve.
                    bool treatAsPath;
                    if (pendingFlagForValue is not null && verbKeyForFlagValuePaths is not null)
                    {
                        // This is the value of a preceding flag — use the
                        // flag-value rule, NOT the positional-index rule.
                        treatAsPath = BashPerVerbRules.ValueOfFlagIsPath(
                            verbKeyForFlagValuePaths, pendingFlagForValue);
                        pendingFlagForValue = null;
                    }
                    else
                    {
                        treatAsPath = BashPerVerbRules.IsPositionalPathArg(verb, positionalIndex, t.Value);
                        positionalIndex++;
                    }

                    var (kind, resolved, isPath) = BashResolver.Resolve(t.Value, treatAsPath, options);
                    argList.Add(new Arg
                    {
                        Raw = sourceRaw,
                        Resolved = resolved,
                        Kind = kind,
                        IsPath = isPath,
                    });

                    break;
                }

                case BashTokenKind.QuotedString:
                {
                    var sourceRaw = SourceSlice(source, t);

                    // Quoted strings never act as flags (a leading dash in
                    // a quoted string is the user's signal "literal"). They
                    // still classify as positional path / non-path through
                    // the per-verb rule + resolver.
                    bool treatAsPath;
                    if (pendingFlagForValue is not null && verbKeyForFlagValuePaths is not null)
                    {
                        treatAsPath = BashPerVerbRules.ValueOfFlagIsPath(
                            verbKeyForFlagValuePaths, pendingFlagForValue);
                        pendingFlagForValue = null;
                    }
                    else
                    {
                        treatAsPath = BashPerVerbRules.IsPositionalPathArg(verb, positionalIndex, t.Value);
                        positionalIndex++;
                    }

                    var (kind, resolved, isPath) = BashResolver.Resolve(t.Value, treatAsPath, options);
                    argList.Add(new Arg
                    {
                        Raw = sourceRaw,
                        Resolved = resolved,
                        Kind = kind,
                        IsPath = isPath,
                    });
                    break;
                }

                case BashTokenKind.OpaqueSubstitution:
                {
                    // Locked interpretation #2 — opaque region collapses to
                    // a single DynamicSkip arg. Don't bump positionalIndex
                    // — the opaque region replaces what would otherwise be
                    // one positional and the IsPath signal doesn't apply.
                    // Bump the positional counter for the SPEC §12 rm
                    // example so a *subsequent* positional gets the right
                    // index, though.
                    argList.Add(new Arg
                    {
                        Raw = t.Value,
                        Resolved = null,
                        Kind = ArgKind.DynamicSkip,
                        IsPath = false,
                    });
                    positionalIndex++;
                    pendingFlagForValue = null;
                    break;
                }

                default:
                    break;
            }

            i++;
        }

        args = argList;
        redirects = redirectList;
        error = null;
    }

    private static void BuildRedirect(
        RedirectDirection direction,
        BashToken target,
        string source,
        BashParserOptions options,
        List<Redirect> redirectList)
    {
        if (target.Kind == BashTokenKind.OpaqueSubstitution)
        {
            // Opaque region as redirect target → always DynamicSkip.
            // Target carries the raw opaque slice for diagnostics.
            redirectList.Add(new Redirect
            {
                Direction = direction,
                Target = target.Value,
                IsDynamicSkip = true,
            });
            return;
        }

        var raw = SourceSlice(source, target);

        // Redirect targets are always treated as paths. SPEC §8 +
        // locked interpretation #3: a glob target stays IsPath=true with
        // Kind=Glob; an env-var target becomes DynamicSkip; a literal
        // resolves against WorkingDirectory.
        var (kind, resolved, _) = BashResolver.Resolve(target.Value, treatAsPath: true, options);

        bool isDynamic;
        string redirectTarget;
        if (kind == ArgKind.DynamicSkip)
        {
            isDynamic = true;
            redirectTarget = raw;
        }
        else
        {
            isDynamic = false;
            redirectTarget = resolved ?? raw;
        }

        redirectList.Add(new Redirect
        {
            Direction = direction,
            Target = redirectTarget,
            IsDynamicSkip = isDynamic,
        });
    }

    private static bool IsFlag(string raw) =>
        raw.Length > 0 && raw[0] == '-';

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
        if (raw.Length < 2 || raw[0] != '-')
        {
            flagPart = "";
            valuePart = "";
            return false;
        }

        var eq = raw.IndexOf('=');
        if (eq <= 0 || eq == raw.Length - 1)
        {
            flagPart = "";
            valuePart = "";
            return false;
        }

        flagPart = raw.Substring(0, eq);
        valuePart = raw.Substring(eq + 1);
        return true;
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
