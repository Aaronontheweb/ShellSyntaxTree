// -----------------------------------------------------------------------
// <copyright file="PwshStructuralCoordinator.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using ShellSyntaxTree.Internal.Resolving;
using ShellSyntaxTree.Internal.Pwsh.Verbs;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

internal static partial class PwshCommandParser
{
    private static ParsedCommand ParseStructured(
        string source,
        IReadOnlyList<PwshToken> tokens,
        PwshParserOptions options,
        int recursionDepth,
        int structuralDepth,
        bool markWrapped,
        PwshSetLocationContext? sharedLocation)
    {
        var coordinator = new StructuralCoordinator(
            source,
            tokens,
            options,
            recursionDepth,
            structuralDepth,
            markWrapped,
            sharedLocation,
            sourceStart: 0,
            source.Length,
            CompoundOperator.None,
            insideCommandSubstitution: false);
        if (!coordinator.TryParse(out var syntax, out var error))
        {
            return StructuralFailure(source, error, syntax);
        }

        var incompleteForEachClauses = CollectIncompleteForEachClauses(syntax);
        var initialWorkingDirectory = sharedLocation?.IsDynamic == true
            ? null
            : sharedLocation?.ResolvedCwd ??
              options.WorkingDirectory ??
              Environment.CurrentDirectory;
        if (!PwshForEachValueAnalyzer.TryAnalyze(
                syntax,
                options,
                initialWorkingDirectory,
                coordinator.GetFacts,
                coordinator.GetForEachPlan,
                incompleteForEachClauses,
                out var analyzedSyntax,
                out var analyzedFacts) ||
            !ShellSyntaxProjection.TryProject(
                analyzedSyntax,
                analyzedFacts,
                out var projection))
        {
            return StructuralFailure(
                source,
                "PowerShell structural syntax exceeded limits or contained invalid parser-owned facts",
                analyzedSyntax);
        }

        return new ParsedCommand
        {
            Source = source,
            Syntax = analyzedSyntax,
            Commands = projection.Commands,
            Clauses = projection.Clauses,
        };
    }

    private static ParsedCommand StructuralFailure(
        string source,
        string? reason,
        ShellBlockSyntax? syntax = null) => new()
        {
            Source = source,
            Syntax = syntax ?? new ShellBlockSyntax(),
            Commands = Array.Empty<CommandOccurrence>(),
            Clauses = Array.Empty<Clause>(),
            IsUnparseable = true,
            UnparseableReason = reason,
        };

    private sealed partial class StructuralCoordinator
    {
        private readonly string _source;
        private readonly IReadOnlyList<PwshToken> _tokens;
        private readonly PwshParserOptions _options;
        private readonly int _recursionDepth;
        private readonly int _structuralDepth;
        private readonly bool _markWrapped;
        private readonly PwshSetLocationContext _attribution;
        private readonly int _sourceStart;
        private readonly int _sourceLength;
        private readonly CompoundOperator _firstCompatibilityOperator;
        private readonly bool _insideCommandSubstitution;
        private readonly Dictionary<Clause, CommandOccurrenceFacts> _facts =
            new(ClauseReferenceComparer.Instance);
        private readonly Dictionary<ForEachSyntax, PwshForEachAnalysisPlan> _forEachPlans =
            new(ForEachReferenceComparer.Instance);
        private int _position;
        private int _groupDepth;

        internal StructuralCoordinator(
            string source,
            IReadOnlyList<PwshToken> tokens,
            PwshParserOptions options,
            int recursionDepth,
            int structuralDepth,
            bool markWrapped,
            PwshSetLocationContext? sharedLocation,
            int sourceStart,
            int sourceLength,
            CompoundOperator firstCompatibilityOperator,
            bool insideCommandSubstitution)
        {
            _source = source;
            _tokens = tokens;
            _options = options;
            _recursionDepth = recursionDepth;
            _structuralDepth = structuralDepth;
            _markWrapped = markWrapped;
            _attribution = sharedLocation ?? new PwshSetLocationContext();
            _sourceStart = sourceStart;
            _sourceLength = sourceLength;
            _firstCompatibilityOperator = firstCompatibilityOperator;
            _insideCommandSubstitution = insideCommandSubstitution;
        }

        internal CommandOccurrenceFacts GetFacts(SimpleCommandSyntax simple) =>
            _facts.TryGetValue(simple.Clause, out var facts)
                ? facts
                : CreateDefaultFacts(simple);

        private CommandOccurrenceFacts CreateDefaultFacts(
            SimpleCommandSyntax simple)
        {
            var redirects = PwshRedirectAnalysis.Analyze(simple.Clause);
            var valueProvenance = new List<ShellValueElementProvenance>();
            var redirectProvenance = new List<RedirectTargetProvenance>();
            var hasCompleteValueProvenance = true;
            var reconstructedArgumentBinding =
                ReconstructArgumentBindingCandidate(simple.Clause);
            var redirectIndex = 0;
            for (var elementIndex = 0;
                 elementIndex < simple.Clause.Elements.Count;
                 elementIndex++)
            {
                var element = simple.Clause.Elements[elementIndex];
                if (element.Role == ClauseElementRole.Argument)
                {
                    if (TryGetAuthoredElementValue(element, out var argumentValue))
                    {
                        var argumentBinding =
                            ClauseElementProvenance.TryGetArgumentBindingCandidate(
                                element,
                                out var preservedArgumentBinding)
                                ? preservedArgumentBinding
                                : reconstructedArgumentBinding;
                        valueProvenance.Add(new ShellValueElementProvenance(
                            elementIndex,
                            argumentValue,
                            argumentBinding));
                    }
                    else
                    {
                        hasCompleteValueProvenance = false;
                    }

                    continue;
                }

                if (element.Role != ClauseElementRole.Redirect)
                {
                    continue;
                }

                if (TryGetRedirectTargetValue(element, _tokens, out var value) ||
                    TryGetAuthoredRedirectTargetValue(element, out value))
                {
                    redirectProvenance.Add(new RedirectTargetProvenance(
                        redirectIndex,
                        elementIndex,
                        value,
                        ClauseElementProvenance.RedirectInvocationScopeDepth(
                            element)));
                }

                redirectIndex++;
            }

            return new CommandOccurrenceFacts
            {
                Redirects = redirects,
                RedirectTargetProvenance = redirectProvenance.ToArray(),
                ValueProvenance = valueProvenance.ToArray(),
                HasCompleteValueProvenance = hasCompleteValueProvenance,
                IsComplete = IsStructurallyComplete(simple) &&
                    AreRedirectsComplete(redirects),
            };
        }

        private static bool? ReconstructArgumentBindingCandidate(Clause clause)
        {
            if (clause.Verb.IsDynamic || clause.Verb.Tokens.Count != 1)
            {
                return null;
            }

            var command = clause.Verb.Tokens[0];
            return TryClassifyAuthoredArgumentBinding(command, out var usesNative)
                ? usesNative
                : null;
        }

        internal PwshForEachAnalysisPlan? GetForEachPlan(ForEachSyntax forEach) =>
            _forEachPlans.TryGetValue(forEach, out var plan) ? plan : null;

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

