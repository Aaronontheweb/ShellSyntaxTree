// -----------------------------------------------------------------------
// <copyright file="V03PublicApiSnapshotTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ShellSyntaxTree.Tests;

/// <summary>Locks the stable-v0.3 parser-owned public result surface.</summary>
public class V03PublicApiSnapshotTests
{
    [Fact]
    public void Alpha_only_public_shapes_are_absent()
    {
        var assembly = typeof(ShellSyntaxNode).Assembly;
        var removed = new[]
        {
            "ShellSyntaxTree.ShellSyntaxKind",
            "ShellSyntaxTree.LoopBindingSyntax",
            "ShellSyntaxTree.ConditionLoopSyntax",
            "ShellSyntaxTree.ConditionLoopKind",
            "ShellSyntaxTree.ConditionalSyntax",
            "ShellSyntaxTree.ConditionalBranchSyntax",
            "ShellSyntaxTree.EffectiveArgument",
            "ShellSyntaxTree.ShellValueDomainKind",
            "ShellSyntaxTree.RedirectOperation",
            "ShellSyntaxTree.RedirectSourceKind",
            "ShellSyntaxTree.ShellAnalysisLimits",
        };

        foreach (var name in removed)
        {
            var type = assembly.GetType(name, throwOnError: false);
            Assert.True(type is null || (!type.IsPublic && !type.IsNestedPublic));
        }
    }

    [Fact]
    public void Syntax_family_matches_the_stable_contract()
    {
        AssertClosedBase(
            typeof(ShellSyntaxNode),
            (nameof(ShellSyntaxNode.SourceStart), typeof(int?)),
            (nameof(ShellSyntaxNode.SourceLength), typeof(int?)));
        AssertResultRecord(
            typeof(ShellBlockSyntax),
            (nameof(ShellBlockSyntax.Statements), typeof(IReadOnlyList<ShellSyntaxNode>)));
        AssertResultRecord(
            typeof(SimpleCommandSyntax),
            (nameof(SimpleCommandSyntax.Clause), typeof(Clause)),
            (nameof(SimpleCommandSyntax.Substitutions),
                typeof(IReadOnlyList<CommandSubstitutionSyntax>)),
            (nameof(SimpleCommandSyntax.ExecutionRegions),
                typeof(IReadOnlyList<ExecutionRegionSyntax>)));
        AssertResultRecord(
            typeof(PipelineSyntax),
            (nameof(PipelineSyntax.Stages), typeof(IReadOnlyList<ShellSyntaxNode>)));
        AssertResultRecord(
            typeof(CommandListSyntax),
            (nameof(CommandListSyntax.Items), typeof(IReadOnlyList<CommandListItemSyntax>)));
        AssertResultRecord(
            typeof(CommandListItemSyntax),
            (nameof(CommandListItemSyntax.Operator), typeof(CompoundOperator)),
            (nameof(CommandListItemSyntax.Command), typeof(ShellSyntaxNode)));
        AssertResultRecord(
            typeof(GroupSyntax),
            (nameof(GroupSyntax.GroupKind), typeof(ShellGroupKind)),
            (nameof(GroupSyntax.Body), typeof(ShellBlockSyntax)));
        AssertResultRecord(
            typeof(ForEachSyntax),
            (nameof(ForEachSyntax.BindingName), typeof(string)),
            (nameof(ForEachSyntax.BindingSource), typeof(ShellSourceFragment)),
            (nameof(ForEachSyntax.Iterable), typeof(ShellSourceFragment)),
            (nameof(ForEachSyntax.IteratorCommands), typeof(ShellBlockSyntax)),
            (nameof(ForEachSyntax.Body), typeof(ShellBlockSyntax)));
        AssertResultRecord(
            typeof(ShellSourceFragment),
            (nameof(ShellSourceFragment.Raw), typeof(string)),
            (nameof(ShellSourceFragment.SourceStart), typeof(int?)),
            (nameof(ShellSourceFragment.SourceLength), typeof(int?)));
        AssertResultRecord(
            typeof(CommandSubstitutionSyntax),
            (nameof(CommandSubstitutionSyntax.Body), typeof(ShellBlockSyntax)));
        AssertResultRecord(
            typeof(ExecutionRegionSyntax),
            (nameof(ExecutionRegionSyntax.Origin), typeof(ExecutionRegionOrigin)),
            (nameof(ExecutionRegionSyntax.HostArgument), typeof(ClauseElement)),
            (nameof(ExecutionRegionSyntax.Phase), typeof(ExecutionRegionPhase)),
            (nameof(ExecutionRegionSyntax.Timing), typeof(ExecutionRegionTiming)),
            (nameof(ExecutionRegionSyntax.Cardinality), typeof(ExecutionRegionCardinality)),
            (nameof(ExecutionRegionSyntax.Body), typeof(ShellBlockSyntax)));
    }

