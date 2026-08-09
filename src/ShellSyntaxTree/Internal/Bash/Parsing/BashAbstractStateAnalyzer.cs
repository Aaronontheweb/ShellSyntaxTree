// -----------------------------------------------------------------------
// <copyright file="BashAbstractStateAnalyzer.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Computes execution-order Bash cwd facts after structural parsing. Parser
/// attribution remains a lexical construction aid; this pass owns the facts
/// exposed to security consumers.
/// </summary>
internal sealed class BashAbstractStateAnalyzer
{
    private const int MaxLoopAnalysisTransitions = 4096;

    private readonly BashParserOptions _options;
    private readonly Func<SimpleCommandSyntax, CommandOccurrenceFacts> _factsFactory;
    private readonly Func<ForEachSyntax, BashForInAnalysisPlan?> _forInPlanFactory;
    private readonly Dictionary<Clause, BashAbstractState> _inputs =
        new(ClauseReferenceComparer.Instance);
    private readonly Dictionary<Clause, Dictionary<int, ShellValueDomain>>
        _effectiveArguments = new(ClauseReferenceComparer.Instance);
    private readonly Dictionary<Clause, HashSet<int>> _cwdResolutionSanitization =
        new(ClauseReferenceComparer.Instance);
    private readonly List<BashForInAnalysisPlanReference> _rewrittenForInPlans = new();
    private bool _isComplete = true;
    private int _remainingLoopAnalysisTransitions = MaxLoopAnalysisTransitions;

    private BashAbstractStateAnalyzer(
        BashParserOptions options,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        Func<ForEachSyntax, BashForInAnalysisPlan?> forInPlanFactory)
    {
        _options = options;
        _factsFactory = factsFactory;
        _forInPlanFactory = forInPlanFactory;
    }

    internal static bool TryAnalyze(
        ShellBlockSyntax syntax,
        BashParserOptions options,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        Func<ForEachSyntax, BashForInAnalysisPlan?> forInPlanFactory,
        out ShellBlockSyntax analyzedSyntax,
        out Func<SimpleCommandSyntax, CommandOccurrenceFacts> analyzedFacts,
        out IReadOnlyList<BashForInAnalysisPlanReference> analyzedForInPlans)
    {
        var analyzer = new BashAbstractStateAnalyzer(
            options,
            factsFactory,
            forInPlanFactory);
        var initial = new BashAbstractState(
            options.WorkingDirectory ?? Environment.CurrentDirectory,
            hasCompatibilityAttribution: false,
            new BashLoopBindingContext());
        analyzer.AnalyzeBlock(syntax, initial);
        if (!analyzer._isComplete)
        {
            analyzedSyntax = syntax;
            analyzedFacts = factsFactory;
            analyzedForInPlans = Array.Empty<BashForInAnalysisPlanReference>();
            return false;
        }

        var facts = new Dictionary<Clause, CommandOccurrenceFacts>(
            ClauseReferenceComparer.Instance);
        analyzedSyntax = analyzer.RewriteBlock(syntax, facts);
        if (!analyzer._isComplete)
        {
            analyzedFacts = factsFactory;
            analyzedForInPlans = Array.Empty<BashForInAnalysisPlanReference>();
            return false;
        }

        analyzedFacts = simple => facts.TryGetValue(simple.Clause, out var value)
            ? value
            : new CommandOccurrenceFacts();
        analyzedForInPlans = analyzer._rewrittenForInPlans.ToArray();
        return true;
    }

    private BashFlowResult AnalyzeNode(ShellSyntaxNode node, BashAbstractState input) =>
        node switch
        {
            ShellBlockSyntax block => AnalyzeBlock(block, input),
            SimpleCommandSyntax simple => AnalyzeSimple(simple, input),
            CommandListSyntax list => AnalyzeList(list, input),
            PipelineSyntax pipeline => AnalyzePipeline(pipeline, input),
            GroupSyntax group => AnalyzeGroup(group, input),
            CommandSubstitutionSyntax substitution => AnalyzeIsolatedBody(
                substitution.Body,
                input,
                resetCompatibilityAttribution: true,
                clearBindings: false),
            ForEachSyntax forEach => AnalyzeForEach(forEach, input),
            ConditionLoopSyntax => BashFlowResult.Both(input.WithUnknownCwd()),
            ConditionalSyntax conditional => AnalyzeConditional(conditional, input),
            ConditionalBranchSyntax branch => AnalyzeBranch(branch, input),
            _ => BashFlowResult.Both(input.WithUnknownCwd()),
        };

    private BashFlowResult AnalyzeBlock(ShellBlockSyntax block, BashAbstractState input)
    {
        var flow = BashFlowResult.Both(input);
        foreach (var statement in block.Statements)
        {
            var joined = flow.JoinedState;
            if (joined is null)
            {
                break;
            }

            flow = AnalyzeNode(statement, joined.Value);
        }

        return flow;
    }

    private BashFlowResult AnalyzeSimple(
        SimpleCommandSyntax simple,
        BashAbstractState input)
    {
        foreach (var substitution in simple.Substitutions)
        {
            AnalyzeIsolatedBody(
                substitution.Body,
                input,
                resetCompatibilityAttribution: true,
                clearBindings: false);
        }

        if (input.Bindings.HasBindings &&
            IsPotentialPersistentBindingMutation(simple.Clause))
        {
            _isComplete = false;
            return new BashFlowResult(null, null);
        }

        RecordInput(simple.Clause, input);
        RecordEffectiveArguments(simple, input);
        var cwdTransfer = AnalyzeEffectiveCwdTransfer(simple, input);
        if (cwdTransfer is BashFlowResult effectiveFlow)
        {
            return effectiveFlow;
        }

        if (!TryGetLegacyCwdTransfer(simple.Clause, input, out var success))
        {
            return BashFlowResult.Both(input);
        }

        return new BashFlowResult(success, input);
    }

    private BashFlowResult AnalyzeList(CommandListSyntax list, BashAbstractState input)
    {
        var flow = AnalyzeNode(list.Items[0].Command, input);
        for (var index = 1; index < list.Items.Count; index++)
        {
            var item = list.Items[index];
            switch (item.Operator)
            {
                case CompoundOperator.AndIf:
                    {
                        if (flow.OnSuccess is null)
                        {
                            break;
                        }

                        var right = AnalyzeNode(item.Command, flow.OnSuccess.Value);
                        flow = new BashFlowResult(
                            right.OnSuccess,
                            BashAbstractState.JoinNullable(flow.OnFailure, right.OnFailure));
                        break;
                    }
                case CompoundOperator.OrIf:
                    {
                        if (flow.OnFailure is null)
                        {
                            break;
                        }

                        var right = AnalyzeNode(item.Command, flow.OnFailure.Value);
                        flow = new BashFlowResult(
                            BashAbstractState.JoinNullable(flow.OnSuccess, right.OnSuccess),
                            right.OnFailure);
                        break;
                    }
                case CompoundOperator.Sequence:
                    if (flow.JoinedState is BashAbstractState sequenceInput)
                    {
                        flow = AnalyzeNode(item.Command, sequenceInput);
                    }

                    break;
                default:
                    return flow.JoinedState is BashAbstractState joined
                        ? BashFlowResult.Both(joined.WithUnknownCwd())
                        : flow;
            }
        }

        return flow;
    }

