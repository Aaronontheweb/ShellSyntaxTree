// -----------------------------------------------------------------------
// <copyright file="V03DesignCorpusTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ShellSyntaxTree.Tests.DesignCorpus;

public class V03DesignCorpusTests
{
    private const int MaxValueCandidates = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Design_corpus_is_well_formed_and_balanced_across_shells()
    {
        var files = LoadFiles();
        Assert.Equal(new[] { DesignShell.Bash, DesignShell.PowerShell },
            files.Select(file => file.Shell).OrderBy(shell => shell).ToArray());

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            Assert.True(file.Cases.Count >= 10,
                $"{file.Shell} design corpus has only {file.Cases.Count} cases; expected at least 10.");

            foreach (var designCase in file.Cases)
            {
                Assert.False(string.IsNullOrWhiteSpace(designCase.Id));
                Assert.True(ids.Add(designCase.Id), $"Duplicate design-case id '{designCase.Id}'.");
                Assert.False(string.IsNullOrWhiteSpace(designCase.Concern));
                Assert.False(string.IsNullOrWhiteSpace(designCase.Input));
                ValidateDesiredShape(file.Shell, designCase);
            }

            var values = file.Cases
                .SelectMany(designCase => designCase.Desired.Commands)
                .SelectMany(command => command.EffectiveValues)
                .ToArray();
            Assert.True(values.Any(value =>
                    value.Kind == DesignValueKind.FiniteSet
                    && value.Values.Count == MaxValueCandidates),
                $"{file.Shell}: no case exercises the finite candidate cap.");
        }
    }

    [Fact]
    public void Compatibility_projection_matches_current_or_promoted_expectations()
    {
        foreach (var file in LoadFiles())
        {
            var parser = CreateParser(file.Shell);
            foreach (var designCase in file.Cases)
            {
                var actual = parser.Parse(designCase.Input);
                var expectedIsUnparseable = designCase.CompatibilityProjectionLanded
                    ? designCase.Desired.IsUnparseable
                    : designCase.Current.IsUnparseable;
                Assert.True(
                    actual.IsUnparseable == expectedIsUnparseable,
                    $"{designCase.Id}: IsUnparseable expected "
                    + $"{expectedIsUnparseable}, actual {actual.IsUnparseable}. "
                    + $"Reason: {actual.UnparseableReason}");

                if (!designCase.CompatibilityProjectionLanded
                    && designCase.Current.ReasonContains is not null)
                {
                    Assert.Contains(
                        designCase.Current.ReasonContains,
                        actual.UnparseableReason ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase);
                }

                var expectedArgument = designCase.CompatibilityProjectionLanded
                    ? designCase.Desired.Argument
                    : designCase.Current.Argument;
                if (expectedArgument is not null)
                {
                    var expected = expectedArgument;
                    ValidateArgumentExpectation(designCase.Id, expected, actual.Clauses.Count);
                    var clause = Assert.IsType<Clause>(actual.Clauses[expected.ClauseIndex]);
                    var argument = Assert.IsType<Arg>(clause.Args[expected.ArgumentIndex]);
                    Assert.Equal(expected.Raw, argument.Raw);
                    Assert.Equal(expected.Kind, argument.Kind);
                    Assert.Equal(expected.IsPath, argument.IsPath);
                    Assert.Equal(expected.Resolved, argument.Resolved);
                }

                var expectedClause = designCase.CompatibilityProjectionLanded
                    ? designCase.Desired.CompatibilityClause
                    : designCase.Current.CompatibilityClause;
                if (expectedClause is not null)
                {
                    AssertCompatibilityClause(
                        designCase.Id,
                        expectedClause,
                        actual);
                }
            }
        }
    }

    private static void ValidateDesiredShape(DesignShell shell, V03DesignCase designCase)
    {
        var desired = designCase.Desired;
        Assert.NotEmpty(desired.Syntax);
        Assert.NotEmpty(desired.SecurityInvariants);

        var nodes = desired.Syntax.ToDictionary(node => node.Id, StringComparer.Ordinal);
        Assert.Single(desired.Syntax, node => node.Parent is null);

        foreach (var node in desired.Syntax)
        {
            Assert.False(string.IsNullOrWhiteSpace(node.Id), $"{designCase.Id}: syntax node has no id.");
            if (node.Parent is not null)
            {
                Assert.True(nodes.ContainsKey(node.Parent),
                    $"{designCase.Id}: node '{node.Id}' references unknown parent '{node.Parent}'.");
            }

            if (node.CommandIndex is not null)
            {
                Assert.Equal(DesignSyntaxKind.SimpleCommand, node.Kind);
                Assert.InRange(node.CommandIndex.Value, 0, desired.Commands.Count - 1);
            }
        }

        if (desired.IsUnparseable)
        {
            Assert.Contains(SecurityInvariant.PartialTreeDiagnosticOnly, desired.SecurityInvariants);
            Assert.Empty(desired.Commands);
            Assert.Empty(desired.Compatibility.Verbs);
            Assert.Null(desired.CompatibilityClause);
        }
        else
        {
            Assert.NotEmpty(desired.Commands);
            Assert.Equal(
                Enumerable.Range(0, desired.Commands.Count),
                desired.Syntax
                    .Where(node => node.CommandIndex is not null)
                    .Select(node => node.CommandIndex!.Value)
                    .OrderBy(index => index));
        }

        foreach (var command in desired.Commands)
        {
            Assert.False(string.IsNullOrWhiteSpace(command.AuthoredVerb));
            Assert.NotNull(command.ImmediateRole);
            Assert.NotEmpty(command.Ancestry);
            foreach (var ancestor in command.Ancestry)
            {
                Assert.True(nodes.ContainsKey(ancestor),
                    $"{designCase.Id}: command '{command.AuthoredVerb}' references unknown ancestor '{ancestor}'.");
            }

            foreach (var value in command.EffectiveValues)
            {
                ValidateValue(shell, designCase.Id, value);
            }

            foreach (var redirect in command.Redirects)
            {
                Assert.True(redirect.IsPathRelevant == (redirect.Operation is
                    DesignRedirectOperation.FileInput
                    or DesignRedirectOperation.FileOutput
                    or DesignRedirectOperation.FileAppend
                    or DesignRedirectOperation.CombinedOutput
                    or DesignRedirectOperation.CombinedOutputAppend));

                if (redirect.Target is not null)
                {
                    ValidateValue(shell, designCase.Id, redirect.Target);
                }

                Assert.Equal(
                    redirect.Operation == DesignRedirectOperation.HereDocument,
                    redirect.HereDocument is not null);

                if (redirect.HereDocument is not null)
                {
                    Assert.False(string.IsNullOrWhiteSpace(redirect.HereDocument.DelimiterRaw));
                    Assert.NotEqual(
                        DesignHereDocumentExpansionMode.Unknown,
                        redirect.HereDocument.ExpansionMode);
                }

                if (redirect.Operation == DesignRedirectOperation.HereString)
                {
                    Assert.NotNull(redirect.Target);
                }
            }
        }

        Assert.Equal(
            desired.Commands.Select(command => command.AuthoredVerb),
            desired.Compatibility.Verbs);

        if (desired.Argument is not null)
        {
            ValidateArgumentExpectation(
                designCase.Id,
                desired.Argument,
                desired.Compatibility.Verbs.Count);
        }

        if (desired.CompatibilityClause is not null)
        {
            ValidateCompatibilityClauseExpectation(
                designCase.Id,
                desired.CompatibilityClause,
                desired.Compatibility.Verbs.Count);
        }
    }

    private static void AssertCompatibilityClause(
        string caseId,
        CompatibilityClauseExpectation expected,
        ParsedCommand actual)
    {
        ValidateCompatibilityClauseExpectation(caseId, expected, actual.Clauses.Count);
        var clause = Assert.IsType<Clause>(actual.Clauses[expected.ClauseIndex]);

        if (expected.ArgumentCount is not null)
        {
            Assert.Equal(expected.ArgumentCount.Value, clause.Args.Count);
        }

        if (expected.RedirectCount is not null)
        {
            Assert.Equal(expected.RedirectCount.Value, clause.Redirects.Count);
        }

        if (expected.ElementCount is not null)
        {
            Assert.Equal(expected.ElementCount.Value, clause.Elements.Count);
        }

        if (expected.Redirect is not null)
        {
            var redirect = Assert.IsType<Redirect>(clause.Redirects[expected.Redirect.RedirectIndex]);
            Assert.Equal(expected.Redirect.Direction, redirect.Direction);
            Assert.Equal(expected.Redirect.Target, redirect.Target);
            Assert.Equal(expected.Redirect.IsDynamicSkip, redirect.IsDynamicSkip);
        }

        if (expected.Element is not null)
        {
            var element = Assert.IsType<ClauseElement>(clause.Elements[expected.Element.ElementIndex]);
            Assert.Equal(expected.Element.Raw, element.Raw);
            Assert.Equal(expected.Element.Value, element.Value);
            Assert.Equal(expected.Element.Role, element.Role);
            Assert.Equal(expected.Element.SourceStart, element.SourceStart);
            Assert.Equal(expected.Element.SourceLength, element.SourceLength);
            Assert.Equal(
                expected.Element.PrecedingVerbElementCount,
                element.PrecedingVerbElementCount);
            Assert.Equal(expected.Element.Kind, element.Kind);
            Assert.Equal(expected.Element.IsFlag, element.IsFlag);
            Assert.Equal(expected.Element.IsPath, element.IsPath);
            Assert.Equal(expected.Element.Resolved, element.Resolved);
        }
    }

    private static void ValidateCompatibilityClauseExpectation(
        string caseId,
        CompatibilityClauseExpectation expected,
        int clauseCount)
    {
        Assert.InRange(expected.ClauseIndex, 0, clauseCount - 1);

        if (expected.ArgumentCount is not null)
        {
            Assert.True(expected.ArgumentCount >= 0, $"{caseId}: argument count must be non-negative.");
        }

        if (expected.RedirectCount is not null)
        {
            Assert.True(expected.RedirectCount >= 0, $"{caseId}: redirect count must be non-negative.");
        }

        if (expected.ElementCount is not null)
        {
            Assert.True(expected.ElementCount >= 0, $"{caseId}: element count must be non-negative.");
        }

        if (expected.Redirect is not null)
        {
            Assert.NotNull(expected.RedirectCount);
            Assert.InRange(expected.Redirect.RedirectIndex, 0, expected.RedirectCount.Value - 1);
            Assert.False(string.IsNullOrWhiteSpace(expected.Redirect.Target));
        }

        if (expected.Element is not null)
        {
            Assert.NotNull(expected.ElementCount);
            Assert.InRange(expected.Element.ElementIndex, 0, expected.ElementCount.Value - 1);
            Assert.False(string.IsNullOrWhiteSpace(expected.Element.Raw));
            Assert.False(string.IsNullOrWhiteSpace(expected.Element.Value));
        }
    }

    private static void ValidateArgumentExpectation(
        string caseId, ArgumentExpectation argument, int clauseCount)
    {
        Assert.InRange(argument.ClauseIndex, 0, clauseCount - 1);
        Assert.True(argument.ArgumentIndex >= 0, $"{caseId}: argument index must be non-negative.");
        Assert.False(string.IsNullOrWhiteSpace(argument.Raw));
        if (argument.Resolved is not null)
        {
            Assert.True(argument.IsPath, $"{caseId}: a resolved argument must be path-relevant.");
            Assert.NotEqual(ArgKind.DynamicSkip, argument.Kind);
        }
    }

    private static void ValidateValue(
        DesignShell shell, string caseId, DesignValueExpectation value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value.SourceElement));
        switch (value.Kind)
        {
            case DesignValueKind.Exact:
                Assert.Single(value.Values);
                Assert.Null(value.Pattern);
                break;
            case DesignValueKind.FiniteSet:
                Assert.InRange(value.Values.Count, 2, MaxValueCandidates);
                Assert.Equal(value.Values.Count, value.Values.Distinct(StringComparer.Ordinal).Count());
                Assert.Null(value.Pattern);
                break;
            case DesignValueKind.Pattern:
                Assert.Empty(value.Values);
                Assert.False(string.IsNullOrWhiteSpace(value.Pattern));
                Assert.False(string.IsNullOrWhiteSpace(value.CoveringDirectory));
                break;
            case DesignValueKind.Unknown:
                Assert.Empty(value.Values);
                Assert.Null(value.Pattern);
                Assert.Null(value.CoveringDirectory);
                break;
            default:
                throw new InvalidOperationException(
                    $"{caseId}: unsupported value kind '{value.Kind}' for {shell}.");
        }
    }

    private static IShellParser CreateParser(DesignShell shell) => shell switch
    {
        DesignShell.Bash => new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
        }),
        DesignShell.PowerShell => new PwshParser(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/user",
            WorkingDirectory = "C:/work",
        }),
        _ => throw new InvalidOperationException($"Unsupported design-corpus shell '{shell}'."),
    };

    private static IReadOnlyList<V03DesignCorpusFile> LoadFiles()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "DesignCorpus", "v0.3");
        Assert.True(Directory.Exists(root), $"Design corpus directory not found: {root}");

        return Directory.GetFiles(root, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<V03DesignCorpusFile>(
                File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"Design corpus file deserialized to null: {path}"))
            .ToArray();
    }
}

