// -----------------------------------------------------------------------
// <copyright file="AstAssert.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Sdk;

namespace ShellSyntaxTree.Tests.Corpus;

/// <summary>
/// Structural-equality helper for the corpus runner. Compares an
/// <see cref="ExpectedParsedCommand"/> (the JSON-deserialized expectation
/// shape) against a live <see cref="ParsedCommand"/> and throws an
/// <see cref="XunitException"/> with a diff-friendly message naming the
/// first differing field on mismatch.
/// </summary>
/// <remarks>
/// <para>
/// SPEC §13's <c>AstAssert.Equal</c> is the canonical entry point. The
/// implementation prioritizes legibility of the CI failure log over a
/// machine-readable diff — the goal is that a human reviewing a failing
/// corpus assertion can identify the differing path
/// (e.g. <c>clauses[1].args[2].kind</c>) and the specific value mismatch
/// without re-reading the entire JSON.
/// </para>
/// <para>
/// Comparison rules (see also <see cref="ExpectedParsedCommand"/> and
/// peers for which fields are required vs. opt-in):
/// </para>
/// <list type="bullet">
///   <item>
///     <c>IsUnparseable</c> is compared first. When true on the expected
///     side, only <c>UnparseableReasonContains</c> (substring match, when
///     supplied) is verified — the clauses payload is not inspected.
///   </item>
///   <item>
///     <c>Clauses.Count</c> mismatch dumps both expected and actual clause
///     summaries so the author can eyeball which clause was added/dropped.
///   </item>
///   <item>
///     Per clause: <c>Operator</c>, <c>Verb.Tokens</c> (sequence equality),
///     <c>Args.Count</c>, per-arg fields, <c>Redirects.Count</c>, per-
///     redirect fields, optional <c>Elements</c>, <c>IsSubshell</c>,
///     <c>IsCommandStringWrapped</c>.
///   </item>
///   <item>
///     Per arg: <c>Raw</c>, <c>Kind</c>, <c>IsPath</c>, <c>IsCwdAttribution</c>
///     always asserted. <c>IsFlag</c> opt-in (computed property; corpus may
///     pin it for documentation but it's not required). <c>Resolved</c>
///     opt-in via the JSON sentinel <c>"__NULL__"</c> for null or a literal
///     string for value equality; omitting the field skips the comparison.
///   </item>
///   <item>
///     <c>Elements</c> is opt-in for legacy corpus compatibility. When the
///     expectation supplies it, every provenance field is compared.
///   </item>
/// </list>
/// </remarks>
internal static class AstAssert
{
    /// <summary>
    /// Assert that <paramref name="actual"/> matches <paramref name="expected"/>.
    /// Throws <see cref="XunitException"/> with a path-prefixed message on
    /// first mismatch; never throws on equality.
    /// </summary>
    /// <param name="expected">The expectation shape from a corpus JSON.</param>
    /// <param name="actual">The live <see cref="ParsedCommand"/> from the parser.</param>
    /// <param name="contextLabel">
    /// Optional human-readable label (typically the corpus file name) used
    /// as a prefix on every failure message. When null, no prefix is added.
    /// </param>
    internal static void Equal(
        ExpectedParsedCommand expected,
        ParsedCommand actual,
        string? contextLabel = null)
    {
        var prefix = contextLabel is null ? string.Empty : $"[{contextLabel}] ";

        if (expected.IsUnparseable != actual.IsUnparseable)
        {
            throw new XunitException(
                prefix + $"isUnparseable: expected={expected.IsUnparseable}, actual={actual.IsUnparseable}; reason={actual.UnparseableReason}");
        }

        if (expected.IsUnparseable)
        {
            if (!string.IsNullOrEmpty(expected.UnparseableReasonContains))
            {
                if (actual.UnparseableReason is null
                    || !actual.UnparseableReason.Contains(expected.UnparseableReasonContains!, System.StringComparison.Ordinal))
                {
                    throw new XunitException(
                        prefix + $"unparseableReason: expected contains '{expected.UnparseableReasonContains}', actual='{actual.UnparseableReason}'");
                }
            }

            if (actual.Commands.Count != 0 || actual.Clauses.Count != 0)
            {
                throw new XunitException(
                    prefix + "unparseable result retained command or compatibility projections");
            }

            return;
        }

        var expectedClauses = expected.Clauses ?? new List<ExpectedClause>();
        if (expectedClauses.Count != actual.Clauses.Count)
        {
            throw new XunitException(
                prefix + $"clauses.count: expected={expectedClauses.Count}, actual={actual.Clauses.Count}\n"
                + "  expected: " + SummarizeExpectedClauses(expectedClauses) + "\n"
                + "  actual:   " + SummarizeActualClauses(actual.Clauses));
        }

        for (var i = 0; i < expectedClauses.Count; i++)
        {
            AssertClauseEqual(expectedClauses[i], actual.Clauses[i], $"{prefix}clauses[{i}]");
        }

        if (expected.Syntax is not null)
        {
            AssertSyntaxEqual(expected.Syntax, expectedClauses, actual, prefix);
        }

        if (expected.Commands is not null)
        {
            AssertCommandsEqual(expected.Commands, actual, prefix);
        }
    }

