// -----------------------------------------------------------------------
// <copyright file="ShellSyntaxProjection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree;

/// <summary>
/// Parser-owned analysis that is joined to a simple-command leaf while its
/// structural projections are built.
/// </summary>
internal sealed class CommandOccurrenceFacts
{
    internal IReadOnlyList<EffectiveArgument> EffectiveArguments { get; init; } =
        Array.Empty<EffectiveArgument>();

    internal ShellValueDomain WorkingDirectory { get; init; } = ShellValueDomain.Unknown;

    internal IReadOnlyList<RedirectAnalysis> Redirects { get; init; } =
        Array.Empty<RedirectAnalysis>();

    internal IReadOnlyList<CwdPathDependency> CwdPathDependencies { get; init; } =
        Array.Empty<CwdPathDependency>();

    internal IReadOnlyList<ShellValueElementProvenance> ValueProvenance { get; init; } =
        Array.Empty<ShellValueElementProvenance>();

    internal bool IsComplete { get; init; }
}

/// <summary>
/// Retains the complete shell-owned value fragments for one authored clause
/// element so execution-state analysis can re-evaluate it per visit.
/// </summary>
internal readonly record struct ShellValueElementProvenance(
    int ClauseElementIndex,
    ShellValue Value);

/// <summary>
/// Retains resolver-owned path provenance and its exact compatibility
/// coordinates for outcome-sensitive rebasing. Public compatibility DTOs do
/// not contain enough lexical or executable-specific context to reconstruct
/// these facts safely later.
/// </summary>
internal readonly record struct CwdPathDependency(
    int ClauseElementIndex,
    int? ClauseArgumentIndex,
    bool DependsOnWorkingDirectory,
    string LogicalValue,
    string AuthoredValue,
    string? ParseWorkingDirectory);

/// <summary>One successful projection of a parser-owned syntax tree.</summary>
internal sealed class ShellProjectionResult
{
    internal static ShellProjectionResult Empty { get; } = new();

    internal IReadOnlyList<CommandOccurrence> Commands { get; init; } =
        Array.Empty<CommandOccurrence>();

    internal IReadOnlyList<Clause> Clauses { get; init; } = Array.Empty<Clause>();
}

/// <summary>
/// Owns executable-command discovery so security consumers never need to
/// recursively match syntax-node types.
/// </summary>
internal static class ShellSyntaxProjection
{
    internal static bool TryProject(
        ShellBlockSyntax syntax,
        out ShellProjectionResult result) =>
        TryProject(syntax, _ => new CommandOccurrenceFacts(), out result);

    internal static bool TryProject(
        ShellBlockSyntax syntax,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        out ShellProjectionResult result)
    {
        if (syntax is null || factsFactory is null)
        {
            result = ShellProjectionResult.Empty;
            return false;
        }

        var walker = new ProjectionWalker(factsFactory);
        return walker.TryProject(syntax, out result);
    }

    private sealed class ProjectionWalker
    {
        private readonly Func<SimpleCommandSyntax, CommandOccurrenceFacts> _factsFactory;
        private readonly List<CommandAncestryFrame> _ancestry = new();
        private readonly List<CommandOccurrence> _commands = new();
        private readonly List<Clause> _clauses = new();
        private readonly HashSet<ShellSyntaxNode> _visitedNodes =
            new(NodeReferenceComparer.Instance);
        private readonly HashSet<Clause> _visitedClauses =
            new(ClauseReferenceComparer.Instance);

        internal ProjectionWalker(
            Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory)
        {
            _factsFactory = factsFactory;
        }

        internal bool TryProject(
            ShellBlockSyntax syntax,
            out ShellProjectionResult result)
        {
            if (!TryVisit(
                    syntax,
                    CommandOccurrenceRole.Ordinary,
                    structuralDepth: 0,
                    isRoot: true))
            {
                result = ShellProjectionResult.Empty;
                return false;
            }

            result = new ShellProjectionResult
            {
                Commands = _commands.ToArray(),
                Clauses = _clauses.ToArray(),
            };
            return true;
        }