public sealed record V03DesignCorpusFile
{
    public DesignShell Shell { get; init; }

    public IReadOnlyList<V03DesignCase> Cases { get; init; } = [];
}

public sealed record V03DesignCase
{
    public string Id { get; init; } = "";

    public string Concern { get; init; } = "";

    public string Input { get; init; } = "";

    public bool CompatibilityProjectionLanded { get; init; }

    public CurrentBehaviorExpectation Current { get; init; } = new();

    public DesiredDesignExpectation Desired { get; init; } = new();

    public string? Notes { get; init; }
}

public sealed record CurrentBehaviorExpectation
{
    public bool IsUnparseable { get; init; }

    public string? ReasonContains { get; init; }

    public ArgumentExpectation? Argument { get; init; }

    public CompatibilityClauseExpectation? CompatibilityClause { get; init; }
}

public sealed record ArgumentExpectation
{
    public int ClauseIndex { get; init; }

    public int ArgumentIndex { get; init; }

    public string Raw { get; init; } = "";

    public ArgKind Kind { get; init; }

    public bool IsPath { get; init; }

    public string? Resolved { get; init; }
}

public sealed record DesiredDesignExpectation
{
    public bool IsUnparseable { get; init; }

    public IReadOnlyList<DesignSyntaxExpectation> Syntax { get; init; } = [];