    private static void AssertSyntaxEqual(
        IReadOnlyList<ExpectedSyntaxNode> expected,
        IReadOnlyList<ExpectedClause> expectedClauses,
        ParsedCommand actual,
        string prefix)
    {
        ValidateExpectedSyntax(expected, expectedClauses, prefix);
        var flattened = new List<ActualSyntaxNode>();
        AppendSyntax(
            actual.Syntax,
            parentIndex: null,
            CommandAncestryRegion.Unknown,
            childIndex: null,
            listOperator: null,
            actual.Clauses,
            flattened,
            isRootBlock: true);
        if (expected.Count != flattened.Count)
        {
            throw new XunitException(
                prefix + $"syntax.count: expected={expected.Count}, actual={flattened.Count}");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var wanted = expected[index];
            var observed = flattened[index];
            if (wanted.Kind != observed.Kind ||
                wanted.ParentIndex != observed.ParentIndex ||
                wanted.Region != observed.Region ||
                wanted.ChildIndex != observed.ChildIndex ||
                wanted.SourceStart != observed.SourceStart ||
                wanted.SourceLength != observed.SourceLength ||
                wanted.ClauseIndex != observed.ClauseIndex ||
                wanted.GroupKind != observed.GroupKind ||
                wanted.ListOperator != observed.ListOperator ||
                wanted.BindingName != observed.BindingName ||
                wanted.BindingRaw != observed.BindingRaw ||
                wanted.BindingSourceStart != observed.BindingSourceStart ||
                wanted.BindingSourceLength != observed.BindingSourceLength ||
                wanted.IterableRaw != observed.IterableRaw ||
                wanted.IterableSourceStart != observed.IterableSourceStart ||
                wanted.IterableSourceLength != observed.IterableSourceLength ||
                wanted.ExecutionOrigin != observed.ExecutionOrigin ||
                wanted.HostClauseElementIndex != observed.HostClauseElementIndex ||
                wanted.ExecutionPhase != observed.ExecutionPhase ||
                wanted.ExecutionTiming != observed.ExecutionTiming ||
                wanted.ExecutionCardinality != observed.ExecutionCardinality)
            {
                throw new XunitException(
                    prefix + $"syntax[{index}]: expected={Summarize(wanted)}, "
                    + $"actual={Summarize(observed)}");
            }

            if (observed.Clause is not null &&
                (wanted.ClauseIndex is null ||
                 wanted.ClauseIndex < 0 ||
                 wanted.ClauseIndex >= actual.Clauses.Count ||
                 !object.ReferenceEquals(
                     observed.Clause,
                     actual.Clauses[wanted.ClauseIndex.Value])))
            {
                throw new XunitException(
                    prefix + $"syntax[{index}].clauseIndex does not reference the exact compatibility Clause");
            }
        }
    }

    private static void ValidateExpectedSyntax(
        IReadOnlyList<ExpectedSyntaxNode> expected,
        IReadOnlyList<ExpectedClause> expectedClauses,
        string prefix)
    {
        if (expected.Count == 0 ||
            expected[0].Kind != "Block" ||
            expected[0].ParentIndex is not null ||
            expected[0].Region != CommandAncestryRegion.Unknown ||
            expected[0].ChildIndex is not null ||
            expected[0].ListOperator is not null)
        {
            throw new XunitException(prefix + "syntax[0] must be the root block");
        }

        var executionRegionCoordinates = new HashSet<(int ParentIndex, int ElementIndex)>();
        for (var index = 0; index < expected.Count; index++)
        {
            var node = expected[index];
            var isExecutionRegion = node.Kind == "ExecutionRegion";
            if (node.Kind == "Unknown" ||
                (node.Kind == "SimpleCommand") != node.ClauseIndex.HasValue ||
                (node.Kind == "Group") != node.GroupKind.HasValue ||
                isExecutionRegion != node.ExecutionOrigin.HasValue ||
                isExecutionRegion != node.ExecutionPhase.HasValue ||
                isExecutionRegion != node.ExecutionTiming.HasValue ||
                isExecutionRegion != node.ExecutionCardinality.HasValue ||
                isExecutionRegion && !IsValidExpectedExecutionRegion(node) ||
                !isExecutionRegion && node.HostClauseElementIndex.HasValue)
            {
                throw new XunitException(
                    prefix + $"syntax[{index}] has an invalid kind-specific field");
            }

            if (node.SourceStart.HasValue != node.SourceLength.HasValue ||
                node.SourceStart < 0 ||
                node.SourceLength < 0 ||
                node.ChildIndex < 0)
            {
                throw new XunitException(
                    prefix + $"syntax[{index}] has an invalid coordinate");
            }

            if (index == 0)
            {
                continue;
            }

            if (node.ParentIndex is null ||
                node.ParentIndex < 0 ||
                node.ParentIndex >= index)
            {
                throw new XunitException(
                    prefix + $"syntax[{index}].parentIndex must name an earlier node");
            }

            var parent = expected[node.ParentIndex.Value];
            var relationshipIsValid = parent.Kind switch
            {
                "Block" =>
                    node.Region == (node.ParentIndex == 0
                        ? CommandAncestryRegion.Root
                        : CommandAncestryRegion.Statement) &&
                    node.ChildIndex.HasValue,
                "SimpleCommand" =>
                    ((node.Kind == "CommandSubstitution" &&
                      node.Region == CommandAncestryRegion.Substitution) ||
                     (node.Kind == "ExecutionRegion" &&
                      node.Region == CommandAncestryRegion.ExecutionRegion)) &&
                    node.ChildIndex.HasValue,
                "Pipeline" =>
                    node.Region == CommandAncestryRegion.PipelineStage &&
                    node.ChildIndex.HasValue,
                "CommandList" =>
                    node.Region == CommandAncestryRegion.Statement &&
                    node.ChildIndex.HasValue &&
                    node.ListOperator.HasValue,
                "Group" =>
                    node.Region == CommandAncestryRegion.GroupBody &&
                    node.ChildIndex is null,
                "ForEach" =>
                    (node.Region is CommandAncestryRegion.Iterator or
                        CommandAncestryRegion.LoopBody) &&
                    node.ChildIndex is null,
                "CommandSubstitution" =>
                    node.Region == CommandAncestryRegion.Substitution,
                "ExecutionRegion" =>
                    node.Kind == "Block" &&
                    node.Region == CommandAncestryRegion.ExecutionRegion,
                _ => false,
            };
            var executionRegionPlacementIsValid =
                node.Kind != "ExecutionRegion" ||
                (node.ExecutionOrigin == ExecutionRegionOrigin.CommandArgument) ==
                (parent.Kind == "SimpleCommand");
            var executionRegionCoordinateIsValid =
                node.Kind != "ExecutionRegion" ||
                node.ExecutionOrigin != ExecutionRegionOrigin.CommandArgument ||
                IsValidExpectedHostCoordinate(
                    node,
                    parent,
                    node.ParentIndex.Value,
                    expectedClauses,
                    executionRegionCoordinates);
            if (!relationshipIsValid ||
                !executionRegionPlacementIsValid ||
                !executionRegionCoordinateIsValid ||
                parent.Kind != "CommandList" && node.ListOperator.HasValue)
            {
                throw new XunitException(
                    prefix + $"syntax[{index}] has an invalid parent relationship");
            }
        }
    }

