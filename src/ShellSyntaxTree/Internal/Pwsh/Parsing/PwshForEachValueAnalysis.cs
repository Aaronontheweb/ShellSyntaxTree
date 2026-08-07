// -----------------------------------------------------------------------
// <copyright file="PwshForEachValueAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

internal enum PwshIterationCardinality
{
    Never,
    OneOrMore,
    ZeroOrMore,
}

internal sealed record PwshForEachAnalysisPlan(
    string BindingName,
    IReadOnlyList<ShellValueDomain> OrderedCandidates,
    int? AuthoredVisitCount,
    PwshIterationCardinality Cardinality,
    bool RequiresFixedPoint,
    ShellValueDomain Summary);

internal static class PwshForEachValueAnalysis
{
    // PowerShell 7.x fresh-host and contextual built-ins whose assignment can
    // fail, coerce values, or mutate host behavior. The live oracle test keeps
    // the fresh-host portion synchronized as supported 7.x releases evolve.
    private static readonly HashSet<string> IneligibleBindingNames = new(
        new[]
        {
            "_", "args", "ConfirmPreference", "ConsoleFileName", "DebugPreference",
            "EnabledExperimentalFeatures", "Error", "ErrorActionPreference", "ErrorView",
            "Event", "EventArgs", "EventSubscriber", "ExecutionContext", "false",
            "foreach", "FormatEnumerationLimit", "HOME", "Host",
            "InformationPreference", "input", "IsCoreCLR", "IsLinux", "IsMacOS",
            "IsWindows", "LASTEXITCODE", "LogCommandHealthEvent",
            "LogCommandLifecycleEvent", "LogEngineHealthEvent",
            "LogEngineLifecycleEvent", "LogProviderHealthEvent",
            "LogProviderLifecycleEvent", "Matches", "MaximumAliasCount",
            "MaximumDriveCount", "MaximumErrorCount", "MaximumFunctionCount",
            "MaximumHistoryCount", "MaximumVariableCount", "MyInvocation",
            "NestedPromptLevel", "null", "OFS", "OutputEncoding", "PID", "PROFILE",
            "ProgressPreference", "PSBoundParameters", "PSCmdlet", "PSCommandPath",
            "PSCulture", "PSDebugContext", "PSDefaultParameterValues", "PSEdition",
            "PSEmailServer", "PSHOME", "PSItem", "PSModuleAutoLoadingPreference",
            "PSNativeCommandArgumentPassing", "PSNativeCommandUseErrorActionPreference",
            "PSScriptRoot", "PSSenderInfo", "PSSessionApplicationName",
            "PSSessionConfigurationName", "PSSessionOption", "PSStyle", "PSUICulture",
            "PSVersionTable", "PWD", "Sender", "ShellId", "StackTrace", "switch",
            "this", "Transcript", "true", "VerbosePreference", "WarningPreference",
            "WhatIfPreference",
        },
        StringComparer.OrdinalIgnoreCase);

    internal static bool IsEligibleBindingName(string name) =>
        !IneligibleBindingNames.Contains(name);

    internal static PwshForEachAnalysisPlan CapturePlan(
        string bindingName,
        IReadOnlyList<PwshToken> iterableTokens,
        bool isLiteralExpression)
    {
        if (iterableTokens.Count != 1)
        {
            return UnknownPlan(bindingName, PwshIterationCardinality.ZeroOrMore);
        }

        var token = iterableTokens[0];
        if (token.Kind == PwshTokenKind.QuotedString &&
            !token.HasInterpolation &&
            TryGetLiteralValue(token.ResolverValue, out var scalar))
        {
            return new PwshForEachAnalysisPlan(
                bindingName,
                new[] { Exact(scalar) },
                AuthoredVisitCount: 1,
                PwshIterationCardinality.OneOrMore,
                RequiresFixedPoint: false,
                Exact(scalar));
        }

        if (token.Kind == PwshTokenKind.Subexpression &&
            token.Value.StartsWith("@(", StringComparison.Ordinal) &&
            TryCaptureLiteralArray(
                token.Value,
                out var ordered,
                out var authoredVisitCount,
                out var arrayCardinality,
                out var requiresFixedPoint,
                out var summary))
        {
            return new PwshForEachAnalysisPlan(
                bindingName,
                ordered,
                authoredVisitCount,
                arrayCardinality,
                requiresFixedPoint,
                summary);
        }

        if (token.Kind == PwshTokenKind.Word &&
            string.Equals(token.Value, "$null", StringComparison.OrdinalIgnoreCase))
        {
            return new PwshForEachAnalysisPlan(
                bindingName,
                Array.Empty<ShellValueDomain>(),
                AuthoredVisitCount: 0,
                PwshIterationCardinality.Never,
                RequiresFixedPoint: false,
                ShellValueDomain.Unknown);
        }

        return isLiteralExpression
            ? new PwshForEachAnalysisPlan(
                bindingName,
                new[] { ShellValueDomain.Unknown },
                AuthoredVisitCount: 1,
                PwshIterationCardinality.OneOrMore,
                RequiresFixedPoint: false,
                ShellValueDomain.Unknown)
            : UnknownPlan(bindingName, PwshIterationCardinality.ZeroOrMore);
    }