    public IReadOnlyList<DesignCommandExpectation> Commands { get; init; } = [];

    public ArgumentExpectation? Argument { get; init; }

    public CompatibilityClauseExpectation? CompatibilityClause { get; init; }

    public CompatibilityExpectation Compatibility { get; init; } = new();

    public IReadOnlyList<SecurityInvariant> SecurityInvariants { get; init; } = [];
}

public sealed record DesignSyntaxExpectation
{
    public string Id { get; init; } = "";

    public DesignSyntaxKind Kind { get; init; }

    public string? Parent { get; init; }

    public DesignSyntaxSlot Slot { get; init; }

    public string? Binding { get; init; }

    public int? CommandIndex { get; init; }
}

public sealed record DesignCommandExpectation
{
    public string AuthoredVerb { get; init; } = "";

    public string? CanonicalVerb { get; init; }

    public DesignCommandRole? ImmediateRole { get; init; }

    public IReadOnlyList<string> Ancestry { get; init; } = [];

    public bool IsComplete { get; init; }

    public IReadOnlyList<DesignValueExpectation> EffectiveValues { get; init; } = [];

    public IReadOnlyList<DesignRedirectExpectation> Redirects { get; init; } = [];
}

public sealed record DesignValueExpectation
{
    public string SourceElement { get; init; } = "";