    private static bool IsValidExpectedHostCoordinate(
        ExpectedSyntaxNode executionRegion,
        ExpectedSyntaxNode parent,
        int parentIndex,
        IReadOnlyList<ExpectedClause> expectedClauses,
        ISet<(int ParentIndex, int ElementIndex)> coordinates)
    {
        if (parent.ClauseIndex is not int clauseIndex ||
            clauseIndex < 0 ||
            clauseIndex >= expectedClauses.Count ||
            executionRegion.HostClauseElementIndex is not int elementIndex ||
            expectedClauses[clauseIndex].Elements is not { } elements ||
            elementIndex < 0 ||
            elementIndex >= elements.Count)
        {
            return false;
        }

        var element = elements[elementIndex];
        return coordinates.Add((parentIndex, elementIndex)) &&
            element.Role == ClauseElementRole.Argument &&
            element.Kind == ArgKind.DynamicSkip;
    }

    private static bool IsValidExpectedExecutionRegion(ExpectedSyntaxNode node) =>
        node.ExecutionOrigin.HasValue &&
        System.Enum.IsDefined(typeof(ExecutionRegionOrigin), node.ExecutionOrigin.Value) &&
        node.ExecutionOrigin.Value != ExecutionRegionOrigin.Unknown &&
        node.ExecutionPhase.HasValue &&
        System.Enum.IsDefined(typeof(ExecutionRegionPhase), node.ExecutionPhase.Value) &&
        node.ExecutionTiming.HasValue &&
        System.Enum.IsDefined(typeof(ExecutionRegionTiming), node.ExecutionTiming.Value) &&
        node.ExecutionCardinality.HasValue &&
        System.Enum.IsDefined(
            typeof(ExecutionRegionCardinality),
            node.ExecutionCardinality.Value) &&
        node.ExecutionOrigin.Value switch
        {
            ExecutionRegionOrigin.DirectCall or ExecutionRegionOrigin.DotSource =>
                node.HostClauseElementIndex is null,
            ExecutionRegionOrigin.CommandArgument => node.HostClauseElementIndex >= 0,
            _ => false,
        };

