// -----------------------------------------------------------------------
// <copyright file="ShellSyntaxProjectionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ShellSyntaxTree.Tests;

/// <summary>Tests parser-owned command and compatibility projections.</summary>
public class ShellSyntaxProjectionTests
{
    [Fact]
    public void Root_command_preserves_identity_and_joins_analysis_facts()
    {
        var clause = ClauseFor("echo") with
        {
            Args = new[] { new Arg { Raw = "ready", Kind = ArgKind.Literal } },
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement { Role = ClauseElementRole.Argument },
            },
            Redirects = new[] { new Redirect() },
        };
        var leaf = new SimpleCommandSyntax
        {
            Clause = clause,
            SourceStart = 0,
            SourceLength = 10,
        };
        var root = new ShellBlockSyntax
        {
            SourceStart = 0,
            SourceLength = 10,
            Statements = new ShellSyntaxNode[] { leaf },
        };
        var effectiveArguments = new[]
        {
            new EffectiveArgumentFacts
            {
                ClauseElementIndex = 1,
                Value = new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { "ready" },
                },
            },
        };
        var redirects = new[]
        {
            new RedirectAnalysisFacts
            {
                RedirectIndex = 0,
                Source = new RedirectSourceFacts { Kind = RedirectSourceKind.Default },
                Operation = RedirectOperation.FileOutput,
                Target = new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { "/work/out.txt" },
                },
                IsPathRelevant = true,
                IsComplete = true,
            },
        };
        var workingDirectory = new ShellValueDomainFacts
        {
            Kind = ShellValueDomainKind.Exact,
            Values = new[] { "/work" },
        };

        var succeeded = ShellSyntaxProjection.TryProject(
            root,
            _ => new CommandOccurrenceFacts
            {
                EffectiveArguments = effectiveArguments,
                WorkingDirectory = workingDirectory,
                Redirects = redirects,
                IsComplete = true,
            },
            out var result);

        Assert.True(succeeded);
        var occurrence = Assert.Single(result.Commands);
        Assert.Same(clause, occurrence.Clause);
        Assert.Same(clause, Assert.Single(result.Clauses));
        Assert.Equal(CommandOccurrenceRole.Ordinary, occurrence.ImmediateRole);
        Assert.True(occurrence.IsComplete);
        Assert.Equal("/work", Assert.IsType<ShellValueDomain.Exact>(
            occurrence.WorkingDirectory).Value);
        var argument = Assert.Single(occurrence.Arguments);
        Assert.Same(clause.Args[0], argument.Argument);
        Assert.Same(clause.Elements[1], argument.Element);
        Assert.Equal("ready", Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value);
        var redirect = Assert.IsType<FileRedirectAnalysis>(
            Assert.Single(occurrence.Redirects));
        Assert.Same(clause.Redirects[0], redirect.Authored);
        Assert.IsType<RedirectSource.Default>(redirect.Source);
        Assert.Equal(FileRedirectMode.Output, redirect.Mode);
        Assert.Equal("/work/out.txt", Assert.IsType<ShellValueDomain.Exact>(
            redirect.Target).Value);

        var rootFrame = Assert.Single(occurrence.Ancestry);
        Assert.Equal(ShellSyntaxKind.Block, rootFrame.AncestorKind);
        Assert.Equal(CommandAncestryRegion.Root, rootFrame.Region);
        Assert.Equal(0, rootFrame.ChildIndex);
        Assert.Equal(0, rootFrame.SourceStart);
        Assert.Equal(10, rootFrame.SourceLength);
    }

    [Fact]
    public void Embedded_substitution_precedes_its_containing_command()
    {
        var find = Leaf("find", 5);
        var remove = Leaf("rm", 0) with
        {
            Substitutions = new[]
            {
                Substitution(3, find),
            },
        };
        var root = Block(0, remove);

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.True(succeeded);
        Assert.Equal(new[] { "find", "rm" }, result.Commands.Select(Verb));
        Assert.Equal(result.Commands.Select(command => command.Clause), result.Clauses);
        Assert.Same(find.Clause, result.Clauses[0]);
        Assert.Same(remove.Clause, result.Clauses[1]);
        AssertFrames(
            result.Commands[0],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.CommandSubstitution, CommandAncestryRegion.Substitution, 0),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
        AssertFrames(
            result.Commands[1],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0));
    }

    [Fact]
    public void Sibling_substitutions_use_authored_order_and_child_indices_without_spans()
    {
        var first = Leaf("first", 1);
        var second = Leaf("second", 2);
        var outer = Leaf("printf", 0) with
        {
            Substitutions = new[]
            {
                new CommandSubstitutionSyntax
                {
                    Body = new ShellBlockSyntax
                    {
                        Statements = new ShellSyntaxNode[] { first },
                    },
                },
                new CommandSubstitutionSyntax
                {
                    Body = new ShellBlockSyntax
                    {
                        Statements = new ShellSyntaxNode[] { second },
                    },
                },
            },
        };

        var succeeded = ShellSyntaxProjection.TryProject(Block(0, outer), out var result);

        Assert.True(succeeded);
        Assert.Equal(new[] { "first", "second", "printf" }, result.Commands.Select(Verb));
        Assert.Equal(0, result.Commands[0].Ancestry[1].ChildIndex);
        Assert.Equal(1, result.Commands[1].Ancestry[1].ChildIndex);
        Assert.Null(result.Commands[0].Ancestry[1].SourceStart);
        Assert.Null(result.Commands[1].Ancestry[1].SourceStart);
    }

    [Fact]
    public void Nested_substitutions_are_innermost_first_and_keep_parentage()
    {
        var whoami = Leaf("whoami", 9);
        var echo = Leaf("echo", 5) with
        {
            Substitutions = new[]
            {
                Substitution(8, whoami),
            },
        };
        var print = Leaf("printf", 0) with
        {
            Substitutions = new[]
            {
                Substitution(3, echo),
            },
        };

        var succeeded = ShellSyntaxProjection.TryProject(Block(0, print), out var result);

        Assert.True(succeeded);
        Assert.Equal(new[] { "whoami", "echo", "printf" }, result.Commands.Select(Verb));
        AssertFrames(
            result.Commands[0],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.CommandSubstitution, CommandAncestryRegion.Substitution, 0),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0),
            (ShellSyntaxKind.CommandSubstitution, CommandAncestryRegion.Substitution, 0),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
    }

    [Fact]
    public void Direct_execution_region_projects_only_its_body()
    {
        var remove = Leaf("Remove-Item", 3);
        var region = ExecutionRegion(
            ExecutionRegionOrigin.DirectCall,
            hostClauseElementIndex: null,
            1,
            remove);

        var succeeded = ShellSyntaxProjection.TryProject(
            Block(0, region),
            _ => new CommandOccurrenceFacts { IsComplete = true },
            out var result);

        Assert.True(succeeded);
        var occurrence = Assert.Single(result.Commands);
        Assert.Equal("Remove-Item", Verb(occurrence));
        Assert.Equal(CommandOccurrenceRole.ExecutionRegion, occurrence.ImmediateRole);
        Assert.True(occurrence.IsComplete);
        Assert.Same(remove.Clause, Assert.Single(result.Clauses));
        AssertFrames(
            occurrence,
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.ExecutionRegion, CommandAncestryRegion.ExecutionRegion, 0),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
    }

    [Fact]
    public void Command_owned_regions_follow_the_host_in_authored_order()
    {
        var hostClause = ClauseFor("ForEach-Object") with
        {
            Args = new[]
            {
                new Arg { Raw = "end", Kind = ArgKind.DynamicSkip },
                new Arg { Raw = "begin", Kind = ArgKind.DynamicSkip },
            },
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement
                {
                    Role = ClauseElementRole.Argument,
                    Kind = ArgKind.DynamicSkip,
                },
                new ClauseElement
                {
                    Role = ClauseElementRole.Argument,
                    Kind = ArgKind.DynamicSkip,
                },
            },
        };
        var host = Leaf(hostClause, 1) with
        {
            Substitutions = new[]
            {
                Substitution(1, Leaf("prepare", 1)),
            },
            ExecutionRegions = new[]
            {
                ExecutionRegion(
                    ExecutionRegionOrigin.CommandArgument,
                    hostClauseElementIndex: 1,
                    2,
                    Leaf("end", 3),
                    ExecutionRegionPhase.End) with
                {
                    HostArgument = hostClause.Elements[1],
                },
                ExecutionRegion(
                    ExecutionRegionOrigin.CommandArgument,
                    hostClauseElementIndex: 2,
                    4,
                    Leaf("begin", 5),
                    ExecutionRegionPhase.Begin) with
                {
                    HostArgument = hostClause.Elements[2],
                },
            },
        };
        var pipeline = new PipelineSyntax
        {
            Stages = new ShellSyntaxNode[] { host },
        };

        var succeeded = ShellSyntaxProjection.TryProject(
            Block(0, pipeline),
            _ => new CommandOccurrenceFacts { IsComplete = true },
            out var result);

        Assert.True(succeeded);
        Assert.Equal(
            new[] { "prepare", "ForEach-Object", "end", "begin" },
            result.Commands.Select(Verb));
        Assert.Equal(result.Commands.Select(command => command.Clause), result.Clauses);
        Assert.Equal(CommandOccurrenceRole.Substitution, result.Commands[0].ImmediateRole);
        Assert.Equal(CommandOccurrenceRole.PipelineStage, result.Commands[1].ImmediateRole);
        Assert.All(
            result.Commands.Skip(2),
            occurrence => Assert.Equal(
                CommandOccurrenceRole.ExecutionRegion,
                occurrence.ImmediateRole));
        Assert.All(result.Commands, occurrence => Assert.True(occurrence.IsComplete));
        AssertFrames(
            result.Commands[2],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.Pipeline, CommandAncestryRegion.PipelineStage, 0),
            (ShellSyntaxKind.ExecutionRegion, CommandAncestryRegion.ExecutionRegion, 0),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
        AssertFrames(
            result.Commands[3],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.Pipeline, CommandAncestryRegion.PipelineStage, 0),
            (ShellSyntaxKind.ExecutionRegion, CommandAncestryRegion.ExecutionRegion, 1),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
    }

    [Fact]
    public void Unknown_execution_region_facts_make_host_and_body_incomplete()
    {
        var clause = ClauseFor("Invoke-Custom") with
        {
            Args = new[] { new Arg { Raw = "body", Kind = ArgKind.DynamicSkip } },
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement
                {
                    Role = ClauseElementRole.Argument,
                    Kind = ArgKind.DynamicSkip,
                },
            },
        };
        var host = Leaf(clause, 1) with
        {
            ExecutionRegions = new[]
            {
                new ExecutionRegionSyntax
                {
                    Origin = ExecutionRegionOrigin.CommandArgument,
                    HostArgument = clause.Elements[1],
                    HostClauseElementIndex = 1,
                    Body = Block(2, Leaf("Remove-Item", 3)),
                },
            },
        };

        var succeeded = ShellSyntaxProjection.TryProject(
            Block(0, host),
            _ => new CommandOccurrenceFacts { IsComplete = true },
            out var result);

        Assert.True(succeeded);
        Assert.Equal(new[] { "Invoke-Custom", "Remove-Item" }, result.Commands.Select(Verb));
        Assert.All(result.Commands, occurrence => Assert.False(occurrence.IsComplete));
    }

    [Fact]
    public void PowerShell_decoded_wrapper_clone_preserves_regions_and_targets_last_body_leaf()
    {
        var source = DecodedExecutionRegionTree();
        var wrapperRedirect = new Redirect
        {
            Direction = RedirectDirection.Out,
            Target = "wrapper.txt",
        };
        var wrapperElement = new ClauseElement
        {
            Role = ClauseElementRole.Redirect,
            Raw = "> wrapper.txt",
            Value = "wrapper.txt",
        };

        var clone = InvokePowerShellDecodedClone(
            source,
            CompoundOperator.AndIf,
            new[] { wrapperRedirect },
            new[] { wrapperElement });

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(clone.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        var body = Assert.IsType<SimpleCommandSyntax>(Assert.Single(region.Body.Statements));
        Assert.Equal(CompoundOperator.AndIf, host.Clause.Operator);
        Assert.Empty(host.Clause.Redirects);
        Assert.Equal(CompoundOperator.None, body.Clause.Operator);
        Assert.Same(wrapperRedirect, Assert.Single(body.Clause.Redirects));
        Assert.Equal(
            wrapperElement with { PrecedingVerbElementCount = 1 },
            Assert.Single(body.Clause.Elements.Skip(1)));
        Assert.True(host.Clause.IsSubshell);
        Assert.True(body.Clause.IsSubshell);
        Assert.True(host.Clause.IsCommandStringWrapped);
        Assert.True(body.Clause.IsCommandStringWrapped);
        AssertExecutionRegionClone(region);
    }

    [Fact]
    public void Bash_decoded_wrapper_clone_preserves_regions_after_the_host()
    {
        var clone = InvokeBashDecodedClone(
            DecodedExecutionRegionTree(),
            CompoundOperator.OrIf);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(clone.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        var body = Assert.IsType<SimpleCommandSyntax>(Assert.Single(region.Body.Statements));
        Assert.Equal(CompoundOperator.OrIf, host.Clause.Operator);
        Assert.Equal(CompoundOperator.None, body.Clause.Operator);
        Assert.True(host.Clause.IsSubshell);
        Assert.True(body.Clause.IsSubshell);
        Assert.True(host.Clause.IsCommandStringWrapped);
        Assert.True(body.Clause.IsCommandStringWrapped);
        AssertExecutionRegionClone(region);
    }

    [Fact]
    public void Iterator_substitutions_keep_nearest_role_and_authored_indices()
    {
        var first = Substitution(2, Leaf("first", 3));
        var second = Substitution(4, Leaf("second", 5));
        var body = Leaf("body", 8);
        var loop = new ForEachSyntax
        {
            Binding = Binding("item"),
            Iterable = new ShellSourceFragment { Raw = "$(first) $(second)" },
            IteratorCommands = Block(1, first, second),
            Body = Block(7, body),
        };

        var succeeded = ShellSyntaxProjection.TryProject(Block(0, loop), out var result);

        Assert.True(succeeded);
        Assert.Equal(new[] { "first", "second", "body" }, result.Commands.Select(Verb));
        Assert.Equal(
            new[]
            {
                CommandOccurrenceRole.Substitution,
                CommandOccurrenceRole.Substitution,
                CommandOccurrenceRole.LoopBody,
            },
            result.Commands.Select(command => command.ImmediateRole));
        AssertFrames(
            result.Commands[0],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.ForEach, CommandAncestryRegion.Iterator, null),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0),
            (ShellSyntaxKind.CommandSubstitution, CommandAncestryRegion.Substitution, 0),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
        AssertFrames(
            result.Commands[1],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.ForEach, CommandAncestryRegion.Iterator, null),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 1),
            (ShellSyntaxKind.CommandSubstitution, CommandAncestryRegion.Substitution, 1),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
    }

    [Fact]
    public void Invalid_or_reused_substitution_children_discard_partial_projections()
    {
        Assert.Throws<ArgumentNullException>(() => Leaf("null-collection", 0) with
        {
            Substitutions = null!,
        });

        var nullChild = Leaf("null-child", 0) with
        {
            Substitutions = new CommandSubstitutionSyntax[] { null! },
        };
        Assert.False(ShellSyntaxProjection.TryProject(
            Block(0, nullChild),
            out var nullChildResult));
        Assert.Empty(nullChildResult.Commands);
        Assert.Empty(nullChildResult.Clauses);

        var shared = Substitution(1, Leaf("inner", 2));
        var reused = Leaf("reused", 0) with
        {
            Substitutions = new[] { shared, shared },
        };
        Assert.False(ShellSyntaxProjection.TryProject(
            Block(0, reused),
            out var reusedResult));
        Assert.Empty(reusedResult.Commands);
        Assert.Empty(reusedResult.Clauses);
    }

    [Fact]
    public void Nested_projection_is_source_ordered_and_uses_nearest_execution_role()
    {
        var find = Leaf("find", 12);
        var iteratorSort = Leaf("sort-iterator", 20);
        var print = Leaf("printf", 40);
        var bodySort = Leaf("sort-body", 50);
        var inner = Leaf("inner", 130);

        var loop = new ForEachSyntax
        {
            SourceStart = 0,
            SourceLength = 60,
            Binding = Binding("f"),
            Iterable = new ShellSourceFragment { Raw = "$(find .)" },
            IteratorCommands = Block(
                10,
                new PipelineSyntax
                {
                    SourceStart = 10,
                    SourceLength = 20,
                    Stages = new ShellSyntaxNode[] { find, iteratorSort },
                }),
            Body = Block(
                35,
                new PipelineSyntax
                {
                    SourceStart = 35,
                    SourceLength = 25,
                    Stages = new ShellSyntaxNode[] { print, bodySort },
                }),
        };
        var substitution = new CommandSubstitutionSyntax
        {
            SourceStart = 125,
            SourceLength = 15,
            Body = Block(128, inner),
        };
        var root = new ShellBlockSyntax
        {
            SourceStart = 0,
            SourceLength = 140,
            Statements = new ShellSyntaxNode[] { loop, substitution },
        };

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.True(succeeded);
        Assert.Equal(
            new[]
            {
                "find", "sort-iterator", "printf", "sort-body", "inner",
            },
            result.Commands.Select(Verb));
        Assert.Equal(
            new[]
            {
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.Substitution,
            },
            result.Commands.Select(command => command.ImmediateRole));
        Assert.Equal(result.Commands.Select(command => command.Clause), result.Clauses);

        AssertFrames(
            result.Commands[2],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.ForEach, CommandAncestryRegion.LoopBody, null),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0),
            (ShellSyntaxKind.Pipeline, CommandAncestryRegion.PipelineStage, 0));
        AssertFrames(
            result.Commands[4],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 1),
            (ShellSyntaxKind.CommandSubstitution, CommandAncestryRegion.Substitution, 1),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
    }

    [Fact]
    public void Sixteen_structural_containers_are_supported()
    {
        var root = NestedGroups(ShellAnalysisLimits.MaxStructuralNesting);

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.True(succeeded);
        Assert.Single(result.Commands);
        Assert.Equal(
            (ShellAnalysisLimits.MaxStructuralNesting * 2) + 1,
            result.Commands[0].Ancestry.Count);
    }

    [Fact]
    public void Sixteen_nested_substitutions_are_supported()
    {
        var root = NestedSubstitutions(ShellAnalysisLimits.MaxStructuralNesting);

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.True(succeeded);
        Assert.Equal(ShellAnalysisLimits.MaxStructuralNesting + 1, result.Commands.Count);
        Assert.Equal(
            (ShellAnalysisLimits.MaxStructuralNesting * 2) + 1,
            result.Commands[0].Ancestry.Count);
    }

    [Fact]
    public void Seventeenth_nested_substitution_discards_partial_projections()
    {
        var root = NestedSubstitutions(ShellAnalysisLimits.MaxStructuralNesting + 1);

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.False(succeeded);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Execution_regions_share_the_structural_depth_budget()
    {
        var exact = NestedExecutionRegions(ShellAnalysisLimits.MaxStructuralNesting);
        var overflow = NestedExecutionRegions(ShellAnalysisLimits.MaxStructuralNesting + 1);

        Assert.True(ShellSyntaxProjection.TryProject(exact, out var exactResult));
        Assert.Single(exactResult.Commands);
        Assert.Equal(
            (ShellAnalysisLimits.MaxStructuralNesting * 2) + 1,
            exactResult.Commands[0].Ancestry.Count);

        Assert.False(ShellSyntaxProjection.TryProject(overflow, out var overflowResult));
        Assert.Empty(overflowResult.Commands);
        Assert.Empty(overflowResult.Clauses);
    }

    [Fact]
    public void Structural_depth_overflow_discards_partial_projections()
    {
        var first = Leaf("first", 0);
        var overflow = NestedGroups(ShellAnalysisLimits.MaxStructuralNesting + 1).Statements[0];
        var root = Block(0, first, overflow);

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.False(succeeded);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Published_collections_cannot_be_mutated_into_cycles()
    {
        var statements = new List<ShellSyntaxNode>();
        var root = new ShellBlockSyntax { Statements = statements };
        statements.Add(Leaf("first", 0));
        statements.Add(root);

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.True(succeeded);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Reused_node_or_clause_identity_discards_the_whole_projection()
    {
        var sharedLeaf = Leaf("shared", 1);
        var repeatedNode = Block(0, sharedLeaf, sharedLeaf);

        Assert.False(ShellSyntaxProjection.TryProject(repeatedNode, out var nodeResult));
        Assert.Empty(nodeResult.Commands);
        Assert.Empty(nodeResult.Clauses);

        var sharedClause = ClauseFor("shared-clause");
        var repeatedClause = Block(
            0,
            Leaf(sharedClause, 1),
            Leaf(sharedClause, 2));

        Assert.False(ShellSyntaxProjection.TryProject(repeatedClause, out var clauseResult));
        Assert.Empty(clauseResult.Commands);
        Assert.Empty(clauseResult.Clauses);
    }

    [Fact]
    public void Unknown_or_empty_structural_shapes_fail_closed()
    {
        var invalidNodes = new ShellSyntaxNode[]
        {
            new GroupSyntax { Body = Block(1, Leaf("group", 1)) },
            new ConditionLoopSyntax
            {
                Condition = Block(1),
                Body = Block(2, Leaf("loop", 2)),
            },
            new PipelineSyntax(),
            new CommandListSyntax(),
            new CommandListSyntax
            {
                Items = new[]
                {
                    new CommandListItemSyntax
                    {
                        Command = Leaf("invalid-operator", 1),
                        Operator = (CompoundOperator)999,
                    },
                },
            },
            new ConditionalSyntax(),
            new ForEachSyntax
            {
                Binding = new LoopBindingSyntax(),
                Iterable = new ShellSourceFragment(),
                Body = Block(2, Leaf("foreach", 2)),
            },
            new ForEachSyntax
            {
                Binding = Binding("item"),
                Iterable = new ShellSourceFragment
                {
                    Raw = "value",
                    SourceStart = -1,
                    SourceLength = 1,
                },
                Body = Block(2, Leaf("foreach-span", 2)),
            },
            new ExecutionRegionSyntax
            {
                Body = Block(2, Leaf("unknown-origin", 2)),
            },
            new ExecutionRegionSyntax
            {
                Origin = ExecutionRegionOrigin.DirectCall,
                HostClauseElementIndex = 1,
                Phase = ExecutionRegionPhase.Main,
                Timing = ExecutionRegionTiming.Synchronous,
                Cardinality = ExecutionRegionCardinality.Once,
                Body = Block(2, Leaf("direct-host-index", 2)),
            },
            new ExecutionRegionSyntax
            {
                Origin = ExecutionRegionOrigin.CommandArgument,
                HostClauseElementIndex = 1,
                Phase = ExecutionRegionPhase.Main,
                Timing = ExecutionRegionTiming.Synchronous,
                Cardinality = ExecutionRegionCardinality.Once,
                Body = Block(2, Leaf("detached-argument", 2)),
            },
        };

        foreach (var invalidNode in invalidNodes)
        {
            var succeeded = ShellSyntaxProjection.TryProject(
                Block(0, invalidNode),
                out var result);
            Assert.False(succeeded);
            Assert.Empty(result.Commands);
            Assert.Empty(result.Clauses);
        }
    }

    [Fact]
    public void Invalid_command_owned_execution_regions_fail_closed()
    {
        var clause = ClauseFor("host") with
        {
            Args = new[] { new Arg { Raw = "body", Kind = ArgKind.DynamicSkip } },
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement
                {
                    Role = ClauseElementRole.Argument,
                    Kind = ArgKind.DynamicSkip,
                },
            },
        };
        var valid = ExecutionRegion(
            ExecutionRegionOrigin.CommandArgument,
            hostClauseElementIndex: 1,
            2,
            Leaf("body", 3)) with
        {
            HostArgument = clause.Elements[1],
        };
        var invalidCollections = new IReadOnlyList<ExecutionRegionSyntax>[]
        {
            new ExecutionRegionSyntax[] { null! },
            new[] { valid with { Origin = ExecutionRegionOrigin.DirectCall } },
            new[] { valid with { HostClauseElementIndex = null } },
            new[] { valid with { HostClauseElementIndex = 2 } },
            new[] { valid with { HostClauseElementIndex = 0 } },
            new[] { valid with { Phase = (ExecutionRegionPhase)999 } },
            new[] { valid with { Body = null! } },
            new[] { valid, valid },
            new[]
            {
                valid,
                valid with
                {
                    Body = Block(4, Leaf("distinct-duplicate-coordinate", 5)),
                },
            },
        };

        Assert.Throws<ArgumentNullException>(() =>
            Leaf(clause, 1) with { ExecutionRegions = null! });

        foreach (var executionRegions in invalidCollections)
        {
            var host = Leaf(clause, 1) with { ExecutionRegions = executionRegions };
            var succeeded = ShellSyntaxProjection.TryProject(Block(0, host), out var result);

            Assert.False(succeeded);
            Assert.Empty(result.Commands);
            Assert.Empty(result.Clauses);
        }

        var literalHost = Leaf(
            clause with
            {
                Elements = new[]
                {
                    new ClauseElement { Role = ClauseElementRole.Verb },
                    new ClauseElement
                    {
                        Role = ClauseElementRole.Argument,
                        Kind = ArgKind.Literal,
                    },
                },
            },
            1) with
        {
            ExecutionRegions = new[] { valid },
        };
        Assert.False(ShellSyntaxProjection.TryProject(
            Block(0, literalHost),
            out var literalResult));
        Assert.Empty(literalResult.Commands);
        Assert.Empty(literalResult.Clauses);
    }

    [Fact]
    public void Group_and_command_list_preserve_ordinary_role_and_ancestry()
    {
        var body = Leaf("body", 12);
        var root = Block(
            0,
            new GroupSyntax
            {
                GroupKind = ShellGroupKind.CurrentScope,
                Body = Block(
                    10,
                    new CommandListSyntax
                    {
                        Items = new[]
                        {
                            new CommandListItemSyntax
                            {
                                Command = body,
                                Operator = CompoundOperator.None,
                            },
                        },
                    }),
            });

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.True(succeeded);
        Assert.Equal(CommandOccurrenceRole.Ordinary, result.Commands[0].ImmediateRole);
        AssertFrames(
            result.Commands[0],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.Group, CommandAncestryRegion.GroupBody, null),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0),
            (ShellSyntaxKind.CommandList, CommandAncestryRegion.Statement, 0));
    }

    [Fact]
    public void Null_spans_are_preserved_but_malformed_spans_fail_closed()
    {
        var root = new ShellBlockSyntax
        {
            Statements = new ShellSyntaxNode[] { Leaf("nullable-span", 1) },
        };

        Assert.True(ShellSyntaxProjection.TryProject(root, out var validResult));
        var frame = Assert.Single(validResult.Commands[0].Ancestry);
        Assert.Null(frame.SourceStart);
        Assert.Null(frame.SourceLength);

        var malformed = new PipelineSyntax
        {
            SourceStart = 1,
            Stages = new ShellSyntaxNode[] { Leaf("bad-span", 2) },
        };
        Assert.False(ShellSyntaxProjection.TryProject(Block(0, malformed), out var invalidResult));
        Assert.Empty(invalidResult.Commands);
        Assert.Empty(invalidResult.Clauses);
    }

    [Fact]
    public void Invalid_value_domains_and_argument_coordinates_fail_closed()
    {
        var clause = ClauseFor("echo") with
        {
            Args = new[] { new Arg { Raw = "value", Kind = ArgKind.Literal } },
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement { Role = ClauseElementRole.Argument },
            },
        };
        var root = Block(0, Leaf(clause, 0));
        var invalidFacts = new Func<CommandOccurrenceFacts>[]
        {
            () => new CommandOccurrenceFacts
            {
                WorkingDirectory = new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.FiniteSet,
                    Values = new[] { "/a", "/b" },
                },
            },
            () => new CommandOccurrenceFacts
            {
                EffectiveArguments = new[]
                {
                    new EffectiveArgumentFacts
                    {
                        ClauseElementIndex = 0,
                        Value = ShellValueDomainFacts.Unknown,
                    },
                },
            },
            () => new CommandOccurrenceFacts
            {
                EffectiveArguments = new[]
                {
                    new EffectiveArgumentFacts
                    {
                        ClauseElementIndex = 1,
                        Value = new ShellValueDomainFacts { Kind = ShellValueDomainKind.Exact },
                    },
                },
            },
            () => new CommandOccurrenceFacts
            {
                EffectiveArguments = new[]
                {
                    new EffectiveArgumentFacts
                    {
                        ClauseElementIndex = 1,
                        Value = ShellValueDomainFacts.Unknown,
                    },
                    new EffectiveArgumentFacts
                    {
                        ClauseElementIndex = 1,
                        Value = ShellValueDomainFacts.Unknown,
                    },
                },
            },
        };

        foreach (var createFacts in invalidFacts)
        {
            var succeeded = ShellSyntaxProjection.TryProject(
                root,
                _ => createFacts(),
                out var result);
            Assert.False(succeeded);
            Assert.Empty(result.Commands);
            Assert.Empty(result.Clauses);
        }
    }

    [Fact]
    public void Value_domain_validator_accepts_only_the_six_locked_shapes()
    {
        var clause = ClauseFor("echo") with
        {
            Args = new[] { new Arg { Raw = "value", Kind = ArgKind.Literal } },
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement { Role = ClauseElementRole.Argument },
            },
        };
        var root = Block(0, Leaf(clause, 0));
        var validDomains = new[]
        {
            ShellValueDomainFacts.Unknown,
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { "one" },
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = new[] { "one", "two" },
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Pattern,
                Pattern = "/work/*.txt",
                CoveringDirectory = "/work",
            },
            ShellValueDomainFacts.IntegerRange(0, 255),
            ShellValueDomainFacts.Concatenate(new[]
            {
                new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { "status=" },
                },
                ShellValueDomainFacts.IntegerRange(0, 255),
            }),
        };

        foreach (var domain in validDomains)
        {
            var succeeded = ShellSyntaxProjection.TryProject(
                root,
                _ => FactsForArgument(domain),
                out var result);
            Assert.True(succeeded);
            Assert.Single(result.Commands);
        }

        var overLimit = Enumerable.Range(
                0,
                ShellAnalysisLimits.MaxValueCandidates + 1)
            .Select(index => index.ToString())
            .ToArray();
        var invalidDomains = new[]
        {
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Unknown,
                Values = new[] { "unexpected" },
            },
            new ShellValueDomainFacts { Kind = ShellValueDomainKind.Exact },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { "one", "two" },
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { "one" },
                MaximumInclusive = 1,
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = new[] { "one" },
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = new[] { "duplicate", "duplicate" },
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = overLimit,
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Pattern,
                Pattern = "/work/*.txt",
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.IntegerRange,
                MinimumInclusive = 1,
                MaximumInclusive = 0,
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Concatenation,
                Parts = new[] { ShellValueDomainFacts.IntegerRange(0, 255) },
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Concatenation,
                Parts = new[]
                {
                    ShellValueDomainFacts.IntegerRange(0, 255),
                    ShellValueDomainFacts.Unknown,
                },
            },
            new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Concatenation,
                Parts = new[]
                {
                    new ShellValueDomainFacts
                    {
                        Kind = ShellValueDomainKind.Exact,
                        Values = new[] { "left" },
                    },
                    new ShellValueDomainFacts
                    {
                        Kind = ShellValueDomainKind.Exact,
                        Values = new[] { "right" },
                    },
                },
            },
            new ShellValueDomainFacts { Kind = (ShellValueDomainKind)999 },
        };

        foreach (var domain in invalidDomains)
        {
            var succeeded = ShellSyntaxProjection.TryProject(
                root,
                _ => FactsForArgument(domain),
                out var result);
            Assert.False(succeeded);
            Assert.Empty(result.Commands);
            Assert.Empty(result.Clauses);
        }
    }

    [Fact]
    public void Invalid_redirect_facts_fail_closed()
    {
        var clause = ClauseFor("echo") with
        {
            Redirects = new[] { new Redirect() },
        };
        var root = Block(0, Leaf(clause, 0));
        var invalidRedirects = new[]
        {
            new RedirectAnalysisFacts
            {
                RedirectIndex = 1,
                Source = new RedirectSourceFacts { Kind = RedirectSourceKind.Default },
                Operation = RedirectOperation.FileOutput,
                IsPathRelevant = true,
            },
            new RedirectAnalysisFacts
            {
                RedirectIndex = 0,
                Operation = RedirectOperation.FileOutput,
                IsPathRelevant = true,
                IsComplete = true,
            },
            new RedirectAnalysisFacts
            {
                RedirectIndex = 0,
                Source = new RedirectSourceFacts
                {
                    Kind = RedirectSourceKind.Default,
                    Descriptor = 1,
                },
                Operation = RedirectOperation.FileOutput,
                IsPathRelevant = true,
            },
            new RedirectAnalysisFacts
            {
                RedirectIndex = 0,
                Source = new RedirectSourceFacts { Kind = RedirectSourceKind.Default },
                Operation = RedirectOperation.HereDocument,
            },
            new RedirectAnalysisFacts
            {
                RedirectIndex = 0,
                Source = new RedirectSourceFacts { Kind = RedirectSourceKind.Default },
                Operation = RedirectOperation.HereDocument,
                HereDocument = new HereDocumentAnalysis
                {
                    Delimiter = new ShellSourceFragment
                    {
                        Raw = "EOF",
                        SourceStart = 1,
                    },
                    ExpansionMode = HereDocumentExpansionMode.Literal,
                },
            },
        };

        foreach (var invalidRedirect in invalidRedirects)
        {
            var succeeded = ShellSyntaxProjection.TryProject(
                root,
                _ => new CommandOccurrenceFacts
                {
                    Redirects = new[] { invalidRedirect },
                },
                out var result);
            Assert.False(succeeded);
            Assert.Empty(result.Commands);
            Assert.Empty(result.Clauses);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, 2)]
    public void Known_redirect_sources_cannot_publish_an_unresolved_operation(
        bool isDescriptor,
        int? descriptor)
    {
        var clause = ClauseFor("echo") with
        {
            Redirects = new[] { new Redirect() },
        };
        var root = Block(0, Leaf(clause, 0));

        var succeeded = ShellSyntaxProjection.TryProject(
            root,
            _ => new CommandOccurrenceFacts
            {
                Redirects = new[]
                {
                    new RedirectAnalysisFacts
                    {
                        RedirectIndex = 0,
                        Source = new RedirectSourceFacts
                        {
                            Kind = isDescriptor
                                ? RedirectSourceKind.Descriptor
                                : RedirectSourceKind.Default,
                            Descriptor = descriptor,
                        },
                    },
                },
            },
            out var result);

        Assert.False(succeeded);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Invalid_parser_owned_analysis_discards_partial_projections()
    {
        var root = Block(0, Leaf("echo", 0));

        var succeeded = ShellSyntaxProjection.TryProject(
            root,
            _ => null!,
            out var result);

        Assert.False(succeeded);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    private static ShellBlockSyntax NestedGroups(int count)
    {
        ShellSyntaxNode current = Leaf("deepest", count);
        for (var index = 0; index < count; index++)
        {
            current = new GroupSyntax
            {
                GroupKind = ShellGroupKind.CurrentScope,
                SourceStart = index,
                SourceLength = count - index,
                Body = Block(index, current),
            };
        }

        return Block(0, current);
    }

    private static ShellBlockSyntax NestedSubstitutions(int count)
    {
        var current = Leaf("deepest", count);
        for (var index = count - 1; index >= 0; index--)
        {
            current = Leaf($"level-{index}", index) with
            {
                Substitutions = new[]
                {
                    Substitution(index, current),
                },
            };
        }

        return Block(0, current);
    }

    private static ShellBlockSyntax NestedExecutionRegions(int count)
    {
        ShellSyntaxNode current = Leaf("deepest", count);
        for (var index = count - 1; index >= 0; index--)
        {
            current = ExecutionRegion(
                ExecutionRegionOrigin.DirectCall,
                hostClauseElementIndex: null,
                index,
                current);
        }

        return Block(0, current);
    }

    private static ShellBlockSyntax DecodedExecutionRegionTree()
    {
        var hostClause = ClauseFor("host") with
        {
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement
                {
                    Role = ClauseElementRole.Argument,
                    Kind = ArgKind.DynamicSkip,
                },
            },
        };
        return Block(
            0,
            Leaf(hostClause, 1) with
            {
                ExecutionRegions = new[]
                {
                    ExecutionRegion(
                        ExecutionRegionOrigin.CommandArgument,
                        hostClauseElementIndex: 1,
                        2,
                        Leaf("body", 3),
                        ExecutionRegionPhase.End) with
                    {
                        HostArgument = hostClause.Elements[1],
                        Timing = ExecutionRegionTiming.Concurrent,
                        Cardinality = ExecutionRegionCardinality.ZeroOrMore,
                    },
                },
            });
    }

    private static ShellBlockSyntax InvokePowerShellDecodedClone(
        ShellBlockSyntax source,
        CompoundOperator firstOperator,
        IReadOnlyList<Redirect> wrapperRedirects,
        IReadOnlyList<ClauseElement> wrapperElements)
    {
        var parserType = typeof(PwshParser).Assembly.GetType(
            "ShellSyntaxTree.Internal.Pwsh.Parsing.PwshCommandParser",
            throwOnError: true)!;
        var stateType = parserType.GetNestedType(
            "DecodedCloneState",
            BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, nonPublic: true)!;
        SetProperty(stateType, state, "FirstOperator", firstOperator);
        SetProperty(stateType, state, "OuterSubshell", true);
        SetProperty(stateType, state, "LeafCount", 2);
        SetProperty(stateType, state, "WrapperRedirects", wrapperRedirects);
        SetProperty(stateType, state, "WrapperRedirectElements", wrapperElements);
        var method = parserType.GetMethod(
            "TryCloneDecodedBlock",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var arguments = new object?[] { source, state, null };

        Assert.True((bool)method.Invoke(null, arguments)!);
        Assert.Equal(2, GetProperty<int>(stateType, state, "LeafIndex"));
        return Assert.IsType<ShellBlockSyntax>(arguments[2]);
    }

    private static ShellBlockSyntax InvokeBashDecodedClone(
        ShellBlockSyntax source,
        CompoundOperator firstOperator)
    {
        var parserType = typeof(BashParser).Assembly.GetType(
            "ShellSyntaxTree.Internal.Bash.Parsing.BashCommandParser",
            throwOnError: true)!;
        var method = parserType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == "TryCloneDecodedBlock" &&
                candidate.GetParameters()[4].ParameterType ==
                    typeof(ShellBlockSyntax).MakeByRefType());
        var arguments = new object?[]
        {
            source,
            firstOperator,
            true,
            true,
            null,
            null,
        };

        Assert.True((bool)method.Invoke(null, arguments)!);
        Assert.False(Assert.IsType<bool>(arguments[3]));
        Assert.NotNull(arguments[5]);
        return Assert.IsType<ShellBlockSyntax>(arguments[4]);
    }

    private static void AssertExecutionRegionClone(ExecutionRegionSyntax region)
    {
        Assert.Equal(ExecutionRegionOrigin.CommandArgument, region.Origin);
        Assert.Equal(1, region.HostClauseElementIndex);
        Assert.Equal(ExecutionRegionPhase.End, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Concurrent, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.ZeroOrMore, region.Cardinality);
        Assert.Null(region.SourceStart);
        Assert.Null(region.SourceLength);
    }

    private static void SetProperty(
        Type declaringType,
        object target,
        string name,
        object value) =>
        declaringType.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static T GetProperty<T>(Type declaringType, object target, string name) =>
        Assert.IsType<T>(declaringType
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target));

    private static ShellBlockSyntax Block(int sourceStart, params ShellSyntaxNode[] statements) =>
        new()
        {
            SourceStart = sourceStart,
            SourceLength = statements.Length,
            Statements = statements,
        };

    private static SimpleCommandSyntax Leaf(string verb, int sourceStart) =>
        Leaf(ClauseFor(verb), sourceStart);

    private static SimpleCommandSyntax Leaf(Clause clause, int sourceStart) =>
        new()
        {
            Clause = clause,
            SourceStart = sourceStart,
            SourceLength = 1,
        };

    private static CommandSubstitutionSyntax Substitution(
        int sourceStart,
        params ShellSyntaxNode[] statements) =>
        new()
        {
            SourceStart = sourceStart,
            SourceLength = statements.Length,
            Body = Block(sourceStart, statements),
        };

    private static ExecutionRegionSyntax ExecutionRegion(
        ExecutionRegionOrigin origin,
        int? hostClauseElementIndex,
        int sourceStart,
        ShellSyntaxNode statement,
        ExecutionRegionPhase phase = ExecutionRegionPhase.Main) =>
        new()
        {
            Origin = origin,
            HostClauseElementIndex = hostClauseElementIndex,
            Phase = phase,
            Timing = ExecutionRegionTiming.Synchronous,
            Cardinality = ExecutionRegionCardinality.Once,
            SourceStart = sourceStart,
            SourceLength = 1,
            Body = Block(sourceStart, statement),
        };

    private static Clause ClauseFor(
        string verb,
        CompoundOperator @operator = CompoundOperator.None) =>
        new()
        {
            Operator = @operator,
            Verb = new VerbChain { Tokens = new[] { verb } },
            Elements = new[] { new ClauseElement { Role = ClauseElementRole.Verb } },
        };

    private static LoopBindingSyntax Binding(string name) =>
        new()
        {
            Name = name,
            Source = new ShellSourceFragment { Raw = name },
        };

    private static CommandOccurrenceFacts FactsForArgument(ShellValueDomainFacts domain) =>
        new()
        {
            EffectiveArguments = new[]
            {
                new EffectiveArgumentFacts
                {
                    ClauseElementIndex = 1,
                    Value = domain,
                },
            },
        };

    private static string Verb(CommandOccurrence occurrence) =>
        Assert.Single(occurrence.Clause.Verb.Tokens);

    private static void AssertFrames(
        CommandOccurrence occurrence,
        params (ShellSyntaxKind Kind, CommandAncestryRegion Region, int? ChildIndex)[] expected)
    {
        Assert.Equal(expected.Length, occurrence.Ancestry.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Kind, occurrence.Ancestry[index].AncestorKind);
            Assert.Equal(expected[index].Region, occurrence.Ancestry[index].Region);
            Assert.Equal(expected[index].ChildIndex, occurrence.Ancestry[index].ChildIndex);
        }
    }
}
