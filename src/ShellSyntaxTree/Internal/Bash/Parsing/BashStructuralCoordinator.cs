// -----------------------------------------------------------------------
// <copyright file="BashStructuralCoordinator.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

internal static partial class BashCommandParser
{
    private static BashParseResult ParseStructured(
        string source,
        IReadOnlyList<BashToken> tokens,
        BashParserOptions options,
        int bashCDepth,
        int structuralDepth,
        bool markBashCWrapped)
    {
        var coordinator = new StructuralCoordinator(
            source,
            tokens,
            options,
            bashCDepth,
            structuralDepth,
            markBashCWrapped,
            sourceStart: 0,
            source.Length);
        if (!coordinator.TryParse(out var syntax, out var error))
        {
            return StructuralFailure(source, error, syntax);
        }

        if (!BashAbstractStateAnalyzer.TryAnalyze(
                syntax,
                options,
                coordinator.GetFacts,
                coordinator.GetForInPlan,
                coordinator.ArithmeticSites,
                out var analyzedSyntax,
                out var analyzedFacts,
                out var analyzedForInPlans,
                out var analysisFailure) ||
            !ShellSyntaxProjection.TryProject(
                analyzedSyntax,
                analyzedFacts,
                ShellProjectionLanguage.Bash,
                out var projection))
        {
            return StructuralFailure(
                source,
                analysisFailure ??
                    "Bash structural syntax exceeded limits or contained invalid parser-owned facts",
                analyzedSyntax);
        }

        var command = new ParsedCommand
        {
            Source = source,
            Syntax = analyzedSyntax,
            Commands = projection.Commands,
            Clauses = projection.Clauses,
        };
        return new BashParseResult(
            command,
            CreateDependencySets(projection.Commands, analyzedFacts),
            CreateValueProvenanceSets(projection.Commands, analyzedFacts),
            CreateRedirectProvenanceSets(projection.Commands, analyzedFacts),
            CreateRedirectAnalysisSets(projection.Commands, analyzedFacts),
            analyzedForInPlans);
    }

    private static IReadOnlyList<CwdPathDependencySet> CreateDependencySets(
        IReadOnlyList<CommandOccurrence> commands,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory)
    {
        var sets = new CwdPathDependencySet[commands.Count];
        for (var index = 0; index < sets.Length; index++)
        {
            var clause = commands[index].Clause;
            var facts = factsFactory(new SimpleCommandSyntax { Clause = clause });
            sets[index] = new CwdPathDependencySet(
                clause,
                facts.CwdPathDependencies);
        }

        return sets;
    }

    private static IReadOnlyList<ShellValueProvenanceSet> CreateValueProvenanceSets(
        IReadOnlyList<CommandOccurrence> commands,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory)
    {
        var sets = new ShellValueProvenanceSet[commands.Count];
        for (var index = 0; index < sets.Length; index++)
        {
            var clause = commands[index].Clause;
            var facts = factsFactory(new SimpleCommandSyntax { Clause = clause });
            sets[index] = new ShellValueProvenanceSet(
                clause,
                facts.ValueProvenance);
        }

        return sets;
    }

    private static IReadOnlyList<RedirectTargetProvenanceSet>
        CreateRedirectProvenanceSets(
            IReadOnlyList<CommandOccurrence> commands,
            Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory)
    {
        var sets = new RedirectTargetProvenanceSet[commands.Count];
        for (var index = 0; index < sets.Length; index++)
        {
            var clause = commands[index].Clause;
            var facts = factsFactory(new SimpleCommandSyntax { Clause = clause });
            sets[index] = new RedirectTargetProvenanceSet(
                clause,
                facts.RedirectTargetProvenance);
        }

        return sets;
    }

    private static IReadOnlyList<RedirectAnalysisSet> CreateRedirectAnalysisSets(
        IReadOnlyList<CommandOccurrence> commands,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory)
    {
        var sets = new RedirectAnalysisSet[commands.Count];
        for (var index = 0; index < sets.Length; index++)
        {
            var clause = commands[index].Clause;
            var facts = factsFactory(new SimpleCommandSyntax { Clause = clause });
            sets[index] = new RedirectAnalysisSet(clause, facts.Redirects);
        }

        return sets;
    }

    private static BashParseResult StructuralFailure(
        string source,
        string? reason,
        ShellBlockSyntax? syntax = null) => new(
            new ParsedCommand
            {
                Source = source,
                Syntax = syntax ?? new ShellBlockSyntax(),
                Commands = Array.Empty<CommandOccurrence>(),
                Clauses = Array.Empty<Clause>(),
                IsUnparseable = true,
                UnparseableReason = reason,
            },
            Array.Empty<CwdPathDependencySet>(),
            Array.Empty<ShellValueProvenanceSet>(),
            Array.Empty<RedirectTargetProvenanceSet>(),
            Array.Empty<RedirectAnalysisSet>(),
            Array.Empty<BashForInAnalysisPlanReference>());

    private sealed class StructuralCoordinator
    {
        private static readonly HashSet<string> BashBuiltins = new(StringComparer.Ordinal)
        {
            ".", ":", "[", "alias", "bg", "bind", "break", "builtin", "caller",
            "cd", "command", "compgen", "complete", "compopt", "continue", "declare",
            "dirs", "disown", "echo", "enable", "eval", "exec", "exit", "export",
            "false", "fc", "fg", "getopts", "hash", "help", "history", "jobs", "kill",
            "let", "local", "logout", "mapfile", "popd", "printf", "pushd", "pwd",
            "read", "readarray", "readonly", "return", "set", "shift", "shopt",
            "source", "suspend", "test", "times", "trap", "true", "type", "typeset",
            "ulimit", "umask", "unalias", "unset", "wait",
        };

        private readonly string _source;
        private readonly IReadOnlyList<BashToken> _tokens;
        // Not readonly: the live launch facts (#200) shrink as statements
        // that can change a variable are parsed. Every option object built
        // from this field carries the facts that are live at that point.
        private BashParserOptions _options;
        private readonly int _bashCDepth;
        private readonly int _structuralDepth;
        private readonly bool _markBashCWrapped;
        private readonly int _sourceStart;
        private readonly int _sourceLength;
        private readonly CdAttributionContext _attribution = new();
        private readonly List<string> _activeLoopBindings;
        private readonly Dictionary<Clause, CommandOccurrenceFacts> _facts =
            new(ClauseReferenceComparer.Instance);
        private readonly Dictionary<ForEachSyntax, BashForInAnalysisPlan> _forInPlans =
            new(ForEachReferenceComparer.Instance);

        // The source start of each bounded arithmetic expansion in this
        // source and in every nested substitution body (#227). The state pass
        // must prove the reads of each one, or the parse fails closed.
        private readonly HashSet<int> _arithmeticSites = new();
        private int _position;
        private int _subshellDepth;
        private int _loopDepth;

        // The nesting of `if` and `case` statements (#212). It shares the
        // structural depth limit, so deep nesting fails closed before the
        // recursive parse can exhaust the stack.
        private int _compoundDepth;
        private bool _hasUnmodeledShellStateMutation;
        private bool _hasUnmodeledVariableStateMutation;
        private readonly HashSet<string> _boundedAssignmentNames;

        internal StructuralCoordinator(
            string source,
            IReadOnlyList<BashToken> tokens,
            BashParserOptions options,
            int bashCDepth,
            int structuralDepth,
            bool markBashCWrapped,
            int sourceStart,
            int sourceLength,
            IReadOnlyList<string>? activeLoopBindings = null,
            bool hasUnmodeledShellStateMutation = false,
            bool hasUnmodeledVariableStateMutation = false,
            IEnumerable<string>? boundedAssignmentNames = null)
        {
            _source = source;
            _tokens = tokens;
            _options = options;
            _bashCDepth = bashCDepth;
            _structuralDepth = structuralDepth;
            _markBashCWrapped = markBashCWrapped;
            _sourceStart = sourceStart;
            _sourceLength = sourceLength;
            _activeLoopBindings = activeLoopBindings is null
                ? new List<string>()
                : new List<string>(activeLoopBindings);
            _hasUnmodeledShellStateMutation = hasUnmodeledShellStateMutation;
            _hasUnmodeledVariableStateMutation = hasUnmodeledVariableStateMutation;
            _boundedAssignmentNames = boundedAssignmentNames is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(boundedAssignmentNames, StringComparer.Ordinal);
            foreach (var fragment in CollectArithmetic(tokens))
            {
                _arithmeticSites.Add(fragment.SourceStart ?? -1);
            }
        }

        internal IReadOnlyCollection<int> ArithmeticSites => _arithmeticSites;

        internal CommandOccurrenceFacts GetFacts(SimpleCommandSyntax simple) =>
            _facts.TryGetValue(simple.Clause, out var facts)
                ? facts
                : CreateDefaultFacts(simple.Clause);

        internal BashForInAnalysisPlan? GetForInPlan(ForEachSyntax forEach) =>
            _forInPlans.TryGetValue(forEach, out var plan) ? plan : null;

        private CommandOccurrenceFacts CreateDefaultFacts(Clause clause) => new()
        {
            Redirects = BashRedirectAnalysis.Analyze(clause),
            IsComplete = clause.Verb.Tokens.Count > 0 &&
                AreRedirectsComplete(clause) &&
                !HasUnexpandedCommandString(clause),
        };

        private static bool AreRedirectsComplete(Clause clause)
        {
            var redirects = BashRedirectAnalysis.Analyze(clause);
            if (redirects.Count != clause.Redirects.Count)
            {
                return false;
            }

            foreach (var redirect in redirects)
            {
                if (!redirect.IsComplete)
                {
                    return false;
                }
            }

            return true;
        }

        internal bool TryParse(out ShellBlockSyntax syntax, out string? error)
        {
            // A decoded command string is a separate Bash process. Its source
            // offsets do not map to the outer source, so the state pass
            // cannot prove its arithmetic reads (#227).
            if (_bashCDepth > 0 && _arithmeticSites.Count > 0)
            {
                syntax = new ShellBlockSyntax();
                error = "a Bash arithmetic expansion inside a decoded command string is not supported";
                return false;
            }

            SkipNewlines();
            if (_position == _tokens.Count)
            {
                syntax = new ShellBlockSyntax
                {
                    SourceStart = _sourceStart,
                    SourceLength = _sourceLength,
                };
                error = null;
                return true;
            }

            if (!TryParseList(
                    stopAtRightParen: false,
                    stopWords: null,
                    CompoundOperator.None,
                    out var command,
                    out error) ||
                command is null)
            {
                syntax = new ShellBlockSyntax();
                return false;
            }

            SkipNewlines();
            if (_position != _tokens.Count)
            {
                syntax = new ShellBlockSyntax();
                error = IsOperator(")")
                    ? $"unbalanced parens at position {_tokens[_position].SourceStart}"
                    : $"unexpected token at position {_tokens[_position].SourceStart}";
                return false;
            }

            syntax = new ShellBlockSyntax
            {
                Statements = new[] { command },
                SourceStart = _sourceStart,
                SourceLength = _sourceLength,
            };
            return true;
        }

        private bool TryParseList(
            bool stopAtRightParen,
            string[]? stopWords,
            CompoundOperator firstCompatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error,
            bool stopAtCaseTerminator = false)
        {
            command = null;
            error = null;
            SkipNewlines();
            if (_position == _tokens.Count ||
                stopAtRightParen && IsOperator(")") ||
                IsAnyWord(stopWords) ||
                stopAtCaseTerminator && IsCaseTerminator())
            {
                return true;
            }

            var andOrStart = 0;
            var andOrAttribution = SnapshotAttribution();
            if (!TryParsePipeline(firstCompatibilityOperator, out var first, out error))
            {
                return false;
            }

            var items = new List<CommandListItemSyntax>
            {
                new() { Operator = CompoundOperator.None, Command = first! },
            };

            while (_position < _tokens.Count)
            {
                if (stopAtRightParen && IsOperator(")"))
                {
                    break;
                }

                if (IsAnyWord(stopWords) ||
                    stopAtCaseTerminator && IsCaseTerminator())
                {
                    break;
                }

                if (IsOperator(")"))
                {
                    error = $"unbalanced parens at position {_tokens[_position].SourceStart}";
                    return false;
                }

                if (IsOperator("&"))
                {
                    // `&` ends the and-or list that began after the last `;`,
                    // newline, or `&`. That list runs in an asynchronous
                    // subshell, so its `cd` does not change the next command.
                    var ampersand = _tokens[_position++];
                    WrapBackgroundList(items, andOrStart, ampersand);
                    _attribution.Restore(andOrAttribution.Cwd, andOrAttribution.IsDynamic);
                    andOrStart = items.Count;
                    SkipNewlines();
                    if (_position == _tokens.Count ||
                        stopAtRightParen && IsOperator(")") ||
                        IsAnyWord(stopWords) ||
                        stopAtCaseTerminator && IsCaseTerminator())
                    {
                        break;
                    }

                    andOrAttribution = SnapshotAttribution();
                    if (!TryParsePipeline(CompoundOperator.Sequence, out var afterBackground, out error))
                    {
                        return false;
                    }

                    items.Add(new CommandListItemSyntax
                    {
                        Operator = CompoundOperator.Sequence,
                        Command = afterBackground!,
                    });
                    continue;
                }

                if (!TryReadListOperator(out var listOperator))
                {
                    error = $"unexpected token at position {_tokens[_position].SourceStart}";
                    return false;
                }

                SkipNewlines();
                if (_position == _tokens.Count ||
                    stopAtRightParen && IsOperator(")") ||
                    IsAnyWord(stopWords) ||
                    stopAtCaseTerminator && IsCaseTerminator())
                {
                    if (listOperator == CompoundOperator.Sequence)
                    {
                        break;
                    }

                    error = "compound operator is missing a following command";
                    return false;
                }

                if (listOperator == CompoundOperator.Sequence)
                {
                    andOrStart = items.Count;
                    andOrAttribution = SnapshotAttribution();
                }

                if (!TryParsePipeline(listOperator, out var next, out error))
                {
                    return false;
                }

                items.Add(new CommandListItemSyntax
                {
                    Operator = listOperator,
                    Command = next!,
                });
            }

            if (items.Count == 1)
            {
                // A background `&` can replace the first item with its group.
                command = items[0].Command;
                return true;
            }

            var children = new List<ShellSyntaxNode>(items.Count);
            foreach (var item in items)
            {
                children.Add(item.Command);
            }

            command = new CommandListSyntax
            {
                Items = items,
                SourceStart = CombinedStart(children),
                SourceLength = CombinedLength(children),
            };
            return true;
        }