    [Fact]
    public void Occurrence_family_matches_the_stable_contract()
    {
        AssertResultRecord(
            typeof(CommandOccurrence),
            (nameof(CommandOccurrence.Clause), typeof(Clause)),
            (nameof(CommandOccurrence.ImmediateRole), typeof(CommandOccurrenceRole)),
            (nameof(CommandOccurrence.Ancestry),
                typeof(IReadOnlyList<CommandAncestryFrame>)),
            (nameof(CommandOccurrence.Arguments), typeof(IReadOnlyList<AnalyzedArgument>)),
            (nameof(CommandOccurrence.WorkingDirectory), typeof(ShellValueDomain)),
            (nameof(CommandOccurrence.Redirects), typeof(IReadOnlyList<RedirectAnalysis>)),
            (nameof(CommandOccurrence.IsComplete), typeof(bool)));
        AssertResultRecord(
            typeof(CommandAncestryFrame),
            (nameof(CommandAncestryFrame.Ancestor), typeof(ShellSyntaxNode)),
            (nameof(CommandAncestryFrame.Region), typeof(CommandAncestryRegion)),
            (nameof(CommandAncestryFrame.ChildIndex), typeof(int?)));
        AssertResultRecord(
            typeof(AnalyzedArgument),
            (nameof(AnalyzedArgument.Argument), typeof(Arg)),
            (nameof(AnalyzedArgument.Element), typeof(ClauseElement)),
            (nameof(AnalyzedArgument.Value), typeof(ShellValueDomain)));

        AssertEnum<CommandOccurrenceRole>(
            "Unknown", "Ordinary", "PipelineStage", "Iterator", "LoopBody",
            "Substitution", "ExecutionRegion");
        AssertEnum<CommandAncestryRegion>(
            "Unknown", "Root", "Statement", "PipelineStage", "GroupBody",
            "Iterator", "LoopBody", "Substitution", "ExecutionRegion");
    }

    [Fact]
    public void Value_domain_is_a_closed_runtime_discriminated_family()
    {
        AssertClosedBase(typeof(ShellValueDomain));
        AssertResultRecord(typeof(ShellValueDomain.Unknown));
        AssertResultRecordAccessors(
            typeof(ShellValueDomain.Exact),
            (nameof(ShellValueDomain.Exact.Value), typeof(string), false));
        AssertResultRecordAccessors(
            typeof(ShellValueDomain.FiniteSet),
            (nameof(ShellValueDomain.FiniteSet.Values),
                typeof(IReadOnlyList<string>), false));
        AssertResultRecordAccessors(
            typeof(ShellValueDomain.PathPattern),
            (nameof(ShellValueDomain.PathPattern.Pattern), typeof(string), false),
            (nameof(ShellValueDomain.PathPattern.CoveringDirectory), typeof(string), false));
    }

    [Fact]
    public void Redirect_families_match_the_stable_contract()
    {
        AssertClosedBase(typeof(RedirectSource));
        AssertResultRecord(typeof(RedirectSource.Unknown));
        AssertResultRecord(typeof(RedirectSource.Default));
        AssertResultRecordAccessors(
            typeof(RedirectSource.Descriptor),
            (nameof(RedirectSource.Descriptor.Value), typeof(int), false));
        AssertResultRecord(typeof(RedirectSource.PowerShellAllStreams));

        AssertClosedBase(
            typeof(RedirectAnalysis),
            (nameof(RedirectAnalysis.Authored), typeof(Redirect)),
            (nameof(RedirectAnalysis.Source), typeof(RedirectSource)),
            (nameof(RedirectAnalysis.IsComplete), typeof(bool)));
        AssertResultRecordAccessors(
            typeof(FileRedirectAnalysis),
            (nameof(FileRedirectAnalysis.Mode), typeof(FileRedirectMode), false),
            (nameof(FileRedirectAnalysis.Target), typeof(ShellValueDomain), true));
        AssertResultRecord(typeof(UnresolvedRedirectAnalysis));
        AssertResultRecord(
            typeof(DescriptorDuplicateRedirectAnalysis),
            (nameof(DescriptorDuplicateRedirectAnalysis.TargetDescriptor), typeof(int)));
        AssertResultRecord(
            typeof(DescriptorMoveRedirectAnalysis),
            (nameof(DescriptorMoveRedirectAnalysis.TargetDescriptor), typeof(int)));
        AssertResultRecord(typeof(DescriptorCloseRedirectAnalysis));
        AssertResultRecord(
            typeof(HereDocumentRedirectAnalysis),
            (nameof(HereDocumentRedirectAnalysis.Document),
                typeof(HereDocumentAnalysis)));
        AssertResultRecord(
            typeof(HereStringRedirectAnalysis),
            (nameof(HereStringRedirectAnalysis.Data), typeof(ShellValueDomain)));
        AssertResultRecord(
            typeof(HereDocumentAnalysis),
            (nameof(HereDocumentAnalysis.Delimiter), typeof(ShellSourceFragment)),
            (nameof(HereDocumentAnalysis.Body), typeof(ShellSourceFragment)),
            (nameof(HereDocumentAnalysis.ExpansionMode),
                typeof(HereDocumentExpansionMode)),
            (nameof(HereDocumentAnalysis.StripLeadingTabs), typeof(bool)),
            (nameof(HereDocumentAnalysis.IsComplete), typeof(bool)));

        AssertEnum<FileRedirectMode>(
            "Input", "Output", "Append", "CombinedOutput", "CombinedOutputAppend");
        AssertEnum<HereDocumentExpansionMode>("Unknown", "Literal", "Expand");
    }

