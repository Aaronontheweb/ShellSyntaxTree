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
    internal IReadOnlyList<EffectiveArgumentFacts> EffectiveArguments { get; init; } =
        Array.Empty<EffectiveArgumentFacts>();

    internal ShellValueDomainFacts WorkingDirectory { get; init; } = ShellValueDomainFacts.Unknown;

    internal IReadOnlyList<RedirectAnalysisFacts> Redirects { get; init; } =
        Array.Empty<RedirectAnalysisFacts>();

    internal IReadOnlyList<RedirectTargetProvenance> RedirectTargetProvenance { get; init; } =
        Array.Empty<RedirectTargetProvenance>();

    internal IReadOnlyList<CwdPathDependency> CwdPathDependencies { get; init; } =
        Array.Empty<CwdPathDependency>();

    internal IReadOnlyList<ShellValueElementProvenance> ValueProvenance { get; init; } =
        Array.Empty<ShellValueElementProvenance>();

    internal bool HasCompleteValueProvenance { get; init; }

    internal bool IsComplete { get; init; }
}

internal sealed record EffectiveArgumentFacts
{
    internal int ClauseElementIndex { get; init; } = -1;

    internal ShellValueDomainFacts Value { get; init; } = ShellValueDomainFacts.Unknown;
}

internal sealed record ShellValueDomainFacts
{
    internal static ShellValueDomainFacts Unknown { get; } = new();

    internal ShellValueDomainKind Kind { get; init; }

    internal IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();

    internal string? Pattern { get; init; }

    internal string? CoveringDirectory { get; init; }
}

/// <summary>
/// Retains the complete shell-owned value fragments for one authored clause
/// element so execution-state analysis can re-evaluate it per visit.
/// </summary>
internal readonly record struct ShellValueElementProvenance(
    int ClauseElementIndex,
    ShellValue Value,
    bool? UsesNativeArgumentBinding = null);

