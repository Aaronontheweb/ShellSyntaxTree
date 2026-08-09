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
                out var analyzedSyntax,
                out var analyzedFacts,
                out var analyzedForInPlans) ||
            !ShellSyntaxProjection.TryProject(
                analyzedSyntax,
                analyzedFacts,
                out var projection))
        {
            return StructuralFailure(
                source,
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
            Array.Empty<BashForInAnalysisPlanReference>());

    private sealed class StructuralCoordinator
    {
        private readonly string _source;
        private readonly IReadOnlyList<BashToken> _tokens;
        private readonly BashParserOptions _options;
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
        private int _position;
        private int _subshellDepth;
        private int _loopDepth;
        private bool _hasUnmodeledShellStateMutation;
        private bool _hasUnmodeledVariableStateMutation;

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
            bool hasUnmodeledVariableStateMutation = false)
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
        }

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
                    stopWord: null,
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
            string? stopWord,
            CompoundOperator firstCompatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            error = null;
            SkipNewlines();
            if (_position == _tokens.Count ||
                stopAtRightParen && IsOperator(")") ||
                stopWord is not null && IsWord(stopWord))
            {
                return true;
            }

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

                if (stopWord is not null && IsWord(stopWord))
                {
                    break;
                }

                if (IsOperator(")"))
                {
                    error = $"unbalanced parens at position {_tokens[_position].SourceStart}";
                    return false;
                }

                if (!TryReadListOperator(out var listOperator))
                {
                    error = $"unexpected token at position {_tokens[_position].SourceStart}";
                    return false;
                }

                SkipNewlines();
                if (_position == _tokens.Count ||
                    stopAtRightParen && IsOperator(")") ||
                    stopWord is not null && IsWord(stopWord))
                {
                    if (listOperator == CompoundOperator.Sequence)
                    {
                        break;
                    }

                    error = "compound operator is missing a following command";
                    return false;
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
                command = first;
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

            if (IsWord("do") || IsWord("done"))
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

            if (HasAssignmentPrefix(segmentTokens))
            {
                error = "Bash assignment-prefix commands are not supported";
                return false;
            }

            if (!TryCollectCommandSubstitutions(
                    segmentTokens,
                    rejectCommandNameSubstitution: true,
                    out var substitutionFragments,
                    out error))
            {
                return false;
            }

            if (TryDetectBashCWrapper(segment, _source, out var innerCommand))
            {
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

                var innerOptions = _hasUnmodeledVariableStateMutation
                    ? _options with { InitialStateMode = BashInitialStateMode.Unknown }
                    : _options;
                var innerResult = ParseInternal(
                    innerCommand!,
                    innerOptions,
                    _bashCDepth + 1,
                    _structuralDepth + _subshellDepth + _loopDepth + 1,
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
                effectiveOptions = new BashParserOptions
                {
                    HomeDirectory = _options.HomeDirectory,
                    WorkingDirectory = _attribution.ResolvedCwd,
                    InitialStateMode = _options.InitialStateMode,
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
            var executionBoundary =
                BashCwdInvocationGrammar.ClassifyExecutionBoundary(emitted);
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
                (_options.InitialStateMode != BashInitialStateMode.IsolatedNonInteractive ||
                 _hasUnmodeledVariableStateMutation))
            {
                error = "Bash named parameter expansion requires proved variable-attribute state";
                return false;
            }

            var dispatchKind = BashCwdInvocationGrammar.Classify(
                emitted,
                out _);
            var isPotentialStateMutation = IsPotentialBindingMutation(emitted);
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
                IsPotentialVariableStateMutation(emitted) && !isModeledCwdTransfer;

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
                SourceStart = firstSource.SourceStart,
                SourceLength = sourceEnd - firstSource.SourceStart,
            };
            RegisterFacts(
                simple,
                segmentTokens,
                parsed.PathResolutions,
                effectiveOptions);
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

            if (_structuralDepth + _subshellDepth + _loopDepth >=
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
            if (_options.InitialStateMode != BashInitialStateMode.IsolatedNonInteractive)
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
                stopWord: "done",
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

        private bool TryParseSubshell(
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            if (_structuralDepth + _subshellDepth + _loopDepth >=
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                command = null;
                error = "Bash structural nesting depth exceeded (>16)";
                return false;
            }

            var open = _tokens[_position++];
            var outerMutationState = _hasUnmodeledShellStateMutation;
            var outerVariableMutationState = _hasUnmodeledVariableStateMutation;
            _attribution.PushForSubshell();
            _subshellDepth++;
            var parsed = TryParseList(
                stopAtRightParen: true,
                stopWord: null,
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

        private bool IsListTerminator() =>
            IsOperator(";") ||
            _position < _tokens.Count &&
            _tokens[_position].Kind == BashTokenKind.Whitespace &&
            _tokens[_position].IsStatementSeparator;

        private BashParserOptions CurrentOptions() =>
            _attribution.HasAttribution && !_attribution.IsDynamic
                ? new BashParserOptions
                {
                    HomeDirectory = _options.HomeDirectory,
                    WorkingDirectory = _attribution.ResolvedCwd,
                    InitialStateMode = _options.InitialStateMode,
                }
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

        private bool HasAssignmentPrefix(IReadOnlyList<BashToken> tokens)
        {
            var spelling = _source.Substring(tokens[0].SourceStart, tokens[0].SourceLength)
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

            if (_structuralDepth + _subshellDepth + _loopDepth + 1 >
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
                _structuralDepth + _subshellDepth + _loopDepth + 1,
                _markBashCWrapped,
                sourceStart,
                sourceLength,
                _activeLoopBindings,
                _hasUnmodeledShellStateMutation,
                _hasUnmodeledVariableStateMutation);
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
            BashParserOptions parseOptions)
        {
            var valueProvenance = new List<ShellValueElementProvenance>();
            var cwdPathDependencies = new List<CwdPathDependency>();
            for (var elementIndex = 0;
                 elementIndex < simple.Clause.Elements.Count;
                 elementIndex++)
            {
                var element = simple.Clause.Elements[elementIndex];
                if (!TryGetElementValue(element, sourceTokens, out var value))
                {
                    continue;
                }

                if (element.Role == ClauseElementRole.Argument)
                {
                    valueProvenance.Add(new ShellValueElementProvenance(
                        elementIndex,
                        value));
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

            var redirectAnalysis = BashRedirectAnalysis.Analyze(
                simple.Clause,
                _source,
                sourceTokens);
            _facts.Add(simple.Clause, new CommandOccurrenceFacts
            {
                Redirects = redirectAnalysis,
                ValueProvenance = valueProvenance.ToArray(),
                CwdPathDependencies = cwdPathDependencies.ToArray(),
                IsComplete = simple.Clause.Verb.Tokens.Count > 0 &&
                    AreRedirectsComplete(redirectAnalysis) &&
                    !HasUnexpandedCommandString(simple.Clause),
            });
        }

        private static bool AreRedirectsComplete(
            IReadOnlyList<RedirectAnalysis> redirects)
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

        private static bool TryGetElementValue(
            ClauseElement element,
            IReadOnlyList<BashToken> sourceTokens,
            out ShellValue value)
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
            foreach (var token in sourceTokens)
            {
                var tokenEnd = token.SourceStart + token.SourceLength;
                if (token.SourceStart < elementStart || tokenEnd > elementEnd)
                {
                    continue;
                }

                coveredStart = coveredStart < 0 ? token.SourceStart : coveredStart;
                coveredEnd = tokenEnd;
                values.Add(token.ResolverValue ??
                    ShellValue.Literal(token.Value, token.SourceStart, token.SourceLength));
            }

            if (values.Count == 0 ||
                coveredStart != elementStart ||
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

                _facts.Add(clonedClause, new CommandOccurrenceFacts
                {
                    Redirects = ClearDecodedHereDocumentSpans(source.Redirects),
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

        private static IReadOnlyList<RedirectAnalysis> ClearDecodedHereDocumentSpans(
            IReadOnlyList<RedirectAnalysis> redirects)
        {
            var rewritten = new RedirectAnalysis[redirects.Count];
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
            if (verb is "unset" or "read" or "readarray" or "mapfile" or
                "declare" or "typeset" or "local" or "export" or "readonly" or
                "let" or "eval" or "." or "source" or "getopts" or "set" or
                "cd" or "chdir" or "pushd" or "popd" or "trap" or
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
            shifted[index] = token with
            {
                SourceStart = token.SourceStart + sourceOffset,
                ResolverValue = ShiftValue(token.ResolverValue, sourceOffset),
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
            "(" or ")" or "&&" or "||" or ";" or "|";

    private static bool IsListOperator(BashToken token) =>
        token.Kind == BashTokenKind.Whitespace ||
        token.Kind == BashTokenKind.Operator && token.OperatorText is "&&" or "||" or ";";

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

        for (var index = 0; index < clause.Elements.Count; index++)
        {
            var element = clause.Elements[index];
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
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
        }

        return false;
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
                clone = new SimpleCommandSyntax
                {
                    Clause = clonedClause,
                    Substitutions = substitutions,
                    ExecutionRegions = executionRegions,
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