    private static void AssertCommandsEqual(
        IReadOnlyList<ExpectedCommandOccurrence> expected,
        ParsedCommand actual,
        string prefix)
    {
        if (expected.Count != actual.Commands.Count)
        {
            throw new XunitException(
                prefix + $"commands.count: expected={expected.Count}, actual={actual.Commands.Count}");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var wanted = expected[index];
            var observed = actual.Commands[index];
            if (wanted.ImmediateRole == CommandOccurrenceRole.Unknown ||
                observed.ImmediateRole == CommandOccurrenceRole.Unknown)
            {
                throw new XunitException(
                    prefix + $"commands[{index}].immediateRole must be known");
            }

            if (wanted.ClauseIndex < 0 || wanted.ClauseIndex >= actual.Clauses.Count)
            {
                throw new XunitException(
                    prefix + $"commands[{index}].clauseIndex is outside Clauses");
            }

            if (!object.ReferenceEquals(
                    observed.Clause,
                    actual.Clauses[wanted.ClauseIndex]))
            {
                throw new XunitException(
                    prefix + $"commands[{index}].clauseIndex does not reference the exact compatibility Clause");
            }

            if (wanted.ImmediateRole != observed.ImmediateRole ||
                wanted.IsComplete != observed.IsComplete)
            {
                throw new XunitException(
                    prefix + $"commands[{index}]: expected role={wanted.ImmediateRole}, complete={wanted.IsComplete}; "
                    + $"actual role={observed.ImmediateRole}, complete={observed.IsComplete}");
            }

            var expectedFrames = wanted.Ancestry ?? new List<ExpectedCommandAncestryFrame>();
            if (expectedFrames.Count != observed.Ancestry.Count)
            {
                throw new XunitException(
                    prefix + $"commands[{index}].ancestry.count: "
                    + $"expected={expectedFrames.Count}, actual={observed.Ancestry.Count}");
            }

            for (var frameIndex = 0; frameIndex < expectedFrames.Count; frameIndex++)
            {
                var expectedFrame = expectedFrames[frameIndex];
                var actualFrame = observed.Ancestry[frameIndex];
                if (expectedFrame.AncestorKind == "Unknown" ||
                    SyntaxKind(actualFrame.Ancestor) == "Unknown" ||
                    expectedFrame.Region == CommandAncestryRegion.Unknown ||
                    actualFrame.Region == CommandAncestryRegion.Unknown)
                {
                    throw new XunitException(
                        prefix + $"commands[{index}].ancestry[{frameIndex}] contains an unknown enum");
                }

                if (expectedFrame.AncestorKind != SyntaxKind(actualFrame.Ancestor) ||
                    expectedFrame.Region != actualFrame.Region ||
                    expectedFrame.ChildIndex != actualFrame.ChildIndex ||
                    expectedFrame.SourceStart != actualFrame.Ancestor.SourceStart ||
                    expectedFrame.SourceLength != actualFrame.Ancestor.SourceLength)
                {
                    throw new XunitException(
                        prefix + $"commands[{index}].ancestry[{frameIndex}] differs");
                }
            }

            if (wanted.Arguments is not null)
            {
                if (wanted.Arguments.Count != observed.Arguments.Count)
                {
                    throw new XunitException(
                        prefix + $"commands[{index}].arguments.count differs");
                }

                for (var argumentIndex = 0;
                     argumentIndex < wanted.Arguments.Count;
                     argumentIndex++)
                {
                    var expectedArgument = wanted.Arguments[argumentIndex];
                    var actualArgument = observed.Arguments[argumentIndex];
                    if (expectedArgument.ClauseArgumentIndex !=
                            FindArgumentIndex(observed.Clause, actualArgument.Argument) ||
                        expectedArgument.ClauseElementIndex !=
                            FindElementIndex(observed.Clause, actualArgument.Element))
                    {
                        throw new XunitException(
                            prefix + $"commands[{index}].arguments[{argumentIndex}] coordinate differs");
                    }

                    AssertValueDomainEqual(
                        expectedArgument.Value,
                        actualArgument.Value,
                        prefix + $"commands[{index}].arguments[{argumentIndex}].value");
                    if (expectedArgument.AuthoredFileSystemValue is not null)
                    {
                        AssertValueDomainEqual(
                            expectedArgument.AuthoredFileSystemValue,
                            actualArgument.AuthoredFileSystemValue,
                            prefix + $"commands[{index}].arguments[{argumentIndex}].authoredFileSystemValue");
                    }
                }
            }

            if (wanted.WorkingDirectory is not null)
            {
                AssertValueDomainEqual(
                    wanted.WorkingDirectory,
                    observed.WorkingDirectory,
                    prefix + $"commands[{index}].workingDirectory");
            }

            if (wanted.WorkingDirectoryEffect is not null)
            {
                AssertWorkingDirectoryEffectEqual(
                    wanted.WorkingDirectoryEffect,
                    observed.WorkingDirectoryEffect,
                    prefix + $"commands[{index}].workingDirectoryEffect");
            }

            if (wanted.Redirects is not null)
            {
                AssertRedirectAnalysesEqual(
                    wanted.Redirects,
                    observed.Redirects,
                    prefix + $"commands[{index}].redirects");
            }
        }
    }

    private static void AssertWorkingDirectoryEffectEqual(
        ExpectedWorkingDirectoryEffect expected,
        ShellWorkingDirectoryEffect actual,
        string path)
    {
        switch (expected.Kind)
        {
            case ExpectedWorkingDirectoryEffectKind.Unknown:
                Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(actual);
                Assert.Null(expected.Target);
                break;
            case ExpectedWorkingDirectoryEffectKind.Unchanged:
                Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(actual);
                Assert.Null(expected.Target);
                break;
            case ExpectedWorkingDirectoryEffectKind.ChangesOnSuccess:
                var changed = Assert.IsType<
                    ShellWorkingDirectoryEffect.ChangesOnSuccess>(actual);
                Assert.NotNull(expected.Target);
                AssertValueDomainEqual(expected.Target!, changed.Target, path + ".target");
                break;
            default:
                throw new XunitException($"{path}.kind is unsupported: {expected.Kind}");
        }
    }

    private static void AssertRedirectAnalysesEqual(
        IReadOnlyList<ExpectedRedirectAnalysis> expected,
        IReadOnlyList<RedirectAnalysis> actual,
        string path)
    {
        if (expected.Count != actual.Count)
        {
            throw new XunitException(
                $"{path}.count: expected={expected.Count}, actual={actual.Count}");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var wanted = expected[index];
            var observed = actual[index];
            if (wanted.RedirectIndex != index ||
                wanted.Kind != observed.GetType().Name ||
                wanted.SourceKind != observed.Source.GetType().Name ||
                wanted.SourceDescriptor !=
                    (observed.Source as RedirectSource.Descriptor)?.Value ||
                wanted.FileMode != (observed as FileRedirectAnalysis)?.Mode ||
                wanted.TargetDescriptor != GetTargetDescriptor(observed) ||
                wanted.IsComplete != observed.IsComplete)
            {
                throw new XunitException(
                    $"{path}[{index}] differs: expected index={wanted.RedirectIndex}, "
                    + $"source={wanted.SourceKind}/{wanted.SourceDescriptor}, "
                    + $"kind={wanted.Kind}, mode={wanted.FileMode}, "
                    + $"targetDescriptor={wanted.TargetDescriptor}, complete={wanted.IsComplete}; "
                    + $"actual source={observed.Source.GetType().Name}, "
                    + $"kind={observed.GetType().Name}, complete={observed.IsComplete}");
            }

            if (wanted.Value is not null)
            {
                AssertValueDomainEqual(
                    wanted.Value,
                    GetRedirectValue(observed),
                    $"{path}[{index}].value");
            }
            AssertHereDocumentEqual(
                wanted.HereDocument,
                (observed as HereDocumentRedirectAnalysis)?.Document,
                $"{path}[{index}].hereDocument");
        }
    }

