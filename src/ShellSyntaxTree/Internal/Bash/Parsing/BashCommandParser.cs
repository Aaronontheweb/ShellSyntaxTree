// -----------------------------------------------------------------------
// <copyright file="BashCommandParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Bash.Verbs;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Translates a <see cref="BashLexer"/> token stream into the public
/// <see cref="ParsedCommand"/> AST. PR 4 wired per-verb path classification
/// (SPEC §7), the resolver (SPEC §8), and the flag-with-value-aware verb-
/// chain probe. PR 5 lands cd-in-compound attribution propagation
/// (SPEC §9), subshell isolation (SPEC §10), and <c>bash -c</c> recursion
/// with the depth-5 cap per locked interpretation #4.
/// </summary>
internal static class BashCommandParser
{
    /// <summary>
    /// Maximum allowed <c>bash -c</c> / <c>sh -c</c> nesting depth before
    /// the parser safe-fails per locked interpretation #4. Hard-coded —
    /// SPEC §10 picks 5 because hostile input would have to chain 5+ wrapper
    /// invocations to evade analysis and that's well beyond any legitimate
    /// agent emission.
    /// </summary>
    private const int MaxBashCRecursionDepth = 5;

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

        return ParseInternal(source, options, bashCDepth: 0, markBashCWrapped: false);
    }

    /// <summary>
    /// Recursion entry point. <paramref name="bashCDepth"/> counts how many
    /// <c>bash -c</c> wrappers we've unwrapped to reach this call; the
    /// outer caller passes 0. <paramref name="markBashCWrapped"/> sets
    /// <see cref="Clause.IsCommandStringWrapped"/> on every emitted clause and
    /// fires only on recursive calls (the outer top-level command doesn't
    /// pretend to be wrapped).
    /// </summary>
    private static ParsedCommand ParseInternal(
        string source,
        BashParserOptions options,
        int bashCDepth,
        bool markBashCWrapped)
    {
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
        // Clauses) so consumers can't build on a partial AST whose shape
        // a sibling clause might invalidate. The reason text comes
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

        // Step 4: walk segments with the cd-attribution context and the
        // bash -c recursion machinery.
        var clauses = new List<Clause>(segments.Count);
        var attribution = new CdAttributionContext();
        IReadOnlyList<int> prevStack = new[] { 0 };

        foreach (var segment in segments)
        {
            // ---- Subshell push/pop driven by SubshellStack divergence ----
            //
            // Each segment carries the *full* stack of subshell IDs it
            // sits inside (outer-most → inner-most), with ID 0 reserved
            // for the top-level command. We pop pushed frames back to the
            // common prefix between prevStack and segment.SubshellStack,
            // then push fresh frames for each new ID we're entering. This
            // correctly handles `(a) && (b)`: between the two segments
            // we exit subshell A (pop) and enter subshell B (push), even
            // though SubshellDepth=1 on both.
            var commonPrefix = 0;
            while (commonPrefix < prevStack.Count
                && commonPrefix < segment.SubshellStack.Count
                && prevStack[commonPrefix] == segment.SubshellStack[commonPrefix])
            {
                commonPrefix++;
            }

            // Pop everything past the common prefix in prevStack.
            for (var k = prevStack.Count - 1; k >= commonPrefix; k--)
            {
                attribution.PopForSubshell();
            }

            // Push fresh frames for the new IDs in segment.SubshellStack.
            for (var k = commonPrefix; k < segment.SubshellStack.Count; k++)
            {
                attribution.PushForSubshell();
            }

            prevStack = segment.SubshellStack;

            // ---- bash -c detection (before clause-build) ----
            //
            // Locked interpretation #4: nested `bash -c "..."` wrappers
            // expand inline; the outer wrapper clause is consumed. The cap
            // at depth 5 fires here — one more level past 5 → outer
            // ParsedCommand.IsUnparseable = true with reason naming the
            // overflow. Sub-clauses parsed *up to* the cap may still appear,
            // but per SPEC §11 + locked interpretation #4 we keep clauses
            // empty for hostile-input safety.
            if (TryDetectBashCWrapper(segment, source, out var innerCommand))
            {
                if (bashCDepth + 1 > MaxBashCRecursionDepth)
                {
                    return new ParsedCommand
                    {
                        Source = source,
                        Clauses = Array.Empty<Clause>(),
                        IsUnparseable = true,
                        UnparseableReason = "bash -c recursion depth exceeded (>5)",
                    };
                }

                // Recurse with the original options — bash -c spawns a
                // fresh shell, so outer cd-attribution does *not* propagate
                // into the inner command. This is a v0.1 decision; v0.1.x
                // can revisit if real-world commands surface a counter-case.
                var inner = ParseInternal(
                    innerCommand!,
                    options,
                    bashCDepth: bashCDepth + 1,
                    markBashCWrapped: true);

                if (inner.IsUnparseable)
                {
                    return new ParsedCommand
                    {
                        Source = source,
                        Clauses = Array.Empty<Clause>(),
                        IsUnparseable = true,
                        UnparseableReason = inner.UnparseableReason,
                    };
                }

                // First inner clause inherits the outer segment's operator
                // (since the bash -c clause itself is consumed). Remaining
                // inner clauses keep their parsed operators.
                var innerClauses = inner.Clauses;
                for (var k = 0; k < innerClauses.Count; k++)
                {
                    var ic = innerClauses[k];
                    var op = k == 0 ? segment.PrecedingOperator : ic.Operator;
                    var isSubshell = segment.SubshellDepth > 0 || ic.IsSubshell;
                    clauses.Add(ic with
                    {
                        Operator = op,
                        IsSubshell = isSubshell,
                        IsCommandStringWrapped = true,
                    });
                }

                continue;
            }

            // ---- Normal clause path with attribution propagation ----
            //
            // Effective resolution options depend on attribution state:
            //   - no attribution → caller options pass through unchanged.
            //   - literal cd attribution → swap WorkingDirectory to the cd
            //     target so relative path args in subsequent clauses
            //     resolve under it (SPEC §9 example: `cd /a && cat foo`
            //     → cat's `foo` resolves to `/a/foo`).
            //   - dynamic cd attribution (locked interpretation #6) →
            //     keep caller options but set the resolver's
            //     `workingDirectoryUnknown` flag so relative paths surface
            //     as DynamicSkip (the daemon cwd is *not* the right
            //     fallback; we statically don't know the actual cwd).
            var effectiveOptions = options;
            var workingDirectoryUnknown = false;
            if (attribution.HasAttribution && !attribution.IsDynamic)
            {
                effectiveOptions = new BashParserOptions
                {
                    HomeDirectory = options.HomeDirectory,
                    WorkingDirectory = attribution.ResolvedCwd,
                };
            }
            else if (attribution.IsDynamic)
            {
                workingDirectoryUnknown = true;
            }

            var clauseOrError = ParseClauseSegment(segment, source, effectiveOptions, workingDirectoryUnknown);
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
                // Apply the IsSubshell / IsCommandStringWrapped flags first; both
                // are properties of the *segment*, not the clause body.
                var withFlags = clause with
                {
                    IsSubshell = segment.SubshellDepth > 0,
                    IsCommandStringWrapped = markBashCWrapped,
                };

                // Inspect the verb to decide whether this clause updates
                // the attribution context after emission.
                var verb = clause.Verb;
                var firstVerbToken = verb.Tokens.Count > 0 ? verb.Tokens[0] : null;
                var isCdLike = firstVerbToken is not null
                    && (string.Equals(firstVerbToken, "cd", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(firstVerbToken, "chdir", StringComparison.OrdinalIgnoreCase));

                // Every clause receives the *current* attribution as a
                // synthetic arg — including cd/chdir clauses themselves
                // (per SPEC §9 rule 2 "subsequent clauses inherit", which
                // applies to cd /b in `cd /a && cd /b`). The attribution
                // state is updated *after* the arg is attached, so the
                // newly-set cd /b doesn't attribute to itself.
                var emitted = AttachAttributionArg(withFlags, attribution);

                if (isCdLike)
                {
                    UpdateAttributionFromCd(clause, attribution);
                }

                clauses.Add(emitted);
            }
        }

        return new ParsedCommand
        {
            Source = source,
            Clauses = clauses,
            IsUnparseable = false,
        };
    }

    // ---------------------------------------------------------------- cd attribution

    /// <summary>
    /// Inspect a freshly-parsed <c>cd</c> / <c>chdir</c> clause and update
    /// <paramref name="attribution"/> from its first non-flag positional
    /// arg. Per locked interpretation #5 only cd/chdir reach here;
    /// pushd/popd/push-location/set-location parse as CwdVerbs but the
    /// caller skips this update.
    /// </summary>
    private static void UpdateAttributionFromCd(Clause clause, CdAttributionContext attribution)
    {
        Arg? firstPositional = null;
        foreach (var a in clause.Args)
        {
            if (!a.IsFlag)
            {
                firstPositional = a;
                break;
            }
        }

        if (firstPositional is null)
        {
            // `cd` with no target → bash semantics is "cd to $HOME". We
            // could expand to HomeDirectory here, but security-gate
            // consumers care about *explicit* cwds; treat as no attribution
            // change (the previous attribution, if any, persists). A
            // synthetic arg is also not appended downstream since
            // HasAttribution stays as it was.
            return;
        }

        switch (firstPositional.Kind)
        {
            case ArgKind.Literal:
            case ArgKind.Tilde:
                // Literal or tilde-expanded path. Resolved should be the
                // normalized absolute path. When it isn't (resolver fell
                // through), treat as dynamic so we don't carry a stale
                // attribution.
                if (firstPositional.Resolved is not null)
                {
                    attribution.SetLiteralAttribution(firstPositional.Resolved);
                }
                else
                {
                    attribution.SetDynamicAttribution();
                }
                break;
            case ArgKind.DynamicSkip:
            case ArgKind.EnvVar:
            case ArgKind.Glob:
                // Locked interpretation #6 (and the symmetric Glob case):
                // we can't statically know the resolved cwd. Subsequent
                // clauses get a synthetic DynamicSkip attribution arg.
                attribution.SetDynamicAttribution();
                break;
        }
    }

    /// <summary>
    /// Append a synthetic <see cref="Arg.IsCwdAttribution"/> arg to
    /// <paramref name="clause"/> when <paramref name="attribution"/> has
    /// active state. Literal-cd attribution appends a Literal/IsPath=true
    /// arg with Resolved set; dynamic-cd attribution (locked interpretation
    /// #6) appends a DynamicSkip arg with Resolved=null. When no
    /// attribution is active, returns <paramref name="clause"/> unchanged.
    /// </summary>
    private static Clause AttachAttributionArg(Clause clause, CdAttributionContext attribution)
    {
        if (!attribution.HasAttribution)
        {
            return clause;
        }

        Arg synthetic;
        if (attribution.IsDynamic)
        {
            synthetic = new Arg
            {
                Raw = "<dynamic-cwd>",
                Resolved = null,
                Kind = ArgKind.DynamicSkip,
                IsPath = false,
                IsCwdAttribution = true,
            };
        }
        else
        {
            var cwd = attribution.ResolvedCwd!;
            synthetic = new Arg
            {
                Raw = cwd,
                Resolved = cwd,
                Kind = ArgKind.Literal,
                IsPath = true,
                IsCwdAttribution = true,
            };
        }

        var newArgs = new List<Arg>(clause.Args.Count + 1);
        newArgs.AddRange(clause.Args);
        newArgs.Add(synthetic);
        return clause with { Args = newArgs };
    }

    // ---------------------------------------------------------------- bash -c detection

    /// <summary>
    /// Detect whether <paramref name="segment"/> is a <c>bash -c "..."</c>
    /// or <c>sh -c "..."</c> wrapper. On match, <paramref name="innerCommand"/>
    /// receives the unquoted inner command string (suitable for recursive
    /// parsing) and the method returns true.
    /// </summary>
    /// <remarks>
    /// We scan the segment's tokens directly (rather than running through
    /// the full clause parser first) so the wrapper is consumed cleanly —
    /// the outer clause never appears in <c>ParsedCommand.Clauses</c>. The
    /// scan looks for: a Word verb of <c>bash</c> or <c>sh</c>, followed by
    /// any combination of flag tokens, then a <c>-c</c> Word flag, then an
    /// adjacent QuotedString token whose value becomes the inner command.
    /// </remarks>
    private static bool TryDetectBashCWrapper(Segment segment, string source, out string? innerCommand)
    {
        innerCommand = null;

        if (segment.Tokens.Count < 3)
        {
            return false;
        }

        // First non-flag Word token must be `bash` or `sh`.
        var t0 = segment.Tokens[0];
        if (t0.Kind != BashTokenKind.Word)
        {
            return false;
        }

        if (!string.Equals(t0.Value, "bash", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(t0.Value, "sh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Scan from index 1 for `-c` followed immediately by a QuotedString.
        for (var i = 1; i < segment.Tokens.Count - 1; i++)
        {
            var t = segment.Tokens[i];
            if (t.Kind != BashTokenKind.Word)
            {
                return false;
            }

            if (string.Equals(t.Value, "-c", StringComparison.Ordinal))
            {
                var next = segment.Tokens[i + 1];
                if (next.Kind == BashTokenKind.QuotedString)
                {
                    innerCommand = next.Value;
                    return true;
                }

                // `-c` not followed by a quoted string → treat as a regular
                // bash clause. (Caller falls through to the normal path.)
                return false;
            }

            if (!IsFlagWord(t))
            {
                // First non-flag positional before reaching `-c` → not a
                // bash-c wrapper. Treat as `bash script.sh ...` etc.
                return false;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- token filtering

    private static List<BashToken> FilterSignificant(IReadOnlyList<BashToken> tokens)
    {
        var filtered = new List<BashToken>(tokens.Count);
        foreach (var t in tokens)
        {
            // A newline-bearing Whitespace token is a statement separator
            // (SPEC §4) — it survives filtering so SplitIntoSegments can
            // split clauses on it, exactly like an explicit ';'. Plain
            // space/tab Whitespace, Continuation, and Comment carry no
            // structural signal and are dropped.
            if ((t.Kind == BashTokenKind.Whitespace && !t.IsStatementSeparator)
                || t.Kind == BashTokenKind.Continuation
                || t.Kind == BashTokenKind.Comment)
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
        // Control-flow keyword at verb position runs FIRST (SPEC §11
        // precedence). A keyword like `case x in a) ;; esac` would
        // otherwise trip the paren-balance check in SplitIntoSegments
        // before we get to ParseClauseSegment's per-clause keyword check
        // — and the resulting "unbalanced parens" reason hides the real
        // cause. Verb position = index 0 OR immediately after a clause
        // separator (`&&`, `||`, `;`, `|`, or the `(` that opens a
        // subshell). This intentionally skips `case` / `for` / etc.
        // appearing as POSITIONAL ARGS (e.g. `echo case`), matching the
        // ParseClauseSegment scoping.
        var nextIsVerbSlot = true;
        foreach (var t in tokens)
        {
            if (t.Kind == BashTokenKind.Operator)
            {
                nextIsVerbSlot = t.OperatorText is "&&" or "||" or ";" or "|" or "(";
                continue;
            }

            // A retained Whitespace token is a newline statement separator
            // (SPEC §4); the word after it sits at a verb slot, exactly as
            // it would after ';'. Without this, a control-flow keyword that
            // opens a newline-separated clause would slip past detection.
            if (t.Kind == BashTokenKind.Whitespace)
            {
                nextIsVerbSlot = true;
                continue;
            }

            if (nextIsVerbSlot
                && t.Kind == BashTokenKind.Word
                && BashVerbs.ControlFlowKeywords.Contains(t.Value))
            {
                reason = $"control-flow keyword '{t.Value}' is not supported in v0.1";
                return true;
            }

            nextIsVerbSlot = false;
        }

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

        /// <summary>
        /// Paren-nesting depth of this segment. 0 = top-level; 1 = direct
        /// child of one subshell; etc. PR 5 uses transitions in this value
        /// across consecutive segments to push/pop the cd-attribution stack.
        /// </summary>
        public int SubshellDepth { get; init; }

        /// <summary>
        /// Stack of subshell IDs from outermost to innermost. ID 0 is the
        /// top-level command; each subsequent <c>(</c> open assigns a fresh
        /// monotonically-increasing ID. PR 5 uses divergence in this stack
        /// across consecutive segments to detect exit-then-re-enter
        /// boundaries (e.g. <c>(a) &amp;&amp; (b)</c> where both segments
        /// have SubshellDepth=1 but live in different subshells).
        /// </summary>
        public IReadOnlyList<int> SubshellStack { get; init; } = Array.Empty<int>();
    }

    private static List<Segment> SplitIntoSegments(
        IReadOnlyList<BashToken> tokens,
        string source,
        out string? error)
    {
        var segments = new List<Segment>();
        var subshellStack = new List<int> { 0 }; // ID 0 is the top-level command.
        var nextSubshellId = 1;
        var depth = 0;

        // Shared factory for the segment opened at every clause boundary:
        // FromSubshell / SubshellDepth / SubshellStack are uniform (driven
        // by the current `depth`), so only the preceding operator varies.
        Segment OpenSegment(CompoundOperator precedingOperator) => new()
        {
            PrecedingOperator = precedingOperator,
            FromSubshell = depth > 0,
            SubshellDepth = depth,
            SubshellStack = subshellStack.ToArray(),
        };

        var current = OpenSegment(CompoundOperator.None);

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            // A retained Whitespace token is a newline statement separator
            // (SPEC §4), equivalent to ';'. Unlike a stray ';', a newline
            // on an empty pending segment is NOT an error — it simply
            // collapses, so blank lines, leading newlines, and a newline
            // right after a compound operator never produce an empty clause.
            if (t.Kind == BashTokenKind.Whitespace)
            {
                if (current.Tokens.Count == 0)
                {
                    continue;
                }

                segments.Add(current);
                current = OpenSegment(CompoundOperator.Sequence);
                continue;
            }

            if (t.Kind == BashTokenKind.Operator)
            {
                var op = t.OperatorText;

                if (op == "(")
                {
                    if (current.Tokens.Count > 0)
                    {
                        segments.Add(current);
                    }

                    depth++;
                    subshellStack.Add(nextSubshellId++);
                    current = OpenSegment(current.Tokens.Count > 0
                        ? CompoundOperator.Sequence
                        : current.PrecedingOperator);
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
                    subshellStack.RemoveAt(subshellStack.Count - 1);

                    if (current.Tokens.Count > 0)
                    {
                        segments.Add(current);
                    }

                    current = OpenSegment(CompoundOperator.None);
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

                    current = OpenSegment(MapOperator(op));
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

    private static ClauseResult ParseClauseSegment(
        Segment segment, string source, BashParserOptions options)
        => ParseClauseSegment(segment, source, options, workingDirectoryUnknown: false);

    private static ClauseResult ParseClauseSegment(
        Segment segment, string source, BashParserOptions options, bool workingDirectoryUnknown)
    {
        if (segment.Tokens.Count == 0)
        {
            return ClauseResult.Empty();
        }

        // Verb-chain extraction per SPEC §6.1. The FileVerb carveout is
        // load-bearing: downstream per-verb positional-arg classification
        // depends on the verb chain staying 1 token for FILE verbs so
        // bare-name targets like `cat README` still surface as Args with
        // IsPath=true. Flag-with-value consumption must run *before* the
        // carveout gate so `tar -C /repo` still attributes IsPath to /repo.
        var consumedFlagValueIndices = new HashSet<int>();
        var verbTokens = new List<string>(4);
        var verbPositions = new HashSet<int>();

        var firstToken = segment.Tokens[0];
        string? firstVerb = null;
        if (firstToken.Kind == BashTokenKind.Word && !IsFlagWord(firstToken))
        {
            firstVerb = firstToken.Value;
            verbTokens.Add(firstVerb);
            verbPositions.Add(0);
        }
        else if (firstToken.Kind == BashTokenKind.QuotedString)
        {
            // Quoted command (`"git" push`): emit a 1-token chain and skip
            // the walk. Bash semantics treat the quoted form as a verb
            // identity carrier; remaining tokens are arg-list material.
            verbTokens.Add(firstToken.Value);
            verbPositions.Add(0);
        }

        BashVerbs.FlagsWithValue.TryGetValue(firstVerb ?? string.Empty, out var flagsForVerb);
        var fileVerbCarveout = firstVerb is not null
            && BashVerbs.FileVerbs.Contains(firstVerb);

        if (firstVerb is not null)
        {
            for (var i = 1; i < segment.Tokens.Count; i++)
            {
                var t = segment.Tokens[i];
                if (t.Kind != BashTokenKind.Word)
                {
                    break;
                }

                if (IsFlagWord(t))
                {
                    if (flagsForVerb is null)
                    {
                        break;
                    }

                    var eq = t.Value.IndexOf('=');
                    var flagKey = eq > 0 ? t.Value.Substring(0, eq) : t.Value;
                    if (!flagsForVerb.Contains(flagKey))
                    {
                        break;
                    }

                    if (eq > 0)
                    {
                        // `--flag=value` is a single token; arg-extraction
                        // splits on `=`. Stop the walk here.
                        break;
                    }

                    if (i + 1 >= segment.Tokens.Count
                        || (segment.Tokens[i + 1].Kind != BashTokenKind.Word
                            && segment.Tokens[i + 1].Kind != BashTokenKind.QuotedString))
                    {
                        break;
                    }

                    consumedFlagValueIndices.Add(i);
                    consumedFlagValueIndices.Add(i + 1);
                    i++;
                    continue;
                }

                // Path evidence wins before the lexical verb heuristic.
                // The argument pass uses the same classifier.
                if (fileVerbCarveout
                    || BashResolver.LooksLikePath(t.Value)
                    || !BashVerbs.IsVerbLikeToken(t))
                {
                    break;
                }

                verbTokens.Add(t.Value);
                verbPositions.Add(i);
            }
        }

        if (verbTokens.Count == 0)
        {
            // Redirect-only clause: no verb identified (e.g. clause starts
            // with an operator, opaque substitution, or a leading flag with
            // no Word firstVerb).
            ExtractRedirectsAndArgs(
                segment.Tokens,
                0,
                source,
                options,
                verb: new VerbChain(),
                consumedFlagValueIndices: consumedFlagValueIndices,
                workingDirectoryUnknown: workingDirectoryUnknown,
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
                IsCommandStringWrapped = false,
            });
        }

        if (BashVerbs.ControlFlowKeywords.Contains(verbTokens[0]))
        {
            return ClauseResult.Fail(
                $"control-flow keyword '{verbTokens[0]}' is not supported in v0.1");
        }

        var verbChain = new VerbChain { Tokens = verbTokens };

        ExtractRedirectsAndArgs(
            segment.Tokens,
            0,
            source,
            options,
            verb: verbChain,
            consumedFlagValueIndices: consumedFlagValueIndices,
            skipIndices: verbPositions,
            verbKeyForFlagValuePaths: verbTokens[0],
            workingDirectoryUnknown: workingDirectoryUnknown,
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
            IsCommandStringWrapped = false,
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
        bool workingDirectoryUnknown,
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
            workingDirectoryUnknown: workingDirectoryUnknown,
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
        bool workingDirectoryUnknown,
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

                    BuildRedirect(dir, target, source, options, redirectList, workingDirectoryUnknown);
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
                    if (NativeFlagSyntax.TrySplitEqualsFlag(
                            t.Value, out var flagPart, out var valuePart))
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
                        var (vKind, vResolved, vIsPath) = BashResolver.Resolve(valuePart, valueIsPath, options, workingDirectoryUnknown);
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

                    var (kind, resolved, isPath) = BashResolver.Resolve(t.Value, treatAsPath, options, workingDirectoryUnknown);
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

                    // Single-quoted tokens carry literal bytes per SPEC §5
                    // — bypass tilde / $HOME / $VAR / glob handling so
                    // `'$HOME'` doesn't expand.
                    var (kind, resolved, isPath) = BashResolver.Resolve(
                        t.Value, treatAsPath, options, workingDirectoryUnknown, t.IsSingleQuoted);
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
        List<Redirect> redirectList,
        bool workingDirectoryUnknown)
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

        if (IsFdDupTarget(target.Value))
        {
            // POSIX fd-dup / fd-close shorthand: `&N`, `&N-`, `&-`. These
            // duplicate or close a file descriptor; they are NOT file paths
            // and MUST NOT be path-resolved (else `2>&1` would resolve to
            // `<cwd>/&1`, a phantom file). Carry the raw token verbatim and
            // mark IsDynamicSkip=true so consumers iterating redirects as
            // paths skip them by default.
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
        var (kind, resolved, _) = BashResolver.Resolve(target.Value, treatAsPath: true, options, workingDirectoryUnknown);

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

    private static bool IsFdDupTarget(string value)
    {
        // Recognized shapes (POSIX `[n]>&word` / `[n]<&word`):
        //   &-        close fd
        //   &N        duplicate fd N (one or more digits)
        //   &N-       duplicate fd N, then close the source (move semantics)
        // Anything else (e.g. `&foo`, `&`, `&1bad`) falls through and is
        // treated as an ordinary redirect target — currently the lexer
        // already treats bare `&` followed by a word char as part of a
        // Word token, so we only need to recognize these well-formed cases.
        if (value.Length < 2 || value[0] != '&')
        {
            return false;
        }

        if (value.Length == 2 && value[1] == '-')
        {
            return true;
        }

        var i = 1;
        while (i < value.Length && value[i] >= '0' && value[i] <= '9')
        {
            i++;
        }

        if (i == 1)
        {
            return false;
        }

        if (i == value.Length)
        {
            return true;
        }

        return i == value.Length - 1 && value[i] == '-';
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
