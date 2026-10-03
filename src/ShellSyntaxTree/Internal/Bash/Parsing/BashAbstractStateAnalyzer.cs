// -----------------------------------------------------------------------
// <copyright file="BashAbstractStateAnalyzer.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
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
    private readonly Dictionary<Clause, Dictionary<int, ShellValueDomainFacts>>
        _effectiveArguments = new(ClauseReferenceComparer.Instance);
    private readonly Dictionary<Clause, Dictionary<int, ShellValueDomainFacts>>
        _authoredArguments = new(ClauseReferenceComparer.Instance);
    private readonly Dictionary<Clause, HashSet<int>> _cwdResolutionSanitization =
        new(ClauseReferenceComparer.Instance);
    private readonly Dictionary<Clause, ShellWorkingDirectoryEffectFacts>
        _workingDirectoryEffects = new(ClauseReferenceComparer.Instance);
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
            new BashLoopBindingContext(),
            assignments: null);
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
            ConditionLoopSyntax loop => AnalyzeConditionLoop(loop, input),
            ConditionalSyntax conditional => AnalyzeConditional(conditional, input),
            CaseSyntax caseSyntax => AnalyzeCase(caseSyntax, input),
            ConditionalBranchSyntax branch => AnalyzeBranch(branch, input),
            ShellAssignmentSyntax assignment => AnalyzeAssignment(assignment, input),
            _ => BashFlowResult.Both(input.WithUnknownCwd()),
        };

    private BashFlowResult AnalyzeAssignment(
        ShellAssignmentSyntax syntax,
        BashAbstractState input)
    {
        if (syntax.Assignment is not
            {
                Scope: ShellVariableAssignmentScope.ShellState,
            } assignment)
        {
            return new BashFlowResult(null, null);
        }

        foreach (var substitution in syntax.Substitutions)
        {
            AnalyzeIsolatedBody(
                substitution.Body,
                input,
                resetCompatibilityAttribution: true,
                clearBindings: false);
        }

        var domain = syntax.Value is null
            ? DomainOf(assignment.EffectiveValue)
            : EvaluateAssignmentValue(syntax.Value, input);
        var published = assignment with { EffectiveValue = ToAssignmentDomain(domain) };
        var output = input.WithBinding(published.Name, domain).WithAssignment(published);

        // With a command substitution, the statement has the exit status of
        // the substitution. Without one, the assignment succeeds.
        return syntax.Value?.HasSubstitution == true
            ? BashFlowResult.Both(output)
            : BashFlowResult.Success(output);
    }

    /// <summary>
    /// Evaluates a Bash assignment right-hand side with the bindings and the
    /// launch facts that are live at the assignment (#209). Bash does not
    /// split or glob the value. A substitution gives <c>Unknown</c>.
    /// </summary>
    private ShellValueDomainFacts EvaluateAssignmentValue(
        BashAssignmentValue value,
        BashAbstractState input)
    {
        if (value.HasSubstitution)
        {
            return ShellValueDomainFacts.Unknown;
        }

        var options = OptionsFor(input, value.LaunchEnvironment);
        var shellValue = value.Value;
        if (value.LeadingTilde)
        {
            if (!ShellLaunchFacts.TryGetValue(options, "HOME", out var home) || home.Length == 0)
            {
                return ShellValueDomainFacts.Unknown;
            }

            shellValue = ShellValue.Concat(new[]
            {
                ShellValue.Literal(home),
                shellValue.Slice(1),
            });
        }

        if (input.Bindings.TryAnalyzeAuthoredValue(shellValue, out var bound))
        {
            return bound;
        }

        return TryAnalyzeParserKnownValue(shellValue, options, out var known) &&
               known.Kind == ShellValueDomainKind.Exact
            ? known
            : ShellValueDomainFacts.Unknown;
    }

    private static ShellValueDomainFacts DomainOf(ShellValueDomain value) =>
        value is ShellValueDomain.Exact exact
            ? new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { exact.Value },
            }
            : ShellValueDomainFacts.Unknown;

    private static ShellValueDomain ToAssignmentDomain(ShellValueDomainFacts domain) =>
        domain.Kind == ShellValueDomainKind.Exact && domain.Values.Count == 1
            ? new ShellValueDomain.Exact(domain.Values[0])
            : new ShellValueDomain.Unknown();

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
            IsPotentialPersistentBindingMutation(simple.Clause) &&
            !IsModeledRead(simple.Clause))
        {
            _isComplete = false;
            return new BashFlowResult(null, null);
        }

        RecordInput(simple.Clause, input);
        RecordEffectiveArguments(simple, input);
        if (TryAnalyzeRead(simple.Clause, input, out var readFlow))
        {
            return readFlow;
        }

        var cwdTransfer = AnalyzeEffectiveCwdTransfer(simple, input);
        if (cwdTransfer is BashFlowResult effectiveFlow)
        {
            return effectiveFlow;
        }

        if (!TryGetLegacyCwdTransfer(simple.Clause, input, out var success))
        {
            RecordWorkingDirectoryEffect(
                simple.Clause,
                ShellWorkingDirectoryEffectFacts.Unchanged);
            return BashFlowResult.Both(input);
        }

        RecordWorkingDirectoryEffect(
            simple.Clause,
            ShellWorkingDirectoryEffectFacts.Unknown);
        return new BashFlowResult(success, input);
    }

    private bool IsModeledRead(Clause clause) =>
        _options.InitialStateMode == BashInitialStateMode.FreshNonInteractiveNoStartup &&
        BashReadBuiltin.IsBoundedRead(clause);

    /// <summary>
    /// A bounded <c>read</c> (#212) assigns each name from its input, also
    /// when it fails at the end of the input. Each name is bound to an
    /// unknown value on both paths.
    /// </summary>
    private bool TryAnalyzeRead(Clause clause, BashAbstractState input, out BashFlowResult flow)
    {
        flow = default;
        if (!IsModeledRead(clause) ||
            !BashReadBuiltin.TryGetNames(clause, out var names))
        {
            return false;
        }

        var output = input;
        foreach (var (name, elementIndex) in names)
        {
            var element = clause.Elements[elementIndex];
            if (element.SourceStart is not int start ||
                element.SourceLength is not int length ||
                length <= 0)
            {
                _isComplete = false;
                return false;
            }

            output = output
                .WithBinding(name, ShellValueDomainFacts.Unknown)
                .WithAssignment(new ShellVariableAssignment
                {
                    Name = name,
                    AuthoredValue = new ShellValueDomain.Unknown(),
                    EffectiveValue = new ShellValueDomain.Unknown(),
                    Scope = ShellVariableAssignmentScope.ShellState,
                    MayAffectProcessEnvironment = true,
                    SourceStart = start,
                    SourceLength = length,
                });
        }

        RecordWorkingDirectoryEffect(clause, ShellWorkingDirectoryEffectFacts.Unchanged);
        flow = BashFlowResult.Both(output);
        return true;
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
            // The iteration plan does not expand `~` or launch variables, so
            // it needs no launch facts (#200).
            OptionsFor(loopInput.Value, launchEnvironment: null),
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

    /// <summary>
    /// Bash tries each condition in order. A later condition runs only when
    /// every earlier condition failed. The first successful condition runs its
    /// body. With no successful condition and no <c>else</c>, the statement
    /// succeeds (#212).
    /// </summary>
    private BashFlowResult AnalyzeConditional(
        ConditionalSyntax conditional,
        BashAbstractState input)
    {
        BashAbstractState? success = null;
        BashAbstractState? failure = null;
        BashAbstractState? pending = input;
        foreach (var branch in conditional.Branches)
        {
            if (pending is null)
            {
                break;
            }

            var condition = AnalyzeBlock(branch.Condition, pending.Value);
            if (condition.OnSuccess is BashAbstractState taken)
            {
                var body = AnalyzeBlock(branch.Body, taken);
                success = BashAbstractState.JoinNullable(success, body.OnSuccess);
                failure = BashAbstractState.JoinNullable(failure, body.OnFailure);
            }

            pending = condition.OnFailure;
        }

        if (pending is BashAbstractState unmatched)
        {
            if (conditional.Else is not null)
            {
                var elseFlow = AnalyzeBlock(conditional.Else, unmatched);
                success = BashAbstractState.JoinNullable(success, elseFlow.OnSuccess);
                failure = BashAbstractState.JoinNullable(failure, elseFlow.OnFailure);
            }
            else
            {
                success = BashAbstractState.JoinNullable(success, unmatched);
            }
        }

        return new BashFlowResult(success, failure);
    }

    /// <summary>
    /// The parser cannot prove which pattern matches. Any one item body, or
    /// none, can run. With no match, the statement succeeds (#212).
    /// </summary>
    private BashFlowResult AnalyzeCase(CaseSyntax caseSyntax, BashAbstractState input)
    {
        BashAbstractState? success = input;
        BashAbstractState? failure = null;
        foreach (var item in caseSyntax.Items)
        {
            var body = AnalyzeBlock(item.Body, input);
            success = BashAbstractState.JoinNullable(success, body.OnSuccess);
            failure = BashAbstractState.JoinNullable(failure, body.OnFailure);
        }

        return new BashFlowResult(success, failure);
    }

    /// <summary>
    /// A <c>while</c> or <c>until</c> loop (#212). The head state joins the
    /// loop entry and every body exit until it is stable. The loop ends when
    /// the condition fails (<c>while</c>) or succeeds (<c>until</c>). The exit
    /// status of the loop can be zero or not, so both paths get the exit
    /// state.
    /// </summary>
    private BashFlowResult AnalyzeConditionLoop(
        ConditionLoopSyntax loop,
        BashAbstractState input)
    {
        if (loop.LoopKind is not (ConditionLoopKind.While or ConditionLoopKind.Until))
        {
            _isComplete = false;
            return new BashFlowResult(null, null);
        }

        BashAbstractState? exit = null;
        var head = input;
        var wideningBase = input;
        for (var iteration = 0;
             iteration <= ShellAnalysisLimits.MaxValueCandidates + 1;
             iteration++)
        {
            if (!TryConsumeLoopAnalysisTransition())
            {
                return new BashFlowResult(null, null);
            }

            if (iteration == ShellAnalysisLimits.MaxValueCandidates + 1)
            {
                // Widen disagreement after the finite-domain budget, then run
                // the loop once more so occurrence facts reflect the widened
                // head state.
                head = BashAbstractState.Widen(wideningBase, head);
            }

            var condition = AnalyzeBlock(loop.Condition, head);
            var (stay, leave) = loop.LoopKind == ConditionLoopKind.While
                ? (condition.OnSuccess, condition.OnFailure)
                : (condition.OnFailure, condition.OnSuccess);
            exit = BashAbstractState.JoinNullable(exit, leave);
            if (stay is not BashAbstractState bodyInput)
            {
                break;
            }

            var body = AnalyzeBlock(loop.Body, bodyInput);
            if (body.JoinedState is not BashAbstractState bodyExit)
            {
                break;
            }

            var nextHead = BashAbstractState.Join(head, bodyExit);
            if (head.StateEquals(nextHead))
            {
                break;
            }

            wideningBase = head;
            head = nextHead;
        }

        return exit is BashAbstractState state
            ? BashFlowResult.Both(state)
            : new BashFlowResult(null, null);
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

        Dictionary<int, ShellValueDomainFacts>? accumulated = null;
        var evaluator = input.Bindings;
        var clauseOptions = OptionsFor(input, sourceFacts.LaunchEnvironment);
        foreach (var provenance in sourceFacts.ValueProvenance)
        {
            var dependsOnTrackedBinding =
                input.Bindings.ReferencesTrackedBinding(provenance.Value);
            if (_options.PublishAuthoredSourceFacts && dependsOnTrackedBinding)
            {
                var authoredValue = NormalizeAuthoredParserKnownFragments(
                    provenance.Value,
                    clauseOptions);
                evaluator.TryAnalyzeAuthoredValue(authoredValue, out var authoredDomain);
                AccumulateArgumentDomain(
                    _authoredArguments,
                    simple.Clause,
                    provenance.ClauseElementIndex,
                    authoredDomain);
                if (PublishesAuthoredFactsOnly)
                {
                    continue;
                }
            }

            var hasStateDependentValue = evaluator.TryAnalyzeEffectiveValue(
                provenance.Value,
                out var domain);
            if (!hasStateDependentValue &&
                !RequiresIndependentEffectiveValue(simple.Clause, provenance))
            {
                continue;
            }

            if (!hasStateDependentValue)
            {
                if (BashGlobPatternAnalysis.TryAnalyze(
                        provenance.Value,
                        clauseOptions,
                        GlobWorkingDirectory(input),
                        out var globPattern))
                {
                    domain = globPattern;
                }
                else if (TryAnalyzeParserKnownValue(
                        provenance.Value,
                        clauseOptions,
                        out var knownValue))
                {
                    domain = knownValue;
                }
                else
                {
                    domain = evaluator.AnalyzeWordForTransfer(provenance.Value);
                }
            }

            accumulated ??= GetEffectiveArguments(simple.Clause);
            AccumulateArgumentDomain(
                accumulated,
                provenance.ClauseElementIndex,
                domain);
        }
    }

    private static ShellValue NormalizeAuthoredParserKnownFragments(
        ShellValue value,
        BashParserOptions options)
    {
        var homeDirectory = BashResolver.GetHomeDirectory(options);
        var builder = new ShellValueBuilder();
        for (var index = 0; index < value.Fragments.Count; index++)
        {
            var fragment = value.Fragments[index];
            if (fragment.Kind == ShellValueFragmentKind.Expansion &&
                fragment.Expansion is { Kind: ShellExpansionKind.Tilde })
            {
                var kind = BashResolver.ClassifyTildeExpansion(value, index);
                if (kind == BashTildeExpansionKind.Home && homeDirectory.Length > 0)
                {
                    builder.AppendLiteral(homeDirectory, null, null);
                    continue;
                }

                if (kind == BashTildeExpansionKind.Literal)
                {
                    builder.AppendLiteral(fragment.Value, null, null);
                    continue;
                }
            }

            builder.Append(fragment);
        }

        return builder.Build();
    }

    private static void AccumulateArgumentDomain(
        Dictionary<Clause, Dictionary<int, ShellValueDomainFacts>> arguments,
        Clause clause,
        int elementIndex,
        ShellValueDomainFacts domain)
    {
        if (!arguments.TryGetValue(clause, out var accumulated))
        {
            accumulated = new Dictionary<int, ShellValueDomainFacts>();
            arguments.Add(clause, accumulated);
        }

        AccumulateArgumentDomain(accumulated, elementIndex, domain);
    }

    private static void AccumulateArgumentDomain(
        IDictionary<int, ShellValueDomainFacts> accumulated,
        int elementIndex,
        ShellValueDomainFacts domain)
    {
        if (accumulated.TryGetValue(elementIndex, out var prior))
        {
            accumulated[elementIndex] = BashLoopBindingContext.JoinDomains(prior, domain);
        }
        else
        {
            accumulated.Add(elementIndex, domain);
        }
    }

    private static bool RequiresIndependentEffectiveValue(
        Clause clause,
        ShellValueElementProvenance provenance)
    {
        if (provenance.ClauseElementIndex < 0 ||
            provenance.ClauseElementIndex >= clause.Elements.Count)
        {
            return false;
        }

        var nonEmptyLiteralFragments = 0;
        var hasLiteralLexicalTransform = false;
        foreach (var fragment in provenance.Value.Fragments)
        {
            if (fragment.Kind != ShellValueFragmentKind.Literal)
            {
                return true;
            }

            if (fragment.Value.Length == 0)
            {
                continue;
            }

            nonEmptyLiteralFragments++;
            if (fragment.SourceLength != fragment.Value.Length)
            {
                hasLiteralLexicalTransform = true;
            }
        }

        // Clause.Elements already carries ordinary authored literals. Keep the
        // overlay for values whose shell decoding or shell-specific path
        // spelling gives a policy consumer additional information.
        var element = clause.Elements[provenance.ClauseElementIndex];
        if ((element.IsPath || element.IsFlag) &&
            (hasLiteralLexicalTransform || nonEmptyLiteralFragments > 1))
        {
            return true;
        }

        return element.IsPath && HasProviderQualifier(provenance.Value.Decoded);
    }

    private static bool TryAnalyzeParserKnownValue(
        ShellValue value,
        BashParserOptions options,
        out ShellValueDomainFacts domain)
    {
        var homeDirectory = BashResolver.GetHomeDirectory(options);
        var literal = new StringBuilder(value.Decoded.Length);
        var parts = new List<ShellValueDomainFacts>();
        for (var fragmentIndex = 0;
             fragmentIndex < value.Fragments.Count;
             fragmentIndex++)
        {
            var fragment = value.Fragments[fragmentIndex];
            if (fragment.Kind == ShellValueFragmentKind.Literal)
            {
                literal.Append(fragment.Value);
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is not ShellExpansionReference expansion)
            {
                domain = ShellValueDomainFacts.Unknown;
                return false;
            }

            if (expansion.Kind == ShellExpansionKind.Glob &&
                (fragment.AllowedTransforms & ShellLexicalTransform.Glob) == 0)
            {
                literal.Append(fragment.Value);
                continue;
            }

            if (expansion.Kind == ShellExpansionKind.Tilde)
            {
                var tildeKind = BashResolver.ClassifyTildeExpansion(value, fragmentIndex);
                if (tildeKind == BashTildeExpansionKind.Literal)
                {
                    literal.Append(fragment.Value);
                    continue;
                }

                if (tildeKind == BashTildeExpansionKind.Unknown ||
                    homeDirectory.Length == 0)
                {
                    domain = ShellValueDomainFacts.Unknown;
                    return false;
                }

                literal.Append(homeDirectory);
                continue;
            }

            if (IsQuotedExitStatus(fragment, expansion))
            {
                FlushLiteralPart(literal, parts);
                parts.Add(ShellValueDomainFacts.IntegerRange(0, 255));
                continue;
            }

            if (expansion.Kind == ShellExpansionKind.Variable &&
                !string.Equals(expansion.Name, "HOME", StringComparison.Ordinal) &&
                fragment.Cardinality == ShellValueCardinality.ExactlyOne &&
                (fragment.AllowedTransforms & ShellLexicalTransform.Variable) != 0 &&
                ShellLaunchFacts.TryGetValue(options, expansion.Name, out var launchValue))
            {
                // A launcher-proved value (#200). An unquoted value that can
                // split or glob is not one exact value.
                if (!ShellLaunchFacts.IsSingleWordValue(
                        launchValue,
                        (fragment.AllowedTransforms & ShellLexicalTransform.FieldSplit) != 0))
                {
                    domain = ShellValueDomainFacts.Unknown;
                    return false;
                }

                literal.Append(launchValue);
                continue;
            }

            if (expansion.Kind != ShellExpansionKind.Variable ||
                !string.Equals(expansion.Name, "HOME", StringComparison.Ordinal) ||
                fragment.Cardinality != ShellValueCardinality.ExactlyOne ||
                (fragment.AllowedTransforms & ShellLexicalTransform.Variable) == 0 ||
                homeDirectory.Length == 0 ||
                ((fragment.AllowedTransforms & ShellLexicalTransform.FieldSplit) != 0 &&
                 ContainsFieldSplitOrGlobCharacter(homeDirectory)))
            {
                domain = ShellValueDomainFacts.Unknown;
                return false;
            }

            literal.Append(homeDirectory);
        }

        FlushLiteralPart(literal, parts);
        domain = ShellValueDomainFacts.Concatenate(parts);
        return true;
    }

    private static bool IsQuotedExitStatus(
        ShellValueFragment fragment,
        ShellExpansionReference expansion) =>
        expansion.Kind == ShellExpansionKind.SpecialParameter &&
        string.Equals(expansion.Name, "?", StringComparison.Ordinal) &&
        fragment.Cardinality == ShellValueCardinality.ExactlyOne &&
        (fragment.AllowedTransforms & ShellLexicalTransform.Variable) != 0 &&
        (fragment.AllowedTransforms & ShellLexicalTransform.FieldSplit) == 0;

    private static void FlushLiteralPart(
        StringBuilder literal,
        ICollection<ShellValueDomainFacts> parts)
    {
        if (literal.Length == 0)
        {
            return;
        }

        parts.Add(new ShellValueDomainFacts
        {
            Kind = ShellValueDomainKind.Exact,
            Values = new[] { literal.ToString() },
        });
        literal.Clear();
    }

    private static bool ContainsFieldSplitOrGlobCharacter(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '*' or '?' or '[')
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasProviderQualifier(string value)
    {
        var separator = value.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        for (var index = 0; index < separator; index++)
        {
            if (!char.IsLetterOrDigit(value[index]) && value[index] != '-')
            {
                return false;
            }
        }

        return true;
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
                            ShellValueDomainFacts.Unknown;
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
            case CaseSyntax caseSyntax:
                foreach (var item in caseSyntax.Items)
                {
                    RecordUnvisitedBindingArguments(item.Body, bindingName);
                }

                break;
            case ShellAssignmentSyntax assignment:
                foreach (var substitution in assignment.Substitutions)
                {
                    RecordUnvisitedBindingArguments(substitution.Body, bindingName);
                }

                break;
            case CommandSubstitutionSyntax substitution:
                RecordUnvisitedBindingArguments(substitution.Body, bindingName);
                break;
        }
    }

    private Dictionary<int, ShellValueDomainFacts> GetEffectiveArguments(Clause clause)
    {
        if (_effectiveArguments.TryGetValue(clause, out var accumulated))
        {
            return accumulated;
        }

        accumulated = new Dictionary<int, ShellValueDomainFacts>();
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
            RecordWorkingDirectoryEffect(
                simple.Clause,
                ShellWorkingDirectoryEffectFacts.Unchanged);
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

        if (PublishesAuthoredFactsOnly)
        {
            foreach (var argumentValue in argumentValues)
            {
                if (!input.Bindings.ReferencesTrackedBinding(argumentValue))
                {
                    continue;
                }

                RecordCwdResolutionSanitization(simple.Clause, argumentElementIndices);
                RecordWorkingDirectoryEffect(
                    simple.Clause,
                    ShellWorkingDirectoryEffectFacts.Unknown);
                return BashFlowResult.Both(input.WithUnknownCwd());
            }
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
            RecordWorkingDirectoryEffect(
                simple.Clause,
                ShellWorkingDirectoryEffectFacts.ChangesOnSuccess(
                    ShellValueDomainFacts.Unknown));
            return new BashFlowResult(input.WithUnknownCwd(), input);
        }

        BashAbstractState? success = null;
        BashAbstractState? failure = null;
        ShellWorkingDirectoryEffectFacts? effect = null;
        foreach (var bindings in bindingAlternatives)
        {
            BuildEffectiveCwdArguments(
                bindings,
                argumentElementIndices,
                argumentValues,
                input,
                simple.Clause,
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
            var visitEffect = CreateCwdTransferEffect(visit);
            effect = effect is null
                ? visitEffect
                : ShellWorkingDirectoryEffectFacts.Join(effect, visitEffect);
        }

        RecordWorkingDirectoryEffect(
            simple.Clause,
            effect ?? ShellWorkingDirectoryEffectFacts.Unknown);
        return new BashFlowResult(success, failure);
    }

    private static ShellWorkingDirectoryEffectFacts CreateCwdTransferEffect(
        BashFlowResult flow)
    {
        if (flow.OnSuccess is not BashAbstractState success)
        {
            return ShellWorkingDirectoryEffectFacts.Unchanged;
        }

        return ShellWorkingDirectoryEffectFacts.ChangesOnSuccess(success.ToDomain());
    }

    private void RecordWorkingDirectoryEffect(
        Clause clause,
        ShellWorkingDirectoryEffectFacts effect)
    {
        _workingDirectoryEffects[clause] = _workingDirectoryEffects.TryGetValue(
            clause,
            out var prior)
            ? ShellWorkingDirectoryEffectFacts.Join(prior, effect)
            : effect;
    }

    private ShellWorkingDirectoryEffectFacts GetWorkingDirectoryEffect(Clause clause) =>
        _workingDirectoryEffects.TryGetValue(clause, out var effect)
            ? effect
            : ShellWorkingDirectoryEffectFacts.Unknown;

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
        Clause clause,
        out IReadOnlyList<EffectiveCwdArgument>? arguments)
    {
        var clauseOptions = OptionsFor(input, LaunchFor(clause));
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
                TryResolveKnownHomeWord(argumentValues[index], input, clauseOptions, out var homeWord))
            {
                domain = new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { homeWord },
                };
            }

            // A launcher-proved word (#200) keeps its expanded spelling, so
            // the cd rules below still see a relative operand and apply the
            // CDPATH rule to it.
            if (domain.Kind != ShellValueDomainKind.Exact &&
                ShellLaunchFacts.TryExpandWord(argumentValues[index], clauseOptions, out var launchWord))
            {
                domain = new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { launchWord },
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

    private static bool TryResolveKnownHomeWord(
        ShellValue value,
        BashAbstractState input,
        BashParserOptions clauseOptions,
        out string resolvedValue)
    {
        var resolved = BashResolver.Resolve(
            value,
            treatAsPath: true,
            clauseOptions,
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

        var clauseOptions = OptionsFor(input, LaunchFor(clause));
        if (operand is null)
        {
            var homeDirectory = BashResolver.GetCdHomeDirectory(clauseOptions);
            var home = physical || homeDirectory is null
                ? input.WithUnknownCwd()
                : input.WithCwd(homeDirectory, true);
            return new BashFlowResult(home, input);
        }

        if (physical ||
            operand.Value.Value == "-" ||
            IsCdPathSearchCandidate(operand.Value.Value) &&
            !CanResolveRelativeCd(clauseOptions))
        {
            return new BashFlowResult(input.WithUnknownCwd(), input);
        }

        var resolved = BashResolver.Resolve(
            operand.Value.Value,
            treatAsPath: true,
            clauseOptions,
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
            if (dispatched is "cd" or "pushd" or "popd" or
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

        if (verb != "cd")
        {
            return false;
        }

        if (!TryGetCdOperand(
                clause,
                out _,
                out var target,
                out var resolutionMustBeUnknown,
                out var searchesCdPath))
        {
            if (resolutionMustBeUnknown)
            {
                success = input.WithUnknownCwd();
                return true;
            }

            var homeDirectory = BashResolver.GetCdHomeDirectory(
                OptionsFor(input, LaunchFor(clause)));
            success = homeDirectory is null
                ? input.WithUnknownCwd()
                : input.WithCwd(homeDirectory, true);
            return true;
        }

        if (resolutionMustBeUnknown || searchesCdPath)
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
        var options = OptionsFor(input, LaunchFor(clause));
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

    /// <summary>
    /// Finds the first <c>cd</c> operand. <paramref name="resolutionMustBeUnknown"/>
    /// is true for a rule that needs runtime state: <c>-P</c>, <c>-@</c>, or
    /// <c>cd -</c>. <paramref name="searchesCdPath"/> is true for a literal
    /// operand that bash searches in <c>CDPATH</c> first. Only proof that
    /// <c>CDPATH</c> is unset can make that operand exact.
    /// </summary>
    private static bool TryGetCdOperand(
        Clause clause,
        out int argumentIndex,
        out Arg target,
        out bool resolutionMustBeUnknown,
        out bool searchesCdPath)
    {
        argumentIndex = -1;
        target = null!;
        resolutionMustBeUnknown = false;
        searchesCdPath = false;
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
            resolutionMustBeUnknown = physical || value == "-";
            searchesCdPath = target.Kind == ArgKind.Literal &&
                IsCdPathSearchCandidate(value);
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
            ShellAssignmentSyntax assignment => RewriteAssignment(assignment, facts),
            CaseSyntax caseSyntax => caseSyntax with
            {
                Items = RewriteCaseItems(caseSyntax.Items, facts),
            },
            _ => node,
        };

    private IReadOnlyList<CaseItemSyntax> RewriteCaseItems(
        IReadOnlyList<CaseItemSyntax> items,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new CaseItemSyntax[items.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = items[index] with { Body = RewriteBlock(items[index].Body, facts) };
        }

        return rewritten;
    }

    private ShellAssignmentSyntax RewriteAssignment(
        ShellAssignmentSyntax assignment,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        if (assignment.Substitutions.Count == 0)
        {
            return assignment;
        }

        var substitutions = new CommandSubstitutionSyntax[assignment.Substitutions.Count];
        for (var index = 0; index < substitutions.Length; index++)
        {
            substitutions[index] = (CommandSubstitutionSyntax)RewriteNode(
                assignment.Substitutions[index],
                facts);
        }

        return assignment with { Substitutions = substitutions };
    }

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
                bindings: new BashLoopBindingContext(),
                assignments: null);
        }

        var clause = RewriteClause(simple.Clause, input, sourceFacts.CwdPathDependencies);
        var redirects = RewriteRedirectFacts(
            sourceFacts.Redirects,
            sourceFacts.RedirectTargetProvenance,
            input.Bindings,
            clause,
            allowBindingValues: !PublishesAuthoredFactsOnly,
            OptionsFor(input, sourceFacts.LaunchEnvironment),
            GlobWorkingDirectory(input));
        facts.Add(clause, new CommandOccurrenceFacts
        {
            EffectiveArguments = CreateEffectiveArguments(simple.Clause),
            AuthoredArguments = CreateAuthoredArguments(simple.Clause),
            Assignments = CreateAssignments(
                input,
                simple.EnvironmentAssignments,
                sourceFacts.EnvironmentAssignmentValues),
            PublishAuthoredPathShape = true,
            WorkingDirectory = input.ToDomain(),
            WorkingDirectoryEffect = GetWorkingDirectoryEffect(simple.Clause),
            Redirects = redirects,
            RedirectTargetProvenance = sourceFacts.RedirectTargetProvenance,
            CwdPathDependencies = sourceFacts.CwdPathDependencies,
            ValueProvenance = sourceFacts.ValueProvenance,
            IsComplete = (sourceFacts.IsComplete || sourceFacts.IsCompleteExceptRedirects) &&
                AreRedirectsComplete(redirects),
            LaunchEnvironment = sourceFacts.LaunchEnvironment,
            LaunchWordValues = sourceFacts.LaunchWordValues,
        });
        return simple with
        {
            Clause = clause,
            Substitutions = substitutions,
        };
    }

    private IReadOnlyList<ShellVariableAssignment> CreateAssignments(
        BashAbstractState input,
        IReadOnlyList<ShellVariableAssignment> environmentAssignments,
        IReadOnlyList<BashAssignmentValue?> environmentValues)
    {
        if (input.Assignments.Count == 0 && environmentValues.Count == 0)
        {
            return environmentAssignments;
        }

        var assignments = new List<ShellVariableAssignment>(
            input.Assignments.Count + environmentAssignments.Count);
        assignments.AddRange(input.Assignments);
        for (var index = 0; index < environmentAssignments.Count; index++)
        {
            var assignment = environmentAssignments[index];
            if (index < environmentValues.Count && environmentValues[index] is { } value)
            {
                assignment = assignment with
                {
                    EffectiveValue = ToAssignmentDomain(EvaluateAssignmentValue(value, input)),
                };
            }

            assignments.Add(assignment);
        }

        return assignments;
    }

    private IReadOnlyList<EffectiveArgumentFacts> CreateEffectiveArguments(Clause clause)
        => CreateArguments(_effectiveArguments, clause);

    private IReadOnlyList<EffectiveArgumentFacts> CreateAuthoredArguments(Clause clause)
        => CreateArguments(_authoredArguments, clause);

    private static IReadOnlyList<EffectiveArgumentFacts> CreateArguments(
        IReadOnlyDictionary<Clause, Dictionary<int, ShellValueDomainFacts>> source,
        Clause clause)
    {
        if (!source.TryGetValue(clause, out var accumulated) ||
            accumulated.Count == 0)
        {
            return Array.Empty<EffectiveArgumentFacts>();
        }

        var indices = new List<int>(accumulated.Keys);
        indices.Sort();
        var effective = new EffectiveArgumentFacts[indices.Count];
        for (var index = 0; index < effective.Length; index++)
        {
            effective[index] = new EffectiveArgumentFacts
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
        // A relative operand such as `cd sub` gets the same resolved path as
        // the cd transfer computes. That is true only when the transfer can
        // prove that CDPATH is unset at this clause (#200, #203). Otherwise the
        // operand can select a CDPATH entry, and the resolved path stays
        // empty.
        var clearCdTargetIndex = directVerb is "cd" or "chdir" && TryGetCdOperand(
            clause,
            out var cdTargetIndex,
            out _,
            out var cdResolutionMustBeUnknown,
            out var cdSearchesCdPath) &&
            (cdResolutionMustBeUnknown ||
             cdSearchesCdPath &&
             !CanResolveRelativeCd(OptionsFor(input, LaunchFor(clause))))
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

    private static IReadOnlyList<RedirectAnalysisFacts> RewriteRedirectFacts(
        IReadOnlyList<RedirectAnalysisFacts> source,
        IReadOnlyList<RedirectTargetProvenance> provenance,
        BashLoopBindingContext bindings,
        Clause clause,
        bool allowBindingValues,
        BashParserOptions clauseOptions,
        string? globWorkingDirectory)
    {
        if (source.Count == 0)
        {
            return source;
        }

        var rewritten = new RedirectAnalysisFacts[source.Count];
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
                        bindings,
                        allowBindingValues),
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
            if (redirect.IsDynamicSkip &&
                TryAnalyzeGlobRedirectTarget(
                    fact,
                    provenance,
                    clauseOptions,
                    globWorkingDirectory,
                    out var globTarget))
            {
                // Bash expands a redirect word to one existing match, or keeps
                // the pattern text when nothing matches. More than one match
                // is an ambiguous-redirect error, and the command does not
                // run. Each case names a path that the pattern describes.
                rewritten[index] = fact with
                {
                    Target = globTarget,
                    IsComplete = fact.Source.Kind != RedirectSourceKind.Unknown,
                };
                continue;
            }

            rewritten[index] = fact with
            {
                Target = redirect.IsDynamicSkip
                    ? ShellValueDomainFacts.Unknown
                    : new ShellValueDomainFacts
                    {
                        Kind = ShellValueDomainKind.Exact,
                        Values = new[] { redirect.Target },
                    },
                IsComplete = fact.IsComplete && !redirect.IsDynamicSkip,
            };
        }

        return rewritten;
    }

    private static bool TryAnalyzeGlobRedirectTarget(
        RedirectAnalysisFacts fact,
        IReadOnlyList<RedirectTargetProvenance> provenance,
        BashParserOptions clauseOptions,
        string? globWorkingDirectory,
        out ShellValueDomainFacts target)
    {
        target = ShellValueDomainFacts.Unknown;
        if (fact.Operation is not (
                RedirectOperation.FileInput or
                RedirectOperation.FileOutput or
                RedirectOperation.FileAppend or
                RedirectOperation.CombinedOutput or
                RedirectOperation.CombinedOutputAppend))
        {
            return false;
        }

        foreach (var candidate in provenance)
        {
            if (candidate.RedirectIndex == fact.RedirectIndex)
            {
                return BashGlobPatternAnalysis.TryAnalyze(
                    candidate.Value,
                    clauseOptions,
                    globWorkingDirectory,
                    out target);
            }
        }

        return false;
    }

    private static ShellValueDomainFacts RewriteHereStringTarget(
        RedirectAnalysisFacts fact,
        IReadOnlyList<RedirectTargetProvenance> provenance,
        BashLoopBindingContext bindings,
        bool allowBindingValues)
    {
        foreach (var candidate in provenance)
        {
            if (candidate.RedirectIndex != fact.RedirectIndex)
            {
                continue;
            }
            if (!allowBindingValues && bindings.ReferencesTrackedBinding(candidate.Value))
            {
                return ShellValueDomainFacts.Unknown;
            }

            if (!bindings.TryAnalyzeEffectiveValue(candidate.Value, out var domain))
            {
                return fact.Target;
            }

            if (domain.Kind is not (
                    ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
            {
                return ShellValueDomainFacts.Unknown;
            }

            var values = new string[domain.Values.Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = domain.Values[index] + "\n";
            }

            return new ShellValueDomainFacts
            {
                Kind = domain.Kind,
                Values = values,
            };
        }

        return fact.Target;
    }

    private static bool AreRedirectsComplete(IReadOnlyList<RedirectAnalysisFacts> redirects)
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
                OptionsFor(input, launchEnvironment: null),
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
            OptionsFor(input, launchEnvironment: null),
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

    private BashParserOptions OptionsFor(
        BashAbstractState state,
        ShellLaunchEnvironment? launchEnvironment) => _options with
    {
        WorkingDirectory = state.WorkingDirectory ?? _options.WorkingDirectory,
        LaunchEnvironment = launchEnvironment,
    };

    /// <summary>
    /// The launch facts that the structural pass recorded as live for this
    /// clause (#200). Null when none were supplied or recorded.
    /// </summary>
    private ShellLaunchEnvironment? LaunchFor(Clause clause) =>
        _factsFactory(new SimpleCommandSyntax { Clause = clause }).LaunchEnvironment;

    /// <summary>
    /// A relative <c>cd</c> operand is searched in <c>CDPATH</c> first. The
    /// parser resolves it against the current directory only when the caller
    /// supplied the start directory and the launch facts prove that
    /// <c>CDPATH</c> is still unset (#200).
    /// </summary>
    private bool CanResolveRelativeCd(BashParserOptions clauseOptions) =>
        _options.WorkingDirectory is not null &&
        ShellLaunchFacts.IsUnset(clauseOptions, "CDPATH");

    /// <summary>
    /// The directory that a relative glob word expands in (#206). It is
    /// proved only when the caller supplied the start directory, the same
    /// rule as a relative <c>cd</c>.
    /// </summary>
    private string? GlobWorkingDirectory(BashAbstractState state) =>
        _options.WorkingDirectory is null ? null : state.WorkingDirectory;

    private bool PublishesAuthoredFactsOnly =>
        _options.PublishAuthoredSourceFacts &&
        _options.InitialStateMode != BashInitialStateMode.IsolatedNonInteractive;

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
            BashLoopBindingContext bindings,
            IReadOnlyList<ShellVariableAssignment>? assignments)
        {
            WorkingDirectory = workingDirectory;
            HasCompatibilityAttribution = hasCompatibilityAttribution;
            Bindings = bindings;
            Assignments = assignments ?? Array.Empty<ShellVariableAssignment>();
        }

        internal string? WorkingDirectory { get; }

        internal bool HasCompatibilityAttribution { get; }

        internal BashLoopBindingContext Bindings { get; }

        /// <summary>
        /// The live shell-state assignments, one for each name, in the order
        /// of their latest assignment (#209).
        /// </summary>
        internal IReadOnlyList<ShellVariableAssignment> Assignments { get; }

        internal BashAbstractState WithUnknownCwd() =>
            new(null, true, Bindings, Assignments);

        internal BashAbstractState WithBinding(
            string name,
            ShellValueDomainFacts domain) =>
            new(
                WorkingDirectory,
                HasCompatibilityAttribution,
                Bindings.WithBinding(name, domain),
                Assignments);

        internal BashAbstractState WithAssignment(ShellVariableAssignment assignment)
        {
            var assignments = new List<ShellVariableAssignment>(Assignments.Count + 1);
            foreach (var prior in Assignments)
            {
                if (!string.Equals(prior.Name, assignment.Name, StringComparison.Ordinal))
                {
                    assignments.Add(prior);
                }
            }

            assignments.Add(assignment);
            return new(WorkingDirectory, HasCompatibilityAttribution, Bindings, assignments);
        }

        internal BashAbstractState WithoutBindings() =>
            new(
                WorkingDirectory,
                HasCompatibilityAttribution,
                Bindings.WithoutBindings(),
                Assignments);

        internal BashAbstractState WithCwd(
            string? workingDirectory,
            bool hasCompatibilityAttribution) =>
            new(workingDirectory, hasCompatibilityAttribution, Bindings, Assignments);

        internal BashAbstractState WithoutCompatibilityAttribution() =>
            new(WorkingDirectory, WorkingDirectory is null, Bindings, Assignments);

        internal ShellValueDomainFacts ToDomain() =>
            WorkingDirectory is null
                ? ShellValueDomainFacts.Unknown
                : new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { WorkingDirectory },
                };

        internal bool StateEquals(BashAbstractState other) =>
            string.Equals(WorkingDirectory, other.WorkingDirectory, StringComparison.Ordinal) &&
            HasCompatibilityAttribution == other.HasCompatibilityAttribution &&
            Bindings.StateEquals(other.Bindings) &&
            AssignmentsEqual(Assignments, other.Assignments);

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
                BashLoopBindingContext.JoinState(left.Bindings, right.Bindings),
                JoinAssignments(left.Assignments, right.Assignments));

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
                BashLoopBindingContext.WidenState(left.Bindings, right.Bindings),
                JoinAssignments(left.Assignments, right.Assignments));

        private static bool AssignmentsEqual(
            IReadOnlyList<ShellVariableAssignment> left,
            IReadOnlyList<ShellVariableAssignment> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!Equals(left[index], right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Keeps an assignment only when both paths reach the command with
        /// the same assignment.
        /// </summary>
        private static IReadOnlyList<ShellVariableAssignment> JoinAssignments(
            IReadOnlyList<ShellVariableAssignment> left,
            IReadOnlyList<ShellVariableAssignment> right)
        {
            if (AssignmentsEqual(left, right))
            {
                return left;
            }

            var joined = new List<ShellVariableAssignment>();
            foreach (var assignment in left)
            {
                foreach (var candidate in right)
                {
                    if (Equals(assignment, candidate))
                    {
                        joined.Add(assignment);
                        break;
                    }
                }
            }

            return joined;
        }

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