/// <summary>
/// Retains the shell-owned value fragments for one file-redirect target so
/// bounded state analysis can re-evaluate it without publishing it as an
/// effective command argument.
/// </summary>
internal readonly record struct RedirectTargetProvenance(
    int RedirectIndex,
    int ClauseElementIndex,
    ShellValue Value,
    int InvocationScopeDepth);

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
        private bool _structuralContextIsComplete = true;
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
            int? nestedCollectionChildIndex = null,
            bool isAttachedExecutionRegion = false)
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
                CommandSubstitutionSyntax substitution =>
                    TryVisitCommandSubstitution(
                        substitution,
                        nestedCollectionChildIndex,
                        nextDepth),
                ExecutionRegionSyntax executionRegion =>
                    TryVisitExecutionRegion(
                        executionRegion,
                        nestedCollectionChildIndex,
                        nextDepth,
                        isAttachedExecutionRegion),
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
            if (simple.Substitutions is null ||
                simple.ExecutionRegions is null ||
                simple.Clause is null ||
                !IsValidClauseShape(simple.Clause) ||
                !AreValidAttachedExecutionRegions(simple.Clause, simple.ExecutionRegions))
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
                        nestedCollectionChildIndex: index))
                {
                    return false;
                }
            }

            if (!_visitedClauses.Add(simple.Clause))
            {
                return false;
            }

            var facts = _factsFactory(simple);
            if (!TryCopyFacts(
                    simple.Clause,
                    facts,
                    out var arguments,
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
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Redirects = redirects,
                IsComplete = facts.IsComplete && _structuralContextIsComplete &&
                    AreExecutionRegionFactsComplete(simple.ExecutionRegions),
            });
            _clauses.Add(simple.Clause);

            for (var index = 0; index < simple.ExecutionRegions.Count; index++)
            {
                if (!TryVisit(
                        simple.ExecutionRegions[index],
                        CommandOccurrenceRole.ExecutionRegion,
                        structuralDepth,
                        nestedCollectionChildIndex: index,
                        isAttachedExecutionRegion: true))
                {
                    return false;
                }
            }

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

        private bool TryVisitExecutionRegion(
            ExecutionRegionSyntax executionRegion,
            int? childIndex,
            int structuralDepth,
            bool isAttachedToSimple)
        {
            if (!IsValidExecutionRegion(executionRegion, isAttachedToSimple))
            {
                return false;
            }

            var priorCompleteness = _structuralContextIsComplete;
            _structuralContextIsComplete &= IsExecutionRegionFactComplete(executionRegion);
            var succeeded = TryVisitChild(
                executionRegion,
                executionRegion.Body,
                CommandAncestryRegion.ExecutionRegion,
                childIndex,
                CommandOccurrenceRole.ExecutionRegion,
                structuralDepth);
            _structuralContextIsComplete = priorCompleteness;
            return succeeded;
        }

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
                Ancestor = ancestor,
                Region = region,
                ChildIndex = childIndex,
            });
            var succeeded = TryVisit(
                child,
                role,
                structuralDepth,
                nestedCollectionChildIndex: child is CommandSubstitutionSyntax or ExecutionRegionSyntax
                    ? childIndex
                    : null);
            _ancestry.RemoveAt(_ancestry.Count - 1);
            return succeeded;
        }

        private static bool CountsTowardStructuralDepth(ShellSyntaxNode node) =>
            node is ForEachSyntax or
                GroupSyntax or
                CommandSubstitutionSyntax or
                ExecutionRegionSyntax;

        private static bool AreValidAttachedExecutionRegions(
            Clause clause,
            IReadOnlyList<ExecutionRegionSyntax> executionRegions)
        {
            var hostCoordinates = new HashSet<int>();
            for (var index = 0; index < executionRegions.Count; index++)
            {
                var region = executionRegions[index];
                if (region is null ||
                    !IsValidExecutionRegion(region, isAttachedToSimple: true) ||
                    region.HostArgument is null ||
                    region.HostClauseElementIndex >= clause.Elements.Count ||
                    !hostCoordinates.Add(region.HostClauseElementIndex!.Value) ||
                    !ReferenceEquals(
                        region.HostArgument,
                        clause.Elements[region.HostClauseElementIndex.Value]) ||
                    region.HostArgument.Role !=
                        ClauseElementRole.Argument ||
                    region.HostArgument.Kind !=
                        ArgKind.DynamicSkip)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidExecutionRegion(
            ExecutionRegionSyntax executionRegion,
            bool isAttachedToSimple) =>
            executionRegion.Body is not null &&
            Enum.IsDefined(typeof(ExecutionRegionOrigin), executionRegion.Origin) &&
            Enum.IsDefined(typeof(ExecutionRegionPhase), executionRegion.Phase) &&
            Enum.IsDefined(typeof(ExecutionRegionTiming), executionRegion.Timing) &&
            Enum.IsDefined(
                typeof(ExecutionRegionCardinality),
                executionRegion.Cardinality) &&
            executionRegion.Origin switch
            {
                ExecutionRegionOrigin.DirectCall or ExecutionRegionOrigin.DotSource =>
                    !isAttachedToSimple &&
                    executionRegion.HostArgument is null &&
                    executionRegion.HostClauseElementIndex is null,
                ExecutionRegionOrigin.CommandArgument =>
                    isAttachedToSimple &&
                    executionRegion.HostArgument is not null &&
                    executionRegion.HostClauseElementIndex >= 0,
                _ => false,
            };

        private static bool AreExecutionRegionFactsComplete(
            IReadOnlyList<ExecutionRegionSyntax> executionRegions)
        {
            for (var index = 0; index < executionRegions.Count; index++)
            {
                if (!IsExecutionRegionFactComplete(executionRegions[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsExecutionRegionFactComplete(
            ExecutionRegionSyntax executionRegion) =>
            executionRegion.Phase != ExecutionRegionPhase.Unknown &&
            executionRegion.Timing != ExecutionRegionTiming.Unknown &&
            executionRegion.Cardinality != ExecutionRegionCardinality.Unknown;

        private static bool TryCopyFacts(
            Clause clause,
            CommandOccurrenceFacts facts,
            out IReadOnlyList<AnalyzedArgument> arguments,
            out ShellValueDomain workingDirectory,
            out IReadOnlyList<RedirectAnalysis> redirects)
        {
            arguments = Array.Empty<AnalyzedArgument>();
            workingDirectory = new ShellValueDomain.Unknown();
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

            workingDirectory = ToPublicDomain(facts.WorkingDirectory);
            return TryCreateAnalyzedArguments(
                    clause,
                    facts.EffectiveArguments,
                    out arguments) &&
                TryCreateRedirectAnalyses(clause, facts.Redirects, out redirects);
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

        private static bool TryCreateAnalyzedArguments(
            Clause clause,
            IReadOnlyList<EffectiveArgumentFacts> effective,
            out IReadOnlyList<AnalyzedArgument> arguments)
        {
            arguments = Array.Empty<AnalyzedArgument>();
            var authoredArguments = new List<Arg>();
            for (var index = 0; index < clause.Args.Count; index++)
            {
                if (!clause.Args[index].IsCwdAttribution)
                {
                    authoredArguments.Add(clause.Args[index]);
                }
            }

            var elementIndices = new List<int>();
            for (var index = 0; index < clause.Elements.Count; index++)
            {
                if (clause.Elements[index].Role == ClauseElementRole.Argument)
                {
                    elementIndices.Add(index);
                }
            }

            if (authoredArguments.Count == 0)
            {
                return elementIndices.Count == 0;
            }

            var domains = new Dictionary<int, ShellValueDomainFacts>();
            for (var index = 0; index < effective.Count; index++)
            {
                domains.Add(effective[index].ClauseElementIndex, effective[index].Value);
            }

            var projected = new List<AnalyzedArgument>(authoredArguments.Count);
            var argumentIndex = 0;
            for (var elementOffset = 0; elementOffset < elementIndices.Count; elementOffset++)
            {
                if (argumentIndex >= authoredArguments.Count)
                {
                    return false;
                }

                var elementIndex = elementIndices[elementOffset];
                var element = clause.Elements[elementIndex];
                var remainingArguments = authoredArguments.Count - argumentIndex;
                var remainingElements = elementIndices.Count - elementOffset;
                var count = remainingArguments > remainingElements &&
                            argumentIndex + 1 < authoredArguments.Count &&
                            IsInlineArgumentPair(
                                element,
                                authoredArguments[argumentIndex],
                                authoredArguments[argumentIndex + 1])
                    ? 2
                    : 1;

                var effectiveOffset = count == 1 ||
                                      !authoredArguments[argumentIndex].IsFlag
                    ? 0
                    : 1;
                for (var offset = 0; offset < count; offset++)
                {
                    var argument = authoredArguments[argumentIndex + offset];
                    var hasEffectiveValue = domains.TryGetValue(
                                                elementIndex,
                                                out var domain) &&
                                            offset == effectiveOffset;
                    var value = hasEffectiveValue
                        ? ToPublicDomain(domain!)
                        : DefaultArgumentDomain(argument, element, count == 1);
                    projected.Add(new AnalyzedArgument
                    {
                        Argument = argument,
                        Element = element,
                        Value = value,
                        HasEffectiveValue = hasEffectiveValue,
                    });
                }

                argumentIndex += count;
            }

            if (argumentIndex != authoredArguments.Count)
            {
                return false;
            }

            arguments = projected;
            return true;
        }

        private static bool IsInlineArgumentPair(
            ClauseElement element,
            Arg first,
            Arg second) =>
            IsInlineArgumentPair(element.Value, first.Raw, second.Raw) ||
            IsInlineArgumentPair(element.Raw, first.Raw, second.Raw);

        private static bool IsInlineArgumentPair(
            string combined,
            string first,
            string second) =>
            string.Equals(combined, first + "=" + second, StringComparison.Ordinal) ||
            string.Equals(combined, first + ":" + second, StringComparison.Ordinal);

        private static ShellValueDomain DefaultArgumentDomain(
            Arg argument,
            ClauseElement element,
            bool isOnlyArgumentForElement) =>
            argument.Kind is ArgKind.DynamicSkip or ArgKind.EnvVar or ArgKind.Glob
                ? new ShellValueDomain.Unknown()
                : new ShellValueDomain.Exact(
                    isOnlyArgumentForElement ? element.Value : argument.Raw);

        private static bool TryCreateRedirectAnalyses(
            Clause clause,
            IReadOnlyList<RedirectAnalysisFacts> facts,
            out IReadOnlyList<RedirectAnalysis> redirects)
        {
            redirects = Array.Empty<RedirectAnalysis>();
            if (facts.Count != clause.Redirects.Count)
            {
                return false;
            }

            var projected = new RedirectAnalysis[facts.Count];
            for (var index = 0; index < facts.Count; index++)
            {
                var fact = facts[index];
                if (fact.RedirectIndex < 0 ||
                    fact.RedirectIndex >= projected.Length ||
                    projected[fact.RedirectIndex] is not null ||
                    !TryCreateRedirectAnalysis(
                        clause.Redirects[fact.RedirectIndex],
                        fact,
                        out projected[fact.RedirectIndex]))
                {
                    return false;
                }
            }

            redirects = projected;
            return true;
        }

        private static bool TryCreateRedirectAnalysis(
            Redirect authored,
            RedirectAnalysisFacts fact,
            out RedirectAnalysis analysis)
        {
            var source = ToPublicSource(fact.Source);
            analysis = source is RedirectSource.Unknown
                ? new UnresolvedRedirectAnalysis()
                : fact.Operation switch
            {
                RedirectOperation.FileInput => new FileRedirectAnalysis(FileRedirectMode.Input)
                {
                    Target = ToPublicDomain(fact.Target),
                },
                RedirectOperation.FileOutput => new FileRedirectAnalysis(FileRedirectMode.Output)
                {
                    Target = ToPublicDomain(fact.Target),
                },
                RedirectOperation.FileAppend => new FileRedirectAnalysis(FileRedirectMode.Append)
                {
                    Target = ToPublicDomain(fact.Target),
                },
                RedirectOperation.CombinedOutput =>
                    new FileRedirectAnalysis(FileRedirectMode.CombinedOutput)
                    {
                        Target = ToPublicDomain(fact.Target),
                    },
                RedirectOperation.CombinedOutputAppend =>
                    new FileRedirectAnalysis(FileRedirectMode.CombinedOutputAppend)
                    {
                        Target = ToPublicDomain(fact.Target),
                    },
                RedirectOperation.DescriptorDuplicate when fact.TargetDescriptor.HasValue =>
                    new DescriptorDuplicateRedirectAnalysis
                    {
                        TargetDescriptor = fact.TargetDescriptor.Value,
                    },
                RedirectOperation.DescriptorMove when fact.TargetDescriptor.HasValue =>
                    new DescriptorMoveRedirectAnalysis
                    {
                        TargetDescriptor = fact.TargetDescriptor.Value,
                    },
                RedirectOperation.DescriptorClose => new DescriptorCloseRedirectAnalysis(),
                RedirectOperation.HereDocument when fact.HereDocument is not null =>
                    new HereDocumentRedirectAnalysis { Document = fact.HereDocument },
                RedirectOperation.HereString => new HereStringRedirectAnalysis
                {
                    Data = ToPublicDomain(fact.Target),
                },
                _ => new UnresolvedRedirectAnalysis(),
            };
            analysis = analysis with
            {
                RedirectIndex = fact.RedirectIndex,
                Authored = authored,
                Source = source,
                IsComplete = fact.IsComplete,
            };
            return IsValidSourceOperationPair(analysis);
        }

        private static RedirectSource ToPublicSource(RedirectSourceFacts source) =>
            source.Kind switch
            {
                RedirectSourceKind.Default => new RedirectSource.Default(),
                RedirectSourceKind.Descriptor when source.Descriptor.HasValue =>
                    new RedirectSource.Descriptor(source.Descriptor.Value),
                RedirectSourceKind.PowerShellAllStreams =>
                    new RedirectSource.PowerShellAllStreams(),
                _ => new RedirectSource.Unknown(),
            };

        private static bool IsValidSourceOperationPair(RedirectAnalysis analysis) =>
            (analysis.Source, analysis) switch
            {
                (RedirectSource.Unknown, UnresolvedRedirectAnalysis) =>
                    !analysis.IsComplete,
                (RedirectSource.Default, FileRedirectAnalysis) => true,
                (RedirectSource.Default, DescriptorDuplicateRedirectAnalysis) => true,
                (RedirectSource.Default, DescriptorMoveRedirectAnalysis) => true,
                (RedirectSource.Default, DescriptorCloseRedirectAnalysis) => true,
                (RedirectSource.Default, HereDocumentRedirectAnalysis) => true,
                (RedirectSource.Default, HereStringRedirectAnalysis) => true,
                (RedirectSource.Descriptor, FileRedirectAnalysis
                    { Mode: FileRedirectMode.Input or
                            FileRedirectMode.Output or
                            FileRedirectMode.Append }) => true,
                (RedirectSource.Descriptor, DescriptorDuplicateRedirectAnalysis) => true,
                (RedirectSource.Descriptor, DescriptorMoveRedirectAnalysis) => true,
                (RedirectSource.Descriptor, DescriptorCloseRedirectAnalysis) => true,
                (RedirectSource.Descriptor, HereDocumentRedirectAnalysis) => true,
                (RedirectSource.Descriptor, HereStringRedirectAnalysis) => true,
                (RedirectSource.PowerShellAllStreams,
                    FileRedirectAnalysis
                    { Mode: FileRedirectMode.Output or FileRedirectMode.Append }) => true,
                (RedirectSource.PowerShellAllStreams,
                    DescriptorDuplicateRedirectAnalysis { TargetDescriptor: 1 }) => true,
                (RedirectSource.PowerShellAllStreams, _) => false,
                _ => false,
            };

        private static ShellValueDomain ToPublicDomain(ShellValueDomainFacts domain) =>
            domain.Kind switch
            {
                ShellValueDomainKind.Exact => new ShellValueDomain.Exact(domain.Values[0]),
                ShellValueDomainKind.FiniteSet => new ShellValueDomain.FiniteSet(domain.Values),
                ShellValueDomainKind.Pattern => new ShellValueDomain.PathPattern(
                    domain.Pattern!,
                    domain.CoveringDirectory!),
                _ => new ShellValueDomain.Unknown(),
            };

        private static bool AreValidEffectiveArguments(
            Clause clause,
            IReadOnlyList<EffectiveArgumentFacts> arguments)
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
            IReadOnlyList<RedirectAnalysisFacts> redirects,
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

        private static bool IsValidRedirect(RedirectAnalysisFacts redirect)
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

        private static bool IsValidRedirectSource(RedirectSourceFacts source) =>
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

        private static bool IsValidValueDomain(ShellValueDomainFacts domain)
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