    public DesignValueKind Kind { get; init; }

    public IReadOnlyList<string> Values { get; init; } = [];

    public string? Pattern { get; init; }

    public string? CoveringDirectory { get; init; }

    public bool IsPolicySensitive { get; init; }
}

public sealed record CompatibilityExpectation
{
    public IReadOnlyList<string> Verbs { get; init; } = [];

    public bool PreservesAuthoredDynamicValues { get; init; }
}

public sealed record CompatibilityClauseExpectation
{
    public int ClauseIndex { get; init; }

    public int? ArgumentCount { get; init; }

    public int? RedirectCount { get; init; }

    public int? ElementCount { get; init; }

    public CompatibilityRedirectExpectation? Redirect { get; init; }

    public CompatibilityElementExpectation? Element { get; init; }
}

public sealed record CompatibilityRedirectExpectation
{
    public int RedirectIndex { get; init; }

    public RedirectDirection Direction { get; init; }

    public string Target { get; init; } = "";

    public bool IsDynamicSkip { get; init; }
}

public sealed record CompatibilityElementExpectation
{
    public int ElementIndex { get; init; }

    public string Raw { get; init; } = "";

    public string Value { get; init; } = "";

    public ClauseElementRole Role { get; init; }

    public int? SourceStart { get; init; }

