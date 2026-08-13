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

    internal IReadOnlyList<EffectiveArgumentFacts> AuthoredArguments { get; init; } =
        Array.Empty<EffectiveArgumentFacts>();

    internal bool PublishAuthoredPathShape { get; init; }

    internal ShellValueDomainFacts WorkingDirectory { get; init; } = ShellValueDomainFacts.Unknown;

    internal ShellWorkingDirectoryEffectFacts WorkingDirectoryEffect { get; init; } =
        ShellWorkingDirectoryEffectFacts.Unknown;

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

internal enum ShellWorkingDirectoryEffectKind
{
    Unknown,
    Unchanged,
    ChangesOnSuccess,
}

internal sealed record ShellWorkingDirectoryEffectFacts
{
    internal static ShellWorkingDirectoryEffectFacts Unknown { get; } = new();

    internal static ShellWorkingDirectoryEffectFacts Unchanged { get; } = new()
    {
        Kind = ShellWorkingDirectoryEffectKind.Unchanged,
    };

    internal ShellWorkingDirectoryEffectKind Kind { get; init; }

    internal ShellValueDomainFacts Target { get; init; } = ShellValueDomainFacts.Unknown;

    internal static ShellWorkingDirectoryEffectFacts ChangesOnSuccess(
        ShellValueDomainFacts target) => IsSupportedTarget(target)
        ? new ShellWorkingDirectoryEffectFacts
        {
            Kind = ShellWorkingDirectoryEffectKind.ChangesOnSuccess,
            Target = target,
        }
        : Unknown;

    internal static ShellWorkingDirectoryEffectFacts Join(
        ShellWorkingDirectoryEffectFacts left,
        ShellWorkingDirectoryEffectFacts right)
    {
        if (left.Kind == ShellWorkingDirectoryEffectKind.Unknown ||
            right.Kind == ShellWorkingDirectoryEffectKind.Unknown ||
            left.Kind != right.Kind)
        {
            return Unknown;
        }

        if (left.Kind == ShellWorkingDirectoryEffectKind.Unchanged)
        {
            return Unchanged;
        }

        return ChangesOnSuccess(JoinTargets(left.Target, right.Target));
    }

    private static ShellValueDomainFacts JoinTargets(
        ShellValueDomainFacts left,
        ShellValueDomainFacts right)
    {
        if (!IsSupportedTarget(left) || !IsSupportedTarget(right) ||
            left.Kind == ShellValueDomainKind.Unknown ||
            right.Kind == ShellValueDomainKind.Unknown)
        {
            return ShellValueDomainFacts.Unknown;
        }

        if (ShellValueDomainFacts.AreEqual(left, right))
        {
            return left;
        }

        var distinct = new HashSet<string>(StringComparer.Ordinal);
        AddValues(left, distinct);
        AddValues(right, distinct);
        if (distinct.Count > ShellAnalysisLimits.MaxValueCandidates)
        {
            return ShellValueDomainFacts.Unknown;
        }

        var values = new List<string>(distinct);
        values.Sort(StringComparer.Ordinal);
        return values.Count == 1
            ? new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = values.ToArray(),
            }
            : new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = values.ToArray(),
            };
    }

    private static void AddValues(
        ShellValueDomainFacts domain,
        ISet<string> destination)
    {
        for (var index = 0; index < domain.Values.Count; index++)
        {
            destination.Add(domain.Values[index]);
        }
    }

    private static bool IsSupportedTarget(ShellValueDomainFacts target) =>
        target is not null &&
        target.Kind is ShellValueDomainKind.Unknown or
            ShellValueDomainKind.Exact or
            ShellValueDomainKind.FiniteSet;
}

internal static class ShellPathShapeClassifier
{
    internal static ShellPathShape Classify(ShellValueDomain domain) => domain switch
    {
        ShellValueDomain.Exact exact => ClassifyWord(exact.Value),
        ShellValueDomain.FiniteSet finite => ClassifyWords(finite.Values),
        ShellValueDomain.Concatenation concatenation => ClassifyConcatenation(
            concatenation.Parts),
        _ => ShellPathShape.Unknown,
    };