        private bool TryVisit(
            ShellSyntaxNode node,
            CommandOccurrenceRole role,
            int structuralDepth,
            bool isRoot = false,
            int? substitutionChildIndex = null)
        {
            if (node is null ||
                !IsValidSpan(node.SourceStart, node.SourceLength) ||
                !_visitedNodes.Add(node))
            {
                return false;
            }

            var nextDepth = structuralDepth + (CountsTowardStructuralDepth(node) ? 1 : 0);
            if (nextDepth > ShellAnalysisLimits.MaxStructuralNesting)
            {
                return false;
            }

            var succeeded = node switch
            {
                ShellBlockSyntax block => TryVisitBlock(block, role, nextDepth, isRoot),
                SimpleCommandSyntax simple => TryVisitSimple(simple, role, nextDepth),
                PipelineSyntax pipeline => TryVisitPipeline(pipeline, nextDepth),
                CommandListSyntax list => TryVisitCommandList(list, role, nextDepth),
                GroupSyntax group =>
                    group.GroupKind is ShellGroupKind.CurrentScope or ShellGroupKind.IsolatedScope &&
                    group.Body is not null &&
                    TryVisitChild(
                        group,
                        group.Body,
                        CommandAncestryRegion.GroupBody,
                        childIndex: null,
                        role,
                        nextDepth),
                ForEachSyntax forEach => TryVisitForEach(forEach, nextDepth),
                ConditionLoopSyntax loop =>
                    loop.LoopKind is ConditionLoopKind.While or ConditionLoopKind.Until &&
                    TryVisitConditionLoop(loop, nextDepth),
                ConditionalSyntax conditional => TryVisitConditional(conditional, role, nextDepth),
                ConditionalBranchSyntax branch => TryVisitConditionalBranch(branch, nextDepth),
                CommandSubstitutionSyntax substitution =>
                    TryVisitCommandSubstitution(
                        substitution,
                        substitutionChildIndex,
                        nextDepth),
                _ => false,
            };

            return succeeded;
        }