    public int? SourceLength { get; init; }

    public int PrecedingVerbElementCount { get; init; }

    public ArgKind Kind { get; init; }

    public bool IsFlag { get; init; }

    public bool IsPath { get; init; }

    public string? Resolved { get; init; }
}

public sealed record DesignRedirectExpectation
{
    public DesignRedirectOperation Operation { get; init; }

    public int? SourceDescriptor { get; init; }

    public int? TargetDescriptor { get; init; }

    public DesignValueExpectation? Target { get; init; }

    public DesignHereDocumentExpectation? HereDocument { get; init; }

    public bool IsPathRelevant { get; init; }

    public bool IsComplete { get; init; }
}

public sealed record DesignHereDocumentExpectation
{
    public string DelimiterRaw { get; init; } = "";

    public string BodyRaw { get; init; } = "";

    public DesignHereDocumentExpansionMode ExpansionMode { get; init; }

    public bool StripLeadingTabs { get; init; }

    public bool IsComplete { get; init; }
}

public enum DesignHereDocumentExpansionMode
{
    Unknown,
    Literal,
    Expand,
}

public enum DesignShell
{
    Bash,
    PowerShell,
}

public enum DesignSyntaxKind
{
    Block,
    CommandList,
    Pipeline,
    ForEach,
    ConditionLoop,
    Conditional,
    SimpleCommand,
    OpaqueArgument,
    Unsupported,
}

public enum DesignSyntaxSlot
{
    Root,
    Statement,
    Iterator,
    Condition,
    Body,
    Then,
    Else,
    Stage,
    Argument,
}

public enum DesignCommandRole
{
    Ordinary,
    PipelineStage,
    Condition,
    Iterator,
    LoopBody,
    Branch,
    Substitution,
}

public enum DesignValueKind
{
    Unknown,
    Exact,
    FiniteSet,
    Pattern,
}

public enum DesignRedirectOperation
{
    Unknown,
    FileInput,
    FileOutput,
    FileAppend,
    DescriptorDuplicate,
    DescriptorClose,
    DescriptorMove,
    CombinedOutput,
    CombinedOutputAppend,
    HereDocument,
    HereString,
}

public enum SecurityInvariant
{
    AllCommandsVisible,
    IteratorCommandsVisible,
    ConditionCommandsVisible,
    UnknownPolicyValueFailsClosed,
    EveryFiniteCandidateEvaluated,
    NoFilesystemEnumeration,
    StateJoinConservative,
    NoSyntheticOperator,
    PartialTreeDiagnosticOnly,
    OpaqueDataNotExecuted,
    ContextualKeywordNotControlFlow,
    ShellSpecificOptionSemantics,
    StaticRedirectNotDynamic,
    LiteralExpansionProvenancePreserved,
}
