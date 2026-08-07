// -----------------------------------------------------------------------
// <copyright file="AstAssert.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            AssertSyntaxEqual(expected.Syntax, actual, prefix);
        }

        if (expected.Commands is not null)
        {
            AssertCommandsEqual(expected.Commands, actual, prefix);
        }
    }

    private static void AssertSyntaxEqual(
        IReadOnlyList<ExpectedSyntaxNode> expected,
        ParsedCommand actual,
        string prefix)
    {
        ValidateExpectedSyntax(expected, prefix);
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
                wanted.ListOperator != observed.ListOperator)
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
        string prefix)
    {
        if (expected.Count == 0 ||
            expected[0].Kind != ShellSyntaxKind.Block ||
            expected[0].ParentIndex is not null ||
            expected[0].Region != CommandAncestryRegion.Unknown ||
            expected[0].ChildIndex is not null ||
            expected[0].ListOperator is not null)
        {
            throw new XunitException(prefix + "syntax[0] must be the root block");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var node = expected[index];
            if (node.Kind == ShellSyntaxKind.Unknown ||
                (node.Kind == ShellSyntaxKind.SimpleCommand) != node.ClauseIndex.HasValue ||
                (node.Kind == ShellSyntaxKind.Group) != node.GroupKind.HasValue)
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
                ShellSyntaxKind.Block =>
                    node.Region == (node.ParentIndex == 0
                        ? CommandAncestryRegion.Root
                        : CommandAncestryRegion.Statement) &&
                    node.ChildIndex.HasValue,
                ShellSyntaxKind.SimpleCommand =>
                    node.Region == CommandAncestryRegion.Substitution &&
                    node.ChildIndex.HasValue,
                ShellSyntaxKind.Pipeline =>
                    node.Region == CommandAncestryRegion.PipelineStage &&
                    node.ChildIndex.HasValue,
                ShellSyntaxKind.CommandList =>
                    node.Region == CommandAncestryRegion.Statement &&
                    node.ChildIndex.HasValue &&
                    node.ListOperator.HasValue,
                ShellSyntaxKind.Group =>
                    node.Region == CommandAncestryRegion.GroupBody &&
                    node.ChildIndex is null,
                ShellSyntaxKind.ForEach =>
                    (node.Region is CommandAncestryRegion.Iterator or
                        CommandAncestryRegion.LoopBody) &&
                    node.ChildIndex is null,
                ShellSyntaxKind.ConditionLoop =>
                    (node.Region is CommandAncestryRegion.Condition or
                        CommandAncestryRegion.LoopBody) &&
                    node.ChildIndex is null,
                ShellSyntaxKind.Conditional =>
                    node.Region == CommandAncestryRegion.Branch &&
                    node.ChildIndex.HasValue,
                ShellSyntaxKind.ConditionalBranch =>
                    (node.Region is CommandAncestryRegion.Condition or
                        CommandAncestryRegion.Branch) &&
                    node.ChildIndex is null,
                ShellSyntaxKind.CommandSubstitution =>
                    node.Region == CommandAncestryRegion.Substitution,
                _ => false,
            };
            if (!relationshipIsValid ||
                parent.Kind != ShellSyntaxKind.CommandList && node.ListOperator.HasValue)
            {
                throw new XunitException(
                    prefix + $"syntax[{index}] has an invalid parent relationship");
            }
        }
    }

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
                if (expectedFrame.AncestorKind == ShellSyntaxKind.Unknown ||
                    actualFrame.AncestorKind == ShellSyntaxKind.Unknown ||
                    expectedFrame.Region == CommandAncestryRegion.Unknown ||
                    actualFrame.Region == CommandAncestryRegion.Unknown)
                {
                    throw new XunitException(
                        prefix + $"commands[{index}].ancestry[{frameIndex}] contains an unknown enum");
                }

                if (expectedFrame.AncestorKind != actualFrame.AncestorKind ||
                    expectedFrame.Region != actualFrame.Region ||
                    expectedFrame.ChildIndex != actualFrame.ChildIndex ||
                    expectedFrame.SourceStart != actualFrame.SourceStart ||
                    expectedFrame.SourceLength != actualFrame.SourceLength)
                {
                    throw new XunitException(
                        prefix + $"commands[{index}].ancestry[{frameIndex}] differs");
                }
            }
        }
    }

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
        if (node.Kind == ShellSyntaxKind.Unknown)
        {
            throw new XunitException("Cannot flatten an unknown syntax node into corpus expectations");
        }

        var clause = (node as SimpleCommandSyntax)?.Clause;
        int? clauseIndex = clause is null ? null : FindClauseIndex(clauses, clause);
        var currentIndex = nodes.Count;
        nodes.Add(new ActualSyntaxNode(
            node.Kind,
            parentIndex,
            region,
            childIndex,
            node.SourceStart,
            node.SourceLength,
            clauseIndex,
            (node as GroupSyntax)?.GroupKind,
            listOperator,
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
            case ConditionLoopSyntax loop:
                AppendSyntax(
                    loop.Condition,
                    currentIndex,
                    CommandAncestryRegion.Condition,
                    childIndex: null,
                    listOperator: null,
                    clauses,
                    nodes);
                AppendSyntax(
                    loop.Body,
                    currentIndex,
                    CommandAncestryRegion.LoopBody,
                    childIndex: null,
                    listOperator: null,
                    clauses,
                    nodes);
                break;
            case ConditionalSyntax conditional:
                for (var index = 0; index < conditional.Branches.Count; index++)
                {
                    AppendSyntax(
                        conditional.Branches[index],
                        currentIndex,
                        CommandAncestryRegion.Branch,
                        index,
                        listOperator: null,
                        clauses,
                        nodes);
                }

                if (conditional.Else is not null)
                {
                    AppendSyntax(
                        conditional.Else,
                        currentIndex,
                        CommandAncestryRegion.Branch,
                        conditional.Branches.Count,
                        listOperator: null,
                        clauses,
                        nodes);
                }

                break;
            case ConditionalBranchSyntax branch:
                AppendSyntax(
                    branch.Condition,
                    currentIndex,
                    CommandAncestryRegion.Condition,
                    childIndex: null,
                    listOperator: null,
                    clauses,
                    nodes);
                AppendSyntax(
                    branch.Body,
                    currentIndex,
                    CommandAncestryRegion.Branch,
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

    private static string Summarize(ExpectedSyntaxNode node) =>
        $"{{kind={node.Kind}, parent={node.ParentIndex}, region={node.Region}, "
        + $"child={node.ChildIndex}, span={node.SourceStart}:{node.SourceLength}, "
        + $"clause={node.ClauseIndex}, group={node.GroupKind}, listOp={node.ListOperator}}}";

    private static string Summarize(ActualSyntaxNode node) =>
        $"{{kind={node.Kind}, parent={node.ParentIndex}, region={node.Region}, "
        + $"child={node.ChildIndex}, span={node.SourceStart}:{node.SourceLength}, "
        + $"clause={node.ClauseIndex}, group={node.GroupKind}, listOp={node.ListOperator}}}";

    private sealed record ActualSyntaxNode(
        ShellSyntaxKind Kind,
        int? ParentIndex,
        CommandAncestryRegion Region,
        int? ChildIndex,
        int? SourceStart,
        int? SourceLength,
        int? ClauseIndex,
        ShellGroupKind? GroupKind,
        CompoundOperator? ListOperator,
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