    private static PwshForEachAnalysisPlan UnknownPlan(
        string bindingName,
        PwshIterationCardinality cardinality) =>
        new(
            bindingName,
            Array.Empty<ShellValueDomain>(),
            AuthoredVisitCount: null,
            cardinality,
            RequiresFixedPoint: true,
            ShellValueDomain.Unknown);

    private static bool TryCaptureLiteralArray(
        string raw,
        out IReadOnlyList<ShellValueDomain> orderedCandidates,
        out int authoredVisitCount,
        out PwshIterationCardinality cardinality,
        out bool requiresFixedPoint,
        out ShellValueDomain summary)
    {
        orderedCandidates = Array.Empty<ShellValueDomain>();
        authoredVisitCount = 0;
        cardinality = PwshIterationCardinality.Never;
        requiresFixedPoint = false;
        summary = ShellValueDomain.Unknown;
        var index = 2;
        var end = raw.Length - 1;
        SkipWhitespace(raw, ref index, end);
        if (index == end)
        {
            return true;
        }

        var values = new List<string>();
        var ordered = new List<ShellValueDomain>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var allStrings = true;
        var summaryExceeded = false;
        var count = 0;
        while (index < end)
        {
            var start = index;
            if (!TryReadElement(raw, ref index, end))
            {
                return false;
            }

            count++;
            var element = raw.Substring(start, index - start);
            if (!TryDecodeQuotedElement(element, out var value))
            {
                allStrings = false;
                if (count <= ShellAnalysisLimits.MaxValueCandidates)
                {
                    ordered.Add(ShellValueDomain.Unknown);
                }
            }
            else
            {
                if (count <= ShellAnalysisLimits.MaxValueCandidates)
                {
                    ordered.Add(Exact(value));
                }

                if (!summaryExceeded && distinct.Add(value))
                {
                    if (values.Count == ShellAnalysisLimits.MaxValueCandidates)
                    {
                        summaryExceeded = true;
                    }
                    else
                    {
                        values.Add(value);
                    }
                }
            }

            SkipWhitespace(raw, ref index, end);
            if (index == end)
            {
                break;
            }

            if (raw[index++] != ',')
            {
                return false;
            }

            SkipWhitespace(raw, ref index, end);
        }

        cardinality = count == 0
            ? PwshIterationCardinality.Never
            : PwshIterationCardinality.OneOrMore;
        authoredVisitCount = count;
        requiresFixedPoint = count > ShellAnalysisLimits.MaxValueCandidates;
        orderedCandidates = requiresFixedPoint
            ? Array.Empty<ShellValueDomain>()
            : ordered.ToArray();
        if (allStrings && !summaryExceeded)
        {
            summary = CreateFiniteDomain(values);
        }

        return true;
    }