    [Fact]
    public void Public_read_only_lists_are_defensive_copies()
    {
        var statement = new SimpleCommandSyntax();
        var statements = new ShellSyntaxNode[] { statement };
        var block = new ShellBlockSyntax { Statements = statements };
        statements[0] = new PipelineSyntax();
        Assert.Same(statement, Assert.Single(block.Statements));

        var substitution = new CommandSubstitutionSyntax();
        var substitutions = new[] { substitution };
        var executionRegion = new ExecutionRegionSyntax();
        var executionRegions = new[] { executionRegion };
        var command = new SimpleCommandSyntax
        {
            Substitutions = substitutions,
            ExecutionRegions = executionRegions,
        };
        substitutions[0] = new CommandSubstitutionSyntax();
        executionRegions[0] = new ExecutionRegionSyntax();
        Assert.Same(substitution, Assert.Single(command.Substitutions));
        Assert.Same(executionRegion, Assert.Single(command.ExecutionRegions));

        var stage = new SimpleCommandSyntax();
        var stages = new ShellSyntaxNode[] { stage };
        var pipeline = new PipelineSyntax { Stages = stages };
        stages[0] = new SimpleCommandSyntax();
        Assert.Same(stage, Assert.Single(pipeline.Stages));

        var item = new CommandListItemSyntax();
        var items = new[] { item };
        var commandList = new CommandListSyntax { Items = items };
        items[0] = new CommandListItemSyntax();
        Assert.Same(item, Assert.Single(commandList.Items));

        var frame = new CommandAncestryFrame();
        var ancestry = new[] { frame };
        var analyzed = new AnalyzedArgument();
        var arguments = new[] { analyzed };
        var redirect = new UnresolvedRedirectAnalysis();
        var redirects = new RedirectAnalysis[] { redirect };
        var occurrence = new CommandOccurrence
        {
            Ancestry = ancestry,
            Arguments = arguments,
            Redirects = redirects,
        };
        ancestry[0] = new CommandAncestryFrame();
        arguments[0] = new AnalyzedArgument();
        redirects[0] = new UnresolvedRedirectAnalysis();
        Assert.Same(frame, Assert.Single(occurrence.Ancestry));
        Assert.Same(analyzed, Assert.Single(occurrence.Arguments));
        Assert.Same(redirect, Assert.Single(occurrence.Redirects));

        var occurrences = new[] { occurrence };
        var parsed = new ParsedCommand { Commands = occurrences };
        occurrences[0] = new CommandOccurrence();
        Assert.Same(occurrence, Assert.Single(parsed.Commands));

        var values = new[] { "a", "b" };
        var finite = new ShellValueDomain.FiniteSet(values);
        values[0] = "changed";
        Assert.Equal(new[] { "a", "b" }, finite.Values);

        var readOnlyLists = new object[]
        {
            block.Statements,
            command.Substitutions,
            command.ExecutionRegions,
            pipeline.Stages,
            commandList.Items,
            occurrence.Ancestry,
            occurrence.Arguments,
            occurrence.Redirects,
            parsed.Commands,
            finite.Values,
        };
        Assert.All(readOnlyLists, collection => Assert.False(collection.GetType().IsArray));
    }

    [Fact]
    public void Stable_v03_enums_match_the_locked_order()
    {
        AssertEnum<ShellGroupKind>("Unknown", "CurrentScope", "IsolatedScope");
        AssertEnum<ExecutionRegionOrigin>(
            "Unknown", "DirectCall", "DotSource", "CommandArgument");
        AssertEnum<ExecutionRegionPhase>(
            "Unknown", "Main", "Initialization", "Begin", "Process", "End",
            "Filter", "Action", "Completion");
        AssertEnum<ExecutionRegionTiming>(
            "Unknown", "Synchronous", "Concurrent", "Deferred");
        AssertEnum<ExecutionRegionCardinality>(
            "Unknown", "Once", "OncePerInputObject", "ZeroOrMore");
    }

