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

/// <summary>Locks the additive v0.3 public API and its safe defaults.</summary>
public class V03PublicApiSnapshotTests
{
    [Fact]
    public void Syntax_node_base_is_record_shaped_and_closed_to_external_implementations()
    {
        var type = typeof(ShellSyntaxNode);
        Assert.True(type.IsPublic);
        Assert.True(type.IsAbstract);
        AssertRecord(type);

        AssertGetProperty(type, nameof(ShellSyntaxNode.Kind), typeof(ShellSyntaxKind));
        AssertInitProperty(type, nameof(ShellSyntaxNode.SourceStart), typeof(int?));
        AssertInitProperty(type, nameof(ShellSyntaxNode.SourceLength), typeof(int?));
        Assert.Equal(
            new[]
            {
                nameof(ShellSyntaxNode.Kind),
                nameof(ShellSyntaxNode.SourceLength),
                nameof(ShellSyntaxNode.SourceStart),
            },
            DeclaredPublicProperties(type));

        var constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
        Assert.NotNull(constructor);
        Assert.True(constructor!.IsFamilyAndAssembly);

        var ownership = type.GetProperty(
            "IsLibraryOwnedNode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ownership);
        Assert.NotNull(ownership!.GetMethod);
        Assert.True(ownership.GetMethod!.IsAbstract);
        Assert.True(ownership.GetMethod.IsFamilyAndAssembly);
    }

    [Fact]
    public void Syntax_records_match_the_locked_members_and_defaults()
    {
        AssertNode(
            new ShellBlockSyntax(),
            ShellSyntaxKind.Block,
            (nameof(ShellBlockSyntax.Statements), typeof(IReadOnlyList<ShellSyntaxNode>)));
        AssertNode(
            new SimpleCommandSyntax(),
            ShellSyntaxKind.SimpleCommand,
            (nameof(SimpleCommandSyntax.Clause), typeof(Clause)),
            (nameof(SimpleCommandSyntax.Substitutions),
                typeof(IReadOnlyList<CommandSubstitutionSyntax>)),
            (nameof(SimpleCommandSyntax.ExecutionRegions),
                typeof(IReadOnlyList<ExecutionRegionSyntax>)));
        AssertNode(
            new PipelineSyntax(),
            ShellSyntaxKind.Pipeline,
            (nameof(PipelineSyntax.Stages), typeof(IReadOnlyList<ShellSyntaxNode>)));
        AssertNode(
            new CommandListSyntax(),
            ShellSyntaxKind.CommandList,
            (nameof(CommandListSyntax.Items), typeof(IReadOnlyList<CommandListItemSyntax>)));
        AssertNode(
            new GroupSyntax(),
            ShellSyntaxKind.Group,
            (nameof(GroupSyntax.GroupKind), typeof(ShellGroupKind)),
            (nameof(GroupSyntax.Body), typeof(ShellBlockSyntax)));
        AssertNode(
            new ForEachSyntax(),
            ShellSyntaxKind.ForEach,
            (nameof(ForEachSyntax.Binding), typeof(LoopBindingSyntax)),
            (nameof(ForEachSyntax.Iterable), typeof(ShellSourceFragment)),
            (nameof(ForEachSyntax.IteratorCommands), typeof(ShellBlockSyntax)),
            (nameof(ForEachSyntax.Body), typeof(ShellBlockSyntax)));
        AssertNode(
            new ConditionLoopSyntax(),
            ShellSyntaxKind.ConditionLoop,
            (nameof(ConditionLoopSyntax.LoopKind), typeof(ConditionLoopKind)),
            (nameof(ConditionLoopSyntax.Condition), typeof(ShellBlockSyntax)),
            (nameof(ConditionLoopSyntax.Body), typeof(ShellBlockSyntax)));
        AssertNode(
            new ConditionalSyntax(),
            ShellSyntaxKind.Conditional,
            (nameof(ConditionalSyntax.Branches), typeof(IReadOnlyList<ConditionalBranchSyntax>)),
            (nameof(ConditionalSyntax.Else), typeof(ShellBlockSyntax)));
        AssertNode(
            new ConditionalBranchSyntax(),
            ShellSyntaxKind.ConditionalBranch,
            (nameof(ConditionalBranchSyntax.Condition), typeof(ShellBlockSyntax)),
            (nameof(ConditionalBranchSyntax.Body), typeof(ShellBlockSyntax)));
        AssertNode(
            new CommandSubstitutionSyntax(),
            ShellSyntaxKind.CommandSubstitution,
            (nameof(CommandSubstitutionSyntax.Body), typeof(ShellBlockSyntax)));
        AssertNode(
            new ExecutionRegionSyntax(),
            ShellSyntaxKind.ExecutionRegion,
            (nameof(ExecutionRegionSyntax.Origin), typeof(ExecutionRegionOrigin)),
            (nameof(ExecutionRegionSyntax.HostClauseElementIndex), typeof(int?)),
            (nameof(ExecutionRegionSyntax.Phase), typeof(ExecutionRegionPhase)),
            (nameof(ExecutionRegionSyntax.Timing), typeof(ExecutionRegionTiming)),
            (nameof(ExecutionRegionSyntax.Cardinality), typeof(ExecutionRegionCardinality)),
            (nameof(ExecutionRegionSyntax.Body), typeof(ShellBlockSyntax)));

        var simple = new SimpleCommandSyntax();
        Assert.NotNull(simple.Clause);
        Assert.NotNull(simple.Substitutions);
        Assert.Empty(simple.Substitutions);
        Assert.NotNull(simple.ExecutionRegions);
        Assert.Empty(simple.ExecutionRegions);
        Assert.Empty(new ShellBlockSyntax().Statements);
        Assert.Empty(new PipelineSyntax().Stages);
        Assert.Empty(new CommandListSyntax().Items);
        var group = new GroupSyntax();
        Assert.Equal(ShellGroupKind.Unknown, group.GroupKind);
        Assert.NotNull(group.Body);
        Assert.Empty(group.Body.Statements);

        var forEach = new ForEachSyntax();
        Assert.NotNull(forEach.Binding);
        Assert.NotNull(forEach.Iterable);
        Assert.NotNull(forEach.IteratorCommands);
        Assert.NotNull(forEach.Body);
        Assert.Empty(forEach.IteratorCommands.Statements);
        Assert.Empty(forEach.Body.Statements);

        var conditionLoop = new ConditionLoopSyntax();
        Assert.Equal(ConditionLoopKind.Unknown, conditionLoop.LoopKind);
        Assert.NotNull(conditionLoop.Condition);
        Assert.NotNull(conditionLoop.Body);
        Assert.Empty(conditionLoop.Condition.Statements);
        Assert.Empty(conditionLoop.Body.Statements);

        var conditional = new ConditionalSyntax();
        Assert.Empty(conditional.Branches);
        Assert.Null(conditional.Else);

        var branch = new ConditionalBranchSyntax();
        Assert.NotNull(branch.Condition);
        Assert.NotNull(branch.Body);
        Assert.Empty(branch.Condition.Statements);
        Assert.Empty(branch.Body.Statements);

        var substitution = new CommandSubstitutionSyntax();
        Assert.NotNull(substitution.Body);
        Assert.Empty(substitution.Body.Statements);

        var executionRegion = new ExecutionRegionSyntax();
        Assert.Equal(ExecutionRegionOrigin.Unknown, executionRegion.Origin);
        Assert.Null(executionRegion.HostClauseElementIndex);
        Assert.Equal(ExecutionRegionPhase.Unknown, executionRegion.Phase);
        Assert.Equal(ExecutionRegionTiming.Unknown, executionRegion.Timing);
        Assert.Equal(ExecutionRegionCardinality.Unknown, executionRegion.Cardinality);
        Assert.NotNull(executionRegion.Body);
        Assert.Empty(executionRegion.Body.Statements);
    }

    [Fact]
    public void Supporting_syntax_records_match_the_locked_members_and_defaults()
    {
        AssertRecordWithInitProperties(
            typeof(CommandListItemSyntax),
            (nameof(CommandListItemSyntax.Operator), typeof(CompoundOperator)),
            (nameof(CommandListItemSyntax.Command), typeof(ShellSyntaxNode)));
        var item = new CommandListItemSyntax();
        Assert.Equal(CompoundOperator.None, item.Operator);
        Assert.IsType<ShellBlockSyntax>(item.Command);

        AssertRecordWithInitProperties(
            typeof(LoopBindingSyntax),
            (nameof(LoopBindingSyntax.Name), typeof(string)),
            (nameof(LoopBindingSyntax.Source), typeof(ShellSourceFragment)));
        var binding = new LoopBindingSyntax();
        Assert.Equal("", binding.Name);
        Assert.NotNull(binding.Source);

        AssertRecordWithInitProperties(
            typeof(ShellSourceFragment),
            (nameof(ShellSourceFragment.Raw), typeof(string)),
            (nameof(ShellSourceFragment.SourceStart), typeof(int?)),
            (nameof(ShellSourceFragment.SourceLength), typeof(int?)));
        var fragment = new ShellSourceFragment();
        Assert.Equal("", fragment.Raw);
        Assert.Null(fragment.SourceStart);
        Assert.Null(fragment.SourceLength);
    }

    [Fact]
    public void Command_occurrence_records_match_the_locked_members_and_defaults()
    {
        AssertRecordWithInitProperties(
            typeof(CommandOccurrence),
            (nameof(CommandOccurrence.Clause), typeof(Clause)),
            (nameof(CommandOccurrence.ImmediateRole), typeof(CommandOccurrenceRole)),
            (nameof(CommandOccurrence.Ancestry), typeof(IReadOnlyList<CommandAncestryFrame>)),
            (nameof(CommandOccurrence.EffectiveArguments), typeof(IReadOnlyList<EffectiveArgument>)),
            (nameof(CommandOccurrence.WorkingDirectory), typeof(ShellValueDomain)),
            (nameof(CommandOccurrence.Redirects), typeof(IReadOnlyList<RedirectAnalysis>)),
            (nameof(CommandOccurrence.IsComplete), typeof(bool)));
        var occurrence = new CommandOccurrence();
        Assert.NotNull(occurrence.Clause);
        Assert.Equal(CommandOccurrenceRole.Unknown, occurrence.ImmediateRole);
        Assert.Empty(occurrence.Ancestry);
        Assert.Empty(occurrence.EffectiveArguments);
        Assert.Same(ShellValueDomain.Unknown, occurrence.WorkingDirectory);
        Assert.Empty(occurrence.Redirects);
        Assert.False(occurrence.IsComplete);

        AssertRecordWithInitProperties(
            typeof(CommandAncestryFrame),
            (nameof(CommandAncestryFrame.AncestorKind), typeof(ShellSyntaxKind)),
            (nameof(CommandAncestryFrame.Region), typeof(CommandAncestryRegion)),
            (nameof(CommandAncestryFrame.ChildIndex), typeof(int?)),
            (nameof(CommandAncestryFrame.SourceStart), typeof(int?)),
            (nameof(CommandAncestryFrame.SourceLength), typeof(int?)));
        var ancestry = new CommandAncestryFrame();
        Assert.Equal(ShellSyntaxKind.Unknown, ancestry.AncestorKind);
        Assert.Equal(CommandAncestryRegion.Unknown, ancestry.Region);
        Assert.Null(ancestry.ChildIndex);
        Assert.Null(ancestry.SourceStart);
        Assert.Null(ancestry.SourceLength);

        AssertRecordWithInitProperties(
            typeof(EffectiveArgument),
            (nameof(EffectiveArgument.ClauseElementIndex), typeof(int)),
            (nameof(EffectiveArgument.Value), typeof(ShellValueDomain)));
        var argument = new EffectiveArgument();
        Assert.Equal(-1, argument.ClauseElementIndex);
        Assert.Same(ShellValueDomain.Unknown, argument.Value);
    }

    [Fact]
    public void Shell_value_domain_matches_the_locked_members_and_defaults()
    {
        AssertRecordWithInitProperties(
            typeof(ShellValueDomain),
            (nameof(ShellValueDomain.Kind), typeof(ShellValueDomainKind)),
            (nameof(ShellValueDomain.Values), typeof(IReadOnlyList<string>)),
            (nameof(ShellValueDomain.Pattern), typeof(string)),
            (nameof(ShellValueDomain.CoveringDirectory), typeof(string)));

        var unknown = ShellValueDomain.Unknown;
        Assert.Same(unknown, ShellValueDomain.Unknown);
        Assert.Equal(ShellValueDomainKind.Unknown, unknown.Kind);
        Assert.Empty(unknown.Values);
        Assert.Null(unknown.Pattern);
        Assert.Null(unknown.CoveringDirectory);

        var property = typeof(ShellValueDomain).GetProperty(
            nameof(ShellValueDomain.Unknown),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(property);
        Assert.True(property!.CanRead);
        Assert.False(property.CanWrite);
        Assert.Equal(
            new[] { nameof(ShellValueDomain.Unknown) },
            typeof(ShellValueDomain)
                .GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(candidate => candidate.Name)
                .OrderBy(name => name));
    }

    [Fact]
    public void Redirect_analysis_records_match_the_locked_members_and_defaults()
    {
        AssertRecordWithInitProperties(
            typeof(RedirectAnalysis),
            (nameof(RedirectAnalysis.RedirectIndex), typeof(int)),
            (nameof(RedirectAnalysis.Source), typeof(RedirectSource)),
            (nameof(RedirectAnalysis.Operation), typeof(RedirectOperation)),
            (nameof(RedirectAnalysis.TargetDescriptor), typeof(int?)),
            (nameof(RedirectAnalysis.Target), typeof(ShellValueDomain)),
            (nameof(RedirectAnalysis.HereDocument), typeof(HereDocumentAnalysis)),
            (nameof(RedirectAnalysis.IsPathRelevant), typeof(bool)),
            (nameof(RedirectAnalysis.IsComplete), typeof(bool)));
        var redirect = new RedirectAnalysis();
        Assert.Equal(-1, redirect.RedirectIndex);
        Assert.NotNull(redirect.Source);
        Assert.Equal(RedirectOperation.Unknown, redirect.Operation);
        Assert.Null(redirect.TargetDescriptor);
        Assert.Same(ShellValueDomain.Unknown, redirect.Target);
        Assert.Null(redirect.HereDocument);
        Assert.False(redirect.IsPathRelevant);
        Assert.False(redirect.IsComplete);

        AssertRecordWithInitProperties(
            typeof(HereDocumentAnalysis),
            (nameof(HereDocumentAnalysis.Delimiter), typeof(ShellSourceFragment)),
            (nameof(HereDocumentAnalysis.Body), typeof(ShellSourceFragment)),
            (nameof(HereDocumentAnalysis.ExpansionMode), typeof(HereDocumentExpansionMode)),
            (nameof(HereDocumentAnalysis.StripLeadingTabs), typeof(bool)),
            (nameof(HereDocumentAnalysis.IsComplete), typeof(bool)));
        var hereDocument = new HereDocumentAnalysis();
        Assert.NotNull(hereDocument.Delimiter);
        Assert.NotNull(hereDocument.Body);
        Assert.Equal(HereDocumentExpansionMode.Unknown, hereDocument.ExpansionMode);
        Assert.False(hereDocument.StripLeadingTabs);
        Assert.False(hereDocument.IsComplete);

        AssertRecordWithInitProperties(
            typeof(RedirectSource),
            (nameof(RedirectSource.Kind), typeof(RedirectSourceKind)),
            (nameof(RedirectSource.Descriptor), typeof(int?)));
        var source = new RedirectSource();
        Assert.Equal(RedirectSourceKind.Unknown, source.Kind);
        Assert.Null(source.Descriptor);
    }

    [Fact]
    public void V03_enums_reserve_zero_for_unknown_and_match_the_locked_order()
    {
        AssertEnum<ShellSyntaxKind>(
            "Unknown", "Block", "SimpleCommand", "Pipeline", "CommandList",
            "Group", "ForEach", "ConditionLoop", "Conditional",
            "ConditionalBranch", "CommandSubstitution", "ExecutionRegion");
        AssertEnum<ShellGroupKind>("Unknown", "CurrentScope", "IsolatedScope");
        AssertEnum<ConditionLoopKind>("Unknown", "While", "Until");
        AssertEnum<ExecutionRegionOrigin>(
            "Unknown", "DirectCall", "DotSource", "CommandArgument");
        AssertEnum<ExecutionRegionPhase>(
            "Unknown", "Main", "Initialization", "Begin", "Process", "End",
            "Filter", "Action", "Completion");
        AssertEnum<ExecutionRegionTiming>(
            "Unknown", "Synchronous", "Concurrent", "Deferred");
        AssertEnum<ExecutionRegionCardinality>(
            "Unknown", "Once", "OncePerInputObject", "ZeroOrMore");
        AssertEnum<CommandOccurrenceRole>(
            "Unknown", "Ordinary", "PipelineStage", "Condition", "Iterator",
            "LoopBody", "Branch", "Substitution", "ExecutionRegion");
        AssertEnum<CommandAncestryRegion>(
            "Unknown", "Root", "Statement", "PipelineStage", "GroupBody",
            "Iterator", "LoopBody", "Condition", "Branch", "Substitution",
            "ExecutionRegion");
        AssertEnum<ShellValueDomainKind>("Unknown", "Exact", "FiniteSet", "Pattern");
        AssertEnum<HereDocumentExpansionMode>("Unknown", "Literal", "Expand");
        AssertEnum<RedirectSourceKind>(
            "Unknown", "Default", "Descriptor", "PowerShellAllStreams");
        AssertEnum<RedirectOperation>(
            "Unknown", "FileInput", "FileOutput", "FileAppend",
            "DescriptorDuplicate", "DescriptorClose", "DescriptorMove",
            "CombinedOutput", "CombinedOutputAppend", "HereDocument", "HereString");
    }

    [Fact]
    public void V03_reference_property_nullability_matches_the_locked_contract()
    {
        var types = new[]
        {
            typeof(ParsedCommand),
            typeof(ShellSyntaxNode),
            typeof(ShellBlockSyntax),
            typeof(SimpleCommandSyntax),
            typeof(PipelineSyntax),
            typeof(CommandListSyntax),
            typeof(CommandListItemSyntax),
            typeof(GroupSyntax),
            typeof(ForEachSyntax),
            typeof(LoopBindingSyntax),
            typeof(ShellSourceFragment),
            typeof(ConditionLoopSyntax),
            typeof(ConditionalSyntax),
            typeof(ConditionalBranchSyntax),
            typeof(CommandSubstitutionSyntax),
            typeof(ExecutionRegionSyntax),
            typeof(CommandOccurrence),
            typeof(CommandAncestryFrame),
            typeof(EffectiveArgument),
            typeof(ShellValueDomain),
            typeof(RedirectAnalysis),
            typeof(HereDocumentAnalysis),
            typeof(RedirectSource),
        };
        var nullableProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{nameof(ParsedCommand)}.{nameof(ParsedCommand.UnparseableReason)}",
            $"{nameof(ConditionalSyntax)}.{nameof(ConditionalSyntax.Else)}",
            $"{nameof(ShellValueDomain)}.{nameof(ShellValueDomain.Pattern)}",
            $"{nameof(ShellValueDomain)}.{nameof(ShellValueDomain.CoveringDirectory)}",
            $"{nameof(RedirectAnalysis)}.{nameof(RedirectAnalysis.HereDocument)}",
        };
        var context = new NullabilityInfoContext();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in types)
        {
            var properties = type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var property in properties.Where(
                         property => property.Name != "EqualityContract" &&
                                     !property.PropertyType.IsValueType))
            {
                var key = $"{type.Name}.{property.Name}";
                var expected = nullableProperties.Contains(key)
                    ? NullabilityState.Nullable
                    : NullabilityState.NotNull;
                var nullability = context.Create(property);
                Assert.Equal(expected, nullability.ReadState);
                if (property.CanWrite)
                {
                    Assert.Equal(expected, nullability.WriteState);
                }

                AssertGenericArgumentsAreNotNull(key, nullability);
                visited.Add(key);
            }
        }