            if (!TryParseList(_firstCompatibilityOperator, out var command, out error) ||
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
                    ? $"unbalanced ')' at position {_tokens[_position].SourceStart}"
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
            CompoundOperator firstCompatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            error = null;
            SkipNewlines();
            if (_position == _tokens.Count)
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
                if (IsOperator(")"))
                {
                    error = $"unbalanced ')' at position {_tokens[_position].SourceStart}";
                    return false;
                }

                if (!TryReadListOperator(out var listOperator))
                {
                    error = $"unexpected token at position {_tokens[_position].SourceStart}";
                    return false;
                }

                if (items[items.Count - 1].Command is ForEachSyntax &&
                    listOperator is CompoundOperator.AndIf or CompoundOperator.OrIf)
                {
                    error = "a PowerShell foreach statement cannot participate in an && or || chain";
                    return false;
                }

                SkipNewlines();
                if (_position == _tokens.Count)
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

            if (first is ForEachSyntax && IsOperator("|"))
            {
                error = "a PowerShell foreach statement cannot produce a pipeline stage";
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

                if (IsOperator("("))
                {
                    error = "a grouped expression is only supported as the first pipeline element";
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

            if (TryParseDirectExecutionRegion(out command, out error))
            {
                return error is null;
            }

            if (IsOperator("("))
            {
                return TryParseGroup(compatibilityOperator, out command, out error);
            }

            if (compatibilityOperator != CompoundOperator.Pipe && IsForEachStart())
            {
                return TryParseForEach(compatibilityOperator, out command, out error);
            }

            if (IsOperator(")") || IsListOperator(_tokens[_position]) || IsOperator("|"))
            {
                error = $"unexpected operator at position {_tokens[_position].SourceStart}";
                return false;
            }

            var start = _position;
            var expressionDepth = 0;
            while (_position < _tokens.Count)
            {
                var token = _tokens[_position];
                if (expressionDepth > 0)
                {
                    _position++;
                    if (token.Kind == PwshTokenKind.Operator)
                    {
                        if (token.OperatorText == "(")
                        {
                            expressionDepth++;
                        }
                        else if (token.OperatorText == ")")
                        {
                            expressionDepth--;
                        }
                    }

                    continue;
                }

                if (token.Kind == PwshTokenKind.Operator && token.OperatorText == "(" &&
                    (StartsWithInvokeExpression(CopyTokens(start, _position)) ||
                     IsForEachCommandArgument(
                         CopyTokens(start, _position),
                         compatibilityOperator)))
                {
                    expressionDepth = 1;
                    _position++;
                    continue;
                }

                if (IsStructuralBoundary(token))
                {
                    break;
                }

                _position++;
            }

            if (expressionDepth != 0)
            {
                error = $"unbalanced '(' grouping at position {_source.Length}";
                return false;
            }

            var segmentTokens = CopyTokens(start, _position);
            if (segmentTokens.Count == 0)
            {
                error = $"expected a command at position {_tokens[_position].SourceStart}";
                return false;
            }

            CollapseSafeForEachCommandArgument(segmentTokens, compatibilityOperator);

            if (TryDetectUnsupportedInvocationShape(segmentTokens, out error))
            {
                return false;
            }

            if (_insideCommandSubstitution)
            {
                var firstSegmentToken = segmentTokens[0];
                var lastSegmentToken = segmentTokens[segmentTokens.Count - 1];
                var segmentSource = _source.Substring(
                    firstSegmentToken.SourceStart,
                    lastSegmentToken.SourceStart + lastSegmentToken.SourceLength -
                        firstSegmentToken.SourceStart);
                if (IsUnsupportedSubstitutionBody(segmentSource, segmentTokens))
                {
                    error = "unsupported PowerShell expression statement in subexpression";
                    return false;
                }

                if (segmentTokens[0].Kind == PwshTokenKind.Parameter)
                {
                    // PowerShell permits dash-leading native command names.
                    // At statement position this token is the executable, not
                    // a parameter waiting for a missing command.
                    segmentTokens[0] = segmentTokens[0] with { Kind = PwshTokenKind.Word };
                }
            }

            if (!TryValidateOpaqueExpressions(segmentTokens, out error))
            {
                return false;
            }

            if (!TryCollectCommandSubstitutions(
                    segmentTokens,
                    out var substitutionFragments,
                    out error))
            {
                return false;
            }

            var hasCallOperator = segmentTokens[0].Kind == PwshTokenKind.Operator &&
                segmentTokens[0].OperatorText == "&";
            var firstIsDirectSubstitution = substitutionFragments.Count > 0 &&
                IsDirectCommandSubstitution(segmentTokens[0], substitutionFragments[0]);
            if (!hasCallOperator && firstIsDirectSubstitution)
            {
                if (segmentTokens.Count != 1 || substitutionFragments.Count != 1)
                {
                    error = "a standalone PowerShell subexpression cannot have command-style arguments";
                    return false;
                }

                return TryParseStandaloneSubstitution(
                    substitutionFragments[0],
                    compatibilityOperator,
                    out command,
                    out error);
            }

            if (!TryParseCommandSubstitutions(
                    substitutionFragments,
                    out var substitutions,
                    out error))
            {
                return false;
            }

            var effectiveOptions = _options;
            var workingDirectoryUnknown = false;
            if (_attribution.HasAttribution && !_attribution.IsDynamic)
            {
                effectiveOptions = new PwshParserOptions
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

            var segment = new Segment
            {
                PrecedingOperator = compatibilityOperator,
                Depth = _groupDepth,
            };
            segment.Tokens.AddRange(segmentTokens);
            var built = BuildSegment(
                segment,
                _source,
                _options,
                effectiveOptions,
                workingDirectoryUnknown,
                _recursionDepth,
                _structuralDepth + _groupDepth,
                _markWrapped,
                _attribution);
            if (built.Error is not null)
            {
                error = built.Error;
                return false;
            }

            if (built.Syntax is not null)
            {
                if (substitutions.Count > 0)
                {
                    error = "PowerShell wrapper recursion cannot retain parent-scope substitutions safely";
                    return false;
                }

                command = built.Syntax;
                return true;
            }

            if (built.Clauses.Count != 1)
            {
                error = "simple-command parser did not produce exactly one compatibility leaf";
                return false;
            }

            var clause = built.Clauses[0];
            if (!built.IsRecursion)
            {
                clause = AttachAttributionArg(clause, _attribution);
                UpdateAttribution(built.Clauses[0], _options, _attribution);
            }

            if (!TryParseCommandExecutionRegions(
                    clause,
                    out var executionRegions,
                    out error))
            {
                return false;
            }

            var first = segmentTokens[0];
            var last = segmentTokens[segmentTokens.Count - 1];
            var simple = new SimpleCommandSyntax
            {
                Clause = clause,
                Substitutions = substitutions,
                ExecutionRegions = executionRegions,
                SourceStart = first.SourceStart,
                SourceLength = last.SourceStart + last.SourceLength - first.SourceStart,
            };
            RegisterFacts(
                simple,
                segmentTokens,
                built.UsesNativeArgumentBinding);
            command = simple;
            return true;
        }

        private bool TryParseDirectExecutionRegion(
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            error = null;
            var origin = IsOperator("&")
                ? ExecutionRegionOrigin.DirectCall
                : _tokens[_position].Kind == PwshTokenKind.Word &&
                    _tokens[_position].Value == "."
                    ? ExecutionRegionOrigin.DotSource
                    : ExecutionRegionOrigin.Unknown;
            if (origin == ExecutionRegionOrigin.Unknown ||
                _position + 1 >= _tokens.Count ||
                _tokens[_position + 1].Kind != PwshTokenKind.ScriptBlock)
            {
                return false;
            }

            var token = _tokens[_position + 1];
            var next = _position + 2;
            if (next < _tokens.Count && !IsStructuralBoundary(_tokens[next]))
            {
                error = "direct PowerShell script-block arguments are not supported";
                return true;
            }

            if (!TryParseScriptBlockBody(token, out var body, out error))
            {
                return true;
            }

            _position = next;
            command = new ExecutionRegionSyntax
            {
                Origin = origin,
                Phase = ExecutionRegionPhase.Main,
                Timing = ExecutionRegionTiming.Synchronous,
                Cardinality = ExecutionRegionCardinality.Once,
                Body = body,
                SourceStart = token.SourceStart,
                SourceLength = token.SourceLength,
            };
            return true;
        }

        private bool TryParseCommandExecutionRegions(
            Clause clause,
            out IReadOnlyList<ExecutionRegionSyntax> executionRegions,
            out string? error)
        {
            var binding = PwshExecutionRegionBindingCatalog.Bind(
                clause,
                commandIdentityProven: false);
            if (binding.Status == PwshExecutionRegionBindingStatus.NotApplicable)
            {
                executionRegions = Array.Empty<ExecutionRegionSyntax>();
                error = null;
                return true;
            }

            if (_structuralDepth + _groupDepth + 1 >
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                executionRegions = Array.Empty<ExecutionRegionSyntax>();
                error = "PowerShell structural nesting depth exceeded (>16)";
                return false;
            }

            var parsed = new List<ExecutionRegionSyntax>(binding.Bindings.Count);
            error = null;
            for (var index = 0; index < binding.Bindings.Count; index++)
            {
                var blockBinding = binding.Bindings[index];
                if (blockBinding.HostClauseElementIndex < 0
                    || blockBinding.HostClauseElementIndex >= clause.Elements.Count
                    || !TryCreateScriptBlockToken(
                        clause.Elements[blockBinding.HostClauseElementIndex],
                        out var token)
                    || !TryParseScriptBlockBody(token, out var body, out error))
                {
                    executionRegions = Array.Empty<ExecutionRegionSyntax>();
                    error ??= "PowerShell script-block binding could not be mapped exactly";
                    return false;
                }

                parsed.Add(new ExecutionRegionSyntax
                {
                    Origin = ExecutionRegionOrigin.CommandArgument,
                    HostClauseElementIndex = blockBinding.HostClauseElementIndex,
                    Phase = blockBinding.Phase,
                    Timing = blockBinding.Timing,
                    Cardinality = blockBinding.Cardinality,
                    Body = body,
                    SourceStart = token.SourceStart,
                    SourceLength = token.SourceLength,
                });
            }

            executionRegions = parsed;
            error = null;
            return true;
        }

        private static bool TryCreateScriptBlockToken(
            ClauseElement element,
            out PwshToken token)
        {
            token = default;
            if (element.SourceStart is null || element.SourceLength is null)
            {
                return false;
            }

            var open = element.Raw.IndexOf('{');
            var close = element.Raw.LastIndexOf('}');
            if (open < 0 || close <= open)
            {
                return false;
            }

            var length = close - open + 1;
            token = new PwshToken(
                PwshTokenKind.ScriptBlock,
                element.Raw.Substring(open, length),
                null,
                element.SourceStart.Value + open,
                length,
                null);
            return true;
        }

        private bool TryParseScriptBlockBody(
            PwshToken token,
            out ShellBlockSyntax body,
            out string? error)
        {
            if (token.SourceLength < 2)
            {
                body = new ShellBlockSyntax();
                error = "PowerShell script-block token was not completely delimited";
                return false;
            }

            var sourceStart = token.SourceStart + 1;
            var sourceLength = token.SourceLength - 2;
            var source = _source.Substring(sourceStart, sourceLength);
            var relativeTokens = PwshLexer.Tokenize(source);
            foreach (var relativeToken in relativeTokens)
            {
                if (relativeToken.Kind == PwshTokenKind.UnparseableSentinel)
                {
                    body = new ShellBlockSyntax();
                    error = relativeToken.UnparseableReason;
                    return false;
                }
            }

            var significant = FilterSignificant(relativeTokens);
            if (TryDetectAnomaly(significant, out error))
            {
                body = new ShellBlockSyntax();
                return false;
            }

            if (significant.Count > 0 && IsUnsupportedSubstitutionBody(source, significant))
            {
                foreach (var expressionToken in significant)
                {
                    if (HasPowerShellSubexpression(expressionToken)
                        || expressionToken.Kind is PwshTokenKind.Subexpression
                            or PwshTokenKind.ScriptBlock
                            or PwshTokenKind.Splat
                        || expressionToken.IsStatementSeparator
                        || expressionToken.Kind == PwshTokenKind.Operator)
                    {
                        body = new ShellBlockSyntax();
                        error = "unsupported execution-bearing PowerShell script-block expression";
                        return false;
                    }
                }

                body = new ShellBlockSyntax
                {
                    SourceStart = sourceStart,
                    SourceLength = sourceLength,
                };
                error = null;
                return true;
            }

            var coordinator = new StructuralCoordinator(
                _source,
                ShiftTokens(significant, sourceStart),
                _options,
                _recursionDepth,
                _structuralDepth + _groupDepth + 1,
                _markWrapped,
                _attribution,
                sourceStart,
                sourceLength,
                CompoundOperator.None,
                insideCommandSubstitution: false);
            if (!coordinator.TryParse(out body, out error))
            {
                return false;
            }

            MergeFacts(coordinator);
            return true;
        }

        private static bool HasPowerShellSubexpression(PwshToken token)
        {
            if (token.ResolverValue is null)
            {
                return false;
            }

            foreach (var fragment in token.ResolverValue.Fragments)
            {
                if (fragment.Kind == ShellValueFragmentKind.Opaque
                    && fragment.OpaqueCause == ShellOpaqueCause.PowerShellSubexpression)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsForEachCommandArgument(
            IReadOnlyList<PwshToken> prefix,
            CompoundOperator compatibilityOperator)
        {
            if (prefix.Count == 1 && compatibilityOperator == CompoundOperator.Pipe)
            {
                return prefix[0].Kind == PwshTokenKind.Word &&
                    string.Equals(
                        prefix[0].Value,
                        "foreach",
                        StringComparison.OrdinalIgnoreCase);
            }

            return prefix.Count == 2 &&
                prefix[0].Kind == PwshTokenKind.Operator &&
                prefix[0].OperatorText == "&" &&
                prefix[1].Kind == PwshTokenKind.Word &&
                string.Equals(
                    prefix[1].Value,
                    "foreach",
                    StringComparison.OrdinalIgnoreCase);
        }

        private void CollapseSafeForEachCommandArgument(
            List<PwshToken> tokens,
            CompoundOperator compatibilityOperator)
        {
            var openIndex = compatibilityOperator == CompoundOperator.Pipe ? 1 : 2;
            if (tokens.Count != openIndex + 3 ||
                !IsForEachCommandArgument(tokens.GetRange(0, openIndex), compatibilityOperator) ||
                tokens[openIndex].Kind != PwshTokenKind.Operator ||
                tokens[openIndex].OperatorText != "(" ||
                !IsSafeOpaqueForEachCommandArgument(tokens[openIndex + 1]) ||
                tokens[openIndex + 2].Kind != PwshTokenKind.Operator ||
                tokens[openIndex + 2].OperatorText != ")")
            {
                return;
            }

            var open = tokens[openIndex];
            var close = tokens[openIndex + 2];
            var length = close.SourceStart + close.SourceLength - open.SourceStart;
            var raw = _source.Substring(open.SourceStart, length);
            tokens.RemoveRange(openIndex, 3);
            tokens.Insert(
                openIndex,
                new PwshToken(
                    PwshTokenKind.Word,
                    raw,
                    null,
                    open.SourceStart,
                    length,
                    null)
                {
                    ResolverValue = ShellValue.Opaque(
                        raw,
                        ShellOpaqueCause.Unsupported,
                        open.SourceStart,
                        length),
                });
        }

        private static bool IsSafeOpaqueForEachCommandArgument(PwshToken token)
        {
            if (token.Kind == PwshTokenKind.QuotedString)
            {
                return !token.HasInterpolation;
            }

            if (token.Kind != PwshTokenKind.Word)
            {
                return false;
            }

            return token.Value.StartsWith("$", StringComparison.Ordinal) ||
                IsNumericExpressionWord(token.Value);
        }

        private bool TryParseGroup(
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            if (_structuralDepth + _groupDepth >=
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                command = null;
                error = "PowerShell structural nesting depth exceeded (>16)";
                return false;
            }

            var open = _tokens[_position++];
            _groupDepth++;
            SkipNewlines();
            var parsed = TryParsePipeline(
                compatibilityOperator,
                out var bodyCommand,
                out error);
            _groupDepth--;
            if (!parsed)
            {
                command = null;
                return false;
            }

            SkipNewlines();
            if (_position == _tokens.Count || !IsOperator(")"))
            {
                command = null;
                error = $"unbalanced '(' grouping at position {_sourceStart + _sourceLength}";
                return false;
            }

            var close = _tokens[_position++];
            command = new GroupSyntax
            {
                GroupKind = ShellGroupKind.CurrentScope,
                Body = new ShellBlockSyntax
                {
                    Statements = bodyCommand is null
                        ? Array.Empty<ShellSyntaxNode>()
                        : new[] { bodyCommand },
                    SourceStart = open.SourceStart + open.SourceLength,
                    SourceLength = close.SourceStart - open.SourceStart - open.SourceLength,
                },
                SourceStart = open.SourceStart,
                SourceLength = close.SourceStart + close.SourceLength - open.SourceStart,
            };
            return true;
        }

        private List<PwshToken> CopyTokens(int start, int end)
        {
            var copied = new List<PwshToken>(end - start);
            for (var index = start; index < end; index++)
            {
                copied.Add(_tokens[index]);
            }

            return copied;
        }

        private bool TryReadListOperator(out CompoundOperator @operator)
        {
            @operator = CompoundOperator.None;
            if (_position == _tokens.Count)
            {
                return false;
            }

            var token = _tokens[_position];
            if (token.Kind == PwshTokenKind.Whitespace)
            {
                @operator = CompoundOperator.Sequence;
                _position++;
                return true;
            }

            if (token.Kind != PwshTokenKind.Operator)
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
                   _tokens[_position].Kind == PwshTokenKind.Whitespace)
            {
                _position++;
            }
        }

        private bool IsOperator(string value) =>
            _position < _tokens.Count &&
            _tokens[_position].Kind == PwshTokenKind.Operator &&
            string.Equals(_tokens[_position].OperatorText, value, StringComparison.Ordinal);

        private bool TryCollectCommandSubstitutions(
            IReadOnlyList<PwshToken> tokens,
            out IReadOnlyList<ShellValueFragment> substitutions,
            out string? error)
        {
            var discovered = new List<ShellValueFragment>();
            foreach (var token in tokens)
            {
                if (token.ResolverValue is null)
                {
                    continue;
                }

                foreach (var fragment in token.ResolverValue.Fragments)
                {
                    if (fragment.Kind != ShellValueFragmentKind.Opaque ||
                        fragment.OpaqueCause != ShellOpaqueCause.PowerShellSubexpression)
                    {
                        continue;
                    }

                    if (fragment.SourceStart is null || fragment.SourceLength is null ||
                        fragment.SourceLength < 3 ||
                        fragment.SourceStart < _sourceStart ||
                        fragment.SourceStart + fragment.SourceLength >
                            _sourceStart + _sourceLength)
                    {
                        substitutions = Array.Empty<ShellValueFragment>();
                        error = "PowerShell subexpression has invalid source provenance";
                        return false;
                    }

                    var raw = _source.Substring(
                        fragment.SourceStart.Value,
                        fragment.SourceLength.Value);
                    if (!raw.StartsWith("$(", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (raw[raw.Length - 1] != ')')
                    {
                        substitutions = Array.Empty<ShellValueFragment>();
                        error = "unsupported PowerShell subexpression provenance";
                        return false;
                    }

                    discovered.Add(fragment);
                }
            }

            substitutions = discovered;
            error = null;
            return true;
        }

        private static bool IsUnsupportedSubstitutionBody(
            string source,
            IReadOnlyList<PwshToken> tokens)
        {
            var hasCallOperator = tokens[0].Kind == PwshTokenKind.Operator &&
                tokens[0].OperatorText == "&";
            if (hasCallOperator)
            {
                return false;
            }

            var value = tokens[0].Value;
            if (value.Length == 0)
            {
                return false;
            }

            var trimmed = source.TrimStart();
            if (trimmed.StartsWith(",", StringComparison.Ordinal) ||
                trimmed.StartsWith("!", StringComparison.Ordinal) ||
                trimmed.StartsWith("++", StringComparison.Ordinal) ||
                trimmed.StartsWith("--", StringComparison.Ordinal) ||
                StartsWithUnarySignExpression(trimmed) ||
                StartsWithUnaryExpressionOperator(trimmed) ||
                trimmed.StartsWith("@", StringComparison.Ordinal) ||
                trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return true;
            }

            if (tokens[0].Kind == PwshTokenKind.Word &&
                value[0] == '$' && !value.StartsWith("$(", StringComparison.Ordinal))
            {
                return true;
            }

            if (tokens[0].Kind is not PwshTokenKind.Word and not PwshTokenKind.Parameter)
            {
                return false;
            }

            return IsNumericExpressionWord(value);
        }

        private static bool StartsWithUnaryExpressionOperator(string value)
        {
            foreach (var unaryOperator in new[] { "-not", "-bnot", "-join", "-split" })
            {
                if (value.StartsWith(unaryOperator, StringComparison.OrdinalIgnoreCase) &&
                    (value.Length == unaryOperator.Length ||
                     char.IsWhiteSpace(value[unaryOperator.Length])))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool StartsWithUnarySignExpression(string value) =>
            value.Length > 1 && value[0] is '+' or '-' &&
            value[1] is '$' or '[' or '(' or '\'' or '"' or '@' or '{';

        private static bool IsNumericExpressionWord(string value)
        {
            var index = value[0] is '+' or '-' ? 1 : 0;
            if (index == value.Length || !char.IsDigit(value[index]))
            {
                return false;
            }

            if (index + 1 < value.Length && value[index] == '0' &&
                value[index + 1] is 'x' or 'X' or 'b' or 'B')
            {
                var isHex = value[index + 1] is 'x' or 'X';
                index += 2;
                var digitStart = index;
                while (index < value.Length && (value[index] == '_' ||
                       isHex && Uri.IsHexDigit(value[index]) ||
                       !isHex && value[index] is '0' or '1'))
                {
                    index++;
                }

                return index > digitStart && IsNumericSuffix(value.Substring(index));
            }

            var hasExponent = false;
            while (index < value.Length)
            {
                var character = value[index];
                if (char.IsDigit(character) || character is '_' or '.')
                {
                    index++;
                    continue;
                }

                if (!hasExponent && character is 'e' or 'E')
                {
                    hasExponent = true;
                    index++;
                    if (index < value.Length && value[index] is '+' or '-')
                    {
                        index++;
                    }

                    continue;
                }

                break;
            }

            var suffix = value.Substring(index);
            if (suffix.Length > 0 && suffix[0] is '+' or '-' or '*' or '/' or '%')
            {
                return true;
            }

            return IsNumericSuffix(suffix);
        }

        private static bool IsNumericSuffix(string suffix) =>
            suffix.Length == 0 || suffix.Equals("d", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("l", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("u", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("lu", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("n", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("s", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("us", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("uy", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("kb", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("mb", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("gb", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("tb", StringComparison.OrdinalIgnoreCase) ||
            suffix.Equals("pb", StringComparison.OrdinalIgnoreCase);

        private bool TryValidateOpaqueExpressions(
            IReadOnlyList<PwshToken> tokens,
            out string? error)
        {
            foreach (var token in tokens)
            {
                if (token.ResolverValue is null)
                {
                    continue;
                }

                foreach (var fragment in token.ResolverValue.Fragments)
                {
                    if (fragment.Kind != ShellValueFragmentKind.Opaque ||
                        fragment.OpaqueCause != ShellOpaqueCause.PowerShellSubexpression ||
                        fragment.SourceStart is null || fragment.SourceLength is null)
                    {
                        continue;
                    }

                    var fragmentEnd = fragment.SourceStart.Value +
                        fragment.SourceLength.Value;
                    if (fragmentEnd < _sourceStart + _sourceLength &&
                        _source[fragmentEnd] is '.' or '[')
                    {
                        error = "PowerShell expression suffix after subexpression is not supported";
                        return false;
                    }

                    var raw = _source.Substring(
                        fragment.SourceStart.Value,
                        fragment.SourceLength.Value);
                    if (raw.StartsWith("@(", StringComparison.Ordinal) &&
                        !IsLiteralArrayExpression(raw) ||
                        raw.StartsWith("@{", StringComparison.Ordinal) &&
                        !IsLiteralHashExpression(raw))
                    {
                        error = "execution-bearing PowerShell @() or @{} expressions are not supported";
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private static bool IsLiteralArrayExpression(string raw)
        {
            var index = 2;
            var end = raw.Length - 1;
            SkipExpressionWhitespace(raw, ref index, end);
            if (index == end)
            {
                return true;
            }

            while (index < end)
            {
                if (!TryReadLiteralExpressionValue(raw, ref index, end))
                {
                    return false;
                }

                SkipExpressionWhitespace(raw, ref index, end);
                if (index == end)
                {
                    return true;
                }

                if (raw[index] != ',')
                {
                    return false;
                }

                index++;
                SkipExpressionWhitespace(raw, ref index, end);
            }

            return false;
        }

        private static bool IsLiteralHashExpression(string raw)
        {
            var index = 2;
            var end = raw.Length - 1;
            SkipExpressionWhitespace(raw, ref index, end);
            if (index == end)
            {
                return true;
            }

            while (index < end)
            {
                if (!TryReadLiteralHashKey(raw, ref index, end))
                {
                    return false;
                }

                SkipExpressionWhitespace(raw, ref index, end);
                if (index >= end || raw[index] != '=')
                {
                    return false;
                }

                index++;
                SkipExpressionWhitespace(raw, ref index, end);
                if (!TryReadLiteralExpressionValue(raw, ref index, end))
                {
                    return false;
                }

                SkipExpressionWhitespace(raw, ref index, end);
                if (index == end)
                {
                    return true;
                }

                if (raw[index] != ';')
                {
                    return false;
                }

                index++;
                SkipExpressionWhitespace(raw, ref index, end);
            }

            return false;
        }

        private static bool TryReadLiteralHashKey(string raw, ref int index, int end)
        {
            if (index < end && raw[index] is '\'' or '"')
            {
                return TryReadQuotedLiteral(raw, ref index, end);
            }

            var start = index;
            while (index < end &&
                   (raw[index] == '_' || raw[index] == '-' ||
                    char.IsLetterOrDigit(raw[index])))
            {
                index++;
            }

            return index > start;
        }

        private static bool TryReadLiteralExpressionValue(
            string raw,
            ref int index,
            int end)
        {
            if (index >= end)
            {
                return false;
            }

            if (raw[index] is '\'' or '"')
            {
                return TryReadQuotedLiteral(raw, ref index, end);
            }

            foreach (var literal in new[] { "$true", "$false", "$null" })
            {
                if (index + literal.Length <= end &&
                    string.Compare(
                        raw,
                        index,
                        literal,
                        0,
                        literal.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    index += literal.Length;
                    return true;
                }
            }

            var start = index;
            if (raw[index] is '+' or '-')
            {
                index++;
            }

            var hasDigit = false;
            while (index < end && (char.IsDigit(raw[index]) || raw[index] == '.'))
            {
                hasDigit |= char.IsDigit(raw[index]);
                index++;
            }

            return hasDigit && index > start;
        }

        private static bool TryReadQuotedLiteral(string raw, ref int index, int end)
        {
            var quote = raw[index++];
            while (index < end)
            {
                if (quote == '"' && raw[index] == '`' && index + 1 < end)
                {
                    index += 2;
                    continue;
                }

                if (raw[index] != quote)
                {
                    if (quote == '"' && raw[index] == '$' && index + 1 < end &&
                        raw[index + 1] == '(')
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (quote == '\'' && index + 1 < end && raw[index + 1] == '\'')
                {
                    index += 2;
                    continue;
                }

                index++;
                return true;
            }

            return false;
        }

        private static void SkipExpressionWhitespace(string raw, ref int index, int end)
        {
            while (index < end && char.IsWhiteSpace(raw[index]))
            {
                index++;
            }
        }

        private static bool IsDirectCommandSubstitution(
            PwshToken token,
            ShellValueFragment substitution) =>
            token.Kind == PwshTokenKind.Subexpression &&
            substitution.SourceStart == token.SourceStart &&
            substitution.SourceLength == token.SourceLength;

        private bool TryParseStandaloneSubstitution(
            ShellValueFragment fragment,
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            if (!TryParseSubstitutionBody(
                    fragment,
                    compatibilityOperator,
                    out var body,
                    out error))
            {
                command = null;
                return false;
            }

            command = new CommandSubstitutionSyntax
            {
                Body = body,
                SourceStart = fragment.SourceStart,
                SourceLength = fragment.SourceLength,
            };
            return true;
        }

        private bool TryParseCommandSubstitutions(
            IReadOnlyList<ShellValueFragment> fragments,
            out IReadOnlyList<CommandSubstitutionSyntax> substitutions,
            out string? error)
        {
            if (fragments.Count == 0)
            {
                substitutions = Array.Empty<CommandSubstitutionSyntax>();
                error = null;
                return true;
            }

            if (_structuralDepth + _groupDepth + 1 >
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                substitutions = Array.Empty<CommandSubstitutionSyntax>();
                error = "PowerShell structural nesting depth exceeded (>16)";
                return false;
            }

            var parsed = new List<CommandSubstitutionSyntax>(fragments.Count);
            foreach (var fragment in fragments)
            {
                if (!TryParseSubstitutionBody(
                        fragment,
                        CompoundOperator.None,
                        out var body,
                        out error))
                {
                    substitutions = Array.Empty<CommandSubstitutionSyntax>();
                    return false;
                }

                parsed.Add(new CommandSubstitutionSyntax
                {
                    Body = body,
                    SourceStart = fragment.SourceStart,
                    SourceLength = fragment.SourceLength,
                });
            }

            substitutions = parsed;
            error = null;
            return true;
        }

        private bool TryParseSubstitutionBody(
            ShellValueFragment fragment,
            CompoundOperator firstCompatibilityOperator,
            out ShellBlockSyntax body,
            out string? error)
        {
            var sourceStart = fragment.SourceStart!.Value + 2;
            var sourceLength = fragment.SourceLength!.Value - 3;
            var source = _source.Substring(sourceStart, sourceLength);
            var relativeTokens = PwshLexer.Tokenize(source);
            foreach (var token in relativeTokens)
            {
                if (token.Kind == PwshTokenKind.UnparseableSentinel)
                {
                    body = new ShellBlockSyntax();
                    error = token.UnparseableReason;
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
                _options,
                _recursionDepth,
                _structuralDepth + _groupDepth + 1,
                _markWrapped,
                _attribution,
                sourceStart,
                sourceLength,
                firstCompatibilityOperator,
                insideCommandSubstitution: true);
            if (!coordinator.TryParse(out body, out error))
            {
                return false;
            }

            MergeFacts(coordinator);
            return true;
        }
    }

    private static IReadOnlyList<Clause> CollectIncompleteForEachClauses(
        ShellSyntaxNode syntax)
    {
        var clauses = new List<Clause>();
        CollectIncompleteForEachClauses(syntax, isStateIncomplete: false, clauses);
        return clauses;
    }

    private static bool CollectIncompleteForEachClauses(
        ShellSyntaxNode syntax,
        bool isStateIncomplete,
        List<Clause> clauses)
    {
        switch (syntax)
        {
            case SimpleCommandSyntax simple:
                foreach (var substitution in simple.Substitutions)
                {
                    isStateIncomplete = CollectIncompleteForEachClauses(
                        substitution,
                        isStateIncomplete,
                        clauses);
                }

                if (isStateIncomplete)
                {
                    clauses.Add(simple.Clause);
                }

                return isStateIncomplete;
            case ShellBlockSyntax block:
                foreach (var statement in block.Statements)
                {
                    isStateIncomplete = CollectIncompleteForEachClauses(
                        statement,
                        isStateIncomplete,
                        clauses);
                }

                return isStateIncomplete;
            case CommandListSyntax list:
                foreach (var item in list.Items)
                {
                    isStateIncomplete = CollectIncompleteForEachClauses(
                        item.Command,
                        isStateIncomplete,
                        clauses);
                }

                return isStateIncomplete;
            case PipelineSyntax pipeline:
                foreach (var stage in pipeline.Stages)
                {
                    isStateIncomplete = CollectIncompleteForEachClauses(
                        stage,
                        isStateIncomplete,
                        clauses);
                }

                return isStateIncomplete;
            case GroupSyntax group:
                var groupState = CollectIncompleteForEachClauses(
                    group.Body,
                    isStateIncomplete,
                    clauses);
                return group.GroupKind == ShellGroupKind.IsolatedScope
                    ? isStateIncomplete
                    : groupState;
            case ForEachSyntax forEach:
                CollectIncompleteForEachClauses(
                    forEach.IteratorCommands,
                    isStateIncomplete,
                    clauses);
                CollectIncompleteForEachClauses(
                    forEach.Body,
                    isStateIncomplete: true,
                    clauses);
                return true;
            case ConditionLoopSyntax loop:
                CollectIncompleteForEachClauses(
                    loop.Condition,
                    isStateIncomplete,
                    clauses);
                CollectIncompleteForEachClauses(
                    loop.Body,
                    isStateIncomplete: true,
                    clauses);
                return true;
            case ConditionalSyntax conditional:
                var branchState = isStateIncomplete;
                foreach (var branch in conditional.Branches)
                {
                    branchState |= CollectIncompleteForEachClauses(
                        branch,
                        isStateIncomplete,
                        clauses);
                }

                if (conditional.Else is not null)
                {
                    branchState |= CollectIncompleteForEachClauses(
                        conditional.Else,
                        isStateIncomplete,
                        clauses);
                }

                return branchState;
            case ConditionalBranchSyntax branch:
                var conditionState = CollectIncompleteForEachClauses(
                    branch.Condition,
                    isStateIncomplete,
                    clauses);
                return CollectIncompleteForEachClauses(
                    branch.Body,
                    conditionState,
                    clauses);
            case CommandSubstitutionSyntax substitution:
                return CollectIncompleteForEachClauses(
                    substitution.Body,
                    isStateIncomplete,
                    clauses);
            default:
                return isStateIncomplete;
        }
    }

    private static bool ContainsReference(IReadOnlyList<Clause> clauses, Clause target)
    {
        foreach (var clause in clauses)
        {
            if (ReferenceEquals(clause, target))
            {
                return true;
            }
        }

        return false;
    }

    private sealed partial class StructuralCoordinator
    {
        private void RegisterFacts(
            SimpleCommandSyntax simple,
            IReadOnlyList<PwshToken> sourceTokens,
            bool? usesNativeArgumentBinding)
        {
            var provenance = new List<ShellValueElementProvenance>();
            var redirectProvenance = new List<RedirectTargetProvenance>();
            var hasCompleteProvenance = true;
            var redirectIndex = 0;
            for (var elementIndex = 0;
                 elementIndex < simple.Clause.Elements.Count;
                 elementIndex++)
            {
                var element = simple.Clause.Elements[elementIndex];
                if (element.Role == ClauseElementRole.Redirect)
                {
                    if (TryGetRedirectTargetValue(
                            element,
                            sourceTokens,
                            out var redirectValue))
                    {
                        redirectProvenance.Add(new RedirectTargetProvenance(
                            redirectIndex,
                            elementIndex,
                            redirectValue,
                            InvocationScopeDepth: 0));
                    }

                    redirectIndex++;
                    continue;
                }

                if (element.Role != ClauseElementRole.Argument)
                {
                    continue;
                }

                ClauseElementProvenance.SetArgumentBindingCandidate(
                    element,
                    usesNativeArgumentBinding);

                if (!TryGetElementValue(element, sourceTokens, out var value))
                {
                    hasCompleteProvenance = false;
                    continue;
                }

                provenance.Add(new ShellValueElementProvenance(
                    elementIndex,
                    value,
                    usesNativeArgumentBinding));
            }

            var redirects = PwshRedirectAnalysis.Analyze(simple.Clause);
            _facts.Add(simple.Clause, new CommandOccurrenceFacts
            {
                Redirects = redirects,
                RedirectTargetProvenance = redirectProvenance.ToArray(),
                ValueProvenance = provenance.ToArray(),
                HasCompleteValueProvenance = hasCompleteProvenance,
                IsComplete = IsStructurallyComplete(simple) &&
                    AreRedirectsComplete(redirects),
            });
        }

        private static bool TryGetRedirectTargetValue(
            ClauseElement element,
            IReadOnlyList<PwshToken> sourceTokens,
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
            var sawOperator = false;
            var targetEnd = -1;
            foreach (var token in sourceTokens)
            {
                var tokenEnd = token.SourceStart + token.SourceLength;
                if (token.SourceStart < elementStart || tokenEnd > elementEnd)
                {
                    continue;
                }

                if (!sawOperator)
                {
                    if (token.SourceStart != elementStart ||
                        token.Kind != PwshTokenKind.Operator)
                    {
                        return false;
                    }

                    sawOperator = true;
                    continue;
                }

                targetEnd = tokenEnd;
                values.Add(token.ResolverValue ??
                    ShellValue.Literal(token.Value, token.SourceStart, token.SourceLength));
            }

            if (!sawOperator || values.Count == 0 || targetEnd != elementEnd)
            {
                return false;
            }

            value = values.Count == 1 ? values[0] : ShellValue.Concat(values);
            return true;
        }

        private static bool TryGetAuthoredRedirectTargetValue(
            ClauseElement element,
            out ShellValue value)
        {
            value = ShellValue.Literal(string.Empty);
            var localTokens = PwshLexer.Tokenize(element.Raw);
            foreach (var token in localTokens)
            {
                if (token.Kind == PwshTokenKind.UnparseableSentinel)
                {
                    return false;
                }
            }

            var significant = FilterSignificant(localTokens);
            var localElement = element with
            {
                SourceStart = 0,
                SourceLength = element.Raw.Length,
            };
            return TryGetRedirectTargetValue(localElement, significant, out value);
        }

        private static bool TryGetAuthoredElementValue(
            ClauseElement element,
            out ShellValue value)
        {
            value = ShellValue.Literal(string.Empty);
            var localTokens = PwshLexer.Tokenize(element.Raw);
            foreach (var token in localTokens)
            {
                if (token.Kind == PwshTokenKind.UnparseableSentinel)
                {
                    return false;
                }
            }

            var localElement = element with
            {
                SourceStart = 0,
                SourceLength = element.Raw.Length,
            };
            return TryGetElementValue(
                localElement,
                FilterSignificant(localTokens),
                out value);
        }

        private static bool TryGetElementValue(
            ClauseElement element,
            IReadOnlyList<PwshToken> sourceTokens,
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

            if (values.Count == 0 || coveredStart != elementStart || coveredEnd != elementEnd)
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

            foreach (var pair in nested._forEachPlans)
            {
                _forEachPlans.Add(pair.Key, pair.Value);
            }
        }

        private sealed class ClauseReferenceComparer : IEqualityComparer<Clause>
        {
            internal static ClauseReferenceComparer Instance { get; } = new();

            public bool Equals(Clause? x, Clause? y) => ReferenceEquals(x, y);

            public int GetHashCode(Clause obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class ForEachReferenceComparer : IEqualityComparer<ForEachSyntax>
        {
            internal static ForEachReferenceComparer Instance { get; } = new();

            public bool Equals(ForEachSyntax? x, ForEachSyntax? y) =>
                ReferenceEquals(x, y);

            public int GetHashCode(ForEachSyntax obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    private static IReadOnlyList<PwshToken> ShiftTokens(
        IReadOnlyList<PwshToken> tokens,
        int sourceOffset)
    {
        var shifted = new PwshToken[tokens.Count];
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

    private static bool IsStructuralBoundary(PwshToken token) =>
        token.Kind == PwshTokenKind.Whitespace ||
        token.Kind == PwshTokenKind.Operator && token.OperatorText is
            "(" or ")" or "&&" or "||" or ";" or "|";

    private static bool IsListOperator(PwshToken token) =>
        token.Kind == PwshTokenKind.Whitespace ||
        token.Kind == PwshTokenKind.Operator && token.OperatorText is "&&" or "||" or ";";

    private sealed class DecodedCloneState
    {
        internal CompoundOperator FirstOperator { get; init; }

        internal bool OuterSubshell { get; init; }

        internal int LeafCount { get; init; }

        internal IReadOnlyList<Redirect> WrapperRedirects { get; init; } =
            Array.Empty<Redirect>();

        internal IReadOnlyList<ClauseElement> WrapperRedirectElements { get; init; } =
            Array.Empty<ClauseElement>();

        internal int LeafIndex { get; set; }

        internal int IsolatedScopeDepth { get; set; }

        internal bool PreserveArgumentBindingCandidates { get; init; }
    }

    private static bool TryBuildDecodedWrapper(
        ParsedCommand inner,
        Segment outer,
        ShellGroupKind groupKind,
        IReadOnlyList<Redirect> wrapperRedirects,
        IReadOnlyList<ClauseElement> wrapperRedirectElements,
        out GroupSyntax? wrapper)
    {
        var state = new DecodedCloneState
        {
            FirstOperator = outer.PrecedingOperator,
            OuterSubshell = outer.Depth > 0,
            LeafCount = inner.Commands.Count,
            WrapperRedirects = wrapperRedirects,
            WrapperRedirectElements = wrapperRedirectElements,
            PreserveArgumentBindingCandidates = groupKind == ShellGroupKind.CurrentScope,
        };
        if (!TryCloneDecodedBlock(inner.Syntax, state, out var body))
        {
            wrapper = null;
            return false;
        }

        if (state.LeafCount == 0 && wrapperRedirects.Count > 0)
        {
            var redirectElements = new List<ClauseElement>(wrapperRedirectElements.Count);
            foreach (var element in wrapperRedirectElements)
            {
                redirectElements.Add(element with { PrecedingVerbElementCount = 0 });
            }

            body = new ShellBlockSyntax
            {
                Statements = new ShellSyntaxNode[]
                {
                    new SimpleCommandSyntax
                    {
                        Clause = new Clause
                        {
                            Operator = outer.PrecedingOperator,
                            Verb = new VerbChain(),
                            Args = Array.Empty<Arg>(),
                            Redirects = wrapperRedirects,
                            Elements = redirectElements,
                            IsSubshell = outer.Depth > 0,
                            IsCommandStringWrapped = true,
                        },
                    },
                },
            };
        }

        if (outer.Tokens.Count == 0)
        {
            wrapper = null;
            return false;
        }

        var first = outer.Tokens[0];
        var last = outer.Tokens[outer.Tokens.Count - 1];
        wrapper = new GroupSyntax
        {
            GroupKind = groupKind,
            Body = body,
            SourceStart = first.SourceStart,
            SourceLength = last.SourceStart + last.SourceLength - first.SourceStart,
        };
        return true;
    }

    private static bool TryCloneDecodedBlock(
        ShellBlockSyntax source,
        DecodedCloneState state,
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
            if (!TryCloneDecodedNode(statement, state, out var cloned))
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
        DecodedCloneState state,
        out ShellSyntaxNode? clone)
    {
        clone = null;
        switch (source)
        {
            case ShellBlockSyntax block:
                if (!TryCloneDecodedBlock(block, state, out var clonedBlock))
                {
                    return false;
                }

                clone = clonedBlock;
                return true;
            case SimpleCommandSyntax simple:
                var substitutions = new List<CommandSubstitutionSyntax>(simple.Substitutions.Count);
                foreach (var substitution in simple.Substitutions)
                {
                    if (!TryCloneDecodedNode(substitution, state, out var clonedSubstitution) ||
                        clonedSubstitution is not CommandSubstitutionSyntax typedSubstitution)
                    {
                        return false;
                    }

                    substitutions.Add(typedSubstitution);
                }

                var isFirst = state.LeafIndex == 0;
                var isLast = state.LeafIndex == state.LeafCount - 1;
                state.LeafIndex++;
                var executionRegions = new List<ExecutionRegionSyntax>(
                    simple.ExecutionRegions.Count);
                foreach (var executionRegion in simple.ExecutionRegions)
                {
                    if (!TryCloneDecodedNode(
                            executionRegion,
                            state,
                            out var clonedExecutionRegion) ||
                        clonedExecutionRegion is not ExecutionRegionSyntax typedExecutionRegion)
                    {
                        return false;
                    }

                    executionRegions.Add(typedExecutionRegion);
                }

                var redirects = new List<Redirect>(simple.Clause.Redirects.Count +
                    (isLast ? state.WrapperRedirects.Count : 0));
                redirects.AddRange(simple.Clause.Redirects);
                if (isLast)
                {
                    redirects.AddRange(state.WrapperRedirects);
                }

                var elements = new List<ClauseElement>(simple.Clause.Elements.Count +
                    (isLast ? state.WrapperRedirectElements.Count : 0));
                elements.AddRange(ClauseElementProvenance.WithoutOuterSourceSpans(
                    simple.Clause.Elements,
                    state.PreserveArgumentBindingCandidates));
                if (isLast)
                {
                    foreach (var redirectElement in state.WrapperRedirectElements)
                    {
                        var clonedRedirect = redirectElement with
                        {
                            PrecedingVerbElementCount = simple.Clause.Verb.Tokens.Count,
                        };
                        elements.Add(
                            ClauseElementProvenance.WithRedirectInvocationScopeDepth(
                                clonedRedirect,
                                state.IsolatedScopeDepth + 1));
                    }
                }

                clone = new SimpleCommandSyntax
                {
                    Clause = simple.Clause with
                    {
                        Operator = isFirst ? state.FirstOperator : simple.Clause.Operator,
                        Redirects = redirects,
                        Elements = elements,
                        IsSubshell = state.OuterSubshell || simple.Clause.IsSubshell,
                        IsCommandStringWrapped = true,
                    },
                    Substitutions = substitutions,
                    ExecutionRegions = executionRegions,
                };
                return true;
            case PipelineSyntax pipeline:
                return TryCloneDecodedCollection(
                    pipeline.Stages,
                    state,
                    stages => new PipelineSyntax { Stages = stages },
                    out clone);
            case CommandListSyntax list:
                var items = new List<CommandListItemSyntax>(list.Items.Count);
                foreach (var item in list.Items)
                {
                    if (item is null ||
                        !TryCloneDecodedNode(item.Command, state, out var itemCommand))
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
                if (group.GroupKind == ShellGroupKind.IsolatedScope)
                {
                    state.IsolatedScopeDepth++;
                }

                var clonedGroup = TryCloneDecodedBlock(
                    group.Body,
                    state,
                    out var groupBody);
                if (group.GroupKind == ShellGroupKind.IsolatedScope)
                {
                    state.IsolatedScopeDepth--;
                }

                if (!clonedGroup)
                {
                    return false;
                }

                clone = new GroupSyntax { GroupKind = group.GroupKind, Body = groupBody };
                return true;
            case ForEachSyntax forEach:
                if (!TryCloneDecodedBlock(forEach.IteratorCommands, state, out var iterator) ||
                    !TryCloneDecodedBlock(forEach.Body, state, out var forBody))
                {
                    return false;
                }

                clone = new ForEachSyntax
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
                return true;
            case ConditionLoopSyntax loop:
                if (!TryCloneDecodedBlock(loop.Condition, state, out var condition) ||
                    !TryCloneDecodedBlock(loop.Body, state, out var loopBody))
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
                    if (!TryCloneDecodedNode(branch, state, out var clonedBranch) ||
                        clonedBranch is not ConditionalBranchSyntax typedBranch)
                    {
                        return false;
                    }

                    branches.Add(typedBranch);
                }

                ShellBlockSyntax? @else = null;
                if (conditional.Else is not null &&
                    !TryCloneDecodedBlock(conditional.Else, state, out @else))
                {
                    return false;
                }

                clone = new ConditionalSyntax { Branches = branches, Else = @else };
                return true;
            case ConditionalBranchSyntax branch:
                if (!TryCloneDecodedBlock(branch.Condition, state, out var branchCondition) ||
                    !TryCloneDecodedBlock(branch.Body, state, out var branchBody))
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
                if (!TryCloneDecodedBlock(substitution.Body, state, out var substitutionBody))
                {
                    return false;
                }

                clone = new CommandSubstitutionSyntax { Body = substitutionBody };
                return true;
            case ExecutionRegionSyntax executionRegion:
                if (!TryCloneDecodedBlock(executionRegion.Body, state, out var executionBody))
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
        DecodedCloneState state,
        Func<IReadOnlyList<ShellSyntaxNode>, ShellSyntaxNode> factory,
        out ShellSyntaxNode? clone)
    {
        var children = new List<ShellSyntaxNode>(source.Count);
        foreach (var child in source)
        {
            if (!TryCloneDecodedNode(child, state, out var clonedChild))
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

    private static bool IsStructurallyComplete(SimpleCommandSyntax simple)
    {
        var clause = simple.Clause;
        if (clause.Verb.IsDynamic ||
            clause.Verb.Tokens.Count == 0)
        {
            return false;
        }

        if (HasUnexpandedPowerShellCommandString(clause))
        {
            return false;
        }

        foreach (var element in clause.Elements)
        {
            if (element.Kind == ArgKind.DynamicSkip &&
                (element.Raw.IndexOf("@(", StringComparison.Ordinal) >= 0 ||
                 element.Raw.IndexOf("@{", StringComparison.Ordinal) >= 0))
            {
                return false;
            }
        }

        return true;
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

    private static bool HasUnexpandedPowerShellCommandString(Clause clause)
    {
        var verb = clause.Verb.CanonicalVerb ??
            (clause.Verb.Tokens.Count > 0 ? clause.Verb.Tokens[0] : null);
        if (verb is null)
        {
            return false;
        }

        if (IsInvokeExpressionName(verb))
        {
            return true;
        }

        if (!IsPowerShellHostName(verb))
        {
            return false;
        }

        foreach (var element in clause.Elements)
        {
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (element.Kind != ArgKind.Literal ||
                LooksLikePowerShellCommandStringOption(element.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPowerShellHostName(string verb) =>
        string.Equals(verb, "pwsh", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "powershell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "powershell.exe", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePowerShellCommandStringOption(string value)
    {
        if (value.Length < 2 || value[0] != '-')
        {
            return false;
        }

        var name = value;
        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            name = name.Substring(0, colon);
        }

        return IsCommandParameter(name) ||
            IsEncodedCommandParameter(name) ||
            string.Equals(name, "-cwa", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("-commandw", StringComparison.OrdinalIgnoreCase);
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
}