        private bool TryVisitBlock(
            ShellBlockSyntax block,
            CommandOccurrenceRole role,
            int structuralDepth,
            bool isRoot)
        {
            if (block.Statements is null)
            {
                return false;
            }

            var region = isRoot
                ? CommandAncestryRegion.Root
                : CommandAncestryRegion.Statement;
            for (var index = 0; index < block.Statements.Count; index++)
            {
                var statement = block.Statements[index];
                if (statement is null ||
                    !TryVisitChild(
                        block,
                        statement,
                        region,
                        index,
                        role,
                        structuralDepth))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryVisitSimple(
            SimpleCommandSyntax simple,
            CommandOccurrenceRole role,
            int structuralDepth)
        {
            if (simple.Substitutions is null)
            {
                return false;
            }

            for (var index = 0; index < simple.Substitutions.Count; index++)
            {
                var substitution = simple.Substitutions[index];
                if (substitution is null ||
                    !TryVisit(
                        substitution,
                        CommandOccurrenceRole.Substitution,
                        structuralDepth,
                        substitutionChildIndex: index))
                {
                    return false;
                }
            }

            if (simple.Clause is null ||
                !IsValidClauseShape(simple.Clause) ||
                !_visitedClauses.Add(simple.Clause))
            {
                return false;
            }

            var facts = _factsFactory(simple);
            if (!TryCopyFacts(
                    simple.Clause,
                    facts,
                    out var effectiveArguments,
                    out var workingDirectory,
                    out var redirects))
            {
                return false;
            }

            _commands.Add(new CommandOccurrence
            {
                Clause = simple.Clause,
                ImmediateRole = role,
                Ancestry = _ancestry.ToArray(),
                EffectiveArguments = effectiveArguments,
                WorkingDirectory = workingDirectory,
                Redirects = redirects,
                IsComplete = facts.IsComplete,
            });
            _clauses.Add(simple.Clause);
            return true;
        }

        private bool TryVisitCommandSubstitution(
            CommandSubstitutionSyntax substitution,
            int? childIndex,
            int structuralDepth) =>
            substitution.Body is not null &&
            TryVisitChild(
                substitution,
                substitution.Body,
                CommandAncestryRegion.Substitution,
                childIndex,
                CommandOccurrenceRole.Substitution,
                structuralDepth);

        private bool TryVisitPipeline(
            PipelineSyntax pipeline,
            int structuralDepth)
        {
            if (pipeline.Stages is null || pipeline.Stages.Count == 0)
            {
                return false;
            }

            for (var index = 0; index < pipeline.Stages.Count; index++)
            {
                var stage = pipeline.Stages[index];
                if (stage is null ||
                    !TryVisitChild(
                        pipeline,
                        stage,
                        CommandAncestryRegion.PipelineStage,
                        index,
                        CommandOccurrenceRole.PipelineStage,
                        structuralDepth))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryVisitCommandList(
            CommandListSyntax list,
            CommandOccurrenceRole role,
            int structuralDepth)
        {
            if (list.Items is null || list.Items.Count == 0)
            {
                return false;
            }

            for (var index = 0; index < list.Items.Count; index++)
            {
                var item = list.Items[index];
                if (item is null ||
                    item.Command is null ||
                    !Enum.IsDefined(typeof(CompoundOperator), item.Operator) ||
                    !TryVisitChild(
                        list,
                        item.Command,
                        CommandAncestryRegion.Statement,
                        index,
                        role,
                        structuralDepth))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryVisitForEach(
            ForEachSyntax forEach,
            int structuralDepth)
        {
            if (forEach.Binding is null ||
                string.IsNullOrEmpty(forEach.Binding.Name) ||
                forEach.Binding.Source is null ||
                !IsValidSourceFragment(forEach.Binding.Source) ||
                forEach.Iterable is null ||
                !IsValidSourceFragment(forEach.Iterable) ||
                forEach.IteratorCommands is null ||
                forEach.Body is null)
            {
                return false;
            }

            return TryVisitChild(
                       forEach,
                       forEach.IteratorCommands,
                       CommandAncestryRegion.Iterator,
                       childIndex: null,
                       CommandOccurrenceRole.Iterator,
                       structuralDepth) &&
                   TryVisitChild(
                       forEach,
                       forEach.Body,
                       CommandAncestryRegion.LoopBody,
                       childIndex: null,
                       CommandOccurrenceRole.LoopBody,
                       structuralDepth);
        }

        private bool TryVisitConditionLoop(
            ConditionLoopSyntax loop,
            int structuralDepth)
        {
            if (loop.Condition is null || loop.Body is null)
            {
                return false;
            }

            return TryVisitChild(
                       loop,
                       loop.Condition,
                       CommandAncestryRegion.Condition,
                       childIndex: null,
                       CommandOccurrenceRole.Condition,
                       structuralDepth) &&
                   TryVisitChild(
                       loop,
                       loop.Body,
                       CommandAncestryRegion.LoopBody,
                       childIndex: null,
                       CommandOccurrenceRole.LoopBody,
                       structuralDepth);
        }

        private bool TryVisitConditional(
            ConditionalSyntax conditional,
            CommandOccurrenceRole role,
            int structuralDepth)
        {
            if (conditional.Branches is null || conditional.Branches.Count == 0)
            {
                return false;
            }

            for (var index = 0; index < conditional.Branches.Count; index++)
            {
                var branch = conditional.Branches[index];
                if (branch is null ||
                    !TryVisitChild(
                        conditional,
                        branch,
                        CommandAncestryRegion.Branch,
                        index,
                        role,
                        structuralDepth))
                {
                    return false;
                }
            }

            return conditional.Else is null ||
                   TryVisitChild(
                       conditional,
                       conditional.Else,
                       CommandAncestryRegion.Branch,
                       conditional.Branches.Count,
                       CommandOccurrenceRole.Branch,
                       structuralDepth);
        }

        private bool TryVisitConditionalBranch(
            ConditionalBranchSyntax branch,
            int structuralDepth)
        {
            if (branch.Condition is null || branch.Body is null)
            {
                return false;
            }

            return TryVisitChild(
                       branch,
                       branch.Condition,
                       CommandAncestryRegion.Condition,
                       childIndex: null,
                       CommandOccurrenceRole.Condition,
                       structuralDepth) &&
                   TryVisitChild(
                       branch,
                       branch.Body,
                       CommandAncestryRegion.Branch,
                       childIndex: null,
                       CommandOccurrenceRole.Branch,
                       structuralDepth);
        }

        private bool TryVisitChild(
            ShellSyntaxNode ancestor,
            ShellSyntaxNode child,
            CommandAncestryRegion region,
            int? childIndex,
            CommandOccurrenceRole role,
            int structuralDepth)
        {
            _ancestry.Add(new CommandAncestryFrame
            {
                AncestorKind = ancestor.Kind,
                Region = region,
                ChildIndex = childIndex,
                SourceStart = ancestor.SourceStart,
                SourceLength = ancestor.SourceLength,
            });
            var succeeded = TryVisit(
                child,
                role,
                structuralDepth,
                substitutionChildIndex: child is CommandSubstitutionSyntax
                    ? childIndex
                    : null);
            _ancestry.RemoveAt(_ancestry.Count - 1);
            return succeeded;
        }

        private static bool CountsTowardStructuralDepth(ShellSyntaxNode node) =>
            node is ForEachSyntax or
                ConditionLoopSyntax or
                ConditionalSyntax or
                GroupSyntax or
                CommandSubstitutionSyntax;

        private static bool TryCopyFacts(
            Clause clause,
            CommandOccurrenceFacts facts,
            out IReadOnlyList<EffectiveArgument> effectiveArguments,
            out ShellValueDomain workingDirectory,
            out IReadOnlyList<RedirectAnalysis> redirects)
        {
            effectiveArguments = Array.Empty<EffectiveArgument>();
            workingDirectory = ShellValueDomain.Unknown;
            redirects = Array.Empty<RedirectAnalysis>();
            if (facts is null ||
                facts.EffectiveArguments is null ||
                facts.WorkingDirectory is null ||
                facts.Redirects is null ||
                ContainsNull(facts.EffectiveArguments) ||
                ContainsNull(facts.Redirects) ||
                !IsValidValueDomain(facts.WorkingDirectory) ||
                facts.WorkingDirectory.Kind is not (
                    ShellValueDomainKind.Unknown or ShellValueDomainKind.Exact) ||
                !AreValidEffectiveArguments(clause, facts.EffectiveArguments) ||
                !AreValidRedirects(clause, facts.Redirects, facts.IsComplete) ||
                facts.IsComplete &&
                (clause.Verb.IsDynamic || clause.Verb.Tokens.Count == 0))
            {
                return false;
            }

            effectiveArguments = Copy(facts.EffectiveArguments);
            workingDirectory = facts.WorkingDirectory;
            redirects = Copy(facts.Redirects);
            return true;
        }

        private static bool IsValidClauseShape(Clause clause) =>
            Enum.IsDefined(typeof(CompoundOperator), clause.Operator) &&
            clause.Verb is not null &&
            clause.Verb.Tokens is not null &&
            clause.Args is not null &&
            clause.Redirects is not null &&
            clause.Elements is not null &&
            !ContainsNull(clause.Verb.Tokens) &&
            !ContainsNull(clause.Args) &&
            !ContainsNull(clause.Redirects) &&
            !ContainsNull(clause.Elements);

        private static bool AreValidEffectiveArguments(
            Clause clause,
            IReadOnlyList<EffectiveArgument> arguments)
        {
            var coordinates = new HashSet<int>();
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                if (argument.ClauseElementIndex < 0 ||
                    argument.ClauseElementIndex >= clause.Elements.Count ||
                    clause.Elements[argument.ClauseElementIndex].Role != ClauseElementRole.Argument ||
                    !coordinates.Add(argument.ClauseElementIndex) ||
                    argument.Value is null ||
                    !IsValidValueDomain(argument.Value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreValidRedirects(
            Clause clause,
            IReadOnlyList<RedirectAnalysis> redirects,
            bool occurrenceIsComplete)
        {
            if (occurrenceIsComplete && redirects.Count != clause.Redirects.Count)
            {
                return false;
            }

            var coordinates = new HashSet<int>();
            for (var index = 0; index < redirects.Count; index++)
            {
                var redirect = redirects[index];
                if (redirect.RedirectIndex < 0 ||
                    redirect.RedirectIndex >= clause.Redirects.Count ||
                    !coordinates.Add(redirect.RedirectIndex) ||
                    occurrenceIsComplete && !redirect.IsComplete ||
                    !IsValidRedirect(redirect))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidRedirect(RedirectAnalysis redirect)
        {
            if (redirect.Source is null ||
                redirect.Target is null ||
                !IsValidRedirectSource(redirect.Source) ||
                !IsValidValueDomain(redirect.Target))
            {
                return false;
            }

            if (redirect.IsComplete && redirect.Source.Kind == RedirectSourceKind.Unknown)
            {
                return false;
            }

            return redirect.Operation switch
            {
                RedirectOperation.Unknown =>
                    !redirect.IsComplete &&
                    redirect.TargetDescriptor is null &&
                    redirect.HereDocument is null &&
                    !redirect.IsPathRelevant &&
                    redirect.Target.Kind == ShellValueDomainKind.Unknown,
                RedirectOperation.FileInput or
                    RedirectOperation.FileOutput or
                    RedirectOperation.FileAppend or
                    RedirectOperation.CombinedOutput or
                    RedirectOperation.CombinedOutputAppend =>
                    redirect.TargetDescriptor is null &&
                    redirect.HereDocument is null &&
                    redirect.IsPathRelevant,
                RedirectOperation.DescriptorDuplicate or RedirectOperation.DescriptorMove =>
                    (!redirect.IsComplete || redirect.TargetDescriptor >= 0) &&
                    (redirect.TargetDescriptor is null || redirect.TargetDescriptor >= 0) &&
                    redirect.HereDocument is null &&
                    !redirect.IsPathRelevant &&
                    redirect.Target.Kind == ShellValueDomainKind.Unknown,
                RedirectOperation.DescriptorClose =>
                    redirect.TargetDescriptor is null &&
                    redirect.HereDocument is null &&
                    !redirect.IsPathRelevant &&
                    redirect.Target.Kind == ShellValueDomainKind.Unknown,
                RedirectOperation.HereDocument =>
                    redirect.TargetDescriptor is null &&
                    redirect.HereDocument is not null &&
                    !redirect.IsPathRelevant &&
                    redirect.Target.Kind == ShellValueDomainKind.Unknown &&
                    IsValidHereDocument(redirect.HereDocument, redirect.IsComplete),
                RedirectOperation.HereString =>
                    redirect.TargetDescriptor is null &&
                    redirect.HereDocument is null &&
                    !redirect.IsPathRelevant,
                _ => false,
            };
        }

        private static bool IsValidRedirectSource(RedirectSource source) =>
            source.Kind switch
            {
                RedirectSourceKind.Unknown => source.Descriptor is null,
                RedirectSourceKind.Default => source.Descriptor is null,
                RedirectSourceKind.Descriptor => source.Descriptor >= 0,
                RedirectSourceKind.PowerShellAllStreams => source.Descriptor is null,
                _ => false,
            };

        private static bool IsValidHereDocument(
            HereDocumentAnalysis hereDocument,
            bool redirectIsComplete) =>
            hereDocument.Delimiter is not null &&
            hereDocument.Body is not null &&
            IsValidSourceFragment(hereDocument.Delimiter) &&
            IsValidSourceFragment(hereDocument.Body) &&
            hereDocument.ExpansionMode is
                HereDocumentExpansionMode.Unknown or
                HereDocumentExpansionMode.Literal or
                HereDocumentExpansionMode.Expand &&
            (!hereDocument.IsComplete ||
             hereDocument.ExpansionMode is
                 HereDocumentExpansionMode.Literal or HereDocumentExpansionMode.Expand) &&
            (!redirectIsComplete || hereDocument.IsComplete);

        private static bool IsValidSourceFragment(ShellSourceFragment fragment) =>
            fragment.Raw is not null &&
            IsValidSpan(fragment.SourceStart, fragment.SourceLength);

        private static bool IsValidSpan(int? sourceStart, int? sourceLength) =>
            sourceStart.HasValue == sourceLength.HasValue &&
            (!sourceStart.HasValue || sourceStart >= 0 && sourceLength >= 0);

        private static bool IsValidValueDomain(ShellValueDomain domain)
        {
            if (domain.Values is null || ContainsNull(domain.Values))
            {
                return false;
            }

            return domain.Kind switch
            {
                ShellValueDomainKind.Unknown =>
                    domain.Values.Count == 0 &&
                    domain.Pattern is null &&
                    domain.CoveringDirectory is null,
                ShellValueDomainKind.Exact =>
                    domain.Values.Count == 1 &&
                    domain.Pattern is null &&
                    domain.CoveringDirectory is null,
                ShellValueDomainKind.FiniteSet =>
                    domain.Values.Count >= 2 &&
                    domain.Values.Count <= ShellAnalysisLimits.MaxValueCandidates &&
                    AreDistinct(domain.Values) &&
                    domain.Pattern is null &&
                    domain.CoveringDirectory is null,
                ShellValueDomainKind.Pattern =>
                    domain.Values.Count == 0 &&
                    !string.IsNullOrEmpty(domain.Pattern) &&
                    !string.IsNullOrEmpty(domain.CoveringDirectory),
                _ => false,
            };
        }

        private static bool AreDistinct(IReadOnlyList<string> values)
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Count; index++)
            {
                if (!distinct.Add(values[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static T[] Copy<T>(IReadOnlyList<T> items)
        {
            var copy = new T[items.Count];
            for (var index = 0; index < items.Count; index++)
            {
                copy[index] = items[index];
            }

            return copy;
        }

        private static bool ContainsNull<T>(IReadOnlyList<T> items)
            where T : class
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] is null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class NodeReferenceComparer : IEqualityComparer<ShellSyntaxNode>
    {
        internal static NodeReferenceComparer Instance { get; } = new();

        public bool Equals(ShellSyntaxNode? x, ShellSyntaxNode? y) =>
            ReferenceEquals(x, y);

        public int GetHashCode(ShellSyntaxNode obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class ClauseReferenceComparer : IEqualityComparer<Clause>
    {
        internal static ClauseReferenceComparer Instance { get; } = new();

        public bool Equals(Clause? x, Clause? y) => ReferenceEquals(x, y);

        public int GetHashCode(Clause obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