    private static void AssertHereDocumentEqual(
        ExpectedHereDocumentAnalysis? expected,
        HereDocumentAnalysis? actual,
        string path)
    {
        if (expected is null)
        {
            if (actual is not null)
            {
                throw new XunitException($"{path}: expected null, actual non-null");
            }

            return;
        }

        if (actual is null)
        {
            throw new XunitException($"{path}: expected non-null, actual null");
        }

        AssertSourceFragmentEqual(expected.Delimiter, actual.Delimiter, path + ".delimiter");
        AssertSourceFragmentEqual(expected.Body, actual.Body, path + ".body");
        if (expected.ExpansionMode != actual.ExpansionMode ||
            expected.StripLeadingTabs != actual.StripLeadingTabs ||
            expected.IsComplete != actual.IsComplete)
        {
            throw new XunitException(
                $"{path} differs: expected expansion={expected.ExpansionMode}, "
                + $"stripTabs={expected.StripLeadingTabs}, complete={expected.IsComplete}; "
                + $"actual expansion={actual.ExpansionMode}, "
                + $"stripTabs={actual.StripLeadingTabs}, complete={actual.IsComplete}");
        }
    }

    private static void AssertSourceFragmentEqual(
        ExpectedSourceFragment expected,
        ShellSourceFragment actual,
        string path)
    {
        if (expected.Raw != actual.Raw ||
            expected.SourceStart != actual.SourceStart ||
            expected.SourceLength != actual.SourceLength)
        {
            throw new XunitException(
                $"{path}: expected raw={Quote(expected.Raw)}, "
                + $"span={expected.SourceStart}:{expected.SourceLength}; "
                + $"actual raw={Quote(actual.Raw)}, "
                + $"span={actual.SourceStart}:{actual.SourceLength}");
        }
    }

    private static void AssertValueDomainEqual(
        ExpectedValueDomain expected,
        ShellValueDomain actual,
        string path)
    {
        var expectedValues = expected.Values ?? new List<string>();
        var actualKind = ValueDomainKind(actual);
        var actualValues = ValueDomainValues(actual);
        var actualPattern = (actual as ShellValueDomain.PathPattern)?.Pattern;
        var actualCoveringDirectory =
            (actual as ShellValueDomain.PathPattern)?.CoveringDirectory;
        if (expected.Kind != actualKind ||
            !expectedValues.SequenceEqual(actualValues) ||
            expected.Pattern != actualPattern ||
            expected.CoveringDirectory != actualCoveringDirectory)
        {
            throw new XunitException(
                $"{path}: expected={expected.Kind}[{string.Join(",", expectedValues)}] "
                + $"pattern={expected.Pattern}, covering={expected.CoveringDirectory}; "
                + $"actual={actualKind}[{string.Join(",", actualValues)}] "
                + $"pattern={actualPattern}, covering={actualCoveringDirectory}");
        }
    }

    private static string ValueDomainKind(ShellValueDomain domain) => domain switch
    {
        ShellValueDomain.Unknown => "Unknown",
        ShellValueDomain.Exact => "Exact",
        ShellValueDomain.FiniteSet => "FiniteSet",
        ShellValueDomain.PathPattern => "Pattern",
        _ => "Unknown",
    };

    private static IReadOnlyList<string> ValueDomainValues(ShellValueDomain domain) =>
        domain switch
        {
            ShellValueDomain.Exact exact => new[] { exact.Value },
            ShellValueDomain.FiniteSet finiteSet => finiteSet.Values,
            _ => Array.Empty<string>(),
        };

    private static int? GetTargetDescriptor(RedirectAnalysis analysis) => analysis switch
    {
        DescriptorDuplicateRedirectAnalysis duplicate => duplicate.TargetDescriptor,
        DescriptorMoveRedirectAnalysis move => move.TargetDescriptor,
        _ => null,
    };

    private static ShellValueDomain GetRedirectValue(RedirectAnalysis analysis) =>
        analysis switch
        {
            FileRedirectAnalysis file => file.Target,
            HereStringRedirectAnalysis hereString => hereString.Data,
            _ => new ShellValueDomain.Unknown(),
        };