    private BashFlowResult AnalyzePipeline(
        PipelineSyntax pipeline,
        BashAbstractState input)
    {
        BashFlowResult? last = null;
        foreach (var stage in pipeline.Stages)
        {
            last = AnalyzeNode(stage, input);
        }

        // lastpipe controls whether the last stage can leak state; pipefail
        // independently controls which exit partition receives it. With no
        // proved shell options, both partitions conservatively receive the
        // join of isolated and current-scope outcomes.
        if (last?.JoinedState is not BashAbstractState lastState)
        {
            return new BashFlowResult(null, null);
        }

        var joined = BashAbstractState.Join(input, lastState);
        return BashFlowResult.Both(joined);
    }

    private BashFlowResult AnalyzeGroup(GroupSyntax group, BashAbstractState input)
    {
        if (group.GroupKind == ShellGroupKind.CurrentScope)
        {
            return AnalyzeBlock(group.Body, input);
        }

        return AnalyzeIsolatedBody(
            group.Body,
            input,
            resetCompatibilityAttribution: IsDecodedWrapper(group.Body),
            clearBindings: IsDecodedWrapper(group.Body));
    }

    private BashFlowResult AnalyzeForEach(
        ForEachSyntax forEach,
        BashAbstractState input)
    {
        var iterator = AnalyzeBlock(forEach.IteratorCommands, input);
        var loopInput = iterator.JoinedState;
        var sourcePlan = _forInPlanFactory(forEach);
        if (sourcePlan is null)
        {
            _isComplete = false;
            return new BashFlowResult(null, null);
        }

        if (loopInput is null)
        {
            return new BashFlowResult(null, null);
        }

        var plan = loopInput.Value.Bindings.AnalyzeIterationPlan(
            sourcePlan.Words,
            OptionsFor(loopInput.Value),
            workingDirectoryUnknown: loopInput.Value.WorkingDirectory is null);
        if (plan.Cardinality == BashIterationCardinality.Never)
        {
            RecordUnvisitedBindingArguments(forEach.Body, sourcePlan.BindingName);
            return BashFlowResult.Success(loopInput.Value);
        }

        if (plan.RequiresFixedPoint)
        {
            return AnalyzeForEachFixedPoint(
                forEach,
                loopInput.Value,
                sourcePlan,
                plan);
        }

        BashFlowResult? last = null;
        var iterationInput = loopInput.Value;
        foreach (var candidate in plan.OrderedCandidates)
        {
            if (!TryConsumeLoopAnalysisTransition())
            {
                return new BashFlowResult(null, null);
            }

            iterationInput = iterationInput.WithBinding(
                sourcePlan.BindingName,
                candidate);
            last = AnalyzeBlock(forEach.Body, iterationInput);
            if (last.Value.JoinedState is not BashAbstractState next)
            {
                return new BashFlowResult(null, null);
            }

            iterationInput = next;
        }

        return last ?? BashFlowResult.Success(loopInput.Value);
    }

    private BashFlowResult AnalyzeForEachFixedPoint(
        ForEachSyntax forEach,
        BashAbstractState loopInput,
        BashForInAnalysisPlan sourcePlan,
        BashIterationPlan plan)
    {
        BashAbstractState? success = plan.Cardinality == BashIterationCardinality.ZeroOrMore
            ? loopInput
            : null;
        BashAbstractState? failure = null;
        var head = loopInput;
        var wideningBase = loopInput;
        var nextHead = loopInput;
        for (var iteration = 0;
             iteration <= ShellAnalysisLimits.MaxValueCandidates;
             iteration++)
        {
            if (!TryConsumeLoopAnalysisTransition())
            {
                return new BashFlowResult(null, null);
            }

            var body = AnalyzeBlock(
                forEach.Body,
                head.WithBinding(sourcePlan.BindingName, plan.Summary));
            success = BashAbstractState.JoinNullable(success, body.OnSuccess);
            failure = BashAbstractState.JoinNullable(failure, body.OnFailure);
            if (body.JoinedState is not BashAbstractState bodyExit)
            {
                return new BashFlowResult(success, failure);
            }

            wideningBase = head;
            nextHead = BashAbstractState.Join(head, bodyExit);
            if (head.StateEquals(nextHead))
            {
                return new BashFlowResult(success, failure);
            }

            head = nextHead;
        }

        // Widen disagreement after the finite-domain budget, then run the body
        // once more so occurrence facts also reflect the widened entry state.
        if (!TryConsumeLoopAnalysisTransition())
        {
            return new BashFlowResult(null, null);
        }

        var widened = BashAbstractState.Widen(wideningBase, nextHead);
        var widenedBody = AnalyzeBlock(
            forEach.Body,
            widened.WithBinding(sourcePlan.BindingName, plan.Summary));
        return new BashFlowResult(
            BashAbstractState.JoinNullable(success, widenedBody.OnSuccess),
            BashAbstractState.JoinNullable(failure, widenedBody.OnFailure));
    }

    private bool TryConsumeLoopAnalysisTransition()
    {
        if (_remainingLoopAnalysisTransitions == 0)
        {
            _isComplete = false;
            return false;
        }

        _remainingLoopAnalysisTransitions--;
        return true;
    }

    private BashFlowResult AnalyzeIsolatedBody(
        ShellBlockSyntax body,
        BashAbstractState input,
        bool resetCompatibilityAttribution,
        bool clearBindings)
    {
        var childInput = resetCompatibilityAttribution
            ? input.WithoutCompatibilityAttribution()
            : input;
        if (clearBindings)
        {
            childInput = childInput.WithoutBindings();
        }

        var inner = AnalyzeBlock(body, childInput);
        return new BashFlowResult(
            inner.OnSuccess is null ? null : input,
            inner.OnFailure is null ? null : input);
    }

    private BashFlowResult AnalyzeConditional(
        ConditionalSyntax conditional,
        BashAbstractState input)
    {
        BashAbstractState? success = null;
        BashAbstractState? failure = input;
        foreach (var branch in conditional.Branches)
        {
            if (failure is null)
            {
                break;
            }

            var branchFlow = AnalyzeBranch(branch, failure.Value);
            success = BashAbstractState.JoinNullable(success, branchFlow.OnSuccess);
            failure = branchFlow.OnFailure;
        }

        if (conditional.Else is not null && failure is not null)
        {
            var elseFlow = AnalyzeBlock(conditional.Else, failure.Value);
            success = BashAbstractState.JoinNullable(success, elseFlow.OnSuccess);
            failure = elseFlow.OnFailure;
        }

        return new BashFlowResult(success, failure);
    }