    [Fact]
    public void Parsed_command_record_behavior_includes_the_new_projections()
    {
        var syntax = new ShellBlockSyntax();
        var occurrence = new CommandOccurrence();
        var commands = new[] { occurrence };
        var parsed = new ParsedCommand
        {
            Source = "echo ready",
            Syntax = syntax,
            Commands = commands,
        };
        var equivalent = parsed with { };

        Assert.Equal(parsed, equivalent);
        Assert.Equal(parsed.GetHashCode(), equivalent.GetHashCode());
        Assert.Contains("Syntax =", parsed.ToString(), StringComparison.Ordinal);
        Assert.Contains("Commands =", parsed.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(parsed, parsed with
        {
            Syntax = new ShellBlockSyntax { SourceStart = 1 },
        });
        Assert.NotEqual(parsed, parsed with { Commands = Array.Empty<CommandOccurrence>() });
    }

    [Fact]
    public void Default_json_is_not_a_polymorphic_value_domain_round_trip()
    {
        ShellValueDomain value = new ShellValueDomain.Exact("ready");

        var json = JsonSerializer.Serialize(value);

        Assert.Throws<NotSupportedException>(() =>
            JsonSerializer.Deserialize<ShellValueDomain>(json));
    }

    [Fact]
    public void Unknown_future_enum_values_remain_detectable()
    {
        var enums = new[]
        {
            typeof(CommandOccurrenceRole),
            typeof(CommandAncestryRegion),
            typeof(ShellGroupKind),
            typeof(ExecutionRegionOrigin),
            typeof(ExecutionRegionPhase),
            typeof(ExecutionRegionTiming),
            typeof(ExecutionRegionCardinality),
            typeof(FileRedirectMode),
            typeof(HereDocumentExpansionMode),
        };

        Assert.All(enums, type => Assert.False(Enum.IsDefined(type, 999)));
    }

    private static void AssertClosedBase(
        Type type,
        params (string Name, Type Type)[] properties)
    {
        Assert.True(type.IsPublic);
        Assert.True(type.IsAbstract);
        AssertRecord(type);
        AssertProperties(type, properties.Select(property =>
            (property.Name, property.Type, true)).ToArray());

        var constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
        Assert.NotNull(constructor);
        Assert.True(constructor!.IsFamilyAndAssembly);

        var ownership = type.GetProperty(
            "LibraryOwnership",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ownership);
        Assert.True(ownership!.GetMethod!.IsAbstract);
        Assert.True(ownership.GetMethod.IsFamilyAndAssembly);
    }

    private static void AssertResultRecord(
        Type type,
        params (string Name, Type Type)[] properties) =>
        AssertResultRecordAccessors(
            type,
            properties.Select(property => (property.Name, property.Type, true)).ToArray());

    private static void AssertResultRecordAccessors(
        Type type,
        params (string Name, Type Type, bool HasInternalSetter)[] properties)
    {
        Assert.True(type.IsNestedPublic || type.IsPublic);
        Assert.True(type.IsSealed);
        AssertRecord(type);
        AssertProperties(type, properties);
        Assert.Empty(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    private static void AssertProperties(
        Type type,
        IReadOnlyList<(string Name, Type Type, bool HasInternalSetter)> expected)
    {
        var properties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .OrderBy(property => property.Name)
            .ToArray();
        Assert.Equal(expected.Select(property => property.Name).OrderBy(name => name),
            properties.Select(property => property.Name));
        foreach (var property in expected)
        {
            var actual = Assert.Single(properties, candidate => candidate.Name == property.Name);
            Assert.Equal(property.Type, actual.PropertyType);
            Assert.True(actual.GetMethod!.IsPublic);
            var setter = actual.GetSetMethod(nonPublic: true);
            if (property.HasInternalSetter)
            {
                Assert.NotNull(setter);
                Assert.True(setter!.IsAssembly);
            }
            else
            {
                Assert.Null(setter);
            }
        }
    }

    private static void AssertRecord(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance |
                                   BindingFlags.Public |
                                   BindingFlags.NonPublic;
        Assert.NotNull(type.GetMethod("<Clone>$", flags));
        Assert.NotNull(type.GetProperty("EqualityContract", flags));
    }

    private static void AssertEnum<T>(params string[] names)
        where T : struct, Enum => Assert.Equal(names, Enum.GetNames(typeof(T)));
}