    private static ShellPathShape ClassifyWords(IReadOnlyList<string> words)
    {
        var shape = ShellPathShape.Unknown;
        for (var index = 0; index < words.Count; index++)
        {
            var candidate = ClassifyWord(words[index]);
            if (candidate == ShellPathShape.Unknown)
            {
                return ShellPathShape.Unknown;
            }

            if (shape != ShellPathShape.Unknown && shape != candidate)
            {
                return ShellPathShape.Unknown;
            }

            shape = candidate;
        }

        return shape;
    }

    private static ShellPathShape ClassifyConcatenation(
        IReadOnlyList<ShellValueDomain> parts)
    {
        var prefix = parts.Count == 0 ? null : parts[0] as ShellValueDomain.Exact;
        if (prefix is null)
        {
            return ShellPathShape.Unknown;
        }

        var shape = ClassifyWordPrefix(prefix.Value);
        if (shape != ShellPathShape.Posix)
        {
            return shape;
        }

        for (var index = 1; index < parts.Count; index++)
        {
            if (CanContainBackslash(parts[index]))
            {
                return ShellPathShape.Unknown;
            }
        }

        return ShellPathShape.Posix;
    }

    private static bool CanContainBackslash(ShellValueDomain domain) => domain switch
    {
        ShellValueDomain.Exact exact => exact.Value.IndexOf('\\') >= 0,
        ShellValueDomain.FiniteSet finite => AnyContainsBackslash(finite.Values),
        ShellValueDomain.IntegerRange => false,
        _ => true,
    };

    private static bool AnyContainsBackslash(IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].IndexOf('\\') >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static ShellPathShape ClassifyWordPrefix(string value)
    {
        var shape = ClassifyWord(value);
        if (shape != ShellPathShape.Unknown)
        {
            return shape;
        }

        return value.EndsWith("/", StringComparison.Ordinal)
            ? ShellPathShape.Posix
            : value.EndsWith("\\", StringComparison.Ordinal)
                ? ShellPathShape.Windows
                : ShellPathShape.Unknown;
    }

    private static ShellPathShape ClassifyWord(string value)
    {
        if (LooksLikeUri(value))
        {
            return ShellPathShape.Unknown;
        }

        if (LooksLikeWindowsPath(value))
        {
            return ShellPathShape.Windows;
        }

        return value.StartsWith("/", StringComparison.Ordinal) ||
               value.StartsWith("./", StringComparison.Ordinal) ||
               value.StartsWith("../", StringComparison.Ordinal) ||
               value.StartsWith("~/", StringComparison.Ordinal) ||
               value.IndexOf('/') >= 0
            ? ShellPathShape.Posix
            : ShellPathShape.Unknown;
    }

