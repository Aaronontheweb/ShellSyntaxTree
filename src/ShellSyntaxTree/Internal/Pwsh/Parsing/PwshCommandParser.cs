// -----------------------------------------------------------------------
// <copyright file="PwshCommandParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal.Bash.Verbs;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using ShellSyntaxTree.Internal.Pwsh.Verbs;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

/// <summary>
/// Translates a <see cref="PwshLexer"/> token stream into the public
/// <see cref="ParsedCommand"/> AST. Implements SPEC.POWERSHELL.md §4–§11:
/// pipeline / statement splitting, verb-chain extraction, the §6.5
/// parameter-binding model, the resolver, <c>Set-Location</c> propagation,
/// <c>pwsh -Command</c> / <c>-EncodedCommand</c> recursion, and the
/// safe-fail anomaly contract.
/// </summary>
internal static class PwshCommandParser
{
    /// <summary>Maximum <c>pwsh -Command</c> recursion depth (§10 / §11).</summary>
    private const int MaxRecursionDepth = 5;

    /// <summary>Input cap — 64 KiB of UTF-16 chars (§11 item 10).</summary>
    private const int InputCapChars = 64 * 1024;

    internal static ParsedCommand Parse(string source, PwshParserOptions options)
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
            source, options, recursionDepth: 0, markWrapped: false,
            sharedLocation: null);
    }

    private static ParsedCommand ParseInternal(
        string source, PwshParserOptions options, int recursionDepth, bool markWrapped,
        PwshSetLocationContext? sharedLocation)
    {
        // §11 item 10: the size cap is checked before lexing, on the
        // top-level input and on every decoded payload.
        if (source.Length > InputCapChars)
        {
            return Unparseable(source, $"input exceeds the 64 KiB parser cap ({source.Length} chars)");
        }

        if (source.Length == 0)
        {
            return new ParsedCommand { Source = source, Clauses = Array.Empty<Clause>() };
        }

        var tokens = PwshLexer.Tokenize(source);

        // Lift the first lexer sentinel (unbalanced quote / region, etc.).
        foreach (var t in tokens)
        {
            if (t.Kind == PwshTokenKind.UnparseableSentinel)
            {
                return Unparseable(source, t.UnparseableReason);
            }
        }

        var significant = FilterSignificant(tokens);
        if (significant.Count == 0)
        {
            // Comment-only / whitespace-only input.
            return new ParsedCommand { Source = source, Clauses = Array.Empty<Clause>() };
        }

        if (TryDetectAnomaly(significant, out var anomalyReason))
        {
            return Unparseable(source, anomalyReason);
        }

        var segments = SplitIntoSegments(significant, source, out var splitError);
        if (splitError is not null)
        {
            return Unparseable(source, splitError);
        }

        var clauses = new List<Clause>(segments.Count);
        var attribution = sharedLocation ?? new PwshSetLocationContext();

        foreach (var segment in segments)
        {
            if (segment.Tokens.Count == 0)
            {
                continue;
            }

            // Effective resolver options reflect Set-Location attribution.
            var effectiveOptions = options;
            var workingDirectoryUnknown = false;
            if (attribution.HasAttribution && !attribution.IsDynamic)
            {
                effectiveOptions = new PwshParserOptions
                {
                    HomeDirectory = options.HomeDirectory,
                    WorkingDirectory = attribution.ResolvedCwd,
                };
            }
            else if (attribution.IsDynamic)
            {
                workingDirectoryUnknown = true;
            }

            var built = BuildSegment(
                segment, source, options, effectiveOptions, workingDirectoryUnknown,
                recursionDepth, markWrapped, attribution);
            if (built.Error is not null)
            {
                return Unparseable(source, built.Error);
            }

            for (var k = 0; k < built.Clauses.Count; k++)
            {
                var clause = built.Clauses[k];

                // Expanded command-string clauses already carry wrapper and
                // attribution state. Child pwsh uses an isolated context;
                // Invoke-Expression shares and updates this one.
                if (!built.IsRecursion)
                {
                    clause = AttachAttributionArg(clause, attribution);
                }

                clauses.Add(clause);
            }

            // A directly built Set-Location clause updates the attributed cwd
            // for clauses that follow it (§9). Expanded command strings have
            // already updated the appropriate isolated or shared context.
            if (!built.IsRecursion && built.Clauses.Count == 1)
            {
                UpdateAttribution(built.Clauses[0], options, attribution);
            }
        }

        return new ParsedCommand { Source = source, Clauses = clauses };
    }

    private static ParsedCommand Unparseable(string source, string? reason) => new()
    {
        Source = source,
        Clauses = Array.Empty<Clause>(),
        IsUnparseable = true,
        UnparseableReason = reason,
    };

    // ---------------------------------------------------------------- filtering

    private static List<PwshToken> FilterSignificant(IReadOnlyList<PwshToken> tokens)
    {
        var filtered = new List<PwshToken>(tokens.Count);
        foreach (var t in tokens)
        {
            if ((t.Kind == PwshTokenKind.Whitespace && !t.IsStatementSeparator)
                || t.Kind == PwshTokenKind.Continuation
                || t.Kind == PwshTokenKind.Comment)
            {
                continue;
            }

            filtered.Add(t);
        }

        return filtered;
    }

    // ---------------------------------------------------------------- anomalies

    private static bool TryDetectAnomaly(IReadOnlyList<PwshToken> tokens, out string? reason)
    {
        // Item 3: control-flow / definition / block keyword at a verb slot.
        if (TryDetectKeywordAnomaly(tokens, out reason))
        {
            return true;
        }

        if (TryDetectUnsupportedInvocationShape(tokens, out reason))
        {
            return true;
        }

        // Item 4: a trailing '&' background-job operator.
        if (TryDetectTrailingAmp(tokens, out reason))
        {
            return true;
        }

        // Item 5: an assignment or bare type-literal statement.
        if (TryDetectAssignmentOrTypeLiteral(tokens, out reason))
        {
            return true;
        }

        reason = null;
        return false;
    }

    private static bool TryDetectUnsupportedInvocationShape(
        IReadOnlyList<PwshToken> tokens, out string? reason)
    {
        var verbSlot = true;
        foreach (var token in tokens)
        {
            if (token.Kind == PwshTokenKind.Whitespace)
            {
                verbSlot = true;
                continue;
            }

            if (token.Kind == PwshTokenKind.Operator)
            {
                verbSlot = token.OperatorText is "&&" or "||" or ";" or "|" or "(" or "&";
                continue;
            }

            if (verbSlot && token.Kind == PwshTokenKind.Word)
            {
                if (token.Value == ".")
                {
                    reason = "the dot-source invocation operator is not supported in v0.2";
                    return true;
                }

                if (IsUnsupportedModuleQualifiedCmdlet(token.Value))
                {
                    reason = $"module-qualified cmdlet '{token.Value}' is not supported in v0.2";
                    return true;
                }
            }

            if (verbSlot && token.Kind == PwshTokenKind.QuotedString
                && IsUnsupportedModuleQualifiedCmdlet(token.Value))
            {
                reason = $"module-qualified cmdlet '{token.Value}' is not supported in v0.2";
                return true;
            }

            verbSlot = false;
        }

        reason = null;
        return false;
    }

    private static bool IsUnsupportedModuleQualifiedCmdlet(string command)
    {
        if (string.Equals(
            command,
            "Microsoft.PowerShell.Utility\\Invoke-Expression",
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = command.LastIndexOf('\\');
        return separator > 0 && separator + 1 < command.Length
            && PwshApprovedVerbs.IsCmdletShaped(command.Substring(separator + 1));
    }

    private static bool TryDetectKeywordAnomaly(IReadOnlyList<PwshToken> tokens, out string? reason)
    {
        var verbSlot = true;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == PwshTokenKind.Whitespace)
            {
                verbSlot = true;
                continue;
            }

            if (t.Kind == PwshTokenKind.Operator)
            {
                verbSlot = t.OperatorText is "&&" or "||" or ";" or "|" or "(" or "&";
                continue;
            }

            if (verbSlot && t.Kind == PwshTokenKind.Word
                && PwshVerbs.ControlFlowKeywords.Contains(t.Value))
            {
                if (string.Equals(t.Value, "foreach", StringComparison.OrdinalIgnoreCase))
                {
                    // `foreach (` is the loop keyword; `foreach {` is the
                    // ForEach-Object alias (§6.3 collision rule).
                    if (NextSignificantIsOpenParen(tokens, i))
                    {
                        reason = "control-flow keyword 'foreach' is not supported in v0.2";
                        return true;
                    }
                }
                else
                {
                    reason = $"control-flow / definition keyword '{t.Value}' is not supported in v0.2";
                    return true;
                }
            }

            verbSlot = false;
        }

        reason = null;
        return false;
    }

    private static bool TryDetectTrailingAmp(IReadOnlyList<PwshToken> tokens, out string? reason)
    {
        var verbSlot = true;
        foreach (var t in tokens)
        {
            if (t.Kind == PwshTokenKind.Whitespace)
            {
                verbSlot = true;
                continue;
            }

            if (t.Kind == PwshTokenKind.Operator)
            {
                if (t.OperatorText == "&" && !verbSlot)
                {
                    // `& cmd` is the call operator (verb slot); a `&` after a
                    // command is a background-job operator (§11 item 6).
                    reason = "trailing '&' background-job operator is not supported in v0.2";
                    return true;
                }

                verbSlot = t.OperatorText is "&&" or "||" or ";" or "|" or "(" or "&";
                continue;
            }

            verbSlot = false;
        }

        reason = null;
        return false;
    }

    private static bool TryDetectAssignmentOrTypeLiteral(
        IReadOnlyList<PwshToken> tokens, out string? reason)
    {
        var verbSlot = true;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == PwshTokenKind.Whitespace)
            {
                verbSlot = true;
                continue;
            }

            if (t.Kind == PwshTokenKind.Operator)
            {
                verbSlot = t.OperatorText is "&&" or "||" or ";" or "|" or "(" or "&";
                continue;
            }

            if (verbSlot && t.Kind == PwshTokenKind.Word)
            {
                var v = t.Value;
                if (v.Length > 0 && v[0] == '[')
                {
                    reason = "a bare type-literal / .NET method-call statement is not supported in v0.2";
                    return true;
                }

                if (v.Length > 0 && v[0] == '$'
                    && (v.IndexOf('=') > 0 || NextIsAssignmentOperator(tokens, i)))
                {
                    reason = "an assignment statement is not supported in v0.2";
                    return true;
                }
            }

            verbSlot = false;
        }

        reason = null;
        return false;
    }

    private static bool NextSignificantIsOpenParen(IReadOnlyList<PwshToken> tokens, int i)
    {
        for (var j = i + 1; j < tokens.Count; j++)
        {
            if (tokens[j].Kind == PwshTokenKind.Whitespace)
            {
                continue;
            }

            return tokens[j].Kind == PwshTokenKind.Operator && tokens[j].OperatorText == "(";
        }

        return false;
    }

    private static bool NextIsAssignmentOperator(IReadOnlyList<PwshToken> tokens, int i)
    {
        for (var j = i + 1; j < tokens.Count; j++)
        {
            if (tokens[j].Kind == PwshTokenKind.Whitespace)
            {
                continue;
            }

            if (tokens[j].Kind != PwshTokenKind.Word)
            {
                return false;
            }

            var v = tokens[j].Value;
            return v is "=" or "+=" or "-=" or "*=" or "/=" or "%=";
        }

        return false;
    }

    // ---------------------------------------------------------------- segments

    private sealed class Segment
    {
        public CompoundOperator PrecedingOperator { get; init; }

        public List<PwshToken> Tokens { get; } = new();

        public int Depth { get; init; }
    }

    private static List<Segment> SplitIntoSegments(
        IReadOnlyList<PwshToken> tokens, string source, out string? error)
    {
        var segments = new List<Segment>();
        var depth = 0;
        var expressionArgumentDepth = 0;

        Segment current = new() { PrecedingOperator = CompoundOperator.None, Depth = 0 };

        void Flush()
        {
            if (current.Tokens.Count > 0)
            {
                segments.Add(current);
            }
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (expressionArgumentDepth > 0)
            {
                current.Tokens.Add(t);
                if (t.Kind == PwshTokenKind.Operator)
                {
                    if (t.OperatorText == "(")
                    {
                        expressionArgumentDepth++;
                    }
                    else if (t.OperatorText == ")")
                    {
                        expressionArgumentDepth--;
                    }
                }

                continue;
            }

            if (t.Kind == PwshTokenKind.Whitespace)
            {
                // A retained whitespace token is a newline statement
                // separator. A separator on an empty pending segment
                // collapses (blank lines, a newline after `|` / `&&` / `||`).
                if (current.Tokens.Count == 0)
                {
                    continue;
                }

                segments.Add(current);
                current = new Segment { PrecedingOperator = CompoundOperator.Sequence, Depth = depth };
                continue;
            }

            if (t.Kind == PwshTokenKind.Operator)
            {
                var op = t.OperatorText;
                if (op == "(")
                {
                    if (StartsWithInvokeExpression(current.Tokens))
                    {
                        current.Tokens.Add(t);
                        expressionArgumentDepth = 1;
                        continue;
                    }

                    Flush();
                    depth++;
                    current = new Segment { PrecedingOperator = CompoundOperator.None, Depth = depth };
                    continue;
                }

                if (op == ")")
                {
                    if (depth == 0)
                    {
                        error = $"unbalanced ')' at position {t.SourceStart}";
                        return segments;
                    }

                    depth--;
                    Flush();
                    current = new Segment { PrecedingOperator = CompoundOperator.None, Depth = depth };
                    continue;
                }

                if (op is "&&" or "||" or ";" or "|")
                {
                    if (current.Tokens.Count == 0 && current.PrecedingOperator != CompoundOperator.None)
                    {
                        error = $"unexpected operator '{op}' at position {t.SourceStart}";
                        return segments;
                    }

                    Flush();
                    current = new Segment { PrecedingOperator = MapOperator(op), Depth = depth };
                    continue;
                }

                // Redirect operators and a bare '&' stay inside the segment.
                current.Tokens.Add(t);
                continue;
            }

            current.Tokens.Add(t);
        }

        if (depth != 0 || expressionArgumentDepth != 0)
        {
            error = $"unbalanced '(' grouping at position {source.Length}";
            return segments;
        }

        Flush();
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

    private static bool StartsWithInvokeExpression(List<PwshToken> tokens)
    {
        var verbIndex = tokens.Count > 0
            && tokens[0].Kind == PwshTokenKind.Operator
            && tokens[0].OperatorText == "&"
                ? 1
                : 0;
        if (verbIndex >= tokens.Count
            || tokens[verbIndex].Kind is not PwshTokenKind.Word
                and not PwshTokenKind.QuotedString)
        {
            return false;
        }

        return IsInvokeExpressionName(tokens[verbIndex].Value);
    }

    // ---------------------------------------------------------------- segment build

    private readonly struct BuildResult
    {
        public IReadOnlyList<Clause> Clauses { get; }

        public string? Error { get; }

        public bool IsRecursion { get; }

        private BuildResult(IReadOnlyList<Clause> clauses, string? error, bool isRecursion)
        {
            Clauses = clauses;
            Error = error;
            IsRecursion = isRecursion;
        }

        public static BuildResult Ok(Clause c) => new(new[] { c }, null, false);

        public static BuildResult Recursion(IReadOnlyList<Clause> clauses) =>
            new(clauses, null, true);

        public static BuildResult Fail(string? reason) =>
            new(Array.Empty<Clause>(), reason ?? "inner parse failed", false);
    }

    private static BuildResult BuildSegment(
        Segment segment, string source, PwshParserOptions baseOptions,
        PwshParserOptions effectiveOptions, bool workingDirectoryUnknown,
        int recursionDepth, bool markWrapped, PwshSetLocationContext attribution)
    {
        var body = segment.Tokens;
        var start = 0;
        if (body.Count > 0 && body[0].Kind == PwshTokenKind.Operator
            && body[0].OperatorText == "&")
        {
            // Leading call operator `& cmd` — the command follows.
            start = 1;
        }

        if (start >= body.Count)
        {
            // A lone call operator (`& ( ... )` — the group followed in a
            // separate segment). Emit a dynamic, verb-less clause.
            var dynamicClause = AttachAttributionArg(new Clause
            {
                Operator = segment.PrecedingOperator,
                Verb = new VerbChain { IsDynamic = true },
                IsSubshell = segment.Depth > 0,
                IsCommandStringWrapped = markWrapped,
            }, attribution);
            attribution.SetDynamic();
            return BuildResult.Recursion(new[] { dynamicClause });
        }

        if (start == 0 && body[start].Kind == PwshTokenKind.QuotedString)
        {
            return BuildResult.Fail(
                "a quoted expression at command position is not supported in v0.2");
        }

        // Classify the command.
        var classified = ClassifyVerb(body, start);
        if (TryHandleInvokeExpression(
            body, start, classified, source, baseOptions, recursionDepth,
            segment, markWrapped, attribution, out var expressionResult))
        {
            return expressionResult;
        }

        if (classified.Kind == PwshCommandKind.PwshInvocation)
        {
            var recursion = TryRecurseIntoPwsh(
                body, start, classified, source, baseOptions, recursionDepth,
                segment, markWrapped, out var recursionResult);
            if (recursion)
            {
                return recursionResult;
            }
        }

        // Extract args + redirects. Iteration begins at `start` so a leading
        // call operator `&` is not mistaken for a trailing background `&`.
        var argResult = ExtractArgsAndRedirects(
            body, start, classified, source, effectiveOptions, workingDirectoryUnknown);
        if (argResult.Error is not null)
        {
            return BuildResult.Fail(argResult.Error);
        }

        var verb = new VerbChain
        {
            Tokens = classified.VerbTokens,
            CanonicalVerb = classified.CanonicalVerb,
            IsDynamic = classified.IsDynamic,
        };

        var clause = new Clause
        {
            Operator = segment.PrecedingOperator,
            Verb = verb,
            Args = argResult.Args,
            Redirects = argResult.Redirects,
            Elements = argResult.Elements,
            IsSubshell = segment.Depth > 0,
            IsCommandStringWrapped = markWrapped,
        };

        if (classified.IsDynamic)
        {
            clause = AttachAttributionArg(clause, attribution);
            attribution.SetDynamic();
            return BuildResult.Recursion(new[] { clause });
        }

        return BuildResult.Ok(clause);
    }

    // ---------------------------------------------------------------- verb chain

    private enum PwshCommandKind
    {
        Cmdlet,
        Alias,
        NativeCommand,
        PwshInvocation,
        DynamicCommand,
        QuotedCommand,
        NoVerb,
    }

    private readonly struct ClassifiedVerb
    {
        public PwshCommandKind Kind { get; init; }

        public List<string> VerbTokens { get; init; }

        public string? CanonicalVerb { get; init; }

        public bool IsDynamic { get; init; }

        /// <summary>Body indices that are verb-chain tokens (skipped as args).</summary>
        public HashSet<int> VerbPositions { get; init; }
    }

    private static ClassifiedVerb ClassifyVerb(List<PwshToken> body, int start)
    {
        var head = body[start];
        var verbPositions = new HashSet<int> { start };

        // A clause that starts with a redirect operator, a flag, or a
        // stop-parsing token has no verb.
        if (head.Kind is PwshTokenKind.Operator or PwshTokenKind.Parameter
            or PwshTokenKind.StopParsing)
        {
            return new ClassifiedVerb
            {
                Kind = PwshCommandKind.NoVerb,
                VerbTokens = new List<string>(),
                VerbPositions = new HashSet<int>(),
            };
        }

        // A script block, subexpression, splat, or $-variable at command
        // position is a dynamic command name (§3).
        var isVariableWord = head.Kind == PwshTokenKind.Word
            && head.Value.Length > 0 && head.Value[0] == '$';
        if (isVariableWord || head.HasInterpolation
            || head.Kind is PwshTokenKind.ScriptBlock or PwshTokenKind.Subexpression
                or PwshTokenKind.Splat)
        {
            return new ClassifiedVerb
            {
                Kind = PwshCommandKind.DynamicCommand,
                VerbTokens = new List<string> { head.Value },
                IsDynamic = true,
                VerbPositions = verbPositions,
            };
        }

        if (head.Kind == PwshTokenKind.QuotedString)
        {
            return new ClassifiedVerb
            {
                Kind = PwshCommandKind.QuotedCommand,
                VerbTokens = new List<string> { head.Value },
                VerbPositions = verbPositions,
            };
        }

        // head.Kind == Word.
        var word = head.Value;

        if (PwshApprovedVerbs.IsCmdletShaped(word))
        {
            return new ClassifiedVerb
            {
                Kind = PwshCommandKind.Cmdlet,
                VerbTokens = new List<string> { word },
                VerbPositions = verbPositions,
            };
        }

        var alias = PwshAliases.Resolve(word);
        if (alias is not null)
        {
            return new ClassifiedVerb
            {
                Kind = PwshCommandKind.Alias,
                VerbTokens = new List<string> { word },
                CanonicalVerb = alias,
                VerbPositions = verbPositions,
            };
        }

        if (PwshVerbs.IsPwshHost(word))
        {
            return new ClassifiedVerb
            {
                Kind = PwshCommandKind.PwshInvocation,
                VerbTokens = new List<string> { word },
                VerbPositions = verbPositions,
            };
        }

        // Native command — the bash greedy verb-chain walk (§6.2). A native
        // file utility (xcopy, robocopy, ...) gets a 1-token chain so its
        // arguments classify as paths.
        var verbTokens = new List<string> { word };
        if (!PwshVerbs.FileVerbs.Contains(word))
        {
            BashVerbs.FlagsWithValue.TryGetValue(word, out var flagsForVerb);
            var i = start + 1;
            while (i < body.Count)
            {
                var t = body[i];
                if (t.Kind == PwshTokenKind.Parameter)
                {
                    if (NativeFlagSyntax.TrySplitEqualsFlag(t.Value, out _, out _))
                    {
                        // Match Bash: an inline --flag=value is surfaced by
                        // arg extraction, and terminates the greedy walk.
                        break;
                    }

                    if (flagsForVerb is null || !flagsForVerb.Contains(t.Value))
                    {
                        break;
                    }

                    if (i + 1 >= body.Count
                        || (body[i + 1].Kind != PwshTokenKind.Word
                            && body[i + 1].Kind != PwshTokenKind.QuotedString))
                    {
                        break;
                    }

                    // Flag-with-value pair — transparently consumed by the
                    // walk but still surfaced as args.
                    i += 2;
                    continue;
                }

                // Keep native-command boundaries equal across both shells.
                if (t.Kind != PwshTokenKind.Word
                    || BashResolver.LooksLikePath(t.Value)
                    || !PwshVerbs.IsNativeVerbLikeToken(t.Value))
                {
                    break;
                }

                verbTokens.Add(t.Value);
                verbPositions.Add(i);
                i++;
            }
        }

        return new ClassifiedVerb
        {
            Kind = PwshCommandKind.NativeCommand,
            VerbTokens = verbTokens,
            VerbPositions = verbPositions,
        };
    }

    // ---------------------------------------------------------------- args

    private readonly struct ArgResult
    {
        public IReadOnlyList<Arg> Args { get; }

        public IReadOnlyList<Redirect> Redirects { get; }

        public IReadOnlyList<ClauseElement> Elements { get; }

        public string? Error { get; }

        public ArgResult(
            IReadOnlyList<Arg> args,
            IReadOnlyList<Redirect> redirects,
            IReadOnlyList<ClauseElement> elements,
            string? error)
        {
            Args = args;
            Redirects = redirects;
            Elements = elements;
            Error = error;
        }
    }

    private static ArgResult ExtractArgsAndRedirects(
        List<PwshToken> body, int scanStart, ClassifiedVerb verb, string source,
        PwshParserOptions options, bool workingDirectoryUnknown)
    {
        var args = new List<Arg>();
        var redirects = new List<Redirect>();
        var elements = new List<ClauseElement>();
        var positionalIndex = 0;
        var precedingVerbTokenCount = 0;
        string? pendingValueParam = null;          // cmdlet/alias §6.5 value-binding
        string? pendingNativeFlag = null;          // native flag-with-value
        var cmdletStyle = verb.Kind is PwshCommandKind.Cmdlet or PwshCommandKind.Alias
            or PwshCommandKind.PwshInvocation;
        var canonical = verb.CanonicalVerb ?? (verb.VerbTokens.Count > 0 ? verb.VerbTokens[0] : null);
        var isFileVerb = canonical is not null && PwshVerbs.FileVerbs.Contains(canonical);
        var nativeVerbChain = new VerbChain { Tokens = verb.VerbTokens };

        for (var i = scanStart; i < body.Count; i++)
        {
            if (verb.VerbPositions.Contains(i))
            {
                var verbToken = body[i];
                elements.Add(CreateElement(
                    source,
                    verbToken,
                    ClauseElementRole.Verb,
                    precedingVerbTokenCount,
                    verb.IsDynamic ? ArgKind.DynamicSkip : ArgKind.Literal,
                    isFlag: false,
                    isPath: false,
                    resolved: null));
                precedingVerbTokenCount++;
                continue;
            }

            var t = body[i];

            // ---- redirect operators ----
            if (t.Kind == PwshTokenKind.Operator)
            {
                if (t.OperatorText == "&")
                {
                    // A non-leading '&' is a trailing background-job operator;
                    // the anomaly detector already lifts this, but guard here.
                    return new ArgResult(args, redirects, elements,
                        "trailing '&' background-job operator is not supported in v0.2");
                }

                pendingValueParam = null;
                pendingNativeFlag = null;
                var consumed = BuildRedirect(
                    body,
                    i,
                    source,
                    options,
                    workingDirectoryUnknown,
                    redirects,
                    precedingVerbTokenCount,
                    out var redirectElement,
                    out var redirectError);
                if (redirectError is not null)
                {
                    return new ArgResult(args, redirects, elements, redirectError);
                }

                elements.Add(redirectElement!);

                i += consumed - 1;
                continue;
            }

            // ---- parameter tokens ----
            if (t.Kind == PwshTokenKind.Parameter)
            {
                pendingValueParam = null;
                pendingNativeFlag = null;

                var raw = t.Value;

                if (cmdletStyle)
                {
                    var colon = raw.IndexOf(':');
                    var paramName = colon > 0 ? raw.Substring(0, colon) : raw;
                    var colonValue = colon > 0 ? raw.Substring(colon + 1) : null;

                    args.Add(new Arg { Raw = paramName, Kind = ArgKind.Literal, IsPath = false });

                    if (colonValue is not null && paramName.IndexOf('=') >= 0)
                    {
                        // `-Path=C:\Windows` splits here into name `-Path=C`
                        // and value `\Windows` — exactly how PowerShell
                        // tokenizes it, and a name that can never bind. The
                        // value's role is unknowable, so don't hand the gate
                        // a confident literal (§6.5.3 / SPEC.md §1).
                        args.Add(new Arg
                        {
                            Raw = colonValue,
                            Kind = ArgKind.DynamicSkip,
                            IsPath = false,
                        });
                    }
                    else if (colonValue is not null)
                    {
                        // Colon form always binds (§6.5.3 rule 1).
                        var valueIsPath = PwshPerVerbRules.ParameterValueIsPath(canonical, paramName);
                        args.Add(ResolveValue(colonValue, valueIsPath, options, workingDirectoryUnknown, false));
                    }
                    else if (PwshBindingTables.ResolveBinding(canonical, paramName) == PwshBinding.Value)
                    {
                        pendingValueParam = paramName;
                    }
                }
                else
                {
                    var verbKey = verb.VerbTokens.Count > 0 ? verb.VerbTokens[0] : string.Empty;

                    // Native --flag=value follows Bash exactly: surface the
                    // flag and value separately, and classify a curated
                    // flag's value through the shared per-verb table.
                    if (NativeFlagSyntax.TrySplitEqualsFlag(raw, out var flagPart, out var valuePart))
                    {
                        args.Add(new Arg { Raw = flagPart, Kind = ArgKind.Literal, IsPath = false });
                        var valueIsPath = BashPerVerbRules.ValueOfFlagIsPath(verbKey, flagPart);
                        args.Add(ResolveValue(
                            valuePart, valueIsPath, options, workingDirectoryUnknown, false));
                    }
                    else
                    {
                        // Every other option shape stays one arg. A colon in
                        // particular has no native binding meaning, so the
                        // tail is preserved rather than dropped.
                        args.Add(new Arg { Raw = raw, Kind = ArgKind.Literal, IsPath = false });
                    }

                    if (raw.IndexOf('=') < 0
                        && BashVerbs.FlagsWithValue.TryGetValue(verbKey, out var flags)
                        && flags.Contains(raw))
                    {
                        pendingNativeFlag = raw;
                    }
                }

                var parameterMetadata = args[args.Count - 1];
                elements.Add(CreateElement(
                    source,
                    t,
                    ClauseElementRole.Argument,
                    precedingVerbTokenCount,
                    parameterMetadata.Kind,
                    isFlag: true,
                    isPath: parameterMetadata.IsPath,
                    resolved: parameterMetadata.Resolved));

                continue;
            }

            // ---- opaque tokens (script block / subexpression / splat / --%) ----
            if (t.Kind is PwshTokenKind.ScriptBlock or PwshTokenKind.Subexpression
                or PwshTokenKind.Splat or PwshTokenKind.StopParsing)
            {
                args.Add(new Arg
                {
                    Raw = t.Value,
                    Kind = ArgKind.DynamicSkip,
                    IsPath = false,
                });
                elements.Add(CreateElement(
                    source,
                    t,
                    ClauseElementRole.Argument,
                    precedingVerbTokenCount,
                    ArgKind.DynamicSkip,
                    isFlag: false,
                    isPath: false,
                    resolved: null));

                if (pendingValueParam is not null || pendingNativeFlag is not null)
                {
                    pendingValueParam = null;
                    pendingNativeFlag = null;
                }
                else
                {
                    positionalIndex++;
                }

                continue;
            }

            // ---- value tokens (Word / QuotedString) ----
            var isLiteralBytes = t.Kind == PwshTokenKind.QuotedString && t.IsSingleQuoted;
            var rawValue = SourceSlice(source, t);

            bool treatAsPath;
            if (pendingValueParam is not null)
            {
                treatAsPath = PwshPerVerbRules.ParameterValueIsPath(canonical, pendingValueParam);
                pendingValueParam = null;
            }
            else if (pendingNativeFlag is not null)
            {
                var verbKey = verb.VerbTokens.Count > 0 ? verb.VerbTokens[0] : string.Empty;
                treatAsPath = BashPerVerbRules.ValueOfFlagIsPath(verbKey, pendingNativeFlag);
                pendingNativeFlag = null;
            }
            else
            {
                treatAsPath = cmdletStyle
                    ? PwshPerVerbRules.IsPositionalPathArg(canonical, isFileVerb, positionalIndex, t.Value)
                    : BashPerVerbRules.IsPositionalPathArg(nativeVerbChain, positionalIndex, t.Value);
                positionalIndex++;
            }

            var resolvedArg = ResolveValueToken(
                rawValue, t.Value, treatAsPath, options, workingDirectoryUnknown, isLiteralBytes);
            args.Add(resolvedArg);
            elements.Add(CreateElement(
                source,
                t,
                ClauseElementRole.Argument,
                precedingVerbTokenCount,
                resolvedArg.Kind,
                isFlag: false,
                isPath: resolvedArg.IsPath,
                resolved: resolvedArg.Resolved));
        }

        return new ArgResult(args, redirects, elements, null);
    }

    private static Arg ResolveValueToken(
        string raw, string logicalValue, bool treatAsPath,
        PwshParserOptions options, bool workingDirectoryUnknown, bool isLiteralBytes)
    {
        // §8 comma-array: an unquoted top-level comma in a path slot marks
        // the whole token DynamicSkip — the v0.2.0 parser neither splits nor
        // resolves a comma-joined array path.
        if (treatAsPath && !isLiteralBytes && PwshResolver.LooksLikeCommaArray(logicalValue))
        {
            return new Arg { Raw = raw, Kind = ArgKind.DynamicSkip, IsPath = false };
        }

        var (kind, resolved, isPath) = PwshResolver.Resolve(
            logicalValue, treatAsPath, options, workingDirectoryUnknown, isLiteralBytes);
        return new Arg { Raw = raw, Resolved = resolved, Kind = kind, IsPath = isPath };
    }

    private static Arg ResolveValue(
        string logicalValue, bool treatAsPath,
        PwshParserOptions options, bool workingDirectoryUnknown, bool isLiteralBytes)
        => ResolveValueToken(logicalValue, logicalValue, treatAsPath, options, workingDirectoryUnknown, isLiteralBytes);

    // ---------------------------------------------------------------- redirects

    /// <summary>
    /// Build a redirect from the operator at <paramref name="opIndex"/> and,
    /// for non-merge forms, the following target token. Returns the number
    /// of body tokens consumed.
    /// </summary>
    private static int BuildRedirect(
        List<PwshToken> body, int opIndex, string source,
        PwshParserOptions options, bool workingDirectoryUnknown,
        List<Redirect> redirects, int precedingVerbTokenCount,
        out ClauseElement? element, out string? error)
    {
        element = null;
        error = null;
        var operatorToken = body[opIndex];
        var op = operatorToken.OperatorText ?? string.Empty;
        var direction = MapRedirect(op, out var isMerge, out var mergeTarget);

        if (isMerge)
        {
            redirects.Add(new Redirect
            {
                Direction = direction,
                Target = mergeTarget ?? op,
                IsDynamicSkip = true,
            });
            element = CreateRedirectElement(
                source,
                operatorToken,
                target: null,
                value: mergeTarget ?? op,
                precedingVerbTokenCount,
                ArgKind.DynamicSkip,
                isPath: false,
                resolved: null);
            return 1;
        }

        if (opIndex + 1 >= body.Count || body[opIndex + 1].Kind == PwshTokenKind.Operator)
        {
            error = $"redirect operator '{op}' is missing a target";
            return 1;
        }

        var target = body[opIndex + 1];
        if (target.Kind is PwshTokenKind.ScriptBlock or PwshTokenKind.Subexpression
            or PwshTokenKind.Splat or PwshTokenKind.StopParsing)
        {
            redirects.Add(new Redirect
            {
                Direction = direction,
                Target = target.Value,
                IsDynamicSkip = true,
            });
            element = CreateRedirectElement(
                source,
                operatorToken,
                target,
                target.Value,
                precedingVerbTokenCount,
                ArgKind.DynamicSkip,
                isPath: false,
                resolved: null);
            return 2;
        }

        // $null is the discard sink — not a file (§8).
        if (target.Kind == PwshTokenKind.Word
            && string.Equals(target.Value, "$null", StringComparison.OrdinalIgnoreCase))
        {
            redirects.Add(new Redirect
            {
                Direction = direction,
                Target = "$null",
                IsDynamicSkip = true,
            });
            element = CreateRedirectElement(
                source,
                operatorToken,
                target,
                target.Value,
                precedingVerbTokenCount,
                ArgKind.DynamicSkip,
                isPath: false,
                resolved: null);
            return 2;
        }

        var isLiteralBytes = target.Kind == PwshTokenKind.QuotedString && target.IsSingleQuoted;
        var raw = SourceSlice(source, target);
        var (kind, resolved, isPath) = PwshResolver.Resolve(
            target.Value, treatAsPath: true, options, workingDirectoryUnknown, isLiteralBytes);

        redirects.Add(new Redirect
        {
            Direction = direction,
            Target = kind == ArgKind.DynamicSkip ? raw : resolved ?? raw,
            IsDynamicSkip = kind == ArgKind.DynamicSkip,
        });
        element = CreateRedirectElement(
            source,
            operatorToken,
            target,
            target.Value,
            precedingVerbTokenCount,
            kind,
            isPath,
            kind == ArgKind.DynamicSkip ? null : resolved);
        return 2;
    }

    private static ClauseElement CreateElement(
        string source,
        PwshToken token,
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
        PwshToken operatorToken,
        PwshToken? target,
        string value,
        int precedingVerbTokenCount,
        ArgKind kind,
        bool isPath,
        string? resolved)
    {
        var sourceStart = operatorToken.SourceStart;
        var sourceEnd = target.HasValue
            ? target.Value.SourceStart + target.Value.SourceLength
            : operatorToken.SourceStart + operatorToken.SourceLength;
        return new ClauseElement
        {
            Raw = source.Substring(sourceStart, sourceEnd - sourceStart),
            Value = value,
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

    /// <summary>SPEC.POWERSHELL.md §8: map a PowerShell redirect operator
    /// onto the (lossy) <see cref="RedirectDirection"/> enum.</summary>
    private static RedirectDirection MapRedirect(string op, out bool isMerge, out string? mergeTarget)
    {
        isMerge = false;
        mergeTarget = null;

        if (op == "<")
        {
            return RedirectDirection.In;
        }

        var ampIndex = op.IndexOf('&');
        if (ampIndex >= 0)
        {
            // Stream merge N>&M.
            isMerge = true;
            mergeTarget = op.Substring(ampIndex);
            var streamChar = op.Length > 0 ? op[0] : '1';
            return streamChar == '2' ? RedirectDirection.ErrOut : RedirectDirection.Out;
        }

        // Optional leading stream prefix.
        var prefix = '\0';
        var rest = op;
        if (op.Length > 0 && (op[0] == '*' || (op[0] >= '1' && op[0] <= '6')))
        {
            prefix = op[0];
            rest = op.Substring(1);
        }

        var append = rest == ">>";
        if (prefix == '2')
        {
            return append ? RedirectDirection.ErrAppend : RedirectDirection.ErrOut;
        }

        // 1>, 3>-6>, *> all map (lossily) to Out / Append.
        return append ? RedirectDirection.Append : RedirectDirection.Out;
    }

    // ---------------------------------------------------------------- recursion

    private static bool TryHandleInvokeExpression(
        List<PwshToken> body, int start, ClassifiedVerb verb, string source,
        PwshParserOptions options, int recursionDepth, Segment segment, bool markWrapped,
        PwshSetLocationContext attribution, out BuildResult result)
    {
        result = default;
        if (!IsInvokeExpression(verb))
        {
            return false;
        }

        if (segment.PrecedingOperator == CompoundOperator.Pipe)
        {
            result = BuildResult.Fail(
                "Invoke-Expression pipeline input is dynamic and cannot be parsed safely");
            return true;
        }

        var payloadStart = start + 1;
        string? inlinePayload = null;
        if (payloadStart >= body.Count)
        {
            result = BuildResult.Fail("Invoke-Expression is missing its payload");
            return true;
        }

        if (body[payloadStart].Kind == PwshTokenKind.Parameter)
        {
            var parameter = body[payloadStart].Value;
            var colon = parameter.IndexOf(':');
            var parameterName = colon >= 0
                ? parameter.Substring(0, colon)
                : parameter;
            if (!string.Equals(
                parameterName, "-Command", StringComparison.OrdinalIgnoreCase))
            {
                result = BuildResult.Fail(
                    "Invoke-Expression payload binding is ambiguous");
                return true;
            }

            if (colon >= 0 && colon + 1 < parameter.Length)
            {
                if (payloadStart + 1 != body.Count)
                {
                    result = BuildResult.Fail(
                        "Invoke-Expression payload binding is ambiguous");
                    return true;
                }

                inlinePayload = parameter.Substring(colon + 1);
            }

            payloadStart++;
            if (inlinePayload is null && payloadStart >= body.Count)
            {
                result = BuildResult.Fail("Invoke-Expression is missing its payload");
                return true;
            }
        }

        for (var i = payloadStart; inlinePayload is null && i < body.Count; i++)
        {
            if (body[i].Kind == PwshTokenKind.Operator
                && body[i].OperatorText is not "(" and not ")")
            {
                result = BuildResult.Fail(
                    "Invoke-Expression payload binding is ambiguous");
                return true;
            }
        }

        var payloadEnd = body.Count - 1;
        var payloadValue = inlinePayload;
        var rawPayload = inlinePayload;
        var isStatic = false;
        if (inlinePayload is not null)
        {
            if (!PwshLexer.TryDecodeExpandableValue(
                inlinePayload!, out var decodedPayload, out var hasInterpolation))
            {
                result = BuildResult.Fail("invalid PowerShell Unicode escape in Invoke-Expression payload");
                return true;
            }

            payloadValue = decodedPayload;
            isStatic = !hasInterpolation
                && !PwshResolver.LooksLikeCommaArray(payloadValue);
        }

        if (inlinePayload is null)
        {
            var singlePayload = payloadStart == payloadEnd;
            var payload = body[payloadStart];
            payloadValue = payload.Value;
            rawPayload = SourceSlice(source, body[payloadStart], body[payloadEnd]);
            isStatic = singlePayload
                && payload.Kind is PwshTokenKind.Word or PwshTokenKind.QuotedString
                && !payload.HasInterpolation
                && (payload.Kind != PwshTokenKind.Word
                    || !PwshResolver.LooksLikeCommaArray(payload.Value));
        }

        if (!isStatic)
        {
            var canonicalVerb = verb.CanonicalVerb;
            if (canonicalVerb is null && verb.VerbTokens.Count > 0
                && string.Equals(
                    verb.VerbTokens[0], "iex", StringComparison.OrdinalIgnoreCase))
            {
                canonicalVerb = "Invoke-Expression";
            }

            var dynamicClause = new Clause
            {
                Operator = segment.PrecedingOperator,
                Verb = new VerbChain
                {
                    Tokens = verb.VerbTokens,
                    CanonicalVerb = canonicalVerb,
                    IsDynamic = verb.IsDynamic,
                },
                Args = new[]
                {
                    new Arg
                    {
                        Raw = rawPayload!,
                        Kind = ArgKind.DynamicSkip,
                        IsPath = false,
                    },
                },
                IsSubshell = segment.Depth > 0,
                IsCommandStringWrapped = markWrapped,
            };

            // Runtime code can call Set-Location in the current scope. Once
            // the payload is dynamic, every following relative path must
            // safe-fail rather than retain the previously known location.
            dynamicClause = AttachAttributionArg(dynamicClause, attribution);
            attribution.SetDynamic();
            result = BuildResult.Recursion(new[] { dynamicClause });
            return true;
        }

        if (recursionDepth + 1 > MaxRecursionDepth)
        {
            result = BuildResult.Fail(
                "PowerShell command-string recursion depth exceeded (>5)");
            return true;
        }

        var innerParsed = ParseInternal(
            payloadValue!, options, recursionDepth + 1, markWrapped: true,
            sharedLocation: attribution);
        if (innerParsed.IsUnparseable)
        {
            result = BuildResult.Fail(innerParsed.UnparseableReason);
            return true;
        }

        var expanded = new List<Clause>(innerParsed.Clauses.Count);
        for (var i = 0; i < innerParsed.Clauses.Count; i++)
        {
            var innerClause = innerParsed.Clauses[i];
            expanded.Add(innerClause with
            {
                Operator = i == 0 ? segment.PrecedingOperator : innerClause.Operator,
                IsSubshell = segment.Depth > 0 || innerClause.IsSubshell,
                IsCommandStringWrapped = true,
            });
        }

        result = BuildResult.Recursion(expanded);
        return true;
    }

    private static bool IsInvokeExpression(ClassifiedVerb verb)
    {
        var identity = verb.CanonicalVerb
            ?? (verb.VerbTokens.Count > 0 ? verb.VerbTokens[0] : null);
        return identity is not null && IsInvokeExpressionName(identity);
    }

    private static bool IsInvokeExpressionName(string identity)
    {
        return string.Equals(identity, "Invoke-Expression", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identity, "iex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                identity,
                "Microsoft.PowerShell.Utility\\Invoke-Expression",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRecurseIntoPwsh(
        List<PwshToken> body, int start, ClassifiedVerb verb, string source,
        PwshParserOptions options, int recursionDepth, Segment segment, bool markWrapped,
        out BuildResult result)
    {
        result = default;

        // Scan for -Command / -EncodedCommand among the args.
        for (var i = start + 1; i < body.Count; i++)
        {
            var t = body[i];
            if (t.Kind != PwshTokenKind.Parameter)
            {
                continue;
            }

            var raw = t.Value;
            var colon = raw.IndexOf(':');
            var name = colon > 0 ? raw.Substring(0, colon) : raw;
            var colonValue = colon > 0 ? raw.Substring(colon + 1) : null;

            string? inner = null;
            string? failure = null;
            var isEncoded = false;

            if (IsCommandParameter(name))
            {
                inner = ResolveCommandPayload(body, i, colonValue, source);
            }
            else if (IsEncodedCommandParameter(name))
            {
                isEncoded = true;
                var payloadToken = colonValue ?? NextTokenValue(body, i);
                if (payloadToken is null)
                {
                    failure = "-EncodedCommand is missing its base64 payload";
                }
                else
                {
                    inner = TryDecodeEncodedCommand(payloadToken, out failure);
                }
            }
            else
            {
                continue;
            }

            if (failure is not null)
            {
                result = BuildResult.Fail(failure);
                return true;
            }

            if (inner is null)
            {
                result = BuildResult.Fail(
                    isEncoded ? "-EncodedCommand payload could not be decoded"
                              : "-Command is missing its payload");
                return true;
            }

            if (recursionDepth + 1 > MaxRecursionDepth)
            {
                result = BuildResult.Fail(
                    "pwsh -Command / -EncodedCommand recursion depth exceeded (>5)");
                return true;
            }

            var innerParsed = ParseInternal(
                inner, options, recursionDepth + 1, markWrapped: true,
                sharedLocation: null);
            if (innerParsed.IsUnparseable)
            {
                result = BuildResult.Fail(innerParsed.UnparseableReason);
                return true;
            }

            var expanded = new List<Clause>(innerParsed.Clauses.Count);
            for (var k = 0; k < innerParsed.Clauses.Count; k++)
            {
                var ic = innerParsed.Clauses[k];
                expanded.Add(ic with
                {
                    Operator = k == 0 ? segment.PrecedingOperator : ic.Operator,
                    IsSubshell = segment.Depth > 0 || ic.IsSubshell,
                    IsCommandStringWrapped = true,
                    Elements = ClauseElementProvenance.WithoutOuterSourceSpans(ic.Elements),
                });
            }

            result = BuildResult.Recursion(expanded);
            return true;
        }

        return false;
    }

    private static bool IsCommandParameter(string name)
    {
        // -c, or any prefix -Comm.. of -Command.
        if (string.Equals(name, "-c", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length >= 5
            && "-command".StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEncodedCommandParameter(string name)
    {
        if (string.Equals(name, "-e", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length >= 3
            && "-encodedcommand".StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve the <c>-Command</c> payload (§10): a quoted string is parsed
    /// verbatim; a script block has its braces stripped; a bare/multi-token
    /// payload is the verbatim source slice from the first following token
    /// to the end of the segment body.
    /// </summary>
    private static string? ResolveCommandPayload(
        List<PwshToken> body, int paramIndex, string? colonValue, string source)
    {
        if (colonValue is not null)
        {
            return colonValue;
        }

        if (paramIndex + 1 >= body.Count)
        {
            return null;
        }

        var next = body[paramIndex + 1];
        if (next.Kind == PwshTokenKind.QuotedString)
        {
            return next.Value;
        }

        if (next.Kind == PwshTokenKind.ScriptBlock)
        {
            // Strip the outer { }.
            var v = next.Value;
            if (v.Length >= 2 && v[0] == '{' && v[v.Length - 1] == '}')
            {
                return v.Substring(1, v.Length - 2);
            }

            return v;
        }

        // Bare / multi-token: verbatim slice to the end of the segment body.
        var last = body[body.Count - 1];
        var sliceStart = next.SourceStart;
        var sliceEnd = last.SourceStart + last.SourceLength;
        if (sliceStart < 0 || sliceStart >= source.Length)
        {
            return next.Value;
        }

        if (sliceEnd > source.Length)
        {
            sliceEnd = source.Length;
        }

        return source.Substring(sliceStart, sliceEnd - sliceStart);
    }

    private static string? NextTokenValue(List<PwshToken> body, int paramIndex) =>
        paramIndex + 1 < body.Count ? body[paramIndex + 1].Value : null;

    private static string? TryDecodeEncodedCommand(string payload, out string? failure)
    {
        failure = null;
        try
        {
            var bytes = Convert.FromBase64String(payload);
            var decoded = Encoding.Unicode.GetString(bytes); // UTF-16LE
            if (decoded.Length > 0 && decoded[0] == '﻿')
            {
                decoded = decoded.Substring(1); // strip the UTF-16 BOM
            }

            return decoded;
        }
        catch (FormatException)
        {
            failure = "-EncodedCommand payload is not valid base64";
            return null;
        }
        catch (ArgumentException)
        {
            failure = "-EncodedCommand payload is not valid UTF-16";
            return null;
        }
    }

    // ---------------------------------------------------------------- attribution

    private static Clause AttachAttributionArg(Clause clause, PwshSetLocationContext ctx)
    {
        if (!ctx.HasAttribution)
        {
            return clause;
        }

        Arg synthetic = ctx.IsDynamic
            ? new Arg
            {
                Raw = "<dynamic-cwd>",
                Kind = ArgKind.DynamicSkip,
                IsPath = false,
                IsCwdAttribution = true,
            }
            : new Arg
            {
                Raw = ctx.ResolvedCwd!,
                Resolved = ctx.ResolvedCwd,
                Kind = ArgKind.Literal,
                IsPath = true,
                IsCwdAttribution = true,
            };

        var newArgs = new List<Arg>(clause.Args.Count + 1);
        newArgs.AddRange(clause.Args);
        newArgs.Add(synthetic);
        return clause with { Args = newArgs };
    }

    private static void UpdateAttribution(
        Clause clause, PwshParserOptions options, PwshSetLocationContext ctx)
    {
        var effectiveVerb = clause.Verb.CanonicalVerb
            ?? (clause.Verb.Tokens.Count > 0 ? clause.Verb.Tokens[0] : null);
        if (!string.Equals(effectiveVerb, "Set-Location", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Inspect the args (excluding the synthetic attribution arg).
        Arg? target = null;
        var sawPathFlag = false;
        foreach (var a in clause.Args)
        {
            if (a.IsCwdAttribution)
            {
                continue;
            }

            // `Set-Location -` / `+` — previous/next location, not knowable.
            if (a.Raw is "-" or "+")
            {
                ctx.SetDynamic();
                return;
            }

            if (a.IsFlag)
            {
                sawPathFlag = a.Raw.ToLowerInvariant() is "-path" or "-literalpath"
                    or "-pspath" or "-lp";
                continue;
            }

            if (sawPathFlag)
            {
                target = a;
                break;
            }

            target ??= a;
        }

        if (target is null)
        {
            // `Set-Location` with no positional and no -Path → home (§9 rule 1).
            var home = string.IsNullOrEmpty(options.HomeDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : options.HomeDirectory!;
            ctx.SetLiteral(NormalizeForAttribution(home));
            return;
        }

        if ((target.Kind == ArgKind.Literal || target.Kind == ArgKind.Tilde)
            && target.Resolved is not null)
        {
            ctx.SetLiteral(target.Resolved);
        }
        else
        {
            ctx.SetDynamic();
        }
    }

    private static string NormalizeForAttribution(string path) =>
        path.Replace('\\', '/');

    // ---------------------------------------------------------------- helpers

    private static string SourceSlice(string source, PwshToken token)
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

    private static string SourceSlice(string source, PwshToken first, PwshToken last)
    {
        var start = first.SourceStart;
        var end = last.SourceStart + last.SourceLength;
        if (start < 0 || start >= source.Length || end <= start)
        {
            return first.Value;
        }

        if (end > source.Length)
        {
            end = source.Length;
        }

        return source.Substring(start, end - start);
    }
}