    private BashFlowResult AnalyzeBranch(
        ConditionalBranchSyntax branch,
        BashAbstractState input)
    {
        var condition = AnalyzeBlock(branch.Condition, input);
        if (condition.OnSuccess is null)
        {
            return new BashFlowResult(null, condition.OnFailure);
        }

        var body = AnalyzeBlock(branch.Body, condition.OnSuccess.Value);
        return new BashFlowResult(
            body.OnSuccess,
            BashAbstractState.JoinNullable(condition.OnFailure, body.OnFailure));
    }

    private void RecordInput(Clause clause, BashAbstractState input)
    {
        if (_inputs.TryGetValue(clause, out var prior))
        {
            _inputs[clause] = BashAbstractState.Join(prior, input);
        }
        else
        {
            _inputs.Add(clause, input);
        }
    }

    private void RecordEffectiveArguments(
        SimpleCommandSyntax simple,
        BashAbstractState input)
    {
        var sourceFacts = _factsFactory(simple);
        if (sourceFacts.ValueProvenance.Count == 0)
        {
            return;
        }

        Dictionary<int, ShellValueDomain>? accumulated = null;
        var evaluator = input.Bindings;
        foreach (var provenance in sourceFacts.ValueProvenance)
        {
            if (!evaluator.TryAnalyzeEffectiveValue(provenance.Value, out var domain))
            {
                continue;
            }

            accumulated ??= GetEffectiveArguments(simple.Clause);
            if (accumulated.TryGetValue(provenance.ClauseElementIndex, out var prior))
            {
                accumulated[provenance.ClauseElementIndex] =
                    BashLoopBindingContext.JoinDomains(prior, domain);
            }
            else
            {
                accumulated.Add(provenance.ClauseElementIndex, domain);
            }
        }
    }

    private void RecordUnvisitedBindingArguments(
        ShellBlockSyntax block,
        string bindingName)
    {
        foreach (var statement in block.Statements)
        {
            RecordUnvisitedBindingArguments(statement, bindingName);
        }
    }

    private void RecordUnvisitedBindingArguments(
        ShellSyntaxNode node,
        string bindingName)
    {
        switch (node)
        {
            case SimpleCommandSyntax simple:
                var sourceFacts = _factsFactory(simple);
                foreach (var provenance in sourceFacts.ValueProvenance)
                {
                    if (BashLoopBindingContext.ReferencesBinding(
                            provenance.Value,
                            bindingName))
                    {
                        GetEffectiveArguments(simple.Clause)[provenance.ClauseElementIndex] =
                            ShellValueDomain.Unknown;
                    }
                }

                foreach (var substitution in simple.Substitutions)
                {
                    RecordUnvisitedBindingArguments(substitution.Body, bindingName);
                }

                break;
            case ShellBlockSyntax nestedBlock:
                RecordUnvisitedBindingArguments(nestedBlock, bindingName);
                break;
            case PipelineSyntax pipeline:
                foreach (var stage in pipeline.Stages)
                {
                    RecordUnvisitedBindingArguments(stage, bindingName);
                }

                break;
            case CommandListSyntax list:
                foreach (var item in list.Items)
                {
                    RecordUnvisitedBindingArguments(item.Command, bindingName);
                }

                break;
            case GroupSyntax group:
                RecordUnvisitedBindingArguments(group.Body, bindingName);
                break;
            case ForEachSyntax forEach:
                RecordUnvisitedBindingArguments(forEach.IteratorCommands, bindingName);
                RecordUnvisitedBindingArguments(forEach.Body, bindingName);
                break;
            case ConditionLoopSyntax loop:
                RecordUnvisitedBindingArguments(loop.Condition, bindingName);
                RecordUnvisitedBindingArguments(loop.Body, bindingName);
                break;
            case ConditionalSyntax conditional:
                foreach (var branch in conditional.Branches)
                {
                    RecordUnvisitedBindingArguments(branch, bindingName);
                }

                if (conditional.Else is not null)
                {
                    RecordUnvisitedBindingArguments(conditional.Else, bindingName);
                }

                break;
            case ConditionalBranchSyntax branch:
                RecordUnvisitedBindingArguments(branch.Condition, bindingName);
                RecordUnvisitedBindingArguments(branch.Body, bindingName);
                break;
            case CommandSubstitutionSyntax substitution:
                RecordUnvisitedBindingArguments(substitution.Body, bindingName);
                break;
        }
    }

    private Dictionary<int, ShellValueDomain> GetEffectiveArguments(Clause clause)
    {
        if (_effectiveArguments.TryGetValue(clause, out var accumulated))
        {
            return accumulated;
        }

        accumulated = new Dictionary<int, ShellValueDomain>();
        _effectiveArguments.Add(clause, accumulated);
        return accumulated;
    }

    private void RecordCwdResolutionSanitization(
        Clause clause,
        IReadOnlyList<int> elementIndices)
    {
        if (!_cwdResolutionSanitization.TryGetValue(clause, out var accumulated))
        {
            accumulated = new HashSet<int>();
            _cwdResolutionSanitization.Add(clause, accumulated);
        }

        foreach (var elementIndex in elementIndices)
        {
            accumulated.Add(elementIndex);
        }
    }

    private BashFlowResult? AnalyzeEffectiveCwdTransfer(
        SimpleCommandSyntax simple,
        BashAbstractState input)
    {
        var dispatchKind = BashCwdInvocationGrammar.Classify(
            simple.Clause,
            out var argumentElementIndices);
        if (dispatchKind == BashDispatchKind.Query)
        {
            return BashFlowResult.Both(input);
        }

        if (dispatchKind != BashDispatchKind.CwdTransfer)
        {
            return null;
        }

        if (!TryGetCwdArgumentValues(
                simple,
                argumentElementIndices,
                out var argumentValues))
        {
            _isComplete = false;
            return new BashFlowResult(null, null);
        }

        foreach (var argumentValue in argumentValues)
        {
            if (input.Bindings.ReferencesTrackedBinding(argumentValue) &&
                !HasProvedSingleWordCardinality(argumentValue))
            {
                _isComplete = false;
                return new BashFlowResult(null, null);
            }
        }

        var referencedBindings = input.Bindings.FindReferencedBindingNames(argumentValues);
        var alternativeResult = input.Bindings.EnumerateExactAlternatives(
            ShellAnalysisLimits.MaxValueCandidates,
            referencedBindings,
            out var bindingAlternatives);
        if (alternativeResult == BashBindingAlternativeResult.ExceededLimit)
        {
            _isComplete = false;
            return new BashFlowResult(null, null);
        }

        if (alternativeResult == BashBindingAlternativeResult.Unknown)
        {
            RecordCwdResolutionSanitization(simple.Clause, argumentElementIndices);
            return new BashFlowResult(input.WithUnknownCwd(), input);
        }

        BashAbstractState? success = null;
        BashAbstractState? failure = null;
        foreach (var bindings in bindingAlternatives)
        {
            BuildEffectiveCwdArguments(
                bindings,
                argumentElementIndices,
                argumentValues,
                input,
                out var arguments);

            BashFlowResult visit;
            if (arguments is null)
            {
                RecordCwdResolutionSanitization(
                    simple.Clause,
                    argumentElementIndices);
                visit = new BashFlowResult(input.WithUnknownCwd(), input);
            }
            else
            {
                visit = AnalyzeExactCwdArguments(simple.Clause, input, arguments);
            }

            success = BashAbstractState.JoinNullable(success, visit.OnSuccess);
            failure = BashAbstractState.JoinNullable(failure, visit.OnFailure);
        }

        return new BashFlowResult(success, failure);
    }