        private bool TryParsePipeline(
            CompoundOperator firstCompatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            if (!TryParseCommand(firstCompatibilityOperator, out var first, out error))
            {
                return false;
            }

            var stages = new List<ShellSyntaxNode> { first! };
            while (IsOperator("|"))
            {
                if (stages[stages.Count - 1] is ShellAssignmentSyntax)
                {
                    error = "bounded Bash assignments are not supported in pipelines";
                    return false;
                }

                _position++;
                SkipNewlines();
                if (_position == _tokens.Count || IsOperator(")"))
                {
                    error = "pipeline operator is missing a following command";
                    return false;
                }

                if (!TryParseCommand(CompoundOperator.Pipe, out var stage, out error))
                {
                    return false;
                }

                stages.Add(stage!);
            }

            if (stages.Count > 1 && stages[stages.Count - 1] is ShellAssignmentSyntax)
            {
                error = "bounded Bash assignments are not supported in pipelines";
                return false;
            }

            if (stages.Count == 1)
            {
                command = first;
                return true;
            }

            command = new PipelineSyntax
            {
                Stages = stages,
                SourceStart = CombinedStart(stages),
                SourceLength = CombinedLength(stages),
            };
            error = null;
            return true;
        }

        private bool TryParseCommand(
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            error = null;
            if (_position == _tokens.Count)
            {
                error = "expected a command";
                return false;
            }

            if (IsOperator("("))
            {
                return TryParseSubshell(compatibilityOperator, out command, out error);
            }

            if (IsWord("for"))
            {
                return TryParseForIn(out command, out error);
            }

            if (IsWord("while") || IsWord("until"))
            {
                return TryParseConditionLoop(out command, out error);
            }

            if (IsWord("if"))
            {
                return TryParseIf(out command, out error);
            }

            if (IsWord("case"))
            {
                return TryParseCase(out command, out error);
            }

            if (IsAnyWord(StrayKeywords))
            {
                error = $"stray Bash control-flow keyword '{_tokens[_position].Value}'";
                return false;
            }

            if (IsOperator(")") || IsListOperator(_tokens[_position]) || IsOperator("|"))
            {
                error = $"unexpected operator at position {_tokens[_position].SourceStart}";
                return false;
            }

            var start = _position;
            while (_position < _tokens.Count && !IsStructuralBoundary(_tokens[_position]))
            {
                _position++;
            }

            var segmentTokens = new List<BashToken>(_position - start);
            for (var index = start; index < _position; index++)
            {
                segmentTokens.Add(_tokens[index]);
            }

            var segment = new Segment
            {
                PrecedingOperator = compatibilityOperator,
                Tokens = segmentTokens,
            };
            var segmentSourceStart = segmentTokens[0].SourceStart;
            var assignments = new List<ShellVariableAssignment>();
            var assignmentValues = new List<BashAssignmentValue?>();
            var prefixTokens = new List<BashToken>();

            if (IsAssignmentWord(segmentTokens[0]))
            {
                var scope = segmentTokens.Count == 1
                    ? ShellVariableAssignmentScope.ShellState
                    : ShellVariableAssignmentScope.CommandEnvironment;

                // Bash applies every leading assignment word to the command. Each
                // word must pass the same bounded-value gate. A repeated name is
                // rejected because only the last value reaches the command, so an
                // earlier fact would publish a value the command never receives.
                var prefixCount = 0;
                do
                {
                    if (!TryCreateBoundedAssignment(
                            segmentTokens[prefixCount],
                            scope,
                            out var assignment,
                            out var assignmentValue,
                            out error))
                    {
                        return false;
                    }

                    foreach (var prior in assignments)
                    {
                        if (string.Equals(prior.Name, assignment!.Name, StringComparison.Ordinal))
                        {
                            error = "repeated Bash assignment prefix names are not supported";
                            return false;
                        }
                    }

                    assignments.Add(assignment!);
                    assignmentValues.Add(assignmentValue);
                    prefixCount++;
                }
                while (prefixCount < segmentTokens.Count &&
                       IsAssignmentWord(segmentTokens[prefixCount]));

                // A prefix applies to one command, so its scope does not
                // matter. A shell-state assignment must stay in the scope
                // that the state pass models: the top level or a loop body
                // of the top-level shell (#209). The loop fixed point joins
                // the value of each iteration.
                if (_bashCDepth > 0 ||
                    segmentTokens.Count == 1 &&
                    (_structuralDepth > 0 || _subshellDepth > 0))
                {
                    error = "bounded Bash assignments require a top-level source";
                    return false;
                }

                if (segmentTokens.Count == 1 && _loopDepth > 0)
                {
                    var name = assignments[0].Name;
                    if (_activeLoopBindings.Contains(name))
                    {
                        error = "a Bash loop binding cannot be reassigned in its loop";
                        return false;
                    }

                    // An earlier statement of the body read the launch value
                    // in the first iteration and reads this value later.
                    if (_options.LaunchEnvironment?.TryGetLiveValue(name, out _) == true)
                    {
                        error = "a Bash launch variable assignment inside a loop is not supported";
                        return false;
                    }
                }

                if (segmentTokens.Count == 1)
                {
                    // Bash expands the right-hand side before the assignment,
                    // so a substitution in it sees the earlier bindings.
                    if (!TryParseAssignmentSubstitutions(
                            segmentTokens,
                            out var assignmentSubstitutions,
                            out error))
                    {
                        return false;
                    }

                    // The assignment replaces a supplied launch value with the
                    // same name. Later reads use the assignment rules only.
                    // A command-environment prefix does not change the shell
                    // variable, because Bash expands the command's words first.
                    RevokeLaunchFact(assignments[0].Name);
                    _boundedAssignmentNames.Add(assignments[0].Name);
                    command = new ShellAssignmentSyntax
                    {
                        Assignment = assignments[0],
                        Value = assignmentValues[0],
                        Substitutions = assignmentSubstitutions,
                        SourceStart = segmentTokens[0].SourceStart,
                        SourceLength = segmentTokens[0].SourceLength,
                    };
                    return true;
                }

                if (prefixCount == segmentTokens.Count)
                {
                    error = "multiple Bash assignments are not supported";
                    return false;
                }

                if (segmentTokens[prefixCount].Kind == BashTokenKind.Operator)
                {
                    error = "redirects on assignment-only Bash statements are not supported";
                    return false;
                }

                prefixTokens.AddRange(segmentTokens.GetRange(0, prefixCount));
                segmentTokens.RemoveRange(0, prefixCount);
                segment = new Segment
                {
                    PrecedingOperator = compatibilityOperator,
                    Tokens = segmentTokens,
                };
            }

            if (!TryCollectCommandSubstitutions(
                    segmentTokens,
                    rejectCommandNameSubstitution: true,
                    out var substitutionFragments,
                    out error))
            {
                return false;
            }

            // A substitution in a prefix value runs before the command, in
            // the current shell (#209).
            if (prefixTokens.Count > 0)
            {
                if (!TryCollectCommandSubstitutions(
                        prefixTokens,
                        rejectCommandNameSubstitution: false,
                        out var prefixFragments,
                        out error))
                {
                    return false;
                }

                if (prefixFragments.Count > 0)
                {
                    var combined = new List<ShellValueFragment>(prefixFragments);
                    combined.AddRange(substitutionFragments);
                    substitutionFragments = combined;
                }
            }

            if (TryDetectBashCWrapper(segment, _source, out var innerCommand))
            {
                if (assignments.Count > 0)
                {
                    error = "a Bash assignment prefix on a decoded shell wrapper is not supported";
                    return false;
                }

                if (!IsExactStaticWrapper(segmentTokens))
                {
                    error = "bash -c wrapper has unsupported trailing arguments or redirects";
                    return false;
                }

                if (_bashCDepth + 1 > MaxBashCRecursionDepth)
                {
                    error = "bash -c recursion depth exceeded (>5)";
                    return false;
                }

                var innerOptions = _options with
                {
                    InitialStateMode = _hasUnmodeledVariableStateMutation
                        ? BashInitialStateMode.Unknown
                        : _options.InitialStateMode,
                    // This opt-in describes the source submitted to this parser call.
                    // A decoded child shell is a separate execution boundary and must
                    // receive its own caller assertion before it can publish authored-
                    // only loop facts.
                    PublishAuthoredSourceFacts = false,
                    // The same holds for launch facts (#200). The outer
                    // analysis does not carry them into decoded clauses, so
                    // the child gets none and its variables stay unknown.
                    LaunchEnvironment = null,
                };
                var innerResult = ParseInternal(
                    innerCommand!,
                    innerOptions,
                    _bashCDepth + 1,
                    _structuralDepth + _subshellDepth + _loopDepth + _compoundDepth + 1,
                    markBashCWrapped: true);
                var inner = innerResult.Command;
                if (inner.IsUnparseable)
                {
                    error = inner.UnparseableReason;
                    return false;
                }

                var firstLeaf = true;
                if (!TryCloneDecodedBlock(
                        inner.Syntax,
                        compatibilityOperator,
                        _subshellDepth > 0,
                        ref firstLeaf,
                        out var body,
                        out var referenceMap))
                {
                    error = "decoded bash -c syntax could not be lifted safely";
                    return false;
                }

                var firstToken = segmentTokens[0];
                var lastToken = segmentTokens[segmentTokens.Count - 1];
                command = new GroupSyntax
                {
                    GroupKind = ShellGroupKind.IsolatedScope,
                    Body = body,
                    SourceStart = firstToken.SourceStart,
                    SourceLength = lastToken.SourceStart + lastToken.SourceLength -
                        firstToken.SourceStart,
                };
                if (!TryRegisterDecodedFacts(innerResult, referenceMap, out error))
                {
                    command = null;
                    return false;
                }

                return true;
            }

            var effectiveOptions = _options;
            var workingDirectoryUnknown = false;
            if (_attribution.HasAttribution && !_attribution.IsDynamic)
            {
                effectiveOptions = _options with
                {
                    WorkingDirectory = _attribution.ResolvedCwd,
                };
            }
            else if (_attribution.IsDynamic)
            {
                workingDirectoryUnknown = true;
            }

            var parsed = ParseClauseSegment(
                segment,
                _source,
                effectiveOptions,
                workingDirectoryUnknown);
            if (parsed.Error is not null)
            {
                error = parsed.Error;
                return false;
            }

            if (parsed.Clauses.Count != 1)
            {
                error = "simple-command parser did not produce exactly one compatibility leaf";
                return false;
            }

            var clause = parsed.Clauses[0] with
            {
                IsSubshell = _subshellDepth > 0,
                IsCommandStringWrapped = _markBashCWrapped,
            };
            var emitted = AttachAttributionArg(clause, _attribution);
            if (assignments.Count > 0 && IsBuiltin(emitted))
            {
                error = "a Bash assignment prefix before a builtin is not supported";
                return false;
            }
            var executionBoundary =
                BashCwdInvocationGrammar.ClassifyExecutionBoundary(emitted);
            if (executionBoundary == BashExecutionBoundaryKind.ExecutionBearingBuiltin &&
                IsModeledRead(emitted))
            {
                if (!TryAcceptReadNames(emitted, out error))
                {
                    return false;
                }

                executionBoundary = BashExecutionBoundaryKind.Allowed;
            }

            BashExportFacts? exports = null;
            if (executionBoundary == BashExecutionBoundaryKind.ExecutionBearingBuiltin &&
                IsModeledExportCandidate(emitted, segmentTokens))
            {
                if (!TryAcceptExport(segmentTokens, out exports, out error))
                {
                    return false;
                }

                executionBoundary = BashExecutionBoundaryKind.Allowed;
            }

            if (executionBoundary == BashExecutionBoundaryKind.ExecutionBearingBuiltin &&
                IsModeledSetPositional(emitted))
            {
                executionBoundary = BashExecutionBoundaryKind.Allowed;
            }

            if (executionBoundary == BashExecutionBoundaryKind.ExecutionBearingBuiltin)
            {
                error = "Bash execution-bearing builtin requires structure-aware state analysis";
                return false;
            }

            if (executionBoundary == BashExecutionBoundaryKind.CommandResolutionMutation)
            {
                error = "Bash command-resolution mutation requires structure-aware state analysis";
                return false;
            }

            if (executionBoundary ==
                BashExecutionBoundaryKind.UnsupportedReservedExecutionSyntax)
            {
                error = "Bash reserved execution syntax requires structural analysis";
                return false;
            }

            if (executionBoundary == BashExecutionBoundaryKind.UnsupportedDispatch)
            {
                error = "Bash command or builtin dispatch grammar is not statically supported";
                return false;
            }

            if (ContainsNamedParameterExpansion(segmentTokens) &&
                !AllowsUnknownNamedReads &&
                !CanPublishActiveLoopBindingFacts(segmentTokens) &&
                !CanPublishBoundedAssignmentFacts(segmentTokens) &&
                !CanPublishLaunchFacts(segmentTokens))
            {
                error = "Bash named parameter expansion requires proved variable-attribute state";
                return false;
            }

            var dispatchKind = BashCwdInvocationGrammar.Classify(
                emitted,
                out _);
            var isModeledBuiltin = IsModeledRead(emitted) || exports is not null ||
                IsModeledSetPositional(emitted);
            var isPotentialStateMutation = IsPotentialBindingMutation(emitted) &&
                !isModeledBuiltin;
            var isModeledCwdTransfer = dispatchKind == BashDispatchKind.CwdTransfer;
            if (_activeLoopBindings.Count > 0 &&
                isPotentialStateMutation &&
                !isModeledCwdTransfer)
            {
                error = "Bash loop state mutation or control transfer is not supported for bounded analysis";
                return false;
            }

            _hasUnmodeledShellStateMutation |=
                isPotentialStateMutation && !isModeledCwdTransfer;
            _hasUnmodeledVariableStateMutation |=
                IsPotentialVariableStateMutation(emitted) && !isModeledCwdTransfer &&
                !isModeledBuiltin;
            if ((_hasUnmodeledVariableStateMutation || IsWaitWithOption(emitted)) &&
                !TryRevokeAllLaunchFacts(out error))
            {
                return false;
            }

            // `wait -p name` can assign a bounded name. The state pass does
            // not model it, so a later read would use a stale value.
            if (IsWaitWithOption(emitted) && _boundedAssignmentNames.Count > 0)
            {
                error = "Bash wait with an option after a bounded assignment is not supported";
                return false;
            }

            if (!TryParseCommandSubstitutions(
                    substitutionFragments,
                    effectiveOptions,
                    out var substitutions,
                    out error))
            {
                return false;
            }

            var firstVerbToken = clause.Verb.Tokens.Count > 0 ? clause.Verb.Tokens[0] : null;
            if (firstVerbToken is not null &&
                (string.Equals(firstVerbToken, "cd", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(firstVerbToken, "chdir", StringComparison.OrdinalIgnoreCase)))
            {
                UpdateAttributionFromCd(clause, _attribution);
            }

            var firstSource = segmentTokens[0];
            var sourceEnd = firstSource.SourceStart + firstSource.SourceLength;
            foreach (var token in segmentTokens)
            {
                sourceEnd = Math.Max(
                    sourceEnd,
                    token.HeredocSourceEnd ?? token.SourceStart + token.SourceLength);
            }

            var simple = new SimpleCommandSyntax
            {
                Clause = emitted,
                Substitutions = substitutions,
                EnvironmentAssignments = assignments,
                SourceStart = segmentSourceStart,
                SourceLength = sourceEnd - segmentSourceStart,
            };
            RegisterFacts(
                simple,
                segmentTokens,
                parsed.PathResolutions,
                effectiveOptions,
                assignmentValues,
                exports,
                CollectArithmetic(prefixTokens, segmentTokens));
            command = simple;
            return true;
        }

        private bool TryParseForIn(
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            error = null;
            if (_hasUnmodeledShellStateMutation)
            {
                error = "Bash for-in after prior shell-state mutation requires structure-aware state analysis";
                return false;
            }

            if (_structuralDepth + _subshellDepth + _loopDepth + _compoundDepth >=
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                error = "Bash structural nesting depth exceeded (>16)";
                return false;
            }

            var forToken = _tokens[_position++];
            if (_position == _tokens.Count ||
                _tokens[_position].Kind != BashTokenKind.Word ||
                !HasExactLiteralValue(_tokens[_position]) ||
                !string.Equals(
                    SourceSlice(_source, _tokens[_position]),
                    _tokens[_position].Value,
                    StringComparison.Ordinal) ||
                !IsBashIdentifier(_tokens[_position].Value))
            {
                error = "Bash for-in loop requires a shell-identifier binding";
                return false;
            }

            var bindingToken = _tokens[_position++];
            if (_options.InitialStateMode is not (BashInitialStateMode.IsolatedNonInteractive or
                    BashInitialStateMode.FreshNonInteractiveNoStartup) &&
                !_options.PublishAuthoredSourceFacts)
            {
                error = "Bash for-in requires a proved isolated non-interactive initial state";
                return false;
            }

            if (!IsSupportedScalarBinding(bindingToken.Value))
            {
                error = "Bash for-in binding is outside the supported ordinary-scalar boundary";
                return false;
            }

            if (_activeLoopBindings.Contains(bindingToken.Value))
            {
                error = "nested Bash for-in binding reuse requires state propagation";
                return false;
            }

            // The loop would replace a bounded assignment. The published
            // assignment fact would then be stale (#209).
            if (_boundedAssignmentNames.Contains(bindingToken.Value))
            {
                error = "a Bash for-in binding cannot reuse a bounded assignment name";
                return false;
            }

            // The loop assigns its binding, and the last value stays after
            // the loop. A supplied launch value with that name is no longer
            // trusted, including in the iterable words.
            if (_options.LaunchEnvironment?.TryGetLiveValue(bindingToken.Value, out _) == true)
            {
                if (_loopDepth > 0)
                {
                    error = "a Bash launch variable binding inside a loop is not supported";
                    return false;
                }

                RevokeLaunchFact(bindingToken.Value);
            }

            if (!IsWord("in"))
            {
                error = "Bash for-in loop requires the contextual 'in' keyword";
                return false;
            }

            _position++;
            var iterableWords = new List<BashToken>();
            while (_position < _tokens.Count && !IsListTerminator())
            {
                var token = _tokens[_position];
                if (token.Kind is not (
                        BashTokenKind.Word or
                        BashTokenKind.QuotedString or
                        BashTokenKind.OpaqueSubstitution))
                {
                    error = $"unsupported Bash for-in iterable token at position {token.SourceStart}";
                    return false;
                }

                if (iterableWords.Count > 0 &&
                    iterableWords[iterableWords.Count - 1].SourceStart +
                        iterableWords[iterableWords.Count - 1].SourceLength == token.SourceStart)
                {
                    var previous = iterableWords[iterableWords.Count - 1];
                    var previousValue = previous.ResolverValue ??
                        ShellValue.Literal(
                            previous.Value,
                            previous.SourceStart,
                            previous.SourceLength);
                    var currentValue = token.ResolverValue ??
                        ShellValue.Literal(token.Value, token.SourceStart, token.SourceLength);
                    iterableWords[iterableWords.Count - 1] = new BashToken(
                        BashTokenKind.Word,
                        previous.Value + token.Value,
                        null,
                        previous.SourceStart,
                        token.SourceStart + token.SourceLength - previous.SourceStart,
                        null)
                    {
                        ResolverValue = ShellValue.Concat(new[] { previousValue, currentValue }),
                    };
                }
                else
                {
                    iterableWords.Add(token);
                }

                _position++;
            }

            if (_position == _tokens.Count)
            {
                error = "Bash for-in loop is missing its list terminator and 'do'";
                return false;
            }

            var terminator = _tokens[_position++];
            if (terminator.Kind == BashTokenKind.Whitespace)
            {
                SkipNewlines();
            }

            if (!IsWord("do"))
            {
                error = "Bash for-in loop is missing 'do'";
                return false;
            }

            var doToken = _tokens[_position++];
            var iterableStart = iterableWords.Count == 0
                ? terminator.SourceStart
                : iterableWords[0].SourceStart;
            var iterableEnd = iterableWords.Count == 0
                ? iterableStart
                : iterableWords[iterableWords.Count - 1].SourceStart +
                    iterableWords[iterableWords.Count - 1].SourceLength;
            var analysisPlan = BashLoopBindingContext.CapturePlan(
                bindingToken.Value,
                iterableWords);
            if (!TryParseIteratorSubstitutions(
                    iterableWords,
                    CurrentOptions(),
                    iterableStart,
                    iterableEnd - iterableStart,
                    out var iteratorCommands,
                    out error))
            {
                return false;
            }

            _activeLoopBindings.Add(bindingToken.Value);
            _loopDepth++;
            var parsedBody = TryParseList(
                stopAtRightParen: false,
                stopWords: DoneWord,
                CompoundOperator.None,
                out var bodyCommand,
                out error);
            _loopDepth--;
            _activeLoopBindings.RemoveAt(_activeLoopBindings.Count - 1);
            if (!parsedBody)
            {
                return false;
            }

            if (bodyCommand is null)
            {
                error = "Bash for-in loop body cannot be empty";
                return false;
            }

            if (!IsWord("done"))
            {
                error = "Bash for-in loop is missing 'done'";
                return false;
            }

            var doneToken = _tokens[_position++];
            var body = new ShellBlockSyntax
            {
                Statements = new[] { bodyCommand },
                SourceStart = doToken.SourceStart + doToken.SourceLength,
                SourceLength = doneToken.SourceStart -
                    doToken.SourceStart - doToken.SourceLength,
            };
            var forEach = new ForEachSyntax
            {
                Binding = new LoopBindingSyntax
                {
                    Name = bindingToken.Value,
                    Source = new ShellSourceFragment
                    {
                        Raw = SourceSlice(_source, bindingToken),
                        SourceStart = bindingToken.SourceStart,
                        SourceLength = bindingToken.SourceLength,
                    },
                },
                Iterable = new ShellSourceFragment
                {
                    Raw = _source.Substring(iterableStart, iterableEnd - iterableStart),
                    SourceStart = iterableStart,
                    SourceLength = iterableEnd - iterableStart,
                },
                IteratorCommands = iteratorCommands,
                Body = body,
                SourceStart = forToken.SourceStart,
                SourceLength = doneToken.SourceStart + doneToken.SourceLength -
                    forToken.SourceStart,
            };
            _forInPlans.Add(forEach, analysisPlan);
            command = forEach;
            return true;
        }

        /// <summary>
        /// Parses <c>while COND; do BODY; done</c> and the <c>until</c> form
        /// (#212). The condition and the body both repeat, so both parse at
        /// loop depth.
        /// </summary>
        private bool TryParseConditionLoop(
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            if (!TryEnterCompound(out error))
            {
                return false;
            }

            var keyword = _tokens[_position++];
            var kind = keyword.Value == "while" ? ConditionLoopKind.While : ConditionLoopKind.Until;
            var attribution = SnapshotAttribution();
            _loopDepth++;
            var conditionStart = keyword.SourceStart + keyword.SourceLength;
            var parsed = TryParseRequiredList(DoWord, "condition", out var condition, out error);
            if (parsed && !IsWord("do"))
            {
                error = "Bash condition loop is missing 'do'";
                parsed = false;
            }

            BashToken? doToken = null;
            ShellSyntaxNode? body = null;
            if (parsed)
            {
                doToken = _tokens[_position++];
                parsed = TryParseRequiredList(DoneWord, "body", out body, out error);
                if (parsed && !IsWord("done"))
                {
                    error = "Bash condition loop is missing 'done'";
                    parsed = false;
                }
            }

            _loopDepth--;
            if (!parsed)
            {
                return false;
            }

            var doneToken = _tokens[_position++];
            var doWord = doToken!.Value;
            RestoreAttribution(attribution);
            command = new ConditionLoopSyntax
            {
                LoopKind = kind,
                Condition = Block(condition!, conditionStart, doWord.SourceStart),
                Body = Block(
                    body!,
                    doWord.SourceStart + doWord.SourceLength,
                    doneToken.SourceStart),
                SourceStart = keyword.SourceStart,
                SourceLength = doneToken.SourceStart + doneToken.SourceLength - keyword.SourceStart,
            };
            return true;
        }

        /// <summary>
        /// Parses <c>if COND; then BODY; [elif COND; then BODY;]... [else
        /// BODY;] fi</c> (#212).
        /// </summary>
        private bool TryParseIf(
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            if (!TryEnterCompound(out error))
            {
                return false;
            }

            var ifToken = _tokens[_position];
            var attribution = SnapshotAttribution();
            _compoundDepth++;
            try
            {
                return TryParseIfBody(ifToken, attribution, out command, out error);
            }
            finally
            {
                _compoundDepth--;
            }
        }

        private bool TryParseIfBody(
            BashToken ifToken,
            (string? Cwd, bool IsDynamic) attribution,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            var branches = new List<ConditionalBranchSyntax>();
            ShellBlockSyntax? @else = null;
            while (true)
            {
                var branchKeyword = _tokens[_position++];
                var conditionStart = branchKeyword.SourceStart + branchKeyword.SourceLength;
                if (!TryParseRequiredList(ThenWord, "condition", out var condition, out error))
                {
                    return false;
                }

                if (!IsWord("then"))
                {
                    error = "Bash if statement is missing 'then'";
                    return false;
                }

                var thenToken = _tokens[_position++];
                if (!TryParseRequiredList(BranchEndWords, "body", out var body, out error))
                {
                    return false;
                }

                if (_position == _tokens.Count)
                {
                    error = "Bash if statement is missing 'fi'";
                    return false;
                }

                var bodyEnd = _tokens[_position].SourceStart;
                branches.Add(new ConditionalBranchSyntax
                {
                    Condition = Block(condition!, conditionStart, thenToken.SourceStart),
                    Body = Block(body!, thenToken.SourceStart + thenToken.SourceLength, bodyEnd),
                    SourceStart = branchKeyword.SourceStart,
                    SourceLength = bodyEnd - branchKeyword.SourceStart,
                });
                if (!IsWord("elif"))
                {
                    break;
                }
            }

            if (IsWord("else"))
            {
                var elseToken = _tokens[_position++];
                if (!TryParseRequiredList(FiWord, "body", out var elseBody, out error))
                {
                    return false;
                }

                if (_position == _tokens.Count)
                {
                    error = "Bash if statement is missing 'fi'";
                    return false;
                }

                @else = Block(
                    elseBody!,
                    elseToken.SourceStart + elseToken.SourceLength,
                    _tokens[_position].SourceStart);
            }

            if (!IsWord("fi"))
            {
                error = "Bash if statement is missing 'fi'";
                return false;
            }

            var fiToken = _tokens[_position++];
            RestoreAttribution(attribution);
            command = new ConditionalSyntax
            {
                Branches = branches,
                Else = @else,
                SourceStart = ifToken.SourceStart,
                SourceLength = fiToken.SourceStart + fiToken.SourceLength - ifToken.SourceStart,
            };
            return true;
        }

        /// <summary>
        /// Parses <c>case WORD in [(]PATTERN[|PATTERN]...) LIST ;; ... esac</c>
        /// (#212). The subject and the patterns are words without a command
        /// substitution. A named expansion in them uses the same gate as a
        /// command word. Only the <c>;;</c> item terminator is supported.
        /// </summary>
        private bool TryParseCase(
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            if (!TryEnterCompound(out error))
            {
                return false;
            }

            var caseToken = _tokens[_position++];
            if (_position == _tokens.Count ||
                _tokens[_position].Kind is not (BashTokenKind.Word or BashTokenKind.QuotedString))
            {
                error = "Bash case statement requires one subject word";
                return false;
            }

            var subjectToken = _tokens[_position++];
            if (!TryAcceptCaseWord(subjectToken, out error))
            {
                return false;
            }

            if (!IsWord("in"))
            {
                error = "Bash case statement is missing 'in'";
                return false;
            }

            _position++;
            SkipNewlines();
            var attribution = SnapshotAttribution();
            _compoundDepth++;
            try
            {
                return TryParseCaseItems(caseToken, subjectToken, attribution, out command, out error);
            }
            finally
            {
                _compoundDepth--;
            }
        }

        private bool TryParseCaseItems(
            BashToken caseToken,
            BashToken subjectToken,
            (string? Cwd, bool IsDynamic) attribution,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            var items = new List<CaseItemSyntax>();
            while (!IsWord("esac"))
            {
                if (_position == _tokens.Count)
                {
                    error = "Bash case statement is missing 'esac'";
                    return false;
                }

                var itemStart = _tokens[_position].SourceStart;
                if (IsOperator("("))
                {
                    _position++;
                }

                var patterns = new List<ShellSourceFragment>();
                while (true)
                {
                    if (_position == _tokens.Count ||
                        _tokens[_position].Kind is not (BashTokenKind.Word or BashTokenKind.QuotedString))
                    {
                        error = "Bash case item requires a pattern word";
                        return false;
                    }

                    var pattern = _tokens[_position++];
                    if (!TryAcceptCaseWord(pattern, out error))
                    {
                        return false;
                    }

                    patterns.Add(Fragment(pattern));
                    if (IsOperator("|"))
                    {
                        _position++;
                        continue;
                    }

                    break;
                }

                if (!IsOperator(")"))
                {
                    error = "Bash case item is missing ')'";
                    return false;
                }

                var closeToken = _tokens[_position++];
                if (!TryParseList(
                        stopAtRightParen: false,
                        EsacWord,
                        CompoundOperator.None,
                        out var body,
                        out error,
                        stopAtCaseTerminator: true))
                {
                    return false;
                }

                var bodyEnd = _position < _tokens.Count
                    ? _tokens[_position].SourceStart
                    : _sourceStart + _sourceLength;
                var bodyStart = closeToken.SourceStart + closeToken.SourceLength;
                items.Add(new CaseItemSyntax
                {
                    Patterns = patterns,
                    Body = body is null
                        ? new ShellBlockSyntax
                        {
                            SourceStart = bodyStart,
                            SourceLength = bodyEnd - bodyStart,
                        }
                        : Block(body, bodyStart, bodyEnd),
                    SourceStart = itemStart,
                    SourceLength = bodyEnd - itemStart,
                });
                if (IsCaseTerminator())
                {
                    _position += 2;
                    SkipNewlines();
                    continue;
                }

                if (!IsWord("esac"))
                {
                    error = "Bash case item must end with ';;' or 'esac'";
                    return false;
                }
            }

            var esacToken = _tokens[_position++];
            RestoreAttribution(attribution);
            command = new CaseSyntax
            {
                Subject = Fragment(subjectToken),
                Items = items,
                SourceStart = caseToken.SourceStart,
                SourceLength = esacToken.SourceStart + esacToken.SourceLength - caseToken.SourceStart,
            };
            error = null;
            return true;
        }

        private bool TryAcceptCaseWord(BashToken token, out string? error)
        {
            foreach (var value in TokenValues(new[] { token }))
            {
                foreach (var fragment in value.Fragments)
                {
                    // Bash does not brace-expand a case word, but the lexer
                    // marks the word before it knows its role (#227).
                    if (fragment.Kind == ShellValueFragmentKind.Opaque &&
                        fragment.OpaqueCause == ShellOpaqueCause.BraceExpansion)
                    {
                        error = "a brace list in a Bash case word is not supported";
                        return false;
                    }

                    if (fragment.Kind == ShellValueFragmentKind.Opaque)
                    {
                        error = "a command substitution in a Bash case word is not supported";
                        return false;
                    }
                }
            }

            var tokens = new[] { token };
            if (ContainsNamedParameterExpansion(tokens) &&
                !AllowsUnknownNamedReads &&
                !CanPublishActiveLoopBindingFacts(tokens) &&
                !CanPublishBoundedAssignmentFacts(tokens) &&
                !CanPublishLaunchFacts(tokens))
            {
                error = "Bash named parameter expansion requires proved variable-attribute state";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// True for a direct, bounded <c>read</c> (#212) in the top-level
        /// shell under the fresh-process mode. The state pass binds each name
        /// to an unknown value.
        /// </summary>
        /// <summary>
        /// True when a named expansion of any name is safe to read as an
        /// unknown value (#221). A new non-interactive process imports each
        /// environment entry as an exported scalar, so no ambient nameref or
        /// array attribute can run code. The isolated and fresh-process modes
        /// both prove this until the source makes an unmodeled variable
        /// change.
        /// </summary>
        private bool AllowsUnknownNamedReads =>
            _options.InitialStateMode is BashInitialStateMode.IsolatedNonInteractive or
                BashInitialStateMode.FreshNonInteractiveNoStartup &&
            !_hasUnmodeledVariableStateMutation;

        private bool IsModeledSetPositional(Clause clause) =>
            _options.InitialStateMode == BashInitialStateMode.FreshNonInteractiveNoStartup &&
            BashSetPositionalBuiltin.IsBounded(clause);

        /// <summary>
        /// A direct <c>export</c> in the top-level shell or a loop body of it,
        /// under the fresh-process mode, with no redirect (#221).
        /// </summary>
        private bool IsModeledExportCandidate(Clause clause, IReadOnlyList<BashToken> tokens) =>
            _options.InitialStateMode == BashInitialStateMode.FreshNonInteractiveNoStartup &&
            _bashCDepth == 0 && _structuralDepth == 0 && _subshellDepth == 0 &&
            !_hasUnmodeledShellStateMutation && !_hasUnmodeledVariableStateMutation &&
            !clause.Verb.IsDynamic &&
            clause.Verb.Tokens.Count > 0 &&
            string.Equals(clause.Verb.Tokens[0], "export", StringComparison.Ordinal) &&
            tokens.Count > 0 &&
            tokens[0].Kind == BashTokenKind.Word &&
            string.Equals(SourceSlice(_source, tokens[0]), "export", StringComparison.Ordinal) &&
            clause.Redirects.Count == 0;

        /// <summary>
        /// Accepts <c>export NAME=value...</c> and <c>export NAME...</c>
        /// (#221). Each assignment uses the bounded assignment rules and is
        /// a shell-state assignment that can reach a child process. An option
        /// other than a lone <c>-p</c> query fails closed.
        /// </summary>
        private bool TryAcceptExport(
            IReadOnlyList<BashToken> tokens,
            out BashExportFacts? exports,
            out string? error)
        {
            exports = null;
            var assignments = new List<(ShellVariableAssignment Assignment, BashAssignmentValue Value)>();
            var names = new List<string>();
            error = null;
            if (tokens.Count == 2 &&
                tokens[1].Kind == BashTokenKind.Word &&
                string.Equals(SourceSlice(_source, tokens[1]), "-p", StringComparison.Ordinal))
            {
                exports = new BashExportFacts(assignments, names);
                return true;
            }

            for (var index = 1; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (token.Kind == BashTokenKind.Word && IsAssignmentWord(token))
                {
                    if (!TryCreateBoundedAssignment(
                            token,
                            ShellVariableAssignmentScope.ShellState,
                            out var assignment,
                            out var value,
                            out error))
                    {
                        return false;
                    }

                    assignments.Add((assignment!, value!));
                    names.Add(assignment!.Name);
                    continue;
                }

                if (token.Kind != BashTokenKind.Word ||
                    !HasExactLiteralValue(token) ||
                    !string.Equals(SourceSlice(_source, token), token.Value, StringComparison.Ordinal) ||
                    !BashVariableAssignmentGrammar.IsEligibleCommandEnvironmentName(token.Value))
                {
                    error = "Bash export accepts only bounded names and assignments";
                    return false;
                }

                names.Add(token.Value);
            }

            foreach (var (assignment, _) in assignments)
            {
                if (_activeLoopBindings.Contains(assignment.Name))
                {
                    error = "a Bash loop binding cannot be reassigned in its loop";
                    return false;
                }

                if (_loopDepth > 0 &&
                    _options.LaunchEnvironment?.TryGetLiveValue(assignment.Name, out _) == true)
                {
                    error = "a Bash launch variable assignment inside a loop is not supported";
                    return false;
                }
            }

            foreach (var (assignment, _) in assignments)
            {
                RevokeLaunchFact(assignment.Name);
                _boundedAssignmentNames.Add(assignment.Name);
            }

            exports = new BashExportFacts(assignments, names);
            return true;
        }

        private bool IsModeledRead(Clause clause) =>
            _bashCDepth == 0 &&
            _options.InitialStateMode == BashInitialStateMode.FreshNonInteractiveNoStartup &&
            !_hasUnmodeledVariableStateMutation &&
            BashReadBuiltin.IsBoundedRead(clause);

        private bool TryAcceptReadNames(Clause clause, out string? error)
        {
            BashReadBuiltin.TryGetNames(clause, out var names);
            foreach (var (name, _) in names)
            {
                if (_activeLoopBindings.Contains(name))
                {
                    error = "a Bash loop binding cannot be reassigned in its loop";
                    return false;
                }

                if (_loopDepth > 0 &&
                    _options.LaunchEnvironment?.TryGetLiveValue(name, out _) == true)
                {
                    error = "a Bash launch variable assignment inside a loop is not supported";
                    return false;
                }

                RevokeLaunchFact(name);
                _boundedAssignmentNames.Add(name);
            }

            error = null;
            return true;
        }

        private bool TryEnterCompound(out string? error)
        {
            if (_hasUnmodeledShellStateMutation)
            {
                error = "a Bash compound command after prior shell-state mutation requires structure-aware state analysis";
                return false;
            }

            if (_structuralDepth + _subshellDepth + _loopDepth + _compoundDepth >=
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                error = "Bash structural nesting depth exceeded (>16)";
                return false;
            }

            error = null;
            return true;
        }

        private bool TryParseRequiredList(
            string[] stopWords,
            string part,
            out ShellSyntaxNode? command,
            out string? error)
        {
            if (!TryParseList(
                    stopAtRightParen: false,
                    stopWords,
                    CompoundOperator.None,
                    out command,
                    out error))
            {
                return false;
            }

            if (command is null)
            {
                error = $"Bash compound command {part} cannot be empty";
                return false;
            }

            return true;
        }

        private static ShellBlockSyntax Block(ShellSyntaxNode statement, int start, int end) =>
            new()
            {
                Statements = new[] { statement },
                SourceStart = start,
                SourceLength = Math.Max(0, end - start),
            };

        private ShellSourceFragment Fragment(BashToken token) =>
            new()
            {
                Raw = SourceSlice(_source, token),
                SourceStart = token.SourceStart,
                SourceLength = token.SourceLength,
            };

        // A `cd` inside a branch or a loop can run or not. The compatibility
        // attribution after the compound is then not proved. The state pass
        // owns the exact directory facts.
        private (string? Cwd, bool IsDynamic) SnapshotAttribution() =>
            (_attribution.ResolvedCwd, _attribution.IsDynamic);

        private void RestoreAttribution((string? Cwd, bool IsDynamic) before)
        {
            if (!string.Equals(before.Cwd, _attribution.ResolvedCwd, StringComparison.Ordinal) ||
                before.IsDynamic != _attribution.IsDynamic)
            {
                _attribution.SetDynamicAttribution();
            }
        }

        private bool TryParseSubshell(
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            if (_structuralDepth + _subshellDepth + _loopDepth + _compoundDepth >=
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                command = null;
                error = "Bash structural nesting depth exceeded (>16)";
                return false;
            }

            if (IsArithmeticCommandStart())
            {
                command = null;
                error = "Bash arithmetic command '((…))' is not supported";
                return false;
            }

            var open = _tokens[_position++];
            var outerMutationState = _hasUnmodeledShellStateMutation;
            var outerVariableMutationState = _hasUnmodeledVariableStateMutation;
            _attribution.PushForSubshell();
            _subshellDepth++;
            var parsed = TryParseList(
                stopAtRightParen: true,
                stopWords: null,
                compatibilityOperator,
                out var bodyCommand,
                out error);
            _subshellDepth--;
            _attribution.PopForSubshell();
            _hasUnmodeledShellStateMutation = outerMutationState;
            _hasUnmodeledVariableStateMutation = outerVariableMutationState;

            if (!parsed)
            {
                command = null;
                return false;
            }

            if (_position == _tokens.Count || !IsOperator(")"))
            {
                command = null;
                error = $"unbalanced parens at position {_sourceStart + _sourceLength}";
                return false;
            }

            var close = _tokens[_position++];
            var body = new ShellBlockSyntax
            {
                Statements = bodyCommand is null
                    ? Array.Empty<ShellSyntaxNode>()
                    : new[] { bodyCommand },
                SourceStart = open.SourceStart + open.SourceLength,
                SourceLength = close.SourceStart - open.SourceStart - open.SourceLength,
            };
            command = new GroupSyntax
            {
                GroupKind = ShellGroupKind.IsolatedScope,
                Body = body,
                SourceStart = open.SourceStart,
                SourceLength = close.SourceStart + close.SourceLength - open.SourceStart,
            };
            return true;
        }

        /// <summary>
        /// True when the <c>(</c> at the current position starts a Bash
        /// arithmetic command <c>((…))</c> (#227). Bash reads <c>((</c> as an
        /// arithmetic command when the parenthesis that closes the second
        /// <c>(</c> is followed at once by <c>)</c>. Otherwise it reads two
        /// subshells. An arithmetic command can assign variables, for example
        /// <c>(( p = 0 ))</c>, so it must not parse as a subshell.
        /// </summary>
        private bool IsArithmeticCommandStart()
        {
            var first = _position;
            if (first + 1 >= _tokens.Count ||
                !IsOperatorToken(_tokens[first + 1], "(") ||
                _tokens[first + 1].SourceStart !=
                    _tokens[first].SourceStart + _tokens[first].SourceLength)
            {
                return false;
            }

            var depth = 0;
            for (var index = first + 1; index < _tokens.Count; index++)
            {
                var token = _tokens[index];
                if (IsOperatorToken(token, "("))
                {
                    depth++;
                    continue;
                }

                if (!IsOperatorToken(token, ")"))
                {
                    continue;
                }

                depth--;
                if (depth > 0)
                {
                    continue;
                }

                return index + 1 < _tokens.Count &&
                    IsOperatorToken(_tokens[index + 1], ")") &&
                    _tokens[index + 1].SourceStart == token.SourceStart + token.SourceLength;
            }

            return false;
        }

        private static bool IsOperatorToken(BashToken token, string text) =>
            token.Kind == BashTokenKind.Operator &&
            string.Equals(token.OperatorText, text, StringComparison.Ordinal);

        /// <summary>
        /// Replaces the items of one and-or list with one background group
        /// (#215). The group keeps the operator that came before the list.
        /// </summary>
        private static void WrapBackgroundList(
            List<CommandListItemSyntax> items,
            int andOrStart,
            BashToken ampersand)
        {
            var range = items.GetRange(andOrStart, items.Count - andOrStart);
            var nodes = new List<ShellSyntaxNode>(range.Count);
            foreach (var item in range)
            {
                nodes.Add(item.Command);
            }

            ShellSyntaxNode bodyCommand;
            if (range.Count == 1)
            {
                bodyCommand = range[0].Command;
            }
            else
            {
                var inner = new List<CommandListItemSyntax>(range.Count);
                for (var index = 0; index < range.Count; index++)
                {
                    inner.Add(index == 0
                        ? range[index] with { Operator = CompoundOperator.None }
                        : range[index]);
                }

                bodyCommand = new CommandListSyntax
                {
                    Items = inner,
                    SourceStart = CombinedStart(nodes),
                    SourceLength = CombinedLength(nodes),
                };
            }

            var start = CombinedStart(nodes);
            var length = CombinedLength(nodes);
            var group = new GroupSyntax
            {
                GroupKind = ShellGroupKind.Background,
                Body = new ShellBlockSyntax
                {
                    Statements = new[] { bodyCommand },
                    SourceStart = start,
                    SourceLength = length,
                },
                SourceStart = start,
                SourceLength = start is int groupStart
                    ? ampersand.SourceStart + ampersand.SourceLength - groupStart
                    : null,
            };
            var precedingOperator = range[0].Operator;
            items.RemoveRange(andOrStart, range.Count);
            items.Add(new CommandListItemSyntax
            {
                Operator = precedingOperator,
                Command = group,
            });
        }

        private bool TryReadListOperator(out CompoundOperator @operator)
        {
            @operator = CompoundOperator.None;
            if (_position == _tokens.Count)
            {
                return false;
            }

            var token = _tokens[_position];
            if (token.Kind == BashTokenKind.Whitespace)
            {
                @operator = CompoundOperator.Sequence;
                _position++;
                return true;
            }

            if (token.Kind != BashTokenKind.Operator)
            {
                return false;
            }

            @operator = token.OperatorText switch
            {
                "&&" => CompoundOperator.AndIf,
                "||" => CompoundOperator.OrIf,
                ";" => CompoundOperator.Sequence,
                _ => CompoundOperator.None,
            };
            if (@operator == CompoundOperator.None)
            {
                return false;
            }

            _position++;
            return true;
        }

        private void SkipNewlines()
        {
            while (_position < _tokens.Count &&
                   _tokens[_position].Kind == BashTokenKind.Whitespace)
            {
                _position++;
            }
        }

        private bool IsOperator(string value) =>
            _position < _tokens.Count &&
            _tokens[_position].Kind == BashTokenKind.Operator &&
            string.Equals(_tokens[_position].OperatorText, value, StringComparison.Ordinal);

        private bool IsWord(string value) =>
            _position < _tokens.Count &&
            _tokens[_position].Kind == BashTokenKind.Word &&
            HasExactLiteralValue(_tokens[_position]) &&
            string.Equals(
                SourceSlice(_source, _tokens[_position]),
                value,
                StringComparison.Ordinal) &&
            string.Equals(_tokens[_position].Value, value, StringComparison.Ordinal);

        private static readonly string[] DoneWord = { "done" };
        private static readonly string[] DoWord = { "do" };
        private static readonly string[] ThenWord = { "then" };
        private static readonly string[] BranchEndWords = { "elif", "else", "fi" };
        private static readonly string[] FiWord = { "fi" };
        private static readonly string[] EsacWord = { "esac" };
        private static readonly string[] StrayKeywords =
            { "do", "done", "then", "elif", "else", "fi", "esac" };

        // `;;` ends a case item. The lexer gives two adjacent `;` operators.
        private bool IsCaseTerminator() =>
            IsOperator(";") &&
            _position + 1 < _tokens.Count &&
            _tokens[_position + 1].Kind == BashTokenKind.Operator &&
            _tokens[_position + 1].OperatorText == ";" &&
            _tokens[_position + 1].SourceStart ==
                _tokens[_position].SourceStart + _tokens[_position].SourceLength;

        private bool IsAnyWord(string[]? words)
        {
            if (words is null)
            {
                return false;
            }

            foreach (var word in words)
            {
                if (IsWord(word))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsListTerminator() =>
            IsOperator(";") ||
            _position < _tokens.Count &&
            _tokens[_position].Kind == BashTokenKind.Whitespace &&
            _tokens[_position].IsStatementSeparator;

        private BashParserOptions CurrentOptions() =>
            _attribution.HasAttribution && !_attribution.IsDynamic
                ? _options with { WorkingDirectory = _attribution.ResolvedCwd }
                : _options;

        private bool TryCollectCommandSubstitutions(
            IReadOnlyList<BashToken> tokens,
            bool rejectCommandNameSubstitution,
            out IReadOnlyList<ShellValueFragment> substitutions,
            out string? error)
        {
            var discovered = new List<ShellValueFragment>();
            var commandNameEnd = tokens.Count == 0
                ? _sourceStart
                : tokens[0].SourceStart + tokens[0].SourceLength;
            for (var index = 1; rejectCommandNameSubstitution && index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (token.Kind == BashTokenKind.Operator ||
                    token.SourceStart != commandNameEnd)
                {
                    break;
                }

                commandNameEnd = token.SourceStart + token.SourceLength;
            }

            foreach (var token in tokens)
            {
                foreach (var value in new[] { token.ResolverValue, token.HeredocBodyValue })
                {
                    if (value is null)
                    {
                        continue;
                    }

                    foreach (var fragment in value.Fragments)
                    {
                        if (fragment.Kind == ShellValueFragmentKind.Opaque &&
                            fragment.OpaqueCause is ShellOpaqueCause.ArithmeticExpansion or
                                ShellOpaqueCause.BraceExpansion)
                        {
                            // An arithmetic value or a brace word names no
                            // program that the parser can prove (#227).
                            if (rejectCommandNameSubstitution &&
                                (fragment.SourceStart is not int expansionStart ||
                                 expansionStart < commandNameEnd))
                            {
                                substitutions = Array.Empty<ShellValueFragment>();
                                error = fragment.OpaqueCause == ShellOpaqueCause.ArithmeticExpansion
                                    ? "Bash command-name arithmetic expansion is not supported"
                                    : "Bash command-name brace expansion is not supported";
                                return false;
                            }

                            continue;
                        }

                        if (fragment.Kind != ShellValueFragmentKind.Opaque ||
                            fragment.OpaqueCause != ShellOpaqueCause.CommandSubstitution)
                        {
                            continue;
                        }

                        if (fragment.SourceStart is null || fragment.SourceLength is null ||
                            fragment.SourceLength < 2 ||
                            fragment.SourceStart < _sourceStart ||
                            fragment.SourceStart + fragment.SourceLength >
                                _sourceStart + _sourceLength)
                        {
                            substitutions = Array.Empty<ShellValueFragment>();
                            error = "Bash command substitution has invalid source provenance";
                            return false;
                        }

                        var raw = _source.Substring(
                            fragment.SourceStart.Value,
                            fragment.SourceLength.Value);
                        if (raw[0] == '`')
                        {
                            substitutions = Array.Empty<ShellValueFragment>();
                            error = "legacy backtick command substitution is not supported";
                            return false;
                        }

                        if (!raw.StartsWith("$(", StringComparison.Ordinal) ||
                            raw[raw.Length - 1] != ')')
                        {
                            substitutions = Array.Empty<ShellValueFragment>();
                            error = "unsupported Bash command substitution provenance";
                            return false;
                        }

                        if (rejectCommandNameSubstitution &&
                            fragment.SourceStart < commandNameEnd)
                        {
                            substitutions = Array.Empty<ShellValueFragment>();
                            error = "Bash command-name substitution is not supported";
                            return false;
                        }

                        discovered.Add(fragment);
                    }
                }
            }

            substitutions = discovered;
            error = null;
            return true;
        }

        private bool TryParseIteratorSubstitutions(
            IReadOnlyList<BashToken> iterableWords,
            BashParserOptions options,
            int sourceStart,
            int sourceLength,
            out ShellBlockSyntax iteratorCommands,
            out string? error)
        {
            if (!TryCollectCommandSubstitutions(
                    iterableWords,
                    rejectCommandNameSubstitution: false,
                    out var fragments,
                    out error))
            {
                iteratorCommands = new ShellBlockSyntax();
                return false;
            }

            if (!TryParseCommandSubstitutions(
                    fragments,
                    options,
                    out var substitutions,
                    out error))
            {
                iteratorCommands = new ShellBlockSyntax();
                return false;
            }

            var statements = new ShellSyntaxNode[substitutions.Count];
            for (var index = 0; index < substitutions.Count; index++)
            {
                statements[index] = substitutions[index];
            }

            iteratorCommands = new ShellBlockSyntax
            {
                Statements = statements,
                SourceStart = sourceStart,
                SourceLength = sourceLength,
            };
            return true;
        }

        private bool IsAssignmentWord(BashToken token)
        {
            var spelling = _source.Substring(token.SourceStart, token.SourceLength)
                .Replace("\\\r\n", string.Empty)
                .Replace("\\\n", string.Empty)
                .Replace("\\\r", string.Empty);
            var index = 0;
            if (spelling.Length == 0 || !IsBashIdentifierStart(spelling[index]))
            {
                return false;
            }

            index++;
            while (index < spelling.Length && IsBashIdentifierContinuation(spelling[index]))
            {
                index++;
            }

            if (index < spelling.Length && spelling[index] == '[')
            {
                while (index < spelling.Length && spelling[index] != ']')
                {
                    index++;
                }

                if (index >= spelling.Length)
                {
                    return false;
                }

                index++;
            }

            if (index < spelling.Length && spelling[index] == '+')
            {
                index++;
            }

            return index < spelling.Length && spelling[index] == '=';
        }

        private bool TryCreateBoundedAssignment(
            BashToken token,
            ShellVariableAssignmentScope scope,
            out ShellVariableAssignment? assignment,
            out BashAssignmentValue? assignmentValue,
            out string? error)
        {
            assignment = null;
            assignmentValue = null;
            if (_hasUnmodeledShellStateMutation || _hasUnmodeledVariableStateMutation)
            {
                error = "bounded Bash assignments cannot follow an unmodeled state mutation";
                return false;
            }

            if (_options.InitialStateMode !=
                BashInitialStateMode.FreshNonInteractiveNoStartup)
            {
                error = "Bash assignment-prefix commands are not supported without fresh non-interactive state";
                return false;
            }

            var spelling = _source.Substring(token.SourceStart, token.SourceLength)
                .Replace("\\\r\n", string.Empty)
                .Replace("\\\n", string.Empty)
                .Replace("\\\r", string.Empty);
            var equals = spelling.IndexOf('=');
            var name = equals > 0 ? spelling.Substring(0, equals) : string.Empty;

            // One name gate for both scopes (#209). An upper-case name is
            // accepted when Bash does not own it and it does not control
            // command resolution, startup, or the loader.
            if (!BashVariableAssignmentGrammar.IsEligibleCommandEnvironmentName(name))
            {
                error = "bounded Bash assignment names must use the ordinary scalar boundary";
                return false;
            }

            if (!IsBoundedAssignmentValueSpelling(
                    spelling.Substring(equals + 1),
                    out var leadingTilde))
            {
                error = "bounded Bash assignment values must exclude shell-native expansion syntax";
                return false;
            }

            if (token.ResolverValue is not ShellValue value ||
                value.Decoded.Length < name.Length + 1 ||
                !string.Equals(
                    value.Decoded.Substring(0, name.Length + 1),
                    name + "=",
                    StringComparison.Ordinal))
            {
                error = "bounded Bash assignment values must be exact static scalars";
                return false;
            }

            var rightHandSide = value.Slice(name.Length + 1);
            if (!TryClassifyAssignmentFragments(
                    rightHandSide,
                    out var hasNamedExpansion,
                    out var hasSubstitution,
                    out var hasArithmetic))
            {
                error = "bounded Bash assignment values must be exact static scalars";
                return false;
            }

            // A named expansion in the value reads a bounded binding or a
            // live launch value, with the same gate as a command word.
            if (hasNamedExpansion &&
                !AllowsUnknownNamedReads &&
                !CanReadBoundedNames(new[] { rightHandSide }) &&
                !CanReadLaunchNames(new[] { rightHandSide }))
            {
                error = "Bash named parameter expansion requires proved variable-attribute state";
                return false;
            }

            // The state pass computes the effective value of an expanded
            // right-hand side. The authored value is exact only for literal
            // text, because Bash transforms every other part.
            var isLiteral = !hasNamedExpansion && !hasSubstitution && !hasArithmetic &&
                !leadingTilde;
            ShellValueDomain domain = isLiteral
                ? new ShellValueDomain.Exact(rightHandSide.Decoded)
                : new ShellValueDomain.Unknown();
            assignment = new ShellVariableAssignment
            {
                Name = name,
                AuthoredValue = domain,
                EffectiveValue = domain,
                Scope = scope,
                MayAffectProcessEnvironment = true,
                SourceStart = token.SourceStart,
                SourceLength = token.SourceLength,
            };
            assignmentValue = new BashAssignmentValue(
                rightHandSide,
                leadingTilde,
                hasSubstitution,
                _options.LaunchEnvironment);
            error = null;
            return true;
        }

        /// <summary>
        /// Accepts a right-hand side made of unquoted safe characters,
        /// single-quoted text, double-quoted text, <c>$name</c>,
        /// <c>${name}</c>, <c>$(...)</c> (#209), and a bounded
        /// <c>$((...))</c> (#227). A leading <c>~</c> or
        /// <c>~/</c> is reported, because Bash expands it from <c>HOME</c>.
        /// Every other tilde, a backslash, a backtick, ANSI-C and locale
        /// quotes, other arithmetic, and a complex parameter expansion fail closed.
        /// Bash does not split or glob an assignment value, but the parser
        /// rejects unquoted whitespace, glob, and brace characters so that
        /// one value stays one word.
        /// </summary>
        private static bool IsBoundedAssignmentValueSpelling(string value, out bool leadingTilde)
        {
            leadingTilde = false;
            var index = 0;
            if (value.Length > 0 && value[0] == '~')
            {
                if (value.Length > 1 && value[1] != '/')
                {
                    return false;
                }

                leadingTilde = true;
                index = 1;
            }

            var inDouble = false;
            while (index < value.Length)
            {
                var character = value[index];
                if (character == '\'' && !inDouble)
                {
                    var close = value.IndexOf('\'', index + 1);
                    if (close < 0)
                    {
                        return false;
                    }

                    index = close + 1;
                    continue;
                }

                if (character == '"')
                {
                    inDouble = !inDouble;
                    index++;
                    continue;
                }

                if (character == '$')
                {
                    if (!TrySkipBoundedExpansion(value, ref index))
                    {
                        return false;
                    }

                    continue;
                }

                if (character is '`' or '\\')
                {
                    return false;
                }

                if (!inDouble && !IsExactUnquotedAssignmentCharacter(character))
                {
                    return false;
                }

                index++;
            }

            return !inDouble;
        }

        private static bool TrySkipBoundedExpansion(string value, ref int index)
        {
            if (index + 1 >= value.Length)
            {
                return false;
            }

            var next = value[index + 1];
            if (next == '(')
            {
                if (index + 2 < value.Length && value[index + 2] == '(')
                {
                    if (!BashArithmeticGrammar.TryScanExpansion(value.AsSpan(), index, out var arithmeticEnd))
                    {
                        return false;
                    }

                    index = arithmeticEnd;
                    return true;
                }

                if (!BashLexer.TryFindCommandSubstitutionEnd(value.AsSpan(), index + 1, out var close))
                {
                    return false;
                }

                index = close + 1;
                return true;
            }

            if (next == '{')
            {
                var close = value.IndexOf('}', index + 2);
                if (close < 0 || !IsBashIdentifier(value.Substring(index + 2, close - index - 2)))
                {
                    return false;
                }

                index = close + 1;
                return true;
            }

            // `$!` (the last background job), `$?` (the last status), and a
            // positional parameter `$1`..`$9` are always defined and run no
            // code. Their value is unknown (#215).
            if (next is '!' or '?' or >= '1' and <= '9')
            {
                index += 2;
                return true;
            }

            if (!IsBashIdentifierStart(next))
            {
                return false;
            }

            index += 2;
            while (index < value.Length && IsBashIdentifierContinuation(value[index]))
            {
                index++;
            }

            return true;
        }

        private static bool TryClassifyAssignmentFragments(
            ShellValue value,
            out bool hasNamedExpansion,
            out bool hasSubstitution,
            out bool hasArithmetic)
        {
            hasNamedExpansion = false;
            hasSubstitution = false;
            hasArithmetic = false;
            foreach (var fragment in value.Fragments)
            {
                // The state pass proves the reads of a bounded arithmetic
                // expansion before it accepts the statement (#227).
                if (fragment.Kind == ShellValueFragmentKind.Opaque &&
                    fragment.OpaqueCause == ShellOpaqueCause.ArithmeticExpansion)
                {
                    hasArithmetic = true;
                    continue;
                }

                if (fragment.Kind == ShellValueFragmentKind.Literal &&
                    fragment.Cardinality == ShellValueCardinality.ExactlyOne &&
                    fragment.Expansion is null)
                {
                    continue;
                }

                if (fragment.Kind == ShellValueFragmentKind.Expansion &&
                    fragment.Expansion is { Kind: ShellExpansionKind.Variable, Name: not null } &&
                    fragment.Cardinality == ShellValueCardinality.ExactlyOne)
                {
                    hasNamedExpansion = true;
                    continue;
                }

                if (fragment.Kind == ShellValueFragmentKind.Opaque &&
                    fragment.OpaqueCause == ShellOpaqueCause.CommandSubstitution ||
                    fragment.Kind == ShellValueFragmentKind.Expansion &&
                    (fragment.Expansion is { Kind: ShellExpansionKind.SpecialParameter, Name: "!" or "?" } ||
                     fragment.Expansion is { Kind: ShellExpansionKind.PositionalParameter, Name.Length: 1 }))
                {
                    // The parser cannot prove the value, so the binding is
                    // unknown, as for a substitution.
                    hasSubstitution = true;
                    continue;
                }

                return false;
            }

            return true;
        }

        private bool TryParseAssignmentSubstitutions(
            IReadOnlyList<BashToken> tokens,
            out IReadOnlyList<CommandSubstitutionSyntax> substitutions,
            out string? error)
        {
            substitutions = Array.Empty<CommandSubstitutionSyntax>();
            return TryCollectCommandSubstitutions(
                       tokens,
                       rejectCommandNameSubstitution: false,
                       out var fragments,
                       out error) &&
                   TryParseCommandSubstitutions(
                       fragments,
                       _options,
                       out substitutions,
                       out error);
        }

        private static bool IsExactUnquotedAssignmentCharacter(char value) =>
            value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ||
            value is '_' or '.' or '/' or ',' or ':' or '+' or '-' or '@' or '%' or '=';

        private static bool IsBuiltin(Clause clause) =>
            clause.Verb.Tokens.Count > 0 && BashBuiltins.Contains(clause.Verb.Tokens[0]);

        private static bool IsBashIdentifierStart(char value) =>
            value == '_' || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

        private static bool IsBashIdentifierContinuation(char value) =>
            IsBashIdentifierStart(value) || value is >= '0' and <= '9';

        private bool TryParseCommandSubstitutions(
            IReadOnlyList<ShellValueFragment> fragments,
            BashParserOptions options,
            out IReadOnlyList<CommandSubstitutionSyntax> substitutions,
            out string? error)
        {
            if (fragments.Count == 0)
            {
                substitutions = Array.Empty<CommandSubstitutionSyntax>();
                error = null;
                return true;
            }

            if (_structuralDepth + _subshellDepth + _loopDepth + _compoundDepth + 1 >
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                substitutions = Array.Empty<CommandSubstitutionSyntax>();
                error = "Bash structural nesting depth exceeded (>16)";
                return false;
            }

            var parsed = new List<CommandSubstitutionSyntax>(fragments.Count);
            foreach (var fragment in fragments)
            {
                var sourceStart = fragment.SourceStart!.Value;
                var sourceLength = fragment.SourceLength!.Value;
                var bodyStart = sourceStart + 2;
                var bodyLength = sourceLength - 3;
                if (!TryParseSubstitutionBody(
                        bodyStart,
                        bodyLength,
                        options,
                        out var body,
                        out error))
                {
                    substitutions = Array.Empty<CommandSubstitutionSyntax>();
                    return false;
                }

                parsed.Add(new CommandSubstitutionSyntax
                {
                    Body = body,
                    SourceStart = sourceStart,
                    SourceLength = sourceLength,
                });
            }

            substitutions = parsed;
            error = null;
            return true;
        }

        private bool TryParseSubstitutionBody(
            int sourceStart,
            int sourceLength,
            BashParserOptions options,
            out ShellBlockSyntax body,
            out string? error)
        {
            var source = _source.Substring(sourceStart, sourceLength);
            var relativeTokens = BashLexer.Tokenize(source);
            for (var index = 0; index < relativeTokens.Count; index++)
            {
                if (relativeTokens[index].Kind == BashTokenKind.UnparseableSentinel)
                {
                    body = new ShellBlockSyntax();
                    error = relativeTokens[index].UnparseableReason;
                    return false;
                }
            }

            var significant = FilterSignificant(relativeTokens);
            if (TryDetectAnomaly(significant, out error))
            {
                body = new ShellBlockSyntax();
                return false;
            }

            var shifted = ShiftTokens(significant, sourceStart);
            var coordinator = new StructuralCoordinator(
                _source,
                shifted,
                options,
                _bashCDepth,
                _structuralDepth + _subshellDepth + _loopDepth + _compoundDepth + 1,
                _markBashCWrapped,
                sourceStart,
                sourceLength,
                _activeLoopBindings,
                _hasUnmodeledShellStateMutation,
                _hasUnmodeledVariableStateMutation,
                _boundedAssignmentNames);
            if (!coordinator.TryParse(out body, out error))
            {
                return false;
            }

            MergeFacts(coordinator);
            return true;
        }

        private void RegisterFacts(
            SimpleCommandSyntax simple,
            IReadOnlyList<BashToken> sourceTokens,
            IReadOnlyList<BashPathResolutionSeed> pathResolutions,
            BashParserOptions parseOptions,
            IReadOnlyList<BashAssignmentValue?> environmentAssignmentValues,
            BashExportFacts? exports,
            IReadOnlyList<ShellValueFragment> arithmeticExpansions)
        {
            var valueProvenance = new List<ShellValueElementProvenance>();
            var redirectProvenance = new List<RedirectTargetProvenance>();
            var cwdPathDependencies = new List<CwdPathDependency>();
            var redirectAnalysis = BashRedirectAnalysis.Analyze(
                simple.Clause,
                _source,
                sourceTokens);
            var redirectIndex = 0;
            Dictionary<int, string>? launchWordValues = null;
            for (var elementIndex = 0;
                 elementIndex < simple.Clause.Elements.Count;
                 elementIndex++)
            {
                var element = simple.Clause.Elements[elementIndex];
                var currentRedirectIndex = element.Role == ClauseElementRole.Redirect
                    ? redirectIndex++
                    : -1;
                if (!TryGetElementValue(element, sourceTokens, out var value))
                {
                    continue;
                }

                // Only the program word and the arguments are words that the
                // program receives. A redirect target is not a command word.
                if ((element.Role == ClauseElementRole.Argument ||
                     element.Role == ClauseElementRole.Verb && elementIndex == 0) &&
                    ShellLaunchFacts.TryExpandWord(value, parseOptions, out var launchWord))
                {
                    launchWordValues ??= new Dictionary<int, string>();
                    launchWordValues.Add(elementIndex, launchWord);
                }

                if (element.Role == ClauseElementRole.Argument)
                {
                    valueProvenance.Add(new ShellValueElementProvenance(
                        elementIndex,
                        value));
                }

                // A file redirect target with a wildcard keeps its shell value,
                // so the state pass can prove its pathname expansion (#206).
                if (currentRedirectIndex >= 0 &&
                    currentRedirectIndex < redirectAnalysis.Count &&
                    redirectAnalysis[currentRedirectIndex].IsPathRelevant &&
                    !redirectAnalysis[currentRedirectIndex].IsComplete &&
                    currentRedirectIndex < simple.Clause.Redirects.Count &&
                    TryGetElementValue(
                        element,
                        sourceTokens,
                        out var targetValue,
                        skipLeadingOperator: true) &&
                    HasGlobFragment(targetValue))
                {
                    redirectProvenance.Add(new RedirectTargetProvenance(
                        currentRedirectIndex,
                        elementIndex,
                        targetValue,
                        InvocationScopeDepth: 0));
                }

                if (currentRedirectIndex >= 0 &&
                    currentRedirectIndex < redirectAnalysis.Count &&
                    redirectAnalysis[currentRedirectIndex].Operation ==
                        RedirectOperation.HereString)
                {
                    redirectProvenance.Add(new RedirectTargetProvenance(
                        currentRedirectIndex,
                        elementIndex,
                        BashRedirectAnalysis.NormalizeHereStringOperand(value),
                        InvocationScopeDepth: 0));
                }
            }

            foreach (var pathResolution in pathResolutions)
            {
                var withoutCwd = BashResolver.Resolve(
                    pathResolution.ResolverValue,
                    treatAsPath: true,
                    parseOptions,
                    workingDirectoryUnknown: true,
                    consumer: pathResolution.Consumer);
                cwdPathDependencies.Add(new CwdPathDependency(
                    pathResolution.ClauseElementIndex,
                    pathResolution.ClauseArgumentIndex,
                    withoutCwd.Resolved is null,
                    pathResolution.ResolverValue.Decoded,
                    pathResolution.AuthoredValue,
                    parseOptions.WorkingDirectory ?? Environment.CurrentDirectory));
            }

            _facts.Add(simple.Clause, new CommandOccurrenceFacts
            {
                Redirects = redirectAnalysis,
                RedirectTargetProvenance = redirectProvenance.ToArray(),
                ValueProvenance = valueProvenance.ToArray(),
                CwdPathDependencies = cwdPathDependencies.ToArray(),
                IsComplete = simple.Clause.Verb.Tokens.Count > 0 &&
                    AreRedirectsComplete(redirectAnalysis) &&
                    !HasUnexpandedCommandString(simple.Clause),
                IsCompleteExceptRedirects = simple.Clause.Verb.Tokens.Count > 0 &&
                    !HasUnexpandedCommandString(simple.Clause),
                LaunchEnvironment = parseOptions.LaunchEnvironment,
                EnvironmentAssignmentValues = environmentAssignmentValues,
                Export = exports,
                LaunchWordValues = launchWordValues ??
                    CommandOccurrenceFacts.EmptyLaunchWordValues,
                ArithmeticExpansions = arithmeticExpansions,
            });
        }

        /// <summary>
        /// Gets each bounded arithmetic expansion in the words, redirect
        /// targets, and heredoc bodies of <paramref name="groups"/> (#227).
        /// </summary>
        private static IReadOnlyList<ShellValueFragment> CollectArithmetic(
            params IReadOnlyList<BashToken>[] groups)
        {
            List<ShellValueFragment>? found = null;
            foreach (var tokens in groups)
            {
                foreach (var value in TokenValues(tokens))
                {
                    foreach (var fragment in value.Fragments)
                    {
                        if (fragment.Kind == ShellValueFragmentKind.Opaque &&
                            fragment.OpaqueCause == ShellOpaqueCause.ArithmeticExpansion)
                        {
                            (found ??= new List<ShellValueFragment>()).Add(fragment);
                        }
                    }
                }
            }

            return found is null ? Array.Empty<ShellValueFragment>() : found.ToArray();
        }

        private static bool AreRedirectsComplete(
            IReadOnlyList<RedirectAnalysisFacts> redirects)
        {
            foreach (var redirect in redirects)
            {
                if (!redirect.IsComplete)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasGlobFragment(ShellValue value)
        {
            foreach (var fragment in value.Fragments)
            {
                if (fragment.Expansion is { Kind: ShellExpansionKind.Glob })
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetElementValue(
            ClauseElement element,
            IReadOnlyList<BashToken> sourceTokens,
            out ShellValue value,
            bool skipLeadingOperator = false)
        {
            value = ShellValue.Literal(string.Empty);
            if (element.SourceStart is null || element.SourceLength is null)
            {
                return false;
            }

            var elementStart = element.SourceStart.Value;
            var elementEnd = elementStart + element.SourceLength.Value;
            var values = new List<ShellValue>();
            var coveredStart = -1;
            var coveredEnd = -1;
            var skippedOperator = false;
            foreach (var token in sourceTokens)
            {
                var tokenEnd = token.SourceStart + token.SourceLength;
                if (token.SourceStart < elementStart || tokenEnd > elementEnd)
                {
                    continue;
                }

                // A redirect element starts with its operator. The target
                // word starts after it, so a leading tilde stays at index 0.
                if (skipLeadingOperator &&
                    values.Count == 0 &&
                    token.Kind == BashTokenKind.Operator &&
                    token.SourceStart == elementStart)
                {
                    skippedOperator = true;
                    continue;
                }

                coveredStart = coveredStart < 0 ? token.SourceStart : coveredStart;
                coveredEnd = tokenEnd;
                values.Add(token.ResolverValue ??
                    ShellValue.Literal(token.Value, token.SourceStart, token.SourceLength));
            }

            if (values.Count == 0 ||
                coveredStart != elementStart && !skippedOperator ||
                coveredEnd != elementEnd)
            {
                return false;
            }

            value = values.Count == 1 ? values[0] : ShellValue.Concat(values);
            return true;
        }

        private void MergeFacts(StructuralCoordinator nested)
        {
            foreach (var pair in nested._facts)
            {
                _facts.Add(pair.Key, pair.Value);
            }

            foreach (var pair in nested._forInPlans)
            {
                _forInPlans.Add(pair.Key, pair.Value);
            }

            _arithmeticSites.UnionWith(nested._arithmeticSites);

        }

        private bool TryRegisterDecodedFacts(
            BashParseResult innerResult,
            DecodedReferenceMap referenceMap,
            out string? error)
        {
            var inner = innerResult.Command;
            foreach (var source in inner.Commands)
            {
                if (!referenceMap.TryGetClause(source.Clause, out var clonedClause))
                {
                    error = "decoded bash -c clause facts could not be mapped safely";
                    return false;
                }

                if (!TryFindValueProvenance(
                        innerResult.ValueProvenanceSets,
                        source.Clause,
                        out var valueProvenance))
                {
                    error = "decoded bash -c argument provenance could not be mapped safely";
                    return false;
                }

                if (!TryFindDependencies(
                        innerResult.CwdPathDependencySets,
                        source.Clause,
                        out var cwdPathDependencies))
                {
                    error = "decoded bash -c cwd provenance could not be mapped safely";
                    return false;
                }

                if (!TryFindRedirectProvenance(
                        innerResult.RedirectTargetProvenanceSets,
                        source.Clause,
                        out var redirectProvenance))
                {
                    error = "decoded bash -c redirect provenance could not be mapped safely";
                    return false;
                }

                if (!TryFindRedirectAnalysis(
                        innerResult.RedirectAnalysisSets,
                        source.Clause,
                        out var redirectAnalysis))
                {
                    error = "decoded bash -c redirect analysis could not be mapped safely";
                    return false;
                }

                _facts.Add(clonedClause, new CommandOccurrenceFacts
                {
                    Redirects = ClearDecodedHereDocumentSpans(redirectAnalysis),
                    RedirectTargetProvenance = redirectProvenance,
                    CwdPathDependencies = cwdPathDependencies,
                    ValueProvenance = valueProvenance,
                    IsComplete = source.IsComplete,
                });
            }

            foreach (var sourcePlan in innerResult.ForInPlans)
            {
                if (!referenceMap.TryGetForEach(sourcePlan.Syntax, out var clonedForEach))
                {
                    error = "decoded bash -c loop plan could not be mapped safely";
                    return false;
                }

                _forInPlans.Add(clonedForEach, sourcePlan.Plan);
            }

            error = null;
            return true;
        }

        private static IReadOnlyList<RedirectAnalysisFacts> ClearDecodedHereDocumentSpans(
            IReadOnlyList<RedirectAnalysisFacts> redirects)
        {
            var rewritten = new RedirectAnalysisFacts[redirects.Count];
            var changed = false;
            for (var index = 0; index < rewritten.Length; index++)
            {
                var redirect = redirects[index];
                var hereDocument = redirect.HereDocument;
                if (hereDocument is null)
                {
                    rewritten[index] = redirect;
                    continue;
                }

                changed = true;
                rewritten[index] = redirect with
                {
                    HereDocument = hereDocument with
                    {
                        Delimiter = hereDocument.Delimiter with
                        {
                            SourceStart = null,
                            SourceLength = null,
                        },
                        Body = hereDocument.Body with
                        {
                            SourceStart = null,
                            SourceLength = null,
                        },
                    },
                };
            }

            return changed ? rewritten : redirects;
        }

        private static bool TryFindValueProvenance(
            IReadOnlyList<ShellValueProvenanceSet> provenanceSets,
            Clause clause,
            out IReadOnlyList<ShellValueElementProvenance> provenance)
        {
            foreach (var set in provenanceSets)
            {
                if (object.ReferenceEquals(set.Clause, clause))
                {
                    provenance = set.Provenance;
                    return true;
                }
            }

            provenance = Array.Empty<ShellValueElementProvenance>();
            return false;
        }

        private static bool TryFindRedirectProvenance(
            IReadOnlyList<RedirectTargetProvenanceSet> provenanceSets,
            Clause clause,
            out IReadOnlyList<RedirectTargetProvenance> provenance)
        {
            foreach (var set in provenanceSets)
            {
                if (object.ReferenceEquals(set.Clause, clause))
                {
                    provenance = set.Provenance;
                    return true;
                }
            }

            provenance = Array.Empty<RedirectTargetProvenance>();
            return false;
        }

        private static bool TryFindRedirectAnalysis(
            IReadOnlyList<RedirectAnalysisSet> analysisSets,
            Clause clause,
            out IReadOnlyList<RedirectAnalysisFacts> redirects)
        {
            for (var index = 0; index < analysisSets.Count; index++)
            {
                if (ReferenceEquals(analysisSets[index].Clause, clause))
                {
                    redirects = analysisSets[index].Redirects;
                    return true;
                }
            }

            redirects = Array.Empty<RedirectAnalysisFacts>();
            return false;
        }

        private static bool TryFindDependencies(
            IReadOnlyList<CwdPathDependencySet> dependencySets,
            Clause clause,
            out IReadOnlyList<CwdPathDependency> dependencies)
        {
            foreach (var set in dependencySets)
            {
                if (object.ReferenceEquals(set.Clause, clause))
                {
                    dependencies = set.Dependencies;
                    return true;
                }
            }

            dependencies = Array.Empty<CwdPathDependency>();
            return false;
        }

        private void RevokeLaunchFact(string name)
        {
            if (_options.LaunchEnvironment is { } launch)
            {
                _options = _options with { LaunchEnvironment = launch.Revoke(name) };
            }
        }

        /// <summary>
        /// Stops trust in every supplied launch fact after a statement that
        /// can change a variable the parser does not model (#200). Inside a
        /// loop, an earlier statement of the body runs again after the
        /// change, so the input is rejected while a fact is still live.
        /// </summary>
        private bool TryRevokeAllLaunchFacts(out string? error)
        {
            error = null;
            if (_options.LaunchEnvironment is not { } launch || !launch.HasLiveFacts)
            {
                return true;
            }

            if (_loopDepth > 0)
            {
                error = "a Bash variable change inside a loop with launch facts is not supported";
                return false;
            }

            _options = _options with { LaunchEnvironment = launch.RevokeAll() };
            return true;
        }

        /// <summary>
        /// <c>wait -p name</c> unsets and then assigns <c>name</c>. The
        /// mutation lists above do not model it, so any option revokes the
        /// launch facts.
        /// </summary>
        private static bool IsWaitWithOption(Clause clause)
        {
            if (clause.Verb.Tokens.Count == 0 ||
                !string.Equals(clause.Verb.Tokens[0], "wait", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var argument in clause.Args)
            {
                if (!argument.IsCwdAttribution &&
                    (argument.Kind != ArgKind.Literal ||
                     argument.Raw.StartsWith("-", StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when every named expansion in the command reads a live
        /// launch value (#200). A heredoc body is data for the program, and
        /// its expansions are not modeled here, so it is not accepted.
        /// </summary>
        private bool CanPublishLaunchFacts(IReadOnlyList<BashToken> tokens)
        {
            if (!ShellLaunchFacts.IsActive(_options))
            {
                return false;
            }

            var sawLaunchValue = false;
            foreach (var token in tokens)
            {
                if (token.HeredocBodyValue is { } body && HasNamedExpansion(body))
                {
                    return false;
                }

                if (token.ResolverValue is not { } value)
                {
                    continue;
                }

                foreach (var fragment in value.Fragments)
                {
                    if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                        fragment.Expansion is not { Kind: ShellExpansionKind.Variable } expansion)
                    {
                        continue;
                    }

                    if (!ShellLaunchFacts.TryGetValue(_options, expansion.Name, out _))
                    {
                        return false;
                    }

                    sawLaunchValue = true;
                }
            }

            return sawLaunchValue;
        }

        private static bool HasNamedExpansion(ShellValue value)
        {
            foreach (var fragment in value.Fragments)
            {
                if (fragment.Kind == ShellValueFragmentKind.Expansion &&
                    fragment.Expansion is { Kind: ShellExpansionKind.Variable })
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPotentialBindingMutation(Clause clause)
        {
            var dispatchKind = BashCwdInvocationGrammar.Classify(clause, out _);
            if (dispatchKind == BashDispatchKind.Query)
            {
                return false;
            }

            if (clause.Verb.Tokens.Count == 0)
            {
                return true;
            }

            var verb = clause.Verb.Tokens[0];

            // A bounded `break`, `continue`, `exit`, or `return` changes no
            // variable and no directory. The state pass joins the state at a
            // loop transfer into its loop. Other operands keep the
            // conservative rule below.
            if (BashControlTransferBuiltin.IsBounded(clause))
            {
                return false;
            }

            if (verb is "unset" or "read" or "readarray" or "mapfile" or
                "declare" or "typeset" or "local" or "export" or "readonly" or
                "let" or "eval" or "." or "source" or "getopts" or "set" or
                "cd" or "pushd" or "popd" or "trap" or
                "break" or "continue" or "return" or "exit" or "exec")
            {
                return true;
            }

            // These dispatch builtins can invoke every mutator above after
            // option processing. Until their executable grammar is modeled,
            // accepting them would let `command unset f` retain stale facts.
            if (verb is "command" or "builtin")
            {
                return true;
            }

            if (!string.Equals(verb, "printf", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var argument in clause.Args)
            {
                if (string.Equals(argument.Raw, "-v", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsNamedParameterExpansion(
            IReadOnlyList<BashToken> tokens)
        {
            foreach (var token in tokens)
            {
                foreach (var value in new[] { token.ResolverValue, token.HeredocBodyValue })
                {
                    if (value is null)
                    {
                        continue;
                    }

                    foreach (var fragment in value.Fragments)
                    {
                        if (fragment.Kind == ShellValueFragmentKind.Expansion &&
                            fragment.Expansion is { Kind: ShellExpansionKind.Variable })
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private bool CanPublishActiveLoopBindingFacts(IReadOnlyList<BashToken> tokens)
        {
            if (!_options.PublishAuthoredSourceFacts ||
                _hasUnmodeledVariableStateMutation ||
                _activeLoopBindings.Count == 0)
            {
                return false;
            }

            var sawBinding = false;
            foreach (var token in tokens)
            {
                foreach (var value in new[] { token.ResolverValue, token.HeredocBodyValue })
                {
                    if (value is null)
                    {
                        continue;
                    }

                    foreach (var fragment in value.Fragments)
                    {
                        if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                            fragment.Expansion is not
                            { Kind: ShellExpansionKind.Variable, Name: { } name })
                        {
                            continue;
                        }

                        if (!_activeLoopBindings.Contains(name))
                        {
                            return false;
                        }

                        sawBinding = true;
                    }
                }
            }

            return sawBinding;
        }

        private bool CanPublishBoundedAssignmentFacts(IReadOnlyList<BashToken> tokens) =>
            CanReadBoundedNames(TokenValues(tokens));

        /// <summary>
        /// True when every named expansion reads a bounded binding: a name
        /// that an earlier bounded assignment set, or an active loop binding
        /// (#209). The state pass gives each read the bound value, or
        /// <c>Unknown</c>.
        /// </summary>
        private bool CanReadBoundedNames(IEnumerable<ShellValue> values)
        {
            if (_options.InitialStateMode !=
                    BashInitialStateMode.FreshNonInteractiveNoStartup ||
                _hasUnmodeledVariableStateMutation || _boundedAssignmentNames.Count == 0)
            {
                return false;
            }

            var sawBinding = false;
            foreach (var value in values)
            {
                foreach (var fragment in value.Fragments)
                {
                    if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                        fragment.Expansion is not
                        { Kind: ShellExpansionKind.Variable, Name: { } name })
                    {
                        continue;
                    }

                    if (!_boundedAssignmentNames.Contains(name) &&
                        !(_options.PublishAuthoredSourceFacts &&
                          _activeLoopBindings.Contains(name)))
                    {
                        return false;
                    }

                    sawBinding = true;
                }
            }

            return sawBinding;
        }

        private bool CanReadLaunchNames(IEnumerable<ShellValue> values)
        {
            if (!ShellLaunchFacts.IsActive(_options))
            {
                return false;
            }

            var sawLaunchValue = false;
            foreach (var value in values)
            {
                foreach (var fragment in value.Fragments)
                {
                    if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                        fragment.Expansion is not { Kind: ShellExpansionKind.Variable } expansion)
                    {
                        continue;
                    }

                    if (!ShellLaunchFacts.TryGetValue(_options, expansion.Name, out _))
                    {
                        return false;
                    }

                    sawLaunchValue = true;
                }
            }

            return sawLaunchValue;
        }

        private static IEnumerable<ShellValue> TokenValues(IReadOnlyList<BashToken> tokens)
        {
            foreach (var token in tokens)
            {
                if (token.ResolverValue is { } value)
                {
                    yield return value;
                }

                if (token.HeredocBodyValue is { } body)
                {
                    yield return body;
                }
            }
        }

        private static bool IsPotentialVariableStateMutation(Clause clause)
        {
            var dispatchKind = BashCwdInvocationGrammar.Classify(clause, out _);
            if (dispatchKind is BashDispatchKind.CwdTransfer or BashDispatchKind.Query ||
                clause.Verb.Tokens.Count > 0 &&
                clause.Verb.Tokens[0] is "pushd" or "popd")
            {
                return false;
            }

            return IsPotentialBindingMutation(clause);
        }

        private static bool IsBashIdentifier(string value)
        {
            if (value.Length == 0 || !IsBashIdentifierStart(value[0]))
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

        private static bool IsSupportedScalarBinding(string value)
        {
            if (value.Length == 0 || value[0] < 'a' || value[0] > 'z' ||
                value is "auto_resume" or "histchars")
            {
                return false;
            }

            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if ((character < 'a' || character > 'z') &&
                    (character < '0' || character > '9') &&
                    character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class ClauseReferenceComparer : IEqualityComparer<Clause>
        {
            internal static ClauseReferenceComparer Instance { get; } = new();

            public bool Equals(Clause? x, Clause? y) => object.ReferenceEquals(x, y);

            public int GetHashCode(Clause obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class ForEachReferenceComparer : IEqualityComparer<ForEachSyntax>
        {
            internal static ForEachReferenceComparer Instance { get; } = new();

            public bool Equals(ForEachSyntax? x, ForEachSyntax? y) =>
                object.ReferenceEquals(x, y);

            public int GetHashCode(ForEachSyntax obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    private static IReadOnlyList<BashToken> ShiftTokens(
        IReadOnlyList<BashToken> tokens,
        int sourceOffset)
    {
        var shifted = new BashToken[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            // A heredoc in a substitution body (#217) keeps its body and
            // end positions in the outer source, like the token itself.
            shifted[index] = token with
            {
                SourceStart = token.SourceStart + sourceOffset,
                ResolverValue = ShiftValue(token.ResolverValue, sourceOffset),
                HeredocBodyValue = ShiftValue(token.HeredocBodyValue, sourceOffset),
                HeredocBodyStart = token.HeredocBodyStart + sourceOffset,
                HeredocSourceEnd = token.HeredocSourceEnd + sourceOffset,
            };
        }

        return shifted;
    }

    private static ShellValue? ShiftValue(ShellValue? value, int sourceOffset)
    {
        if (value is null)
        {
            return null;
        }

        var fragments = new ShellValueFragment[value.Fragments.Count];
        for (var index = 0; index < value.Fragments.Count; index++)
        {
            var fragment = value.Fragments[index];
            fragments[index] = fragment with
            {
                SourceStart = fragment.SourceStart + sourceOffset,
            };
        }

        return new ShellValue(value.Decoded, fragments);
    }

    private static bool IsStructuralBoundary(BashToken token) =>
        token.Kind == BashTokenKind.Whitespace ||
        token.Kind == BashTokenKind.Operator && token.OperatorText is
            "(" or ")" or "&&" or "||" or ";" or "|" or "&";

    private static bool IsListOperator(BashToken token) =>
        token.Kind == BashTokenKind.Whitespace ||
        token.Kind == BashTokenKind.Operator && token.OperatorText is "&&" or "||" or ";" or "&";

    private static bool IsExactStaticWrapper(IReadOnlyList<BashToken> tokens)
    {
        for (var index = 1; index + 1 < tokens.Count; index++)
        {
            if (tokens[index].Kind == BashTokenKind.Word &&
                string.Equals(tokens[index].Value, "-c", StringComparison.Ordinal))
            {
                if (index + 2 != tokens.Count ||
                    tokens[index + 1].Kind != BashTokenKind.QuotedString)
                {
                    return false;
                }

                for (var controlIndex = 0; controlIndex <= index + 1; controlIndex++)
                {
                    if (!HasExactLiteralValue(tokens[controlIndex]))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return false;
    }

    private static bool HasExactLiteralValue(BashToken token)
    {
        if (token.ResolverValue is null ||
            !string.Equals(token.ResolverValue.Decoded, token.Value, StringComparison.Ordinal))
        {
            return false;
        }

        var fragments = token.ResolverValue.Fragments;
        for (var index = 0; index < fragments.Count; index++)
        {
            var fragment = fragments[index];
            if (fragment.Kind != ShellValueFragmentKind.Literal ||
                fragment.AllowedTransforms != ShellLexicalTransform.None ||
                fragment.Expansion is not null ||
                fragment.Cardinality != ShellValueCardinality.ExactlyOne ||
                fragment.OpaqueCause != ShellOpaqueCause.None)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUnexpandedCommandString(Clause clause)
    {
        if (clause.Verb.Tokens.Count == 0 ||
            !string.Equals(clause.Verb.Tokens[0], "bash", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(clause.Verb.Tokens[0], "sh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Bash and sh parse their own options only up to the first operand.
        // Without -c, that operand is the script file, and every later word
        // is a script argument: `bash x.sh -c y` runs x.sh with $1 = -c
        // (#206). An option value is not the operand: -o and -O (also in a
        // cluster, and with +) and --rcfile and --init-file take the next
        // word.
        var pendingOptionValues = 0;
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            var element = clause.Elements[index];
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (pendingOptionValues == 0 && IsScriptOperand(element))
            {
                return false;
            }

            if (element.Kind != ArgKind.Literal)
            {
                return true;
            }

            if (string.Equals(element.Value, "--", StringComparison.Ordinal))
            {
                return false;
            }

            if (IsCommandStringOption(element.Value))
            {
                return true;
            }

            if (pendingOptionValues > 0)
            {
                pendingOptionValues--;
                continue;
            }

            pendingOptionValues = CountOptionValues(element.Value);
        }

        return false;
    }

    /// <summary>
    /// True when the word is the script operand of a shell invoker. The word
    /// must not start with <c>-</c> or <c>+</c>, and it must not be able to
    /// expand to such a word. A literal word qualifies. A tilde word
    /// qualifies when its resolved path is absolute.
    /// </summary>
    private static bool IsScriptOperand(ClauseElement element)
    {
        var value = element.Kind switch
        {
            ArgKind.Literal => element.Value,
            ArgKind.Tilde => element.Resolved,
            _ => null,
        };
        return value is { Length: > 0 } &&
               value[0] != '-' &&
               value[0] != '+' &&
               (element.Kind == ArgKind.Literal || value[0] == '/');
    }

    /// <summary>
    /// Counts the words that a shell option takes as its values. Each
    /// <c>o</c> or <c>O</c> in a <c>-</c> or <c>+</c> cluster takes one word.
    /// </summary>
    private static int CountOptionValues(string value)
    {
        if (string.Equals(value, "--rcfile", StringComparison.Ordinal) ||
            string.Equals(value, "--init-file", StringComparison.Ordinal))
        {
            return 1;
        }

        if (value.Length < 2 ||
            value[0] is not ('-' or '+') ||
            value[1] == '-')
        {
            return 0;
        }

        var count = 0;
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] is 'o' or 'O')
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsCommandStringOption(string value)
    {
        if (string.Equals(value, "-c", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Length < 2 || value[0] != '-' || value[1] == '-')
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] == 'c')
            {
                return true;
            }
        }

        return false;
    }

    private static int? CombinedStart(IReadOnlyList<ShellSyntaxNode> nodes)
    {
        if (nodes.Count == 0 || nodes[0].SourceStart is null)
        {
            return null;
        }

        for (var index = 1; index < nodes.Count; index++)
        {
            if (nodes[index].SourceStart is null)
            {
                return null;
            }
        }

        return nodes[0].SourceStart;
    }

    private static int? CombinedLength(IReadOnlyList<ShellSyntaxNode> nodes)
    {
        var start = CombinedStart(nodes);
        var last = nodes.Count == 0 ? null : nodes[nodes.Count - 1];
        if (start is null || last?.SourceStart is null || last.SourceLength is null)
        {
            return null;
        }

        return last.SourceStart.Value + last.SourceLength.Value - start.Value;
    }

    private static bool TryCloneDecodedBlock(
        ShellBlockSyntax source,
        CompoundOperator firstOperator,
        bool outerSubshell,
        ref bool firstLeaf,
        out ShellBlockSyntax clone,
        out DecodedReferenceMap referenceMap)
    {
        referenceMap = new DecodedReferenceMap();
        return TryCloneDecodedBlock(
            source,
            firstOperator,
            outerSubshell,
            ref firstLeaf,
            referenceMap,
            out clone);
    }

    private static bool TryCloneDecodedBlock(
        ShellBlockSyntax source,
        CompoundOperator firstOperator,
        bool outerSubshell,
        ref bool firstLeaf,
        DecodedReferenceMap referenceMap,
        out ShellBlockSyntax clone)
    {
        if (source is null || source.Statements is null)
        {
            clone = new ShellBlockSyntax();
            return false;
        }

        var statements = new List<ShellSyntaxNode>(source.Statements.Count);
        foreach (var statement in source.Statements)
        {
            if (!TryCloneDecodedNode(
                    statement,
                    firstOperator,
                    outerSubshell,
                    ref firstLeaf,
                    referenceMap,
                    out var cloned))
            {
                clone = new ShellBlockSyntax();
                return false;
            }

            statements.Add(cloned!);
        }

        clone = new ShellBlockSyntax { Statements = statements };
        return true;
    }

    private static bool TryCloneDecodedNode(
        ShellSyntaxNode source,
        CompoundOperator firstOperator,
        bool outerSubshell,
        ref bool firstLeaf,
        DecodedReferenceMap referenceMap,
        out ShellSyntaxNode? clone)
    {
        clone = null;
        switch (source)
        {
            case ShellBlockSyntax block:
                if (!TryCloneDecodedBlock(
                        block,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var clonedBlock))
                {
                    return false;
                }

                clone = clonedBlock;
                return true;
            case SimpleCommandSyntax simple:
                var substitutions = new List<CommandSubstitutionSyntax>(simple.Substitutions.Count);
                foreach (var substitution in simple.Substitutions)
                {
                    if (!TryCloneDecodedNode(
                            substitution,
                            firstOperator,
                            outerSubshell,
                            ref firstLeaf,
                            referenceMap,
                            out var clonedSubstitution) ||
                        clonedSubstitution is not CommandSubstitutionSyntax typedSubstitution)
                    {
                        return false;
                    }

                    substitutions.Add(typedSubstitution);
                }

                var clauseOperator = firstLeaf ? firstOperator : simple.Clause.Operator;
                firstLeaf = false;
                var executionRegions = new List<ExecutionRegionSyntax>(
                    simple.ExecutionRegions.Count);
                foreach (var executionRegion in simple.ExecutionRegions)
                {
                    if (!TryCloneDecodedNode(
                            executionRegion,
                            firstOperator,
                            outerSubshell,
                            ref firstLeaf,
                            referenceMap,
                            out var clonedExecutionRegion) ||
                        clonedExecutionRegion is not ExecutionRegionSyntax typedExecutionRegion)
                    {
                        return false;
                    }

                    executionRegions.Add(typedExecutionRegion);
                }

                var clonedClause = simple.Clause with
                {
                    Operator = clauseOperator,
                    IsSubshell = outerSubshell || simple.Clause.IsSubshell,
                    IsCommandStringWrapped = true,
                    Elements = ClauseElementProvenance.WithoutOuterSourceSpans(
                        simple.Clause.Elements),
                };
                var attachedExecutionRegions = new List<ExecutionRegionSyntax>(
                    executionRegions.Count);
                foreach (var executionRegion in executionRegions)
                {
                    var hostIndex = executionRegion.HostClauseElementIndex;
                    if (executionRegion.Origin == ExecutionRegionOrigin.CommandArgument &&
                        (hostIndex is null ||
                         hostIndex < 0 ||
                         hostIndex >= clonedClause.Elements.Count))
                    {
                        return false;
                    }

                    attachedExecutionRegions.Add(executionRegion with
                    {
                        HostArgument = hostIndex.HasValue
                            ? clonedClause.Elements[hostIndex.Value]
                            : null,
                    });
                }

                clone = new SimpleCommandSyntax
                {
                    Clause = clonedClause,
                    Substitutions = substitutions,
                    ExecutionRegions = attachedExecutionRegions,
                };
                referenceMap.Add(simple.Clause, clonedClause);
                return true;
            case PipelineSyntax pipeline:
                return TryCloneDecodedCollection(
                    pipeline.Stages,
                    firstOperator,
                    outerSubshell,
                    ref firstLeaf,
                    referenceMap,
                    stages => new PipelineSyntax { Stages = stages },
                    out clone);
            case CommandListSyntax list:
                var items = new List<CommandListItemSyntax>(list.Items.Count);
                foreach (var item in list.Items)
                {
                    if (item is null || !TryCloneDecodedNode(
                            item.Command,
                            firstOperator,
                            outerSubshell,
                            ref firstLeaf,
                            referenceMap,
                            out var itemCommand))
                    {
                        return false;
                    }

                    items.Add(new CommandListItemSyntax
                    {
                        Operator = item.Operator,
                        Command = itemCommand!,
                    });
                }

                clone = new CommandListSyntax { Items = items };
                return true;
            case GroupSyntax group:
                if (!TryCloneDecodedBlock(
                        group.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var groupBody))
                {
                    return false;
                }

                clone = new GroupSyntax { GroupKind = group.GroupKind, Body = groupBody };
                return true;
            case ForEachSyntax forEach:
                if (!TryCloneDecodedBlock(
                        forEach.IteratorCommands,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var iterator) ||
                    !TryCloneDecodedBlock(
                        forEach.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var forBody))
                {
                    return false;
                }

                var clonedForEach = new ForEachSyntax
                {
                    Binding = new LoopBindingSyntax
                    {
                        Name = forEach.Binding.Name,
                        Source = ClearSpan(forEach.Binding.Source),
                    },
                    Iterable = ClearSpan(forEach.Iterable),
                    IteratorCommands = iterator,
                    Body = forBody,
                };
                clone = clonedForEach;
                referenceMap.Add(forEach, clonedForEach);
                return true;
            case ConditionLoopSyntax loop:
                if (!TryCloneDecodedBlock(
                        loop.Condition,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var condition) ||
                    !TryCloneDecodedBlock(
                        loop.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var loopBody))
                {
                    return false;
                }

                clone = new ConditionLoopSyntax
                {
                    LoopKind = loop.LoopKind,
                    Condition = condition,
                    Body = loopBody,
                };
                return true;
            case ConditionalSyntax conditional:
                var branches = new List<ConditionalBranchSyntax>(conditional.Branches.Count);
                foreach (var branch in conditional.Branches)
                {
                    if (!TryCloneDecodedNode(
                            branch,
                            firstOperator,
                            outerSubshell,
                            ref firstLeaf,
                            referenceMap,
                            out var clonedBranch) ||
                        clonedBranch is not ConditionalBranchSyntax typedBranch)
                    {
                        return false;
                    }

                    branches.Add(typedBranch);
                }

                ShellBlockSyntax? @else = null;
                if (conditional.Else is not null && !TryCloneDecodedBlock(
                        conditional.Else,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out @else))
                {
                    return false;
                }

                clone = new ConditionalSyntax { Branches = branches, Else = @else };
                return true;
            case ConditionalBranchSyntax branch:
                if (!TryCloneDecodedBlock(
                        branch.Condition,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var branchCondition) ||
                    !TryCloneDecodedBlock(
                        branch.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var branchBody))
                {
                    return false;
                }

                clone = new ConditionalBranchSyntax
                {
                    Condition = branchCondition,
                    Body = branchBody,
                };
                return true;
            case CommandSubstitutionSyntax substitution:
                if (!TryCloneDecodedBlock(
                        substitution.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var substitutionBody))
                {
                    return false;
                }

                clone = new CommandSubstitutionSyntax { Body = substitutionBody };
                return true;
            case ExecutionRegionSyntax executionRegion:
                if (!TryCloneDecodedBlock(
                        executionRegion.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        referenceMap,
                        out var executionBody))
                {
                    return false;
                }

                clone = new ExecutionRegionSyntax
                {
                    Origin = executionRegion.Origin,
                    HostArgument = executionRegion.HostArgument,
                    HostClauseElementIndex = executionRegion.HostClauseElementIndex,
                    Phase = executionRegion.Phase,
                    Timing = executionRegion.Timing,
                    Cardinality = executionRegion.Cardinality,
                    Body = executionBody,
                };
                return true;
            default:
                return false;
        }
    }

    private static bool TryCloneDecodedCollection(
        IReadOnlyList<ShellSyntaxNode> source,
        CompoundOperator firstOperator,
        bool outerSubshell,
        ref bool firstLeaf,
        DecodedReferenceMap referenceMap,
        Func<IReadOnlyList<ShellSyntaxNode>, ShellSyntaxNode> factory,
        out ShellSyntaxNode? clone)
    {
        var children = new List<ShellSyntaxNode>(source.Count);
        foreach (var child in source)
        {
            if (!TryCloneDecodedNode(
                    child,
                    firstOperator,
                    outerSubshell,
                    ref firstLeaf,
                    referenceMap,
                    out var clonedChild))
            {
                clone = null;
                return false;
            }

            children.Add(clonedChild!);
        }

        clone = factory(children);
        return true;
    }

    private static ShellSourceFragment ClearSpan(ShellSourceFragment source) => new()
    {
        Raw = source.Raw,
    };

    private sealed class DecodedReferenceMap
    {
        private readonly List<(Clause Source, Clause Clone)> _clauses = new();
        private readonly List<(ForEachSyntax Source, ForEachSyntax Clone)> _forEach = new();

        internal void Add(Clause source, Clause clone) => _clauses.Add((source, clone));

        internal void Add(ForEachSyntax source, ForEachSyntax clone) =>
            _forEach.Add((source, clone));

        internal bool TryGetClause(Clause source, out Clause clone)
        {
            foreach (var pair in _clauses)
            {
                if (object.ReferenceEquals(pair.Source, source))
                {
                    clone = pair.Clone;
                    return true;
                }
            }

            clone = null!;
            return false;
        }

        internal bool TryGetForEach(ForEachSyntax source, out ForEachSyntax clone)
        {
            foreach (var pair in _forEach)
            {
                if (object.ReferenceEquals(pair.Source, source))
                {
                    clone = pair.Clone;
                    return true;
                }
            }

            clone = null!;
            return false;
        }
    }
}