        Assert.All(nullableProperties, property => Assert.Contains(property, visited));

        var unknownProperty = typeof(ShellValueDomain).GetProperty(
            nameof(ShellValueDomain.Unknown),
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.NotNull(unknownProperty);
        Assert.Equal(
            NullabilityState.NotNull,
            context.Create(unknownProperty!).ReadState);
    }

    [Fact]
    public void Analysis_limits_are_static_get_only_and_match_the_locked_values()
    {
        var type = typeof(ShellAnalysisLimits);
        Assert.True(type.IsPublic);
        Assert.True(type.IsAbstract);
        Assert.True(type.IsSealed);

        Assert.Equal(32, ShellAnalysisLimits.MaxValueCandidates);
        Assert.Equal(16, ShellAnalysisLimits.MaxStructuralNesting);
        Assert.Equal(5, ShellAnalysisLimits.MaxWrapperRecursionDepth);

        var properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal(
            new[]
            {
                nameof(ShellAnalysisLimits.MaxStructuralNesting),
                nameof(ShellAnalysisLimits.MaxValueCandidates),
                nameof(ShellAnalysisLimits.MaxWrapperRecursionDepth),
            },
            properties.Select(property => property.Name).OrderBy(name => name));

        foreach (var property in properties)
        {
            Assert.True(property.GetMethod!.IsStatic);
            Assert.False(property.CanWrite);
            Assert.Equal(typeof(int), property.PropertyType);
        }
    }