    private bool TryGetCwdArgumentValues(
        SimpleCommandSyntax simple,
        IReadOnlyList<int> argumentElementIndices,
        out IReadOnlyList<ShellValue> values)
    {
        var ordered = new List<ShellValue>(argumentElementIndices.Count);
        var sourceFacts = _factsFactory(simple);
        foreach (var elementIndex in argumentElementIndices)
        {
            ShellValueElementProvenance? provenance = null;
            foreach (var candidate in sourceFacts.ValueProvenance)
            {
                if (candidate.ClauseElementIndex == elementIndex)
                {
                    provenance = candidate;
                    break;
                }
            }

            if (provenance is null)
            {
                values = Array.Empty<ShellValue>();
                return false;
            }

            ordered.Add(provenance.Value.Value);
        }

        values = ordered;
        return true;
    }

    private void BuildEffectiveCwdArguments(
        BashLoopBindingContext bindings,
        IReadOnlyList<int> argumentElementIndices,
        IReadOnlyList<ShellValue> argumentValues,
        BashAbstractState input,
        out IReadOnlyList<EffectiveCwdArgument>? arguments)
    {
        var exact = new List<EffectiveCwdArgument>(argumentElementIndices.Count);
        for (var index = 0; index < argumentElementIndices.Count; index++)
        {
            var dependsOnBinding = bindings.TryAnalyzeEffectiveValue(
                argumentValues[index],
                out var domain);
            if (!dependsOnBinding)
            {
                domain = bindings.AnalyzeWordForTransfer(argumentValues[index]);
            }

            if (domain.Kind != ShellValueDomainKind.Exact &&
                TryResolveKnownHomeWord(argumentValues[index], input, out var homeWord))
            {
                domain = new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { homeWord },
                };
            }

            if (domain.Kind != ShellValueDomainKind.Exact || domain.Values.Count != 1)
            {
                arguments = null;
                return;
            }

            exact.Add(new EffectiveCwdArgument(
                argumentElementIndices[index],
                domain.Values[0]));
        }