    private static bool TryReadElement(string raw, ref int index, int end)
    {
        if (index >= end)
        {
            return false;
        }

        if (raw[index] is not ('\'' or '"'))
        {
            while (index < end && raw[index] != ',' && !char.IsWhiteSpace(raw[index]))
            {
                index++;
            }

            return true;
        }

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

    private static bool TryDecodeQuotedElement(string raw, out string value)
    {
        value = string.Empty;
        if (raw.Length < 2 || raw[0] is not ('\'' or '"'))
        {
            return false;
        }

        var tokens = PwshLexer.Tokenize(raw);
        if (tokens.Count != 1 ||
            tokens[0].Kind != PwshTokenKind.QuotedString ||
            tokens[0].HasInterpolation ||
            !TryGetLiteralValue(tokens[0].ResolverValue, out value))
        {
            value = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryGetLiteralValue(ShellValue? value, out string literal)
    {
        literal = string.Empty;
        if (value is null)
        {
            return false;
        }

        foreach (var fragment in value.Fragments)
        {
            if (fragment.Kind != ShellValueFragmentKind.Literal)
            {
                return false;
            }
        }

        literal = value.Decoded;
        return true;
    }

    private static ShellValueDomain Exact(string value) => new()
    {
        Kind = ShellValueDomainKind.Exact,
        Values = new[] { value },
    };

    private static ShellValueDomain CreateFiniteDomain(IReadOnlyList<string> values) =>
        values.Count switch
        {
            0 => ShellValueDomain.Unknown,
            1 => Exact(values[0]),
            _ => new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = Copy(values),
            },
        };

    private static string[] Copy(IReadOnlyList<string> values)
    {
        var copy = new string[values.Count];
        for (var index = 0; index < copy.Length; index++)
        {
            copy[index] = values[index];
        }

        return copy;
    }

    private static void SkipWhitespace(string raw, ref int index, int end)
    {
        while (index < end && char.IsWhiteSpace(raw[index]))
        {
            index++;
        }
    }
}

internal sealed class PwshForEachValueAnalyzer
{
    private readonly Func<SimpleCommandSyntax, CommandOccurrenceFacts> _factsFactory;
    private readonly Func<ForEachSyntax, PwshForEachAnalysisPlan?> _planFactory;
    private readonly IReadOnlyList<Clause> _incompleteClauses;
    private readonly Dictionary<Clause, CommandOccurrenceFacts> _facts =
        new(ClauseReferenceComparer.Instance);
    private readonly bool _isolatedInitialState;

    private PwshForEachValueAnalyzer(
        PwshParserOptions options,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        Func<ForEachSyntax, PwshForEachAnalysisPlan?> planFactory,
        IReadOnlyList<Clause> incompleteClauses)
    {
        _factsFactory = factsFactory;
        _planFactory = planFactory;
        _incompleteClauses = incompleteClauses;
        _isolatedInitialState = options.InitialStateMode ==
            PwshInitialStateMode.IsolatedNonInteractiveNoProfile;
    }

    internal static bool TryAnalyze(
        ShellBlockSyntax syntax,
        PwshParserOptions options,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        Func<ForEachSyntax, PwshForEachAnalysisPlan?> planFactory,
        IReadOnlyList<Clause> incompleteClauses,
        out Func<SimpleCommandSyntax, CommandOccurrenceFacts> analyzedFacts)
    {
        var analyzer = new PwshForEachValueAnalyzer(
            options,
            factsFactory,
            planFactory,
            incompleteClauses);
        analyzer.AnalyzeBlock(
            syntax,
            new AnalysisContext(
                canPromote: analyzer._isolatedInitialState,
                insideLoop: false,
                new List<BindingFrame>()));
        analyzedFacts = simple => analyzer._facts.TryGetValue(simple.Clause, out var facts)
            ? facts
            : factsFactory(simple);
        return true;
    }

    private AnalysisContext AnalyzeNode(ShellSyntaxNode node, AnalysisContext input) =>
        node switch
        {
            ShellBlockSyntax block => AnalyzeBlock(block, input),
            SimpleCommandSyntax simple => AnalyzeSimple(simple, input),
            CommandListSyntax list => AnalyzeList(list, input),
            PipelineSyntax pipeline => AnalyzePipeline(pipeline, input),
            GroupSyntax group => AnalyzeGroup(group, input),
            ForEachSyntax forEach => AnalyzeForEach(forEach, input),
            CommandSubstitutionSyntax substitution => AnalyzeSubstitution(substitution, input),
            _ => input.Invalidate(),
        };

    private AnalysisContext AnalyzeBlock(ShellBlockSyntax block, AnalysisContext input)
    {
        var current = input;
        foreach (var statement in block.Statements)
        {
            current = AnalyzeNode(statement, current);
        }

        return current;
    }

    private AnalysisContext AnalyzeSimple(SimpleCommandSyntax simple, AnalysisContext input)
    {
        foreach (var substitution in simple.Substitutions)
        {
            AnalyzeBlock(substitution.Body, input.Invalidate());
        }

        var source = _factsFactory(simple);
        var effective = CreateEffectiveArguments(source.ValueProvenance, input);
        var mayPromote = input.InsideLoop &&
            input.CanPromote &&
            source.HasCompleteValueProvenance &&
            simple.Substitutions.Count == 0 &&
            !simple.Clause.IsCommandStringWrapped;
        _facts.Add(simple.Clause, new CommandOccurrenceFacts
        {
            EffectiveArguments = effective,
            WorkingDirectory = source.WorkingDirectory,
            Redirects = source.Redirects,
            CwdPathDependencies = source.CwdPathDependencies,
            ValueProvenance = source.ValueProvenance,
            HasCompleteValueProvenance = source.HasCompleteValueProvenance,
            IsComplete = source.IsComplete &&
                (!ContainsReference(_incompleteClauses, simple.Clause) || mayPromote),
        });

        return simple.Substitutions.Count == 0 ? input : input.Invalidate();
    }

    private AnalysisContext AnalyzeList(CommandListSyntax list, AnalysisContext input)
    {
        var current = input;
        foreach (var item in list.Items)
        {
            current = AnalyzeNode(item.Command, current);
        }

        return current;
    }

    private AnalysisContext AnalyzePipeline(PipelineSyntax pipeline, AnalysisContext input)
    {
        var result = input;
        foreach (var stage in pipeline.Stages)
        {
            if (!AnalyzeNode(stage, input).CanPromote)
            {
                result = result.Invalidate();
            }
        }

        return result;
    }

    private AnalysisContext AnalyzeGroup(GroupSyntax group, AnalysisContext input)
    {
        AnalyzeBlock(group.Body, input.Invalidate());
        return group.GroupKind == ShellGroupKind.IsolatedScope
            ? input
            : input.Invalidate();
    }

    private AnalysisContext AnalyzeSubstitution(
        CommandSubstitutionSyntax substitution,
        AnalysisContext input)
    {
        AnalyzeBlock(substitution.Body, input.Invalidate());
        return input.Invalidate();
    }

    private AnalysisContext AnalyzeForEach(ForEachSyntax forEach, AnalysisContext input)
    {
        var plan = _planFactory(forEach);
        if (plan is null)
        {
            AnalyzeBlock(forEach.IteratorCommands, input.Invalidate());
            AnalyzeBlock(forEach.Body, input.Invalidate());
            return input.Invalidate();
        }

        var iteratorOutput = AnalyzeBlock(forEach.IteratorCommands, input);
        var sameName = iteratorOutput.ContainsBinding(plan.BindingName);
        var canPromote = _isolatedInitialState &&
            iteratorOutput.CanPromote &&
            !sameName &&
            PwshForEachValueAnalysis.IsEligibleBindingName(plan.BindingName);
        var domain = canPromote ? plan.Summary : ShellValueDomain.Unknown;
        var bodyInput = iteratorOutput.WithBinding(
            plan.BindingName,
            domain,
            canPromote);
        AnalyzeBlock(forEach.Body, bodyInput);

        // PowerShell foreach assignments persist in the current scope. Task
        // 7.4 owns the ordered post-loop state; until then no later command
        // receives a restored parser-frame value.
        return input.Invalidate();
    }

    private static IReadOnlyList<EffectiveArgument> CreateEffectiveArguments(
        IReadOnlyList<ShellValueElementProvenance> provenance,
        AnalysisContext context)
    {
        var effective = new List<EffectiveArgument>();
        foreach (var value in provenance)
        {
            if (context.TryAnalyzeEffectiveValue(value.Value, out var domain))
            {
                effective.Add(new EffectiveArgument
                {
                    ClauseElementIndex = value.ClauseElementIndex,
                    Value = domain,
                });
            }
        }

        return effective.ToArray();
    }

    private static bool ContainsReference(IReadOnlyList<Clause> clauses, Clause expected)
    {
        foreach (var clause in clauses)
        {
            if (ReferenceEquals(clause, expected))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class BindingFrame
    {
        internal BindingFrame(string name, ShellValueDomain domain)
        {
            Name = name;
            Domain = domain;
        }

        internal string Name { get; }

        internal ShellValueDomain Domain { get; }
    }

    private readonly struct AnalysisContext
    {
        private readonly IReadOnlyList<BindingFrame> _bindings;

        internal AnalysisContext(
            bool canPromote,
            bool insideLoop,
            IReadOnlyList<BindingFrame> bindings)
        {
            CanPromote = canPromote;
            InsideLoop = insideLoop;
            _bindings = bindings;
        }

        internal bool CanPromote { get; }

        internal bool InsideLoop { get; }

        internal AnalysisContext Invalidate()
        {
            if (_bindings.Count == 0)
            {
                return new AnalysisContext(false, InsideLoop, _bindings);
            }

            var unknown = new BindingFrame[_bindings.Count];
            for (var index = 0; index < unknown.Length; index++)
            {
                unknown[index] = new BindingFrame(
                    _bindings[index].Name,
                    ShellValueDomain.Unknown);
            }

            return new AnalysisContext(false, InsideLoop, unknown);
        }

        internal bool ContainsBinding(string name) => FindBinding(name) is not null;

        internal AnalysisContext WithBinding(
            string name,
            ShellValueDomain domain,
            bool canPromote)
        {
            var bindings = new List<BindingFrame>(_bindings.Count + 1);
            foreach (var binding in _bindings)
            {
                if (!string.Equals(binding.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    bindings.Add(binding);
                }
            }

            bindings.Add(new BindingFrame(name, domain));
            return new AnalysisContext(canPromote, insideLoop: true, bindings);
        }

        internal bool TryAnalyzeEffectiveValue(
            ShellValue value,
            out ShellValueDomain domain)
        {
            var referenced = new List<BindingFrame>();
            var unresolved = false;
            foreach (var fragment in value.Fragments)
            {
                if (fragment.Kind == ShellValueFragmentKind.Literal)
                {
                    continue;
                }

                if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                    fragment.Expansion is not ShellExpansionReference expansion ||
                    expansion.Kind != ShellExpansionKind.Variable ||
                    expansion.Name is null)
                {
                    unresolved = true;
                    continue;
                }

                var binding = FindBinding(expansion.Name);
                if (binding is null)
                {
                    unresolved = true;
                }
                else if (!ContainsReference(referenced, binding))
                {
                    referenced.Add(binding);
                }
            }

            if (referenced.Count == 0)
            {
                domain = ShellValueDomain.Unknown;
                return false;
            }

            if (unresolved)
            {
                domain = ShellValueDomain.Unknown;
                return true;
            }

            foreach (var binding in referenced)
            {
                if (binding.Domain.Kind is not (
                        ShellValueDomainKind.Exact or ShellValueDomainKind.FiniteSet))
                {
                    domain = ShellValueDomain.Unknown;
                    return true;
                }
            }

            var selected = new Dictionary<BindingFrame, string>();
            var candidates = new List<string>();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            if (!TryCompose(
                    value,
                    referenced,
                    bindingIndex: 0,
                    selected,
                    candidates,
                    distinct))
            {
                domain = ShellValueDomain.Unknown;
                return true;
            }

            domain = candidates.Count == 1
                ? new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { candidates[0] },
                }
                : new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.FiniteSet,
                    Values = candidates.ToArray(),
                };
            return true;
        }

        private bool TryCompose(
            ShellValue value,
            IReadOnlyList<BindingFrame> bindings,
            int bindingIndex,
            Dictionary<BindingFrame, string> selected,
            List<string> candidates,
            HashSet<string> distinct)
        {
            if (bindingIndex == bindings.Count)
            {
                var rendered = Render(value, selected);
                if (rendered is null)
                {
                    return false;
                }

                if (distinct.Add(rendered))
                {
                    if (distinct.Count > ShellAnalysisLimits.MaxValueCandidates)
                    {
                        return false;
                    }

                    candidates.Add(rendered);
                }

                return true;
            }

            var binding = bindings[bindingIndex];
            foreach (var candidate in binding.Domain.Values)
            {
                selected[binding] = candidate;
                if (!TryCompose(
                        value,
                        bindings,
                        bindingIndex + 1,
                        selected,
                        candidates,
                        distinct))
                {
                    return false;
                }
            }

            selected.Remove(binding);
            return true;
        }

        private string? Render(
            ShellValue value,
            IReadOnlyDictionary<BindingFrame, string> selected)
        {
            var rendered = new StringBuilder(value.Decoded.Length);
            foreach (var fragment in value.Fragments)
            {
                if (fragment.Kind == ShellValueFragmentKind.Literal)
                {
                    rendered.Append(fragment.Value);
                    continue;
                }

                if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                    fragment.Expansion is not ShellExpansionReference expansion ||
                    expansion.Kind != ShellExpansionKind.Variable ||
                    expansion.Name is null)
                {
                    return null;
                }

                var binding = FindBinding(expansion.Name);
                if (binding is null || !selected.TryGetValue(binding, out var candidate))
                {
                    return null;
                }

                rendered.Append(candidate);
            }

            return rendered.ToString();
        }

        private BindingFrame? FindBinding(string name)
        {
            for (var index = _bindings.Count - 1; index >= 0; index--)
            {
                if (string.Equals(
                        _bindings[index].Name,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return _bindings[index];
                }
            }

            return null;
        }

        private static bool ContainsReference(
            IReadOnlyList<BindingFrame> bindings,
            BindingFrame expected)
        {
            foreach (var binding in bindings)
            {
                if (ReferenceEquals(binding, expected))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class ClauseReferenceComparer : IEqualityComparer<Clause>
    {
        internal static ClauseReferenceComparer Instance { get; } = new();

        public bool Equals(Clause? x, Clause? y) => ReferenceEquals(x, y);

        public int GetHashCode(Clause obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