    [Fact]
    public void Parsed_command_new_members_participate_in_generated_record_behavior()
    {
        var clauses = Array.Empty<Clause>();
        var commands = Array.Empty<CommandOccurrence>();
        var syntax = new ShellBlockSyntax();
        var original = new ParsedCommand
        {
            Source = "echo ok",
            Syntax = syntax,
            Commands = commands,
            Clauses = clauses,
        };
        var equalCopy = original with { };

        Assert.Equal(original, equalCopy);
        Assert.Equal(original.GetHashCode(), equalCopy.GetHashCode());
        Assert.Contains($"{nameof(ParsedCommand.Syntax)} =", original.ToString());
        Assert.Contains($"{nameof(ParsedCommand.Commands)} =", original.ToString());

        Assert.NotEqual(
            original,
            original with
            {
                Syntax = syntax with { SourceStart = 0, SourceLength = 7 },
            });
        Assert.NotEqual(
            original,
            original with
            {
                Commands = new[] { new CommandOccurrence() },
            });
    }

    [Fact]
    public void Default_json_is_not_a_polymorphic_parser_result_round_trip_contract()
    {
        var clause = new Clause
        {
            Verb = new VerbChain { Tokens = new[] { "echo" } },
        };
        var parsed = new ParsedCommand
        {
            Source = "echo ok",
            Syntax = new ShellBlockSyntax
            {
                Statements = new ShellSyntaxNode[]
                {
                    new SimpleCommandSyntax { Clause = clause },
                },
            },
            Commands = new[]
            {
                new CommandOccurrence
                {
                    Clause = clause,
                    ImmediateRole = CommandOccurrenceRole.Ordinary,
                    IsComplete = true,
                },
            },
            Clauses = new[] { clause },
        };

        var json = JsonSerializer.Serialize(parsed);

        Assert.Contains($"\"{nameof(ParsedCommand.Syntax)}\"", json);
        Assert.Contains($"\"{nameof(ParsedCommand.Commands)}\"", json);
        Assert.Contains($"\"{nameof(ParsedCommand.Clauses)}\"", json);
        Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Deserialize<ParsedCommand>(json));
        var syntaxTypes = typeof(ShellSyntaxNode).Assembly
            .GetExportedTypes()
            .Where(type => typeof(ShellSyntaxNode).IsAssignableFrom(type));
        Assert.All(
            syntaxTypes,
            type => Assert.DoesNotContain(
                type.CustomAttributes,
                attribute => attribute.AttributeType.Namespace ==
                             "System.Text.Json.Serialization"));
        Assert.DoesNotContain(
            typeof(ShellSyntaxNode).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "System.Text.Json");
    }

    [Fact]
    public void Unknown_numeric_enum_values_remain_detectable_for_consumer_rejection()
    {
        const int unknownValue = 999;
        var policySensitiveEnums = new[]
        {
            typeof(BashInitialStateMode),
            typeof(PwshDialect),
            typeof(PwshInitialStateMode),
            typeof(ShellSyntaxKind),
            typeof(ShellGroupKind),
            typeof(ConditionLoopKind),
            typeof(ExecutionRegionOrigin),
            typeof(ExecutionRegionPhase),
            typeof(ExecutionRegionTiming),
            typeof(ExecutionRegionCardinality),
            typeof(CommandOccurrenceRole),
            typeof(CommandAncestryRegion),
            typeof(ShellValueDomainKind),
            typeof(HereDocumentExpansionMode),
            typeof(RedirectSourceKind),
            typeof(RedirectOperation),
        };

        foreach (var enumType in policySensitiveEnums)
        {
            var value = Enum.ToObject(enumType, unknownValue);
            Assert.Equal(unknownValue, Convert.ToInt32(value));
            Assert.False(Enum.IsDefined(enumType, value));
        }
    }

    private static void AssertNode(
        ShellSyntaxNode instance,
        ShellSyntaxKind expectedKind,
        params (string Name, Type Type)[] properties)
    {
        var type = instance.GetType();
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        AssertRecord(type);
        Assert.Equal(expectedKind, instance.Kind);
        Assert.Null(instance.SourceStart);
        Assert.Null(instance.SourceLength);

        AssertGetProperty(type, nameof(ShellSyntaxNode.Kind), typeof(ShellSyntaxKind));
        foreach (var property in properties)
        {
            AssertInitProperty(type, property.Name, property.Type);
        }

        var expectedNames = properties
            .Select(property => property.Name)
            .Append(nameof(ShellSyntaxNode.Kind))
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(expectedNames, DeclaredPublicProperties(type));
    }

    private static void AssertRecordWithInitProperties(
        Type type,
        params (string Name, Type Type)[] properties)
    {
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        AssertRecord(type);
        foreach (var property in properties)
        {
            AssertInitProperty(type, property.Name, property.Type);
        }

        Assert.Equal(
            properties.Select(property => property.Name).OrderBy(name => name),
            DeclaredPublicProperties(type));
    }

    private static string[] DeclaredPublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

    private static void AssertRecord(Type type)
    {
        var clone = type.GetMethod(
            "<Clone>$",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotNull(clone);
    }

    private static void AssertGenericArgumentsAreNotNull(
        string propertyPath,
        NullabilityInfo nullability)
    {
        foreach (var argument in nullability.GenericTypeArguments)
        {
            Assert.True(
                argument.ReadState == NullabilityState.NotNull,
                $"{propertyPath} has a nullable or oblivious generic argument.");
            AssertGenericArgumentsAreNotNull(propertyPath, argument);
        }
    }

    private static void AssertGetProperty(Type type, string name, Type expectedType)
    {
        var property = type.GetProperty(name);
        Assert.NotNull(property);
        Assert.Equal(expectedType, property!.PropertyType);
        Assert.True(property.CanRead);
        Assert.False(property.CanWrite);
    }

    private static void AssertInitProperty(Type type, string name, Type expectedType)
    {
        var property = type.GetProperty(name);
        Assert.NotNull(property);
        Assert.Equal(expectedType, property!.PropertyType);
        Assert.True(property.CanRead);
        Assert.True(property.CanWrite);
        var setter = Assert.IsAssignableFrom<MethodInfo>(property.SetMethod);
        Assert.Contains(
            setter.ReturnParameter.GetRequiredCustomModifiers(),
            modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    private static void AssertEnum<T>(params string[] expectedNames)
        where T : struct, Enum
    {
        Assert.Equal(expectedNames, Enum.GetNames(typeof(T)));
        var values = Enum.GetValues(typeof(T));
        Assert.Equal(expectedNames.Length, values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            Assert.Equal(index, Convert.ToInt32(values.GetValue(index)));
        }
    }
}