        arguments = exact;
    }

    private bool TryResolveKnownHomeWord(
        ShellValue value,
        BashAbstractState input,
        out string resolvedValue)
    {
        var resolved = BashResolver.Resolve(
            value,
            treatAsPath: true,
            OptionsFor(input),
            workingDirectoryUnknown: input.WorkingDirectory is null,
            consumer: ShellResolutionConsumer.BashArgument);
        if (resolved.Kind == ArgKind.Tilde && resolved.Resolved is not null)
        {
            resolvedValue = resolved.Resolved;
            return true;
        }

        resolvedValue = "";
        return false;
    }

    private static bool HasProvedSingleWordCardinality(ShellValue value)
    {
        foreach (var fragment in value.Fragments)
        {
            if (fragment.Cardinality != ShellValueCardinality.ExactlyOne ||
                (fragment.AllowedTransforms &
                 (ShellLexicalTransform.FieldSplit | ShellLexicalTransform.Glob)) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private BashFlowResult AnalyzeExactCwdArguments(
        Clause clause,
        BashAbstractState input,
        IReadOnlyList<EffectiveCwdArgument> arguments)
    {
        var optionsEnded = false;
        var physical = false;
        EffectiveCwdArgument? operand = null;
        foreach (var argument in arguments)
        {
            if (operand is not null)
            {
                return new BashFlowResult(null, input);
            }

            if (!optionsEnded && argument.Value == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded &&
                argument.Value.Length > 1 &&
                argument.Value[0] == '-' &&
                argument.Value != "-")
            {
                if (!IsSupportedCdOption(
                        argument.Value,
                        out var requiresPhysicalResolution))
                {
                    return new BashFlowResult(null, input);
                }

                physical |= requiresPhysicalResolution;
                continue;
            }

            operand = argument;
            if (physical)
            {
                RecordCwdResolutionSanitization(
                    clause,
                    new[] { argument.ElementIndex });
            }
        }

        if (operand is null)
        {
            var home = physical || string.IsNullOrEmpty(_options.HomeDirectory)
                ? input.WithUnknownCwd()
                : input.WithCwd(_options.HomeDirectory, true);
            return new BashFlowResult(home, input);
        }

        if (physical ||
            operand.Value.Value == "-" ||
            IsCdPathSearchCandidate(operand.Value.Value))
        {
            return new BashFlowResult(input.WithUnknownCwd(), input);
        }

        var resolved = BashResolver.Resolve(
            operand.Value.Value,
            treatAsPath: true,
            OptionsFor(input),
            workingDirectoryUnknown: input.WorkingDirectory is null,
            isLiteralBytes: true);
        var success = resolved.Resolved is null
            ? input.WithUnknownCwd()
            : input.WithCwd(resolved.Resolved, true);
        return new BashFlowResult(success, input);
    }

    private static bool IsSupportedCdOption(
        string argument,
        out bool requiresPhysicalResolution)
    {
        requiresPhysicalResolution = false;
        for (var index = 1; index < argument.Length; index++)
        {
            switch (argument[index])
            {
                case 'L':
                case 'e':
                    break;
                case 'P':
                case '@':
                    requiresPhysicalResolution = true;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool IsPotentialPersistentBindingMutation(Clause clause)
    {
        var dispatchKind = BashCwdInvocationGrammar.Classify(clause, out _);
        if (dispatchKind is BashDispatchKind.CwdTransfer or BashDispatchKind.Query)
        {
            return false;
        }

        if (clause.Verb.Tokens.Count == 0)
        {
            return true;
        }

        var verb = clause.Verb.Tokens[0];
        if (verb is "pushd" or "popd")
        {
            return false;
        }

        if (verb is "unset" or "read" or "readarray" or "mapfile" or
            "declare" or "typeset" or "local" or "export" or "readonly" or
            "let" or "eval" or "." or "source" or "getopts" or "set" or
            "trap" or "command" or "builtin")
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

    private bool TryGetLegacyCwdTransfer(
        Clause clause,
        BashAbstractState input,
        out BashAbstractState success)
    {
        success = input;
        var verb = FirstVerb(clause);
        if (verb is null)
        {
            return false;
        }

        if (verb is "command" or "builtin")
        {
            var dispatched = DispatchedVerb(clause);
            if (dispatched is "cd" or "chdir" or "pushd" or "popd" or
                "eval" or "." or "source" or "trap")
            {
                success = input.WithUnknownCwd();
                return true;
            }

            return false;
        }

        if (verb is "eval" or "." or "source" or "trap" or "pushd" or "popd")
        {
            success = input.WithUnknownCwd();
            return true;
        }

        if (verb is not ("cd" or "chdir"))
        {
            return false;
        }

        if (!TryGetCdOperand(
                clause,
                out _,
                out var target,
                out var resolutionMustBeUnknown))
        {
            if (resolutionMustBeUnknown)
            {
                success = input.WithUnknownCwd();
                return true;
            }

            success = string.IsNullOrEmpty(_options.HomeDirectory)
                ? input.WithUnknownCwd()
                : input.WithCwd(_options.HomeDirectory, true);
            return true;
        }

        if (resolutionMustBeUnknown)
        {
            success = input.WithUnknownCwd();
            return true;
        }

        if (target.Kind == ArgKind.Tilde && target.Resolved is not null)
        {
            success = input.WithCwd(target.Resolved, true);
            return true;
        }

        if (target.Kind != ArgKind.Literal)
        {
            success = input.WithUnknownCwd();
            return true;
        }

        var value = ArgumentValue(clause, target);
        var options = OptionsFor(input);
        var resolved = BashResolver.Resolve(
            value,
            treatAsPath: true,
            options,
            workingDirectoryUnknown: input.WorkingDirectory is null,
            isLiteralBytes: true);
        success = resolved.Resolved is null
            ? input.WithUnknownCwd()
            : input.WithCwd(resolved.Resolved, true);
        return true;
    }

    private static string? DispatchedVerb(Clause clause)
    {
        var words = new List<string>(clause.Verb.Tokens.Count + clause.Args.Count);
        foreach (var token in clause.Verb.Tokens)
        {
            words.Add(token);
        }

        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution && !argument.IsFlag)
            {
                words.Add(ArgumentValue(clause, argument));
            }
        }

        var index = 0;
        while (index < words.Count && words[index] is "command" or "builtin")
        {
            index++;
        }

        return index < words.Count ? words[index] : null;
    }

    private static bool TryGetCdOperand(
        Clause clause,
        out int argumentIndex,
        out Arg target,
        out bool resolutionMustBeUnknown)
    {
        argumentIndex = -1;
        target = null!;
        resolutionMustBeUnknown = false;
        var optionsEnded = false;
        var physical = false;
        var current = 0;
        foreach (var argument in clause.Args)
        {
            if (argument.IsCwdAttribution)
            {
                continue;
            }

            var raw = argument.Raw;
            if (!optionsEnded && raw == "--")
            {
                optionsEnded = true;
                current++;
                continue;
            }

            if (!optionsEnded && raw.Length > 1 && raw[0] == '-' && raw != "-")
            {
                if (raw.IndexOf('P') >= 0 || raw.IndexOf('@') >= 0)
                {
                    physical = true;
                }

                current++;
                continue;
            }

            argumentIndex = current;
            target = argument;
            var value = ArgumentValue(clause, argument);
            resolutionMustBeUnknown = physical ||
                value == "-" ||
                target.Kind == ArgKind.Literal && IsCdPathSearchCandidate(value);
            return true;
        }

        resolutionMustBeUnknown = physical;
        return false;
    }

    private static bool IsCdPathSearchCandidate(string value)
    {
        if (value is "." or ".." ||
            value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith("../", StringComparison.Ordinal) ||
            BashResolver.IsRootedPath(value))
        {
            return false;
        }

        const string fileSystemPrefix = "filesystem::";
        return !value.StartsWith(fileSystemPrefix, StringComparison.Ordinal) ||
            !BashResolver.IsRootedPath(value.Substring(fileSystemPrefix.Length));
    }

    private ShellBlockSyntax RewriteBlock(
        ShellBlockSyntax block,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var statements = new ShellSyntaxNode[block.Statements.Count];
        for (var index = 0; index < statements.Length; index++)
        {
            statements[index] = RewriteNode(block.Statements[index], facts);
        }

        return block with { Statements = statements };
    }

    private ShellSyntaxNode RewriteNode(
        ShellSyntaxNode node,
        Dictionary<Clause, CommandOccurrenceFacts> facts) =>
        node switch
        {
            ShellBlockSyntax block => RewriteBlock(block, facts),
            SimpleCommandSyntax simple => RewriteSimple(simple, facts),
            PipelineSyntax pipeline => pipeline with
            {
                Stages = RewriteNodes(pipeline.Stages, facts),
            },
            CommandListSyntax list => list with
            {
                Items = RewriteItems(list.Items, facts),
            },
            GroupSyntax group => group with { Body = RewriteBlock(group.Body, facts) },
            ForEachSyntax forEach => RewriteForEach(forEach, facts),
            ConditionLoopSyntax loop => loop with
            {
                Condition = RewriteBlock(loop.Condition, facts),
                Body = RewriteBlock(loop.Body, facts),
            },
            ConditionalSyntax conditional => conditional with
            {
                Branches = RewriteBranches(conditional.Branches, facts),
                Else = conditional.Else is null ? null : RewriteBlock(conditional.Else, facts),
            },
            ConditionalBranchSyntax branch => branch with
            {
                Condition = RewriteBlock(branch.Condition, facts),
                Body = RewriteBlock(branch.Body, facts),
            },
            CommandSubstitutionSyntax substitution => substitution with
            {
                Body = RewriteBlock(substitution.Body, facts),
            },
            _ => node,
        };

    private ForEachSyntax RewriteForEach(
        ForEachSyntax forEach,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = forEach with
        {
            IteratorCommands = RewriteBlock(forEach.IteratorCommands, facts),
            Body = RewriteBlock(forEach.Body, facts),
        };
        var plan = _forInPlanFactory(forEach);
        if (plan is null)
        {
            _isComplete = false;
        }
        else
        {
            _rewrittenForInPlans.Add(new BashForInAnalysisPlanReference(
                rewritten,
                plan));
        }

        return rewritten;
    }

    private SimpleCommandSyntax RewriteSimple(
        SimpleCommandSyntax simple,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var substitutions = new CommandSubstitutionSyntax[simple.Substitutions.Count];
        for (var index = 0; index < substitutions.Length; index++)
        {
            substitutions[index] = (CommandSubstitutionSyntax)RewriteNode(
                simple.Substitutions[index],
                facts);
        }

        var sourceFacts = _factsFactory(simple);
        if (!_inputs.TryGetValue(simple.Clause, out var input))
        {
            input = new BashAbstractState(
                workingDirectory: null,
                hasCompatibilityAttribution: true,
                bindings: new BashLoopBindingContext());
        }

        var clause = RewriteClause(simple.Clause, input, sourceFacts.CwdPathDependencies);
        var redirects = RewriteRedirectFacts(
            sourceFacts.Redirects,
            sourceFacts.RedirectTargetProvenance,
            input.Bindings,
            clause);
        facts.Add(clause, new CommandOccurrenceFacts
        {
            EffectiveArguments = CreateEffectiveArguments(simple.Clause),
            WorkingDirectory = input.ToDomain(),
            Redirects = redirects,
            RedirectTargetProvenance = sourceFacts.RedirectTargetProvenance,
            CwdPathDependencies = sourceFacts.CwdPathDependencies,
            ValueProvenance = sourceFacts.ValueProvenance,
            IsComplete = sourceFacts.IsComplete && AreRedirectsComplete(redirects),
        });
        return simple with
        {
            Clause = clause,
            Substitutions = substitutions,
        };
    }

    private IReadOnlyList<EffectiveArgument> CreateEffectiveArguments(Clause clause)
    {
        if (!_effectiveArguments.TryGetValue(clause, out var accumulated) ||
            accumulated.Count == 0)
        {
            return Array.Empty<EffectiveArgument>();
        }

        var indices = new List<int>(accumulated.Keys);
        indices.Sort();
        var effective = new EffectiveArgument[indices.Count];
        for (var index = 0; index < effective.Length; index++)
        {
            effective[index] = new EffectiveArgument
            {
                ClauseElementIndex = indices[index],
                Value = accumulated[indices[index]],
            };
        }

        return effective;
    }

    private Clause RewriteClause(
        Clause clause,
        BashAbstractState input,
        IReadOnlyList<CwdPathDependency> dependencies)
    {
        var parseWorkingDirectory = OriginalParseWorkingDirectory(clause, input);
        var directVerb = FirstVerb(clause);
        var clearCdTargetIndex = directVerb is "cd" or "chdir" && TryGetCdOperand(
            clause,
            out var cdTargetIndex,
            out _,
            out var cdResolutionMustBeUnknown) &&
            cdResolutionMustBeUnknown
            ? cdTargetIndex
            : -1;
        var authoredArgs = new List<Arg>(clause.Args.Count);
        var authoredArgumentIndex = 0;
        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution)
            {
                var dependency = FindArgumentDependency(
                    dependencies,
                    authoredArgumentIndex);
                var clearResolution = authoredArgumentIndex == clearCdTargetIndex ||
                    IsArgumentResolutionSanitized(clause, authoredArgumentIndex);
                var rebased = clearResolution
                    ? null
                    : RebaseResolution(
                        argument.Resolved,
                        argument.IsPath,
                        argument.Kind,
                        dependency?.LogicalValue ?? ArgumentValue(clause, argument),
                        dependency?.ParseWorkingDirectory ?? parseWorkingDirectory,
                        input,
                        dependency?.DependsOnWorkingDirectory);
                var promote = !clearResolution &&
                    dependency?.DependsOnWorkingDirectory == true &&
                    rebased is not null;
                authoredArgs.Add(argument with
                {
                    Kind = promote ? ArgKind.Literal : argument.Kind,
                    IsPath = promote || argument.IsPath,
                    Resolved = rebased,
                });
                authoredArgumentIndex++;
            }
        }

        var elements = new ClauseElement[clause.Elements.Count];
        var argumentElementIndex = 0;
        for (var index = 0; index < elements.Length; index++)
        {
            var element = clause.Elements[index];
            var dependency = FindDependency(dependencies, index);
            var clearResolution = element.Role == ClauseElementRole.Argument &&
                (argumentElementIndex++ == clearCdTargetIndex ||
                 IsElementResolutionSanitized(clause, index));
            var rebased = clearResolution
                ? null
                : RebaseResolution(
                    element.Resolved,
                    element.IsPath,
                    element.Kind,
                    dependency?.LogicalValue ?? element.Value,
                    dependency?.ParseWorkingDirectory ?? parseWorkingDirectory,
                    input,
                    dependency?.DependsOnWorkingDirectory);
            var promote = !clearResolution &&
                dependency?.DependsOnWorkingDirectory == true &&
                rebased is not null;
            elements[index] = element with
            {
                Kind = promote ? ArgKind.Literal : element.Kind,
                IsPath = promote || element.IsPath,
                Resolved = rebased,
            };
        }

        var redirects = RewriteCompatibilityRedirects(
            clause,
            parseWorkingDirectory,
            input,
            dependencies);

        if (input.HasCompatibilityAttribution)
        {
            authoredArgs.Add(CreateAttribution(input));
        }

        return clause with
        {
            Args = authoredArgs.ToArray(),
            Elements = elements,
            Redirects = redirects,
        };
    }

    private bool IsArgumentResolutionSanitized(Clause clause, int argumentIndex)
    {
        var currentArgument = 0;
        for (var elementIndex = 0;
             elementIndex < clause.Elements.Count;
             elementIndex++)
        {
            if (clause.Elements[elementIndex].Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (currentArgument++ == argumentIndex)
            {
                return IsElementResolutionSanitized(clause, elementIndex);
            }
        }

        return false;
    }

    private bool IsElementResolutionSanitized(Clause clause, int elementIndex) =>
        _cwdResolutionSanitization.TryGetValue(clause, out var sanitized) &&
        sanitized.Contains(elementIndex);

    private IReadOnlyList<Redirect> RewriteCompatibilityRedirects(
        Clause clause,
        string? parseWorkingDirectory,
        BashAbstractState input,
        IReadOnlyList<CwdPathDependency> dependencies)
    {
        if (clause.Redirects.Count == 0)
        {
            return clause.Redirects;
        }

        var redirectElements = new List<ClauseElement>(clause.Redirects.Count);
        foreach (var element in clause.Elements)
        {
            if (element.Role == ClauseElementRole.Redirect)
            {
                redirectElements.Add(element);
            }
        }

        var redirects = new Redirect[clause.Redirects.Count];
        for (var index = 0; index < redirects.Length; index++)
        {
            var redirect = clause.Redirects[index];
            if (index >= redirectElements.Count)
            {
                redirects[index] = redirect;
                continue;
            }

            var element = redirectElements[index];
            var elementIndex = IndexOfElement(clause.Elements, element);
            var dependency = FindDependency(dependencies, elementIndex);
            if (redirect.IsDynamicSkip && dependency is null)
            {
                redirects[index] = redirect;
                continue;
            }

            var target = RebaseResolution(
                redirect.IsDynamicSkip ? null : redirect.Target,
                element.IsPath,
                element.Kind,
                dependency?.LogicalValue ?? element.Value,
                dependency?.ParseWorkingDirectory ?? parseWorkingDirectory,
                input,
                dependency?.DependsOnWorkingDirectory);
            redirects[index] = target is null
                ? redirect with
                {
                    Target = dependency?.AuthoredValue ?? element.Value,
                    IsDynamicSkip = true,
                }
                : redirect with
                {
                    Target = target,
                    IsDynamicSkip = false,
                };
        }

        return redirects;
    }

    private static IReadOnlyList<RedirectAnalysis> RewriteRedirectFacts(
        IReadOnlyList<RedirectAnalysis> source,
        IReadOnlyList<RedirectTargetProvenance> provenance,
        BashLoopBindingContext bindings,
        Clause clause)
    {
        if (source.Count == 0)
        {
            return source;
        }

        var rewritten = new RedirectAnalysis[source.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            var fact = source[index];
            if (fact.Operation == RedirectOperation.HereString)
            {
                rewritten[index] = fact with
                {
                    Target = RewriteHereStringTarget(
                        fact,
                        provenance,
                        bindings),
                };
                continue;
            }

            if (!fact.IsPathRelevant ||
                fact.RedirectIndex < 0 ||
                fact.RedirectIndex >= clause.Redirects.Count)
            {
                rewritten[index] = fact;
                continue;
            }

            var redirect = clause.Redirects[fact.RedirectIndex];
            rewritten[index] = fact with
            {
                Target = redirect.IsDynamicSkip
                    ? ShellValueDomain.Unknown
                    : new ShellValueDomain
                    {
                        Kind = ShellValueDomainKind.Exact,
                        Values = new[] { redirect.Target },
                    },
                IsComplete = fact.IsComplete && !redirect.IsDynamicSkip,
            };
        }

        return rewritten;
    }

    private static ShellValueDomain RewriteHereStringTarget(
        RedirectAnalysis fact,
        IReadOnlyList<RedirectTargetProvenance> provenance,
        BashLoopBindingContext bindings)
    {
        foreach (var candidate in provenance)
        {
            if (candidate.RedirectIndex != fact.RedirectIndex)
            {
                continue;
            }

            if (!bindings.TryAnalyzeEffectiveValue(candidate.Value, out var domain))
            {
                return fact.Target;
            }

            if (domain.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
            {
                return ShellValueDomain.Unknown;
            }

            var values = new string[domain.Values.Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = domain.Values[index] + "\n";
            }

            return new ShellValueDomain
            {
                Kind = domain.Kind,
                Values = values,
            };
        }

        return fact.Target;
    }

    private static bool AreRedirectsComplete(IReadOnlyList<RedirectAnalysis> redirects)
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

    private string? RebaseResolution(
        string? resolved,
        bool isPath,
        ArgKind kind,
        string authored,
        string? parseWorkingDirectory,
        BashAbstractState input,
        bool? dependsOnWorkingDirectory)
    {
        if (dependsOnWorkingDirectory.HasValue)
        {
            if (!dependsOnWorkingDirectory.Value)
            {
                return resolved;
            }

            if (resolved is not null &&
                parseWorkingDirectory is not null &&
                string.Equals(
                    input.WorkingDirectory,
                    parseWorkingDirectory,
                    StringComparison.Ordinal))
            {
                return resolved;
            }

            if (input.WorkingDirectory is null)
            {
                return null;
            }

            return BashResolver.Resolve(
                authored,
                treatAsPath: true,
                OptionsFor(input),
                workingDirectoryUnknown: false,
                isLiteralBytes: true).Resolved;
        }

        if (!isPath || kind != ArgKind.Literal || resolved is null ||
            parseWorkingDirectory is null)
        {
            return resolved;
        }

        if (parseWorkingDirectory is not null &&
            string.Equals(
                input.WorkingDirectory,
                parseWorkingDirectory,
                StringComparison.Ordinal))
        {
            return resolved;
        }

        var suffix = string.Empty;
        var hasRelativeSuffix = parseWorkingDirectory is not null &&
            TryGetRelativeSuffix(resolved, parseWorkingDirectory, out suffix);
        if (dependsOnWorkingDirectory is null &&
            !hasRelativeSuffix &&
            IsResolverRootedLiteral(authored))
        {
            return resolved;
        }

        if (input.WorkingDirectory is null)
        {
            return null;
        }

        return BashResolver.Resolve(
            hasRelativeSuffix ? suffix : authored,
            treatAsPath: true,
            OptionsFor(input),
            workingDirectoryUnknown: false,
            isLiteralBytes: true).Resolved;
    }

    private static bool IsResolverRootedLiteral(string authored)
    {
        if (BashResolver.IsRootedPath(authored))
        {
            return true;
        }

        const string fileSystemPrefix = "filesystem::";
        return authored.StartsWith(fileSystemPrefix, StringComparison.Ordinal) &&
            BashResolver.IsRootedPath(authored.Substring(fileSystemPrefix.Length));
    }

    private string? OriginalParseWorkingDirectory(
        Clause clause,
        BashAbstractState input)
    {
        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution)
            {
                continue;
            }

            return argument.Kind == ArgKind.Literal ? argument.Resolved : null;
        }

        return clause.IsCommandStringWrapped
            ? _options.WorkingDirectory ?? Environment.CurrentDirectory
            : input.WorkingDirectory ??
                _options.WorkingDirectory ??
                Environment.CurrentDirectory;
    }

    private static CwdPathDependency? FindDependency(
        IReadOnlyList<CwdPathDependency> dependencies,
        int elementIndex)
    {
        if (elementIndex < 0)
        {
            return null;
        }

        foreach (var dependency in dependencies)
        {
            if (dependency.ClauseElementIndex == elementIndex)
            {
                return dependency;
            }
        }

        return null;
    }

    private static CwdPathDependency? FindArgumentDependency(
        IReadOnlyList<CwdPathDependency> dependencies,
        int argumentIndex)
    {
        foreach (var dependency in dependencies)
        {
            if (dependency.ClauseArgumentIndex == argumentIndex)
            {
                return dependency;
            }
        }

        return null;
    }

    private static int IndexOfElement(
        IReadOnlyList<ClauseElement> elements,
        ClauseElement expected)
    {
        for (var index = 0; index < elements.Count; index++)
        {
            if (object.ReferenceEquals(elements[index], expected))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryGetRelativeSuffix(
        string resolved,
        string workingDirectory,
        out string suffix)
    {
        var normalizedResolved = resolved.Replace('\\', '/').TrimEnd('/');
        var normalizedWorkingDirectory = workingDirectory.Replace('\\', '/').TrimEnd('/');
        if (string.Equals(
                normalizedResolved,
                normalizedWorkingDirectory,
                StringComparison.Ordinal))
        {
            suffix = ".";
            return true;
        }

        var prefix = normalizedWorkingDirectory + "/";
        if (normalizedResolved.StartsWith(prefix, StringComparison.Ordinal))
        {
            suffix = normalizedResolved.Substring(prefix.Length);
            return true;
        }

        suffix = string.Empty;
        return false;
    }

    private BashParserOptions OptionsFor(BashAbstractState state) => new()
    {
        HomeDirectory = _options.HomeDirectory,
        WorkingDirectory = state.WorkingDirectory ?? _options.WorkingDirectory,
    };

    private static Arg CreateAttribution(BashAbstractState state) =>
        state.WorkingDirectory is null
            ? new Arg
            {
                Raw = "<dynamic-cwd>",
                Kind = ArgKind.DynamicSkip,
                IsCwdAttribution = true,
            }
            : new Arg
            {
                Raw = state.WorkingDirectory,
                Resolved = state.WorkingDirectory,
                Kind = ArgKind.Literal,
                IsPath = true,
                IsCwdAttribution = true,
            };

    private static string? FirstVerb(Clause clause) =>
        clause.Verb.Tokens.Count == 0 ? null : clause.Verb.Tokens[0];

    private static string? FirstPositionalValue(Clause clause)
    {
        var argument = FirstPositionalArgument(clause);
        return argument is null ? null : ArgumentValue(clause, argument);
    }

    private static Arg? FirstPositionalArgument(Clause clause)
    {
        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution && !argument.IsFlag)
            {
                return argument;
            }
        }

        return null;
    }

    private static bool ContainsAuthoredArgument(Clause clause, string raw)
    {
        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution &&
                string.Equals(argument.Raw, raw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ArgumentValue(Clause clause, Arg expected)
    {
        var argumentIndex = 0;
        foreach (var argument in clause.Args)
        {
            if (argument.IsCwdAttribution)
            {
                continue;
            }

            if (object.ReferenceEquals(argument, expected))
            {
                var current = 0;
                foreach (var element in clause.Elements)
                {
                    if (element.Role != ClauseElementRole.Argument)
                    {
                        continue;
                    }

                    if (current == argumentIndex)
                    {
                        return element.Value;
                    }

                    current++;
                }

                return argument.Raw;
            }

            argumentIndex++;
        }

        return expected.Raw;
    }

    private static bool IsDecodedWrapper(ShellBlockSyntax body)
    {
        var stack = new Stack<ShellSyntaxNode>();
        stack.Push(body);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case SimpleCommandSyntax simple:
                    return simple.Clause.IsCommandStringWrapped;
                case ShellBlockSyntax block:
                    PushReverse(stack, block.Statements);
                    break;
                case CommandListSyntax list:
                    for (var index = list.Items.Count - 1; index >= 0; index--)
                    {
                        stack.Push(list.Items[index].Command);
                    }

                    break;
                case PipelineSyntax pipeline:
                    PushReverse(stack, pipeline.Stages);
                    break;
                case GroupSyntax group:
                    stack.Push(group.Body);
                    break;
                case ForEachSyntax forEach:
                    stack.Push(forEach.Body);
                    stack.Push(forEach.IteratorCommands);
                    break;
                case CommandSubstitutionSyntax substitution:
                    stack.Push(substitution.Body);
                    break;
            }
        }

        return false;
    }

    private static void PushReverse(
        Stack<ShellSyntaxNode> stack,
        IReadOnlyList<ShellSyntaxNode> nodes)
    {
        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            stack.Push(nodes[index]);
        }
    }

    private IReadOnlyList<ShellSyntaxNode> RewriteNodes(
        IReadOnlyList<ShellSyntaxNode> nodes,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new ShellSyntaxNode[nodes.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = RewriteNode(nodes[index], facts);
        }

        return rewritten;
    }

    private IReadOnlyList<CommandListItemSyntax> RewriteItems(
        IReadOnlyList<CommandListItemSyntax> items,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new CommandListItemSyntax[items.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = items[index] with
            {
                Command = RewriteNode(items[index].Command, facts),
            };
        }

        return rewritten;
    }

    private IReadOnlyList<ConditionalBranchSyntax> RewriteBranches(
        IReadOnlyList<ConditionalBranchSyntax> branches,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new ConditionalBranchSyntax[branches.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = (ConditionalBranchSyntax)RewriteNode(branches[index], facts);
        }

        return rewritten;
    }

    private readonly record struct EffectiveCwdArgument(int ElementIndex, string Value);

    private readonly struct BashFlowResult
    {
        internal BashFlowResult(BashAbstractState? onSuccess, BashAbstractState? onFailure)
        {
            OnSuccess = onSuccess;
            OnFailure = onFailure;
        }

        internal BashAbstractState? OnSuccess { get; }

        internal BashAbstractState? OnFailure { get; }

        internal BashAbstractState? JoinedState =>
            BashAbstractState.JoinNullable(OnSuccess, OnFailure);

        internal static BashFlowResult Both(BashAbstractState state) => new(state, state);

        internal static BashFlowResult Success(BashAbstractState state) => new(state, null);
    }

    private readonly struct BashAbstractState
    {
        internal BashAbstractState(
            string? workingDirectory,
            bool hasCompatibilityAttribution,
            BashLoopBindingContext bindings)
        {
            WorkingDirectory = workingDirectory;
            HasCompatibilityAttribution = hasCompatibilityAttribution;
            Bindings = bindings;
        }

        internal string? WorkingDirectory { get; }

        internal bool HasCompatibilityAttribution { get; }

        internal BashLoopBindingContext Bindings { get; }

        internal BashAbstractState WithUnknownCwd() => new(null, true, Bindings);

        internal BashAbstractState WithBinding(
            string name,
            ShellValueDomain domain) =>
            new(
                WorkingDirectory,
                HasCompatibilityAttribution,
                Bindings.WithBinding(name, domain));

        internal BashAbstractState WithoutBindings() =>
            new(WorkingDirectory, HasCompatibilityAttribution, Bindings.WithoutBindings());

        internal BashAbstractState WithCwd(
            string? workingDirectory,
            bool hasCompatibilityAttribution) =>
            new(workingDirectory, hasCompatibilityAttribution, Bindings);

        internal BashAbstractState WithoutCompatibilityAttribution() =>
            new(WorkingDirectory, WorkingDirectory is null, Bindings);

        internal ShellValueDomain ToDomain() =>
            WorkingDirectory is null
                ? ShellValueDomain.Unknown
                : new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { WorkingDirectory },
                };

        internal bool StateEquals(BashAbstractState other) =>
            string.Equals(WorkingDirectory, other.WorkingDirectory, StringComparison.Ordinal) &&
            HasCompatibilityAttribution == other.HasCompatibilityAttribution &&
            Bindings.StateEquals(other.Bindings);

        internal static BashAbstractState Join(
            BashAbstractState left,
            BashAbstractState right) => new(
                string.Equals(
                    left.WorkingDirectory,
                    right.WorkingDirectory,
                    StringComparison.Ordinal)
                    ? left.WorkingDirectory
                    : null,
                left.HasCompatibilityAttribution || right.HasCompatibilityAttribution,
                BashLoopBindingContext.JoinState(left.Bindings, right.Bindings));

        internal static BashAbstractState Widen(
            BashAbstractState left,
            BashAbstractState right) => new(
                string.Equals(
                    left.WorkingDirectory,
                    right.WorkingDirectory,
                    StringComparison.Ordinal)
                    ? left.WorkingDirectory
                    : null,
                left.HasCompatibilityAttribution || right.HasCompatibilityAttribution,
                BashLoopBindingContext.WidenState(left.Bindings, right.Bindings));

        internal static BashAbstractState? JoinNullable(
            BashAbstractState? left,
            BashAbstractState? right)
        {
            if (left is null)
            {
                return right;
            }

            if (right is null)
            {
                return left;
            }

            return Join(left.Value, right.Value);
        }
    }

    private sealed class ClauseReferenceComparer : IEqualityComparer<Clause>
    {
        internal static ClauseReferenceComparer Instance { get; } = new();

        public bool Equals(Clause? x, Clause? y) => object.ReferenceEquals(x, y);

        public int GetHashCode(Clause obj) => RuntimeHelpers.GetHashCode(obj);
    }

}