    private static bool LooksLikeUri(string value)
    {
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (var index = 1; index < separator; index++)
        {
            var character = value[index];
            if (!IsAsciiLetter(character) &&
                !IsAsciiDigit(character) &&
                character is not '+' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeWindowsPath(string value) =>
        value.IndexOf('\\') >= 0 ||
        value.StartsWith("//", StringComparison.Ordinal) ||
        value.Length >= 2 &&
        IsAsciiLetter(value[0]) &&
        value[1] == ':';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
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

    internal long MinimumInclusive { get; init; }

    internal long MaximumInclusive { get; init; }

    internal IReadOnlyList<ShellValueDomainFacts> Parts { get; init; } =
        Array.Empty<ShellValueDomainFacts>();

    internal static ShellValueDomainFacts IntegerRange(
        long minimumInclusive,
        long maximumInclusive) => minimumInclusive <= maximumInclusive
        ? new ShellValueDomainFacts
        {
            Kind = ShellValueDomainKind.IntegerRange,
            MinimumInclusive = minimumInclusive,
            MaximumInclusive = maximumInclusive,
        }
        : Unknown;

    internal static ShellValueDomainFacts Concatenate(
        IReadOnlyList<ShellValueDomainFacts> source)
    {
        var normalized = new List<ShellValueDomainFacts>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var part = source[index];
            if (!IsAllowedConcatenationPart(part))
            {
                return Unknown;
            }

            if (part.Kind == ShellValueDomainKind.Exact)
            {
                var value = part.Values[0];
                if (value.Length == 0)
                {
                    continue;
                }

                if (normalized.Count > 0 &&
                    normalized[normalized.Count - 1].Kind == ShellValueDomainKind.Exact)
                {
                    var previous = normalized[normalized.Count - 1].Values[0];
                    normalized[normalized.Count - 1] = new ShellValueDomainFacts
                    {
                        Kind = ShellValueDomainKind.Exact,
                        Values = new[] { previous + value },
                    };
                    continue;
                }
            }

            normalized.Add(part);
            if (normalized.Count > 16)
            {
                return Unknown;
            }
        }

        if (normalized.Count == 0)
        {
            return new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { string.Empty },
            };
        }

        if (normalized.Count == 1)
        {
            return normalized[0];
        }

        return new ShellValueDomainFacts
        {
            Kind = ShellValueDomainKind.Concatenation,
            Parts = normalized.ToArray(),
        };
    }

    private static bool IsAllowedConcatenationPart(ShellValueDomainFacts part)
    {
        if (part.Values is null || part.Parts is null)
        {
            return false;
        }

        return part.Kind switch
        {
            ShellValueDomainKind.Exact =>
                part.Values.Count == 1 &&
                part.Values[0] is not null &&
                part.Pattern is null &&
                part.CoveringDirectory is null &&
                part.Parts.Count == 0 &&
                part.MinimumInclusive == 0 &&
                part.MaximumInclusive == 0,
            ShellValueDomainKind.FiniteSet =>
                part.Values.Count >= 2 &&
                part.Values.Count <= ShellAnalysisLimits.MaxValueCandidates &&
                HasDistinctNonNullValues(part.Values) &&
                part.Pattern is null &&
                part.CoveringDirectory is null &&
                part.Parts.Count == 0 &&
                part.MinimumInclusive == 0 &&
                part.MaximumInclusive == 0,
            ShellValueDomainKind.IntegerRange =>
                part.Values.Count == 0 &&
                part.Pattern is null &&
                part.CoveringDirectory is null &&
                part.Parts.Count == 0 &&
                part.MinimumInclusive <= part.MaximumInclusive,
            _ => false,
        };
    }

    private static bool HasDistinctNonNullValues(IReadOnlyList<string> values)
    {
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is null || !distinct.Add(values[index]))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AreEqual(
        ShellValueDomainFacts left,
        ShellValueDomainFacts right)
    {
        if (left.Kind != right.Kind ||
            left.MinimumInclusive != right.MinimumInclusive ||
            left.MaximumInclusive != right.MaximumInclusive ||
            !string.Equals(left.Pattern, right.Pattern, StringComparison.Ordinal) ||
            !string.Equals(
                left.CoveringDirectory,
                right.CoveringDirectory,
                StringComparison.Ordinal) ||
            left.Values.Count != right.Values.Count ||
            left.Parts.Count != right.Parts.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Values.Count; index++)
        {
            if (!string.Equals(left.Values[index], right.Values[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        for (var index = 0; index < left.Parts.Count; index++)
        {
            if (!AreEqual(left.Parts[index], right.Parts[index]))
            {
                return false;
            }
        }

        return true;
    }
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

internal enum ShellProjectionLanguage
{
    Unknown,
    Bash,
    PowerShell,
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
        => TryProject(syntax, factsFactory, ShellProjectionLanguage.Unknown, out result);

    internal static bool TryProject(
        ShellBlockSyntax syntax,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        ShellProjectionLanguage language,
        out ShellProjectionResult result)
    {
        if (syntax is null || factsFactory is null ||
            !Enum.IsDefined(typeof(ShellProjectionLanguage), language))
        {
            result = ShellProjectionResult.Empty;
            return false;
        }

        var walker = new ProjectionWalker(factsFactory, language);
        return walker.TryProject(syntax, out result);
    }

    private sealed class ProjectionWalker
    {
        private readonly Func<SimpleCommandSyntax, CommandOccurrenceFacts> _factsFactory;
        private readonly ShellProjectionLanguage _language;
        private readonly List<CommandAncestryFrame> _ancestry = new();
        private readonly List<CommandOccurrence> _commands = new();
        private readonly List<Clause> _clauses = new();
        private bool _structuralContextIsComplete = true;
        private readonly HashSet<ShellSyntaxNode> _visitedNodes =
            new(NodeReferenceComparer.Instance);
        private readonly HashSet<Clause> _visitedClauses =
            new(ClauseReferenceComparer.Instance);

        internal ProjectionWalker(
            Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
            ShellProjectionLanguage language)
        {
            _factsFactory = factsFactory;
            _language = language;
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
                    _language,
                    out var arguments,
                    out var workingDirectory,
                    out var workingDirectoryEffect,
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
                WorkingDirectoryEffect = workingDirectoryEffect,
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
            ShellProjectionLanguage language,
            out IReadOnlyList<AnalyzedArgument> arguments,
            out ShellValueDomain workingDirectory,
            out ShellWorkingDirectoryEffect workingDirectoryEffect,
            out IReadOnlyList<RedirectAnalysis> redirects)
        {
            arguments = Array.Empty<AnalyzedArgument>();
            workingDirectory = new ShellValueDomain.Unknown();
            workingDirectoryEffect = new ShellWorkingDirectoryEffect.Unknown();
            redirects = Array.Empty<RedirectAnalysis>();
            if (facts is null ||
                facts.EffectiveArguments is null ||
                facts.AuthoredArguments is null ||
                facts.WorkingDirectory is null ||
                facts.Redirects is null ||
                facts.ValueProvenance is null ||
                ContainsNull(facts.EffectiveArguments) ||
                ContainsNull(facts.AuthoredArguments) ||
                ContainsNull(facts.Redirects) ||
                !IsValidValueDomain(facts.WorkingDirectory) ||
                facts.WorkingDirectory.Kind is not (
                    ShellValueDomainKind.Unknown or ShellValueDomainKind.Exact) ||
                !AreValidEffectiveArguments(clause, facts.EffectiveArguments) ||
                !AreValidEffectiveArguments(clause, facts.AuthoredArguments) ||
                !AreValidRedirects(clause, facts.Redirects, facts.IsComplete) ||
                facts.IsComplete &&
                (clause.Verb.IsDynamic || clause.Verb.Tokens.Count == 0))
            {
                return false;
            }

            workingDirectory = ToPublicDomain(facts.WorkingDirectory);
            workingDirectoryEffect = ToPublicEffect(
                facts.WorkingDirectoryEffect,
                language);
            return TryCreateAnalyzedArguments(
                    clause,
                    facts.EffectiveArguments,
                    facts.AuthoredArguments,
                    facts.PublishAuthoredPathShape,
                    facts.ValueProvenance,
                    facts.WorkingDirectory,
                    language,
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
            IReadOnlyList<EffectiveArgumentFacts> authored,
            bool publishAuthoredPathShape,
            IReadOnlyList<ShellValueElementProvenance> provenance,
            ShellValueDomainFacts workingDirectory,
            ShellProjectionLanguage language,
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

            var authoredDomains = new Dictionary<int, ShellValueDomainFacts>();
            for (var index = 0; index < authored.Count; index++)
            {
                authoredDomains.Add(authored[index].ClauseElementIndex, authored[index].Value);
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
                    var hasAuthoredValue = authoredDomains.TryGetValue(
                                               elementIndex,
                                               out var authoredDomain) &&
                                           offset == effectiveOffset;
                    var authoredValue = hasAuthoredValue
                        ? ToPublicDomain(authoredDomain!)
                        : value;
                    projected.Add(new AnalyzedArgument
                    {
                        Argument = argument,
                        Element = element,
                        Value = value,
                        AuthoredValue = authoredValue,
                        AuthoredFileSystemValue = new ShellValueDomain.Unknown(),
                        AuthoredPathShape = publishAuthoredPathShape
                            ? ShellPathShapeClassifier.Classify(authoredValue)
                            : ShellPathShape.Unknown,
                        HasEffectiveValue = hasEffectiveValue,
                    });
                }

                argumentIndex += count;
            }

            if (argumentIndex != authoredArguments.Count)
            {
                return false;
            }

            arguments = AuthoredFileSystemValueProjection.Apply(
                language,
                clause,
                provenance,
                workingDirectory,
                projected);
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
                {
                    Mode: FileRedirectMode.Input or
                            FileRedirectMode.Output or
                            FileRedirectMode.Append
                }) => true,
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
                ShellValueDomainKind.IntegerRange => new ShellValueDomain.IntegerRange(
                    domain.MinimumInclusive,
                    domain.MaximumInclusive),
                ShellValueDomainKind.Concatenation => new ShellValueDomain.Concatenation(
                    ToPublicDomains(domain.Parts)),
                _ => new ShellValueDomain.Unknown(),
            };

        private static ShellWorkingDirectoryEffect ToPublicEffect(
            ShellWorkingDirectoryEffectFacts? effect,
            ShellProjectionLanguage language)
        {
            if (effect is null || !IsValidWorkingDirectoryEffect(effect, language))
            {
                return new ShellWorkingDirectoryEffect.Unknown();
            }

            return effect.Kind switch
            {
                ShellWorkingDirectoryEffectKind.Unchanged =>
                    new ShellWorkingDirectoryEffect.Unchanged(),
                ShellWorkingDirectoryEffectKind.ChangesOnSuccess =>
                    new ShellWorkingDirectoryEffect.ChangesOnSuccess(
                        ToPublicDomain(effect.Target)),
                _ => new ShellWorkingDirectoryEffect.Unknown(),
            };
        }

        private static IReadOnlyList<ShellValueDomain> ToPublicDomains(
            IReadOnlyList<ShellValueDomainFacts> domains)
        {
            var projected = new ShellValueDomain[domains.Count];
            for (var index = 0; index < domains.Count; index++)
            {
                projected[index] = ToPublicDomain(domains[index]);
            }

            return projected;
        }

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
            if (domain.Values is null ||
                domain.Parts is null ||
                ContainsNull(domain.Values) ||
                ContainsNull(domain.Parts))
            {
                return false;
            }

            return domain.Kind switch
            {
                ShellValueDomainKind.Unknown =>
                    domain.Values.Count == 0 &&
                    domain.Pattern is null &&
                    domain.CoveringDirectory is null &&
                    domain.Parts.Count == 0 &&
                    domain.MinimumInclusive == 0 &&
                    domain.MaximumInclusive == 0,
                ShellValueDomainKind.Exact =>
                    domain.Values.Count == 1 &&
                    domain.Pattern is null &&
                    domain.CoveringDirectory is null &&
                    domain.Parts.Count == 0 &&
                    domain.MinimumInclusive == 0 &&
                    domain.MaximumInclusive == 0,
                ShellValueDomainKind.FiniteSet =>
                    domain.Values.Count >= 2 &&
                    domain.Values.Count <= ShellAnalysisLimits.MaxValueCandidates &&
                    AreDistinct(domain.Values) &&
                    domain.Pattern is null &&
                    domain.CoveringDirectory is null &&
                    domain.Parts.Count == 0 &&
                    domain.MinimumInclusive == 0 &&
                    domain.MaximumInclusive == 0,
                ShellValueDomainKind.Pattern =>
                    domain.Values.Count == 0 &&
                    !string.IsNullOrEmpty(domain.Pattern) &&
                    !string.IsNullOrEmpty(domain.CoveringDirectory) &&
                    domain.Parts.Count == 0 &&
                    domain.MinimumInclusive == 0 &&
                    domain.MaximumInclusive == 0,
                ShellValueDomainKind.IntegerRange =>
                    domain.Values.Count == 0 &&
                    domain.Pattern is null &&
                    domain.CoveringDirectory is null &&
                    domain.Parts.Count == 0 &&
                    domain.MinimumInclusive <= domain.MaximumInclusive,
                ShellValueDomainKind.Concatenation =>
                    domain.Values.Count == 0 &&
                    domain.Pattern is null &&
                    domain.CoveringDirectory is null &&
                    domain.Parts.Count is >= 2 and <= 16 &&
                    domain.MinimumInclusive == 0 &&
                    domain.MaximumInclusive == 0 &&
                    AreValidConcatenationParts(domain.Parts),
                _ => false,
            };
        }

        private static bool IsValidWorkingDirectoryEffect(
            ShellWorkingDirectoryEffectFacts? effect,
            ShellProjectionLanguage language)
        {
            if (effect is null ||
                effect.Target is null ||
                !IsValidValueDomain(effect.Target))
            {
                return false;
            }

            return effect.Kind switch
            {
                ShellWorkingDirectoryEffectKind.Unknown =>
                    effect.Target.Kind == ShellValueDomainKind.Unknown,
                ShellWorkingDirectoryEffectKind.Unchanged =>
                    effect.Target.Kind == ShellValueDomainKind.Unknown,
                ShellWorkingDirectoryEffectKind.ChangesOnSuccess =>
                    effect.Target.Kind == ShellValueDomainKind.Unknown ||
                    (effect.Target.Kind is ShellValueDomainKind.Exact or
                        ShellValueDomainKind.FiniteSet &&
                     HasValidWorkingDirectoryTargets(effect.Target, language)),
                _ => false,
            };
        }

        private static bool HasValidWorkingDirectoryTargets(
            ShellValueDomainFacts target,
            ShellProjectionLanguage language)
        {
            if (language == ShellProjectionLanguage.Unknown)
            {
                return false;
            }

            WorkingDirectoryPathStyle? expectedStyle = null;
            foreach (var value in target.Values)
            {
                if (!TryGetNormalizedAbsolutePathStyle(value, out var style) ||
                    language == ShellProjectionLanguage.Bash &&
                    style != WorkingDirectoryPathStyle.Posix ||
                    expectedStyle.HasValue && expectedStyle.Value != style)
                {
                    return false;
                }

                expectedStyle = style;
            }

            return expectedStyle.HasValue;
        }

        private static bool TryGetNormalizedAbsolutePathStyle(
            string value,
            out WorkingDirectoryPathStyle style)
        {
            style = default;
            if (string.IsNullOrEmpty(value) ||
                value.IndexOf('\\') >= 0 ||
                value.IndexOf('\0') >= 0)
            {
                return false;
            }

            var segmentStart = 0;
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                style = WorkingDirectoryPathStyle.Windows;
                segmentStart = 2;
            }
            else if (value[0] == '/')
            {
                style = WorkingDirectoryPathStyle.Posix;
                segmentStart = 1;
            }
            else if (value.Length >= 3 &&
                     value[0] is >= 'A' and <= 'Z' &&
                     value[1] == ':' &&
                     value[2] == '/')
            {
                style = WorkingDirectoryPathStyle.Windows;
                segmentStart = 3;
            }
            else
            {
                return false;
            }

            for (var index = segmentStart; index < value.Length; index++)
            {
                if (char.IsControl(value[index]))
                {
                    return false;
                }

                if (value[index] != '/')
                {
                    continue;
                }

                if (index == segmentStart ||
                    index + 1 == value.Length ||
                    value[index + 1] == '/')
                {
                    return false;
                }
            }

            var segments = value.Substring(segmentStart).Split('/');
            foreach (var segment in segments)
            {
                if (segment is "." or "..")
                {
                    return false;
                }
            }

            return segmentStart == value.Length || segments.Length > 0;
        }

        private enum WorkingDirectoryPathStyle
        {
            Posix,
            Windows,
        }

        private static bool AreValidConcatenationParts(
            IReadOnlyList<ShellValueDomainFacts> parts)
        {
            var hasNonExactPart = false;
            for (var index = 0; index < parts.Count; index++)
            {
                if (!IsValidValueDomain(parts[index]) ||
                    parts[index].Kind is not (
                        ShellValueDomainKind.Exact or
                        ShellValueDomainKind.FiniteSet or
                        ShellValueDomainKind.IntegerRange))
                {
                    return false;
                }

                if (parts[index].Kind == ShellValueDomainKind.Exact)
                {
                    if (parts[index].Values[0].Length == 0 ||
                        index > 0 && parts[index - 1].Kind == ShellValueDomainKind.Exact)
                    {
                        return false;
                    }
                }
                else
                {
                    hasNonExactPart = true;
                }
            }

            return hasNonExactPart;
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
