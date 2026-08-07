// -----------------------------------------------------------------------
// <copyright file="BashCommandParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
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
/// with the depth-5 cap per locked interpretation #4. The v0.3 structural
/// coordinator owns lists, pipelines, groups, and command projections while
/// this type retains the proven simple-command leaf analysis.
/// </summary>
internal static partial class BashCommandParser
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

        return ParseInternal(
            source,
            options,
            bashCDepth: 0,
            structuralDepth: 0,
            markBashCWrapped: false).Command;
    }

    /// <summary>
    /// Recursion entry point. <paramref name="bashCDepth"/> counts how many
    /// <c>bash -c</c> wrappers we've unwrapped to reach this call; the
    /// outer caller passes 0. <paramref name="structuralDepth"/> shares the
    /// bounded nesting budget across substitutions, subshells, and decoded
    /// wrappers. <paramref name="markBashCWrapped"/> sets
    /// <see cref="Clause.IsCommandStringWrapped"/> on every emitted clause and
    /// fires only on recursive calls (the outer top-level command doesn't
    /// pretend to be wrapped).
    /// </summary>
    private static BashParseResult ParseInternal(
        string source,
        BashParserOptions options,
        int bashCDepth,
        int structuralDepth,
        bool markBashCWrapped)
    {
        if (source.Length == 0)
        {
            return ParseStructured(
                source,
                Array.Empty<BashToken>(),
                options,
                bashCDepth,
                structuralDepth,
                markBashCWrapped);
        }

        var tokens = BashLexer.Tokenize(source);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == BashTokenKind.UnparseableSentinel)
            {
                return StructuralFailure(source, token.UnparseableReason);
            }
        }

        var significant = FilterSignificant(tokens);
        if (TryDetectAnomaly(significant, out var anomalyReason))
        {
            return StructuralFailure(source, anomalyReason);
        }

        return ParseStructured(
            source,
            significant,
            options,
            bashCDepth,
            structuralDepth,
            markBashCWrapped);
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
    /// scan looks for an exact literal Word verb of <c>bash</c> or <c>sh</c>,
    /// followed by exact literal flag tokens, an exact <c>-c</c> Word flag,
    /// and a quoted body whose outer-shell provenance is entirely literal
    /// and exactly one value. Decoded spelling alone is insufficient because
    /// outer expansions could change the command before the inner shell sees it.
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
        if (t0.Kind != BashTokenKind.Word || !HasExactLiteralValue(t0))
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
            if (t.Kind != BashTokenKind.Word || !HasExactLiteralValue(t))
            {
                return false;
            }

            if (string.Equals(t.Value, "-c", StringComparison.Ordinal))
            {
                var next = segment.Tokens[i + 1];
                if (next.Kind == BashTokenKind.QuotedString &&
                    HasExactLiteralValue(next))
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
        var joinsAcrossContinuation = false;
        var continuationEnd = -1;
        foreach (var t in tokens)
        {
            // A newline-bearing Whitespace token is a statement separator
            // (SPEC §4) — it survives filtering so the structural coordinator
            // can treat it exactly like an explicit ';'. Plain
            // space/tab Whitespace, Continuation, and Comment carry no
            // structural signal and are dropped.
            if (t.Kind == BashTokenKind.Continuation)
            {
                var followsJoinedFragment = joinsAcrossContinuation
                    ? t.SourceStart == continuationEnd
                    : filtered.Count > 0
                      && IsNativeArgumentFragment(filtered[filtered.Count - 1])
                      && filtered[filtered.Count - 1].SourceStart
                         + filtered[filtered.Count - 1].SourceLength == t.SourceStart;
                joinsAcrossContinuation = followsJoinedFragment;
                continuationEnd = t.SourceStart + t.SourceLength;
                continue;
            }

            if ((t.Kind == BashTokenKind.Whitespace && !t.IsStatementSeparator)
                || t.Kind == BashTokenKind.Comment)
            {
                joinsAcrossContinuation = false;
                continue;
            }

            if (filtered.Count > 0
                && IsNativeArgumentFragment(filtered[filtered.Count - 1])
                && IsNativeArgumentFragment(t)
                && (IsAdjacent(filtered[filtered.Count - 1], t)
                    || joinsAcrossContinuation && t.SourceStart == continuationEnd)
                && !IsInlineNativeArgumentPrefix(filtered[filtered.Count - 1]))
            {
                var previous = filtered[filtered.Count - 1];
                var previousValue = previous.ResolverValue
                    ?? ShellValue.Literal(previous.Value, previous.SourceStart, previous.SourceLength);
                var currentValue = t.ResolverValue
                    ?? ShellValue.Literal(t.Value, t.SourceStart, t.SourceLength);
                filtered[filtered.Count - 1] = new BashToken(
                    BashTokenKind.Word,
                    previous.Value + t.Value,
                    null,
                    previous.SourceStart,
                    t.SourceStart + t.SourceLength - previous.SourceStart,
                    null)
                {
                    ResolverValue = ShellValue.Concat(new[] { previousValue, currentValue }),
                };
                joinsAcrossContinuation = false;
                continue;
            }

            joinsAcrossContinuation = false;
            filtered.Add(t);
        }

        return filtered;
    }

    // ---------------------------------------------------------------- anomaly detection

    private static bool TryDetectAnomaly(IReadOnlyList<BashToken> tokens, out string? reason)
    {
        // Control-flow keyword at verb position runs FIRST (SPEC §11
        // precedence). A keyword like `case x in a) ;; esac` would
        // otherwise trip structural parenthesis validation
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
                && BashVerbs.ControlFlowKeywords.Contains(t.Value)
                && t.Value is not ("for" or "do" or "done"))
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

    // ---------------------------------------------------------------- simple-command leaf adapter

    private sealed class Segment
    {
        public CompoundOperator PrecedingOperator { get; init; }

        public List<BashToken> Tokens { get; init; } = new();
    }

    // ---------------------------------------------------------------- clause parse

    private readonly struct ClauseResult
    {
        public IReadOnlyList<Clause> Clauses { get; }

        public IReadOnlyList<BashPathResolutionSeed> PathResolutions { get; }

        public string? Error { get; }

        public ClauseResult(
            IReadOnlyList<Clause> clauses,
            IReadOnlyList<BashPathResolutionSeed> pathResolutions,
            string? error)
        {
            Clauses = clauses;
            PathResolutions = pathResolutions;
            Error = error;
        }

        public static ClauseResult Ok(
            Clause clause,
            IReadOnlyList<BashPathResolutionSeed> pathResolutions) =>
            new(new[] { clause }, pathResolutions, null);

        public static ClauseResult Fail(string reason) => new(
            Array.Empty<Clause>(),
            Array.Empty<BashPathResolutionSeed>(),
            reason);
    }

    private readonly record struct BashPathResolutionSeed(
        int ClauseElementIndex,
        int? ClauseArgumentIndex,
        ShellValue ResolverValue,
        ShellResolutionConsumer Consumer,
        string AuthoredValue);

    private readonly record struct CwdPathDependencySet(
        Clause Clause,
        IReadOnlyList<CwdPathDependency> Dependencies);

    private readonly record struct BashParseResult(
        ParsedCommand Command,
        IReadOnlyList<CwdPathDependencySet> CwdPathDependencySets);

    private static ClauseResult ParseClauseSegment(
        Segment segment, string source, BashParserOptions options, bool workingDirectoryUnknown)
    {
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
        if ((firstToken.Kind == BashTokenKind.Word
                || firstToken.Kind == BashTokenKind.QuotedString)
            && !HasStaticCommandIdentity(firstToken))
        {
            return ClauseResult.Fail(
                "dynamic Bash command identity is not supported in v0.2");
        }

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
                    || BashVerbs.ControlFlowKeywords.Contains(t.Value)
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
                out var emptyElements,
                out var emptyPathResolutions,
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
                Elements = emptyElements,
                IsSubshell = false,
                IsCommandStringWrapped = false,
            }, emptyPathResolutions);
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
            out var elements,
            out var pathResolutions,
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
            Elements = elements,
            IsSubshell = false,
            IsCommandStringWrapped = false,
        };

        return ClauseResult.Ok(clause, pathResolutions);
    }

    private static bool HasStaticCommandIdentity(BashToken token)
    {
        if (token.ResolverValue is null)
        {
            return true;
        }

        foreach (var fragment in token.ResolverValue.Fragments)
        {
            if (fragment.Kind != ShellValueFragmentKind.Literal
                || fragment.Cardinality != ShellValueCardinality.ExactlyOne)
            {
                return false;
            }
        }

        return true;
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
        out IReadOnlyList<ClauseElement> elements,
        out IReadOnlyList<BashPathResolutionSeed> pathResolutions,
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
            out elements,
            out pathResolutions,
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
        out IReadOnlyList<ClauseElement> elements,
        out IReadOnlyList<BashPathResolutionSeed> pathResolutions,
        out string? error)
    {
        var argList = new List<Arg>();
        var redirectList = new List<Redirect>();
        var elementList = new List<ClauseElement>();
        var pathResolutionList = new List<BashPathResolutionSeed>();
        var positionalIndex = 0;
        var i = start;
        var precedingVerbTokenCount = 0;

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
                var verbToken = segmentTokens[i];
                elementList.Add(CreateElement(
                    source,
                    verbToken,
                    ClauseElementRole.Verb,
                    precedingVerbTokenCount,
                    ArgKind.Literal,
                    isFlag: false,
                    isPath: false,
                    resolved: null));
                precedingVerbTokenCount++;
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
                        elements = elementList;
                        pathResolutions = pathResolutionList;
                        return;
                    }

                    var target = segmentTokens[i + 1];
                    if (target.Kind == BashTokenKind.Operator)
                    {
                        error = $"redirect operator '{t.OperatorText}' missing target at position {t.SourceStart}";
                        args = argList;
                        redirects = redirectList;
                        elements = elementList;
                        pathResolutions = pathResolutionList;
                        return;
                    }

                    BuildRedirect(
                        dir,
                        t,
                        target,
                        source,
                        options,
                        redirectList,
                        workingDirectoryUnknown,
                        precedingVerbTokenCount,
                        out var redirectElement,
                        out var redirectResolverValue);
                    if (redirectResolverValue is not null &&
                        (redirectElement.Resolved is not null ||
                         CanResolveWithKnownCwd(
                             redirectResolverValue,
                             options,
                             ShellResolutionConsumer.BashRedirect)))
                    {
                        pathResolutionList.Add(new BashPathResolutionSeed(
                            elementList.Count,
                            ClauseArgumentIndex: null,
                            redirectResolverValue,
                            ShellResolutionConsumer.BashRedirect,
                            SourceSlice(source, target)));
                    }

                    elementList.Add(redirectElement);
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
                        elements = elementList;
                        pathResolutions = pathResolutionList;
                        return;
                    }

                    var delim = segmentTokens[i + 1];
                    redirectList.Add(new Redirect
                    {
                        Direction = RedirectDirection.In,
                        Target = "<<" + delim.Value + ">",
                        IsDynamicSkip = false,
                    });
                    elementList.Add(CreateRedirectElement(
                        source,
                        t,
                        delim,
                        precedingVerbTokenCount,
                        ArgKind.Literal,
                        isPath: false,
                        resolved: null));

                    i += 2;
                    continue;
                }

                error = $"unexpected operator '{t.OperatorText}' at position {t.SourceStart}";
                args = argList;
                redirects = redirectList;
                elements = elementList;
                pathResolutions = pathResolutionList;
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

                        // Bash concatenates adjacent word fragments into one
                        // argv entry. Preserve that behavior for an inline option
                        // whose value is quoted or computed:
                        // `--data="@request file"` / `--data=$(generate)`.
                        if (NativeFlagSyntax.TrySplitEqualsPrefix(
                                t.Value, out var adjacentFlagPart, out var adjacentValuePrefix)
                            && NativeArgumentFragmentClassifier.TryClassify(
                                source,
                                t.SourceStart,
                                t.SourceStart + t.SourceLength,
                                t.SourceStart + sourceRaw.IndexOf('=') + 1,
                                adjacentFlagPart + "=",
                                GetResolverValue(t, adjacentValuePrefix),
                                segmentTokens,
                                i + 1,
                                new BashNativeArgumentFragmentAdapter(),
                                out var fragmentClassification))
                        {
                            var adjacentValue = fragmentClassification.DecodedValue;
                            argList.Add(new Arg
                            {
                                Raw = adjacentFlagPart,
                                Resolved = null,
                                Kind = ArgKind.Literal,
                                IsPath = false,
                            });

                            Arg valueArg;
                            ShellValue? adjacentPathResolverValue = null;
                            var adjacentPathIsResolvable = false;
                            if (fragmentClassification.HasOpaqueFragment
                                || (verbKeyForFlagValuePaths is not null
                                    && BashPerVerbRules.ValueOfFlagIsOpaqueCommand(
                                        verbKeyForFlagValuePaths, adjacentFlagPart)))
                            {
                                valueArg = new Arg
                                {
                                    Raw = fragmentClassification.ValueRaw,
                                    Kind = ArgKind.DynamicSkip,
                                    IsPath = false,
                                };
                            }
                            else
                            {
                                var adjacentValueForResolution = adjacentValue;
                                var adjacentValueIsPath = verbKeyForFlagValuePaths is not null
                                    && BashPerVerbRules.TryGetFlagValuePath(
                                        verbKeyForFlagValuePaths,
                                        adjacentFlagPart,
                                        adjacentValue,
                                        out adjacentValueForResolution);
                                var adjacentResolverValue = GetResolverValue(
                                    fragmentClassification.ResolverValue,
                                    adjacentValueForResolution);
                                adjacentPathResolverValue = adjacentResolverValue;
                                var (adjacentKind, adjacentResolved, adjacentIsPath) = BashResolver.Resolve(
                                    adjacentResolverValue,
                                    adjacentValueIsPath,
                                    options,
                                    workingDirectoryUnknown,
                                    ShellResolutionConsumer.BashArgument);
                                adjacentPathIsResolvable = adjacentValueIsPath &&
                                    (adjacentResolved is not null ||
                                     CanResolveWithKnownCwd(
                                         adjacentResolverValue,
                                         options,
                                         ShellResolutionConsumer.BashArgument));
                                valueArg = new Arg
                                {
                                    Raw = fragmentClassification.ValueRaw,
                                    Resolved = adjacentResolved,
                                    Kind = adjacentKind,
                                    IsPath = adjacentIsPath,
                                };
                            }

                            var valueArgumentIndex = argList.Count;
                            argList.Add(valueArg);
                            if (adjacentPathIsResolvable &&
                                adjacentPathResolverValue is not null)
                            {
                                pathResolutionList.Add(new BashPathResolutionSeed(
                                    elementList.Count,
                                    valueArgumentIndex,
                                    adjacentPathResolverValue,
                                    ShellResolutionConsumer.BashArgument,
                                    fragmentClassification.ValueRaw));
                            }

                            elementList.Add(CreateCombinedElement(
                                fragmentClassification,
                                precedingVerbTokenCount,
                                valueArg.Kind,
                                isFlag: true,
                                valueArg.IsPath,
                                valueArg.Resolved));
                            i = fragmentClassification.NextTokenIndex;
                            continue;
                        }

                        // Equals-form flag-with-value: `--output=file.txt`. The
                        // flag half is a Literal arg with IsFlag=true (Raw
                        // starts with '-'); the value half is classified per
                        // the flag-value path rule.
                        if (TrySplitInlineFlag(
                                t, out var flagPart, out var valuePart))
                        {
                            var rawEquals = sourceRaw.IndexOf('=');
                            var rawValuePart = rawEquals >= 0
                                ? sourceRaw.Substring(rawEquals + 1)
                                : valuePart;
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
                            var inlineValueForResolution = valuePart;
                            var inlineValueIsOpaqueCommand = verbKeyForFlagValuePaths is not null
                                && BashPerVerbRules.ValueOfFlagIsOpaqueCommand(
                                    verbKeyForFlagValuePaths, flagPart);
                            var valueIsPath = !inlineValueIsOpaqueCommand
                                && verbKeyForFlagValuePaths is not null
                                && BashPerVerbRules.TryGetFlagValuePath(
                                    verbKeyForFlagValuePaths,
                                    flagPart,
                                    valuePart,
                                    out inlineValueForResolution);
                            var inlineResolverValue = GetResolverValue(t, inlineValueForResolution);
                            var (vKind, vResolved, vIsPath) = inlineValueIsOpaqueCommand
                                ? (ArgKind.DynamicSkip, null, false)
                                : BashResolver.Resolve(
                                    inlineResolverValue,
                                    valueIsPath,
                                    options,
                                    workingDirectoryUnknown,
                                    ShellResolutionConsumer.BashArgument);
                            var valueArgumentIndex = argList.Count;
                            argList.Add(new Arg
                            {
                                Raw = rawValuePart,
                                Resolved = vResolved,
                                Kind = vKind,
                                IsPath = vIsPath,
                            });
                            if (valueIsPath &&
                                (vResolved is not null ||
                                 CanResolveWithKnownCwd(
                                     inlineResolverValue,
                                     options,
                                     ShellResolutionConsumer.BashArgument)))
                            {
                                pathResolutionList.Add(new BashPathResolutionSeed(
                                    elementList.Count,
                                    valueArgumentIndex,
                                    inlineResolverValue,
                                    ShellResolutionConsumer.BashArgument,
                                    rawValuePart));
                            }

                            elementList.Add(CreateElement(
                                source,
                                t,
                                ClauseElementRole.Argument,
                                precedingVerbTokenCount,
                                vKind,
                                isFlag: true,
                                isPath: vIsPath,
                                resolved: vResolved));

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
                            elementList.Add(CreateElement(
                                source,
                                t,
                                ClauseElementRole.Argument,
                                precedingVerbTokenCount,
                                ArgKind.Literal,
                                isFlag: true,
                                isPath: false,
                                resolved: null));

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
                        var valueForResolution = t.Value;
                        var valueIsOpaqueCommand = false;
                        bool treatAsPath;
                        if (pendingFlagForValue is not null && verbKeyForFlagValuePaths is not null)
                        {
                            // This is the value of a preceding flag — use the
                            // flag-value rule, NOT the positional-index rule.
                            valueIsOpaqueCommand = BashPerVerbRules.ValueOfFlagIsOpaqueCommand(
                                verbKeyForFlagValuePaths, pendingFlagForValue);
                            treatAsPath = !valueIsOpaqueCommand
                                && BashPerVerbRules.TryGetFlagValuePath(
                                    verbKeyForFlagValuePaths,
                                    pendingFlagForValue,
                                    t.Value,
                                    out valueForResolution);
                            pendingFlagForValue = null;
                        }
                        else
                        {
                            treatAsPath = BashPerVerbRules.IsPositionalPathArg(verb, positionalIndex, t.Value);
                            positionalIndex++;
                        }

                        var resolverValue = GetResolverValue(t, valueForResolution);
                        var (kind, resolved, isPath) = valueIsOpaqueCommand
                            ? (ArgKind.DynamicSkip, null, false)
                            : BashResolver.Resolve(
                                resolverValue,
                                treatAsPath,
                                options,
                                workingDirectoryUnknown,
                                ShellResolutionConsumer.BashArgument);
                        var argumentIndex = argList.Count;
                        argList.Add(new Arg
                        {
                            Raw = sourceRaw,
                            Resolved = resolved,
                            Kind = kind,
                            IsPath = isPath,
                        });
                        if (treatAsPath &&
                            (resolved is not null ||
                             CanResolveWithKnownCwd(
                                 resolverValue,
                                 options,
                                 ShellResolutionConsumer.BashArgument)))
                        {
                            pathResolutionList.Add(new BashPathResolutionSeed(
                                elementList.Count,
                                argumentIndex,
                                resolverValue,
                                ShellResolutionConsumer.BashArgument,
                                sourceRaw));
                        }

                        elementList.Add(CreateElement(
                            source,
                            t,
                            ClauseElementRole.Argument,
                            precedingVerbTokenCount,
                            kind,
                            isFlag: false,
                            isPath: isPath,
                            resolved: resolved));

                        break;
                    }

                case BashTokenKind.QuotedString:
                    {
                        var sourceRaw = SourceSlice(source, t);

                        // Quoted strings never act as flags (a leading dash in
                        // a quoted string is the user's signal "literal"). They
                        // still classify as positional path / non-path through
                        // the per-verb rule + resolver.
                        var valueForResolution = t.Value;
                        var valueIsOpaqueCommand = false;
                        bool treatAsPath;
                        if (pendingFlagForValue is not null && verbKeyForFlagValuePaths is not null)
                        {
                            valueIsOpaqueCommand = BashPerVerbRules.ValueOfFlagIsOpaqueCommand(
                                verbKeyForFlagValuePaths, pendingFlagForValue);
                            treatAsPath = !valueIsOpaqueCommand
                                && BashPerVerbRules.TryGetFlagValuePath(
                                    verbKeyForFlagValuePaths,
                                    pendingFlagForValue,
                                    t.Value,
                                    out valueForResolution);
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
                        var resolverValue = GetResolverValue(t, valueForResolution);
                        var (kind, resolved, isPath) = valueIsOpaqueCommand
                            ? (ArgKind.DynamicSkip, null, false)
                            : BashResolver.Resolve(
                                resolverValue,
                                treatAsPath,
                                options,
                                workingDirectoryUnknown,
                                ShellResolutionConsumer.BashArgument);
                        var argumentIndex = argList.Count;
                        argList.Add(new Arg
                        {
                            Raw = sourceRaw,
                            Resolved = resolved,
                            Kind = kind,
                            IsPath = isPath,
                        });
                        if (treatAsPath &&
                            (resolved is not null ||
                             CanResolveWithKnownCwd(
                                 resolverValue,
                                 options,
                                 ShellResolutionConsumer.BashArgument)))
                        {
                            pathResolutionList.Add(new BashPathResolutionSeed(
                                elementList.Count,
                                argumentIndex,
                                resolverValue,
                                ShellResolutionConsumer.BashArgument,
                                sourceRaw));
                        }

                        elementList.Add(CreateElement(
                            source,
                            t,
                            ClauseElementRole.Argument,
                            precedingVerbTokenCount,
                            kind,
                            isFlag: false,
                            isPath: isPath,
                            resolved: resolved));
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
                        elementList.Add(CreateElement(
                            source,
                            t,
                            ClauseElementRole.Argument,
                            precedingVerbTokenCount,
                            ArgKind.DynamicSkip,
                            isFlag: false,
                            isPath: false,
                            resolved: null));
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
        elements = elementList;
        pathResolutions = pathResolutionList;
        error = null;
    }

    private static void BuildRedirect(
        RedirectDirection direction,
        BashToken redirectOperator,
        BashToken target,
        string source,
        BashParserOptions options,
        List<Redirect> redirectList,
        bool workingDirectoryUnknown,
        int precedingVerbTokenCount,
        out ClauseElement element,
        out ShellValue? pathResolverValue)
    {
        pathResolverValue = null;
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
            element = CreateRedirectElement(
                source,
                redirectOperator,
                target,
                precedingVerbTokenCount,
                ArgKind.DynamicSkip,
                isPath: false,
                resolved: null);
            return;
        }

        if (target.Kind == BashTokenKind.Word
            && string.Equals(SourceSlice(source, target), target.Value, StringComparison.Ordinal)
            && IsFdDupTarget(target.Value))
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
            element = CreateRedirectElement(
                source,
                redirectOperator,
                target,
                precedingVerbTokenCount,
                ArgKind.DynamicSkip,
                isPath: false,
                resolved: null);
            return;
        }

        var raw = SourceSlice(source, target);

        // Redirect targets are always treated as paths. SPEC §8 +
        // locked interpretation #3: a glob target stays IsPath=true with
        // Kind=Glob; an env-var target becomes DynamicSkip; a literal
        // resolves against WorkingDirectory.
        var resolverValue = GetResolverValue(target, target.Value);
        var (kind, resolved, isPath) = BashResolver.Resolve(
            resolverValue,
            treatAsPath: true,
            options,
            workingDirectoryUnknown,
            ShellResolutionConsumer.BashRedirect);
        pathResolverValue = resolverValue;

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
        element = CreateRedirectElement(
            source,
            redirectOperator,
            target,
            precedingVerbTokenCount,
            kind,
            isPath: isPath,
            resolved: kind == ArgKind.DynamicSkip ? null : resolved);
    }

    private static bool CanResolveWithKnownCwd(
        ShellValue resolverValue,
        BashParserOptions options,
        ShellResolutionConsumer consumer) =>
        BashResolver.Resolve(
            resolverValue,
            treatAsPath: true,
            options,
            workingDirectoryUnknown: false,
            consumer).Resolved is not null;

    private static ClauseElement CreateElement(
        string source,
        BashToken token,
        ClauseElementRole role,
        int precedingVerbTokenCount,
        ArgKind kind,
        bool isFlag,
        bool isPath,
        string? resolved) => new()
        {
            Raw = SourceSlice(source, token),
            Value = token.Value,
            Role = role,
            SourceStart = token.SourceStart,
            SourceLength = token.SourceLength,
            PrecedingVerbElementCount = precedingVerbTokenCount,
            Kind = kind,
            IsFlag = isFlag,
            IsPath = isPath,
            Resolved = resolved,
        };

    private static ClauseElement CreateRedirectElement(
        string source,
        BashToken redirectOperator,
        BashToken target,
        int precedingVerbTokenCount,
        ArgKind kind,
        bool isPath,
        string? resolved)
    {
        var sourceStart = redirectOperator.SourceStart;
        var sourceEnd = target.SourceStart + target.SourceLength;
        return new ClauseElement
        {
            Raw = source.Substring(sourceStart, sourceEnd - sourceStart),
            Value = target.Value,
            Role = ClauseElementRole.Redirect,
            SourceStart = sourceStart,
            SourceLength = sourceEnd - sourceStart,
            PrecedingVerbElementCount = precedingVerbTokenCount,
            Kind = kind,
            IsFlag = false,
            IsPath = isPath,
            Resolved = resolved,
        };
    }

    private static ShellValue GetResolverValue(BashToken token, string logicalValue)
    {
        var value = token.ResolverValue
            ?? ShellValue.Literal(token.Value, token.SourceStart, token.SourceLength);
        return GetResolverValue(value, logicalValue);
    }

    private static ShellValue GetResolverValue(ShellValue value, string logicalValue)
    {
        if (string.Equals(value.Decoded, logicalValue, StringComparison.Ordinal))
        {
            return value;
        }

        var prefixLength = value.Decoded.Length - logicalValue.Length;
        if (prefixLength >= 0
            && value.Decoded.EndsWith(logicalValue, StringComparison.Ordinal))
        {
            return value.Slice(prefixLength);
        }

        return ShellValue.Opaque(logicalValue, ShellOpaqueCause.Unsupported);
    }

    private static ClauseElement CreateCombinedElement(
        NativeArgumentFragmentClassification classification,
        int precedingVerbTokenCount,
        ArgKind kind,
        bool isFlag,
        bool isPath,
        string? resolved) => new()
        {
            Raw = classification.Raw,
            Value = classification.DecodedArgument,
            Role = ClauseElementRole.Argument,
            SourceStart = classification.SourceStart,
            SourceLength = classification.SourceLength,
            PrecedingVerbElementCount = precedingVerbTokenCount,
            Kind = kind,
            IsFlag = isFlag,
            IsPath = isPath,
            Resolved = resolved,
        };

    private static bool TrySplitInlineFlag(
        BashToken token, out string flagPart, out string valuePart)
    {
        if (NativeFlagSyntax.TrySplitEqualsFlag(
            token.Value, out flagPart, out valuePart))
        {
            return true;
        }

        if (!NativeFlagSyntax.TrySplitEqualsPrefix(
            token.Value, out flagPart, out valuePart))
        {
            return false;
        }

        var equals = token.Value.IndexOf('=');
        return token.ResolverValue is not null
            && token.ResolverValue.Slice(equals + 1).Fragments.Count > 0;
    }

    private static bool IsFlag(string raw) =>
        raw.Length > 0 && raw[0] == '-';

    private static bool IsAdjacent(BashToken first, BashToken second) =>
        first.SourceStart + first.SourceLength == second.SourceStart;

    private static bool IsNativeArgumentFragment(BashToken token) =>
        token.Kind is BashTokenKind.Word
            or BashTokenKind.QuotedString
            or BashTokenKind.OpaqueSubstitution;

    private static bool IsInlineNativeArgumentPrefix(BashToken token) =>
        token.Kind == BashTokenKind.Word
        && NativeFlagSyntax.TrySplitEqualsPrefix(token.Value, out _, out _);

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