    private static void AppendSyntax(
        ShellSyntaxNode node,
        int? parentIndex,
        CommandAncestryRegion region,
        int? childIndex,
        CompoundOperator? listOperator,
        IReadOnlyList<Clause> clauses,
        List<ActualSyntaxNode> nodes,
        bool isRootBlock = false)
    {
        if (SyntaxKind(node) == "Unknown")
        {
            throw new XunitException("Cannot flatten an unknown syntax node into corpus expectations");
        }

        var clause = (node as SimpleCommandSyntax)?.Clause;
        var forEachNode = node as ForEachSyntax;
        var executionRegion = node as ExecutionRegionSyntax;
        int? clauseIndex = clause is null ? null : FindClauseIndex(clauses, clause);
        var currentIndex = nodes.Count;
        nodes.Add(new ActualSyntaxNode(
            SyntaxKind(node),
            parentIndex,
            region,
            childIndex,
            node.SourceStart,
            node.SourceLength,
            clauseIndex,
            (node as GroupSyntax)?.GroupKind,
            listOperator,
            forEachNode?.BindingName,
            forEachNode?.BindingSource.Raw,
            forEachNode?.BindingSource.SourceStart,
            forEachNode?.BindingSource.SourceLength,
            forEachNode?.Iterable.Raw,
            forEachNode?.Iterable.SourceStart,
            forEachNode?.Iterable.SourceLength,
            executionRegion?.Origin,
            FindElementIndex(clauses, executionRegion?.HostArgument),
            executionRegion?.Phase,
            executionRegion?.Timing,
            executionRegion?.Cardinality,
            clause));

        switch (node)
        {
            case ShellBlockSyntax block:
                var statementRegion = isRootBlock
                    ? CommandAncestryRegion.Root
                    : CommandAncestryRegion.Statement;
                for (var index = 0; index < block.Statements.Count; index++)
                {
                    AppendSyntax(
                        block.Statements[index],
                        currentIndex,
                        statementRegion,
                        index,
                        listOperator: null,
                        clauses,
                        nodes);
                }

                break;
            case SimpleCommandSyntax simple:
                for (var index = 0; index < simple.Substitutions.Count; index++)
                {
                    AppendSyntax(
                        simple.Substitutions[index],
                        currentIndex,
                        CommandAncestryRegion.Substitution,
                        index,
                        listOperator: null,
                        clauses,
                        nodes);
                }

                for (var index = 0; index < simple.ExecutionRegions.Count; index++)
                {
                    AppendSyntax(
                        simple.ExecutionRegions[index],
                        currentIndex,
                        CommandAncestryRegion.ExecutionRegion,
                        index,
                        listOperator: null,
                        clauses,
                        nodes);
                }

                break;
            case PipelineSyntax pipeline:
                for (var index = 0; index < pipeline.Stages.Count; index++)
                {
                    AppendSyntax(
                        pipeline.Stages[index],
                        currentIndex,
                        CommandAncestryRegion.PipelineStage,
                        index,
                        listOperator: null,
                        clauses,
                        nodes);
                }

                break;
            case CommandListSyntax list:
                for (var index = 0; index < list.Items.Count; index++)
                {
                    AppendSyntax(
                        list.Items[index].Command,
                        currentIndex,
                        CommandAncestryRegion.Statement,
                        index,
                        list.Items[index].Operator,
                        clauses,
                        nodes);
                }

                break;
            case GroupSyntax group:
                AppendSyntax(
                    group.Body,
                    currentIndex,
                    CommandAncestryRegion.GroupBody,
                    childIndex: null,
                    listOperator: null,
                    clauses,
                    nodes);
                break;
            case ForEachSyntax forEach:
                AppendSyntax(
                    forEach.IteratorCommands,
                    currentIndex,
                    CommandAncestryRegion.Iterator,
                    childIndex: null,
                    listOperator: null,
                    clauses,
                    nodes);
                AppendSyntax(
                    forEach.Body,
                    currentIndex,
                    CommandAncestryRegion.LoopBody,
                    childIndex: null,
                    listOperator: null,
                    clauses,
                    nodes);
                break;
            case CommandSubstitutionSyntax substitution:
                AppendSyntax(
                    substitution.Body,
                    currentIndex,
                    CommandAncestryRegion.Substitution,
                    childIndex,
                    listOperator: null,
                    clauses,
                    nodes);
                break;
            case ExecutionRegionSyntax regionNode:
                AppendSyntax(
                    regionNode.Body,
                    currentIndex,
                    CommandAncestryRegion.ExecutionRegion,
                    childIndex,
                    listOperator: null,
                    clauses,
                    nodes);
                break;
            default:
                throw new XunitException(
                    $"Cannot flatten unsupported syntax type {node.GetType().FullName} into corpus expectations");
        }
    }

    private static int FindClauseIndex(IReadOnlyList<Clause> clauses, Clause clause)
    {
        for (var index = 0; index < clauses.Count; index++)
        {
            if (object.ReferenceEquals(clauses[index], clause))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindArgumentIndex(Clause clause, Arg argument)
    {
        for (var index = 0; index < clause.Args.Count; index++)
        {
            if (ReferenceEquals(clause.Args[index], argument))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindElementIndex(Clause clause, ClauseElement element)
    {
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            if (ReferenceEquals(clause.Elements[index], element))
            {
                return index;
            }
        }

        return -1;
    }

    private static int? FindElementIndex(
        IReadOnlyList<Clause> clauses,
        ClauseElement? element)
    {
        if (element is null)
        {
            return null;
        }

        for (var clauseIndex = 0; clauseIndex < clauses.Count; clauseIndex++)
        {
            var index = FindElementIndex(clauses[clauseIndex], element);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string SyntaxKind(ShellSyntaxNode node) => node switch
    {
        ShellBlockSyntax => "Block",
        SimpleCommandSyntax => "SimpleCommand",
        PipelineSyntax => "Pipeline",
        CommandListSyntax => "CommandList",
        GroupSyntax => "Group",
        ForEachSyntax => "ForEach",
        CommandSubstitutionSyntax => "CommandSubstitution",
        ExecutionRegionSyntax => "ExecutionRegion",
        _ => "Unknown",
    };

    private static string Summarize(ExpectedSyntaxNode node) =>
        $"{{kind={node.Kind}, parent={node.ParentIndex}, region={node.Region}, "
        + $"child={node.ChildIndex}, span={node.SourceStart}:{node.SourceLength}, "
        + $"clause={node.ClauseIndex}, group={node.GroupKind}, listOp={node.ListOperator}, "
        + $"binding={node.BindingName}, iterable={node.IterableRaw}, "
        + $"execution={node.ExecutionOrigin}/{node.ExecutionPhase}/"
        + $"{node.ExecutionTiming}/{node.ExecutionCardinality}, "
        + $"hostElement={node.HostClauseElementIndex}}}";

    private static string Summarize(ActualSyntaxNode node) =>
        $"{{kind={node.Kind}, parent={node.ParentIndex}, region={node.Region}, "
        + $"child={node.ChildIndex}, span={node.SourceStart}:{node.SourceLength}, "
        + $"clause={node.ClauseIndex}, group={node.GroupKind}, listOp={node.ListOperator}, "
        + $"binding={node.BindingName}, iterable={node.IterableRaw}, "
        + $"execution={node.ExecutionOrigin}/{node.ExecutionPhase}/"
        + $"{node.ExecutionTiming}/{node.ExecutionCardinality}, "
        + $"hostElement={node.HostClauseElementIndex}}}";

    private sealed record ActualSyntaxNode(
        string Kind,
        int? ParentIndex,
        CommandAncestryRegion Region,
        int? ChildIndex,
        int? SourceStart,
        int? SourceLength,
        int? ClauseIndex,
        ShellGroupKind? GroupKind,
        CompoundOperator? ListOperator,
        string? BindingName,
        string? BindingRaw,
        int? BindingSourceStart,
        int? BindingSourceLength,
        string? IterableRaw,
        int? IterableSourceStart,
        int? IterableSourceLength,
        ExecutionRegionOrigin? ExecutionOrigin,
        int? HostClauseElementIndex,
        ExecutionRegionPhase? ExecutionPhase,
        ExecutionRegionTiming? ExecutionTiming,
        ExecutionRegionCardinality? ExecutionCardinality,
        Clause? Clause);

    private static void AssertClauseEqual(ExpectedClause expected, Clause actual, string path)
    {
        if (expected.Operator != actual.Operator)
        {
            throw new XunitException(
                $"{path}.operator: expected={expected.Operator}, actual={actual.Operator}");
        }

        var expectedVerb = expected.Verb ?? new List<string>();
        if (!expectedVerb.SequenceEqual(actual.Verb.Tokens))
        {
            throw new XunitException(
                $"{path}.verb: expected=[{string.Join(",", expectedVerb)}], actual=[{string.Join(",", actual.Verb.Tokens)}]");
        }

        if (expected.CanonicalVerb != actual.Verb.CanonicalVerb)
        {
            throw new XunitException(
                $"{path}.canonicalVerb: expected={Quote(expected.CanonicalVerb)}, actual={Quote(actual.Verb.CanonicalVerb)}");
        }

        if (expected.IsDynamic != actual.Verb.IsDynamic)
        {
            throw new XunitException(
                $"{path}.isDynamic: expected={expected.IsDynamic}, actual={actual.Verb.IsDynamic}");
        }

        var expectedArgs = expected.Args ?? new List<ExpectedArg>();
        if (expectedArgs.Count != actual.Args.Count)
        {
            throw new XunitException(
                $"{path}.args.count: expected={expectedArgs.Count}, actual={actual.Args.Count}\n"
                + "  actual args: " + string.Join(", ", actual.Args.Select(a => $"{{raw={a.Raw}, kind={a.Kind}, isPath={a.IsPath}, isCwdAttribution={a.IsCwdAttribution}}}")));
        }

        for (var i = 0; i < expectedArgs.Count; i++)
        {
            AssertArgEqual(expectedArgs[i], actual.Args[i], $"{path}.args[{i}]");
        }

        var expectedRedirects = expected.Redirects ?? new List<ExpectedRedirect>();
        if (expectedRedirects.Count != actual.Redirects.Count)
        {
            throw new XunitException(
                $"{path}.redirects.count: expected={expectedRedirects.Count}, actual={actual.Redirects.Count}\n"
                + "  actual redirects: " + string.Join(", ", actual.Redirects.Select(r => $"{{direction={r.Direction}, target={r.Target}, dynamicSkip={r.IsDynamicSkip}}}")));
        }

        for (var i = 0; i < expectedRedirects.Count; i++)
        {
            AssertRedirectEqual(expectedRedirects[i], actual.Redirects[i], $"{path}.redirects[{i}]");
        }

        if (expected.Elements is not null)
        {
            if (expected.Elements.Count != actual.Elements.Count)
            {
                throw new XunitException(
                    $"{path}.elements.count: expected={expected.Elements.Count}, actual={actual.Elements.Count}\n"
                    + "  actual elements: "
                    + string.Join(", ", actual.Elements.Select(element =>
                        $"{{raw={element.Raw}, role={element.Role}, precedingVerbs={element.PrecedingVerbElementCount}}}")));
            }

            for (var i = 0; i < expected.Elements.Count; i++)
            {
                AssertClauseElementEqual(
                    expected.Elements[i], actual.Elements[i], $"{path}.elements[{i}]");
            }
        }

        if (expected.IsSubshell != actual.IsSubshell)
        {
            throw new XunitException(
                $"{path}.isSubshell: expected={expected.IsSubshell}, actual={actual.IsSubshell}");
        }

        if (expected.IsCommandStringWrapped != actual.IsCommandStringWrapped)
        {
            throw new XunitException(
                $"{path}.isCommandStringWrapped: expected={expected.IsCommandStringWrapped}, actual={actual.IsCommandStringWrapped}");
        }
    }

    private static string Quote(string? value) => value is null ? "null" : $"'{value}'";

    private static void AssertArgEqual(ExpectedArg expected, Arg actual, string path)
    {
        if (expected.Raw != actual.Raw)
        {
            throw new XunitException(
                $"{path}.raw: expected='{expected.Raw}', actual='{actual.Raw}'");
        }

        if (expected.Kind != actual.Kind)
        {
            throw new XunitException(
                $"{path}.kind: expected={expected.Kind}, actual={actual.Kind}");
        }

        if (expected.IsPath != actual.IsPath)
        {
            throw new XunitException(
                $"{path}.isPath: expected={expected.IsPath}, actual={actual.IsPath}");
        }

        if (expected.IsCwdAttribution != actual.IsCwdAttribution)
        {
            throw new XunitException(
                $"{path}.isCwdAttribution: expected={expected.IsCwdAttribution}, actual={actual.IsCwdAttribution}");
        }

        if (expected.IsFlag.HasValue && expected.IsFlag.Value != actual.IsFlag)
        {
            throw new XunitException(
                $"{path}.isFlag: expected={expected.IsFlag}, actual={actual.IsFlag}");
        }

        if (expected.Resolved is not null)
        {
            if (expected.Resolved == "__NULL__")
            {
                if (actual.Resolved is not null)
                {
                    throw new XunitException(
                        $"{path}.resolved: expected=null, actual='{actual.Resolved}'");
                }
            }
            else if (expected.Resolved != actual.Resolved)
            {
                throw new XunitException(
                    $"{path}.resolved: expected='{expected.Resolved}', actual='{actual.Resolved}'");
            }
        }
    }

    private static void AssertClauseElementEqual(
        ExpectedClauseElement expected,
        ClauseElement actual,
        string path)
    {
        if (expected.Raw != actual.Raw
            || expected.Value != actual.Value
            || expected.Role != actual.Role
            || expected.SourceStart != actual.SourceStart
            || expected.SourceLength != actual.SourceLength
            || expected.PrecedingVerbElementCount != actual.PrecedingVerbElementCount
            || expected.Kind != actual.Kind
            || expected.IsFlag != actual.IsFlag
            || expected.IsPath != actual.IsPath
            || expected.Resolved != actual.Resolved)
        {
            throw new XunitException(
                $"{path}: expected={{raw={Quote(expected.Raw)}, value={Quote(expected.Value)}, "
                + $"role={expected.Role}, span={expected.SourceStart}:{expected.SourceLength}, "
                + $"precedingVerbs={expected.PrecedingVerbElementCount}, kind={expected.Kind}, "
                + $"isFlag={expected.IsFlag}, isPath={expected.IsPath}, resolved={Quote(expected.Resolved)}}}; "
                + $"actual={{raw={Quote(actual.Raw)}, value={Quote(actual.Value)}, "
                + $"role={actual.Role}, span={actual.SourceStart}:{actual.SourceLength}, "
                + $"precedingVerbs={actual.PrecedingVerbElementCount}, kind={actual.Kind}, "
                + $"isFlag={actual.IsFlag}, isPath={actual.IsPath}, resolved={Quote(actual.Resolved)}}}");
        }
    }

    private static void AssertRedirectEqual(ExpectedRedirect expected, Redirect actual, string path)
    {
        if (expected.Direction != actual.Direction)
        {
            throw new XunitException(
                $"{path}.direction: expected={expected.Direction}, actual={actual.Direction}");
        }

        if (expected.Target != actual.Target)
        {
            throw new XunitException(
                $"{path}.target: expected='{expected.Target}', actual='{actual.Target}'");
        }

        if (expected.IsDynamicSkip.HasValue && expected.IsDynamicSkip.Value != actual.IsDynamicSkip)
        {
            throw new XunitException(
                $"{path}.isDynamicSkip: expected={expected.IsDynamicSkip}, actual={actual.IsDynamicSkip}");
        }
    }

    private static string SummarizeExpectedClauses(IReadOnlyList<ExpectedClause> clauses)
    {
        if (clauses.Count == 0)
        {
            return "[]";
        }

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < clauses.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var c = clauses[i];
            var verbStr = c.Verb is null || c.Verb.Count == 0 ? "(redirect-only)" : string.Join(" ", c.Verb);
            sb.Append($"{c.Operator}:'{verbStr}'");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string SummarizeActualClauses(IReadOnlyList<Clause> clauses)
    {
        if (clauses.Count == 0)
        {
            return "[]";
        }

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < clauses.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var c = clauses[i];
            var verbStr = c.Verb.Tokens.Count == 0 ? "(redirect-only)" : c.Verb.Joined;
            sb.Append($"{c.Operator}:'{verbStr}'");
        }
        sb.Append(']');
        return sb.ToString();
    }
}
