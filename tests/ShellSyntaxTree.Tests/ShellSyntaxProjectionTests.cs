// -----------------------------------------------------------------------
// <copyright file="ShellSyntaxProjectionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
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
            new EffectiveArgument
            {
                ClauseElementIndex = 1,
                Value = new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { "ready" },
                },
            },
        };
        var redirects = new[]
        {
            new RedirectAnalysis
            {
                RedirectIndex = 0,
                Source = new RedirectSource { Kind = RedirectSourceKind.Default },
                Operation = RedirectOperation.FileOutput,
                Target = new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { "/work/out.txt" },
                },
                IsPathRelevant = true,
                IsComplete = true,
            },
        };
        var workingDirectory = new ShellValueDomain
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
        Assert.Same(workingDirectory, occurrence.WorkingDirectory);
        Assert.NotSame(effectiveArguments, occurrence.EffectiveArguments);
        Assert.Same(effectiveArguments[0], Assert.Single(occurrence.EffectiveArguments));
        Assert.NotSame(redirects, occurrence.Redirects);
        Assert.Same(redirects[0], Assert.Single(occurrence.Redirects));

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
        var nullCollection = Leaf("null-collection", 0) with
        {
            Substitutions = null!,
        };
        Assert.False(ShellSyntaxProjection.TryProject(
            Block(0, nullCollection),
            out var nullCollectionResult));
        Assert.Empty(nullCollectionResult.Commands);
        Assert.Empty(nullCollectionResult.Clauses);

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
        var test = Leaf("test", 70);
        var remove = Leaf("rm", 80);
        var probe = Leaf("probe", 90);
        var echo = Leaf("echo", 100);
        var fallback = Leaf("fallback", 110);
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
        var conditional = new ConditionalSyntax
        {
            SourceStart = 65,
            SourceLength = 55,
            Branches = new[]
            {
                new ConditionalBranchSyntax
                {
                    SourceStart = 65,
                    SourceLength = 20,
                    Condition = Block(68, test),
                    Body = Block(78, remove),
                },
                new ConditionalBranchSyntax
                {
                    SourceStart = 86,
                    SourceLength = 20,
                    Condition = Block(88, probe),
                    Body = Block(98, echo),
                },
            },
            Else = Block(108, fallback),
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
            Statements = new ShellSyntaxNode[] { loop, conditional, substitution },
        };

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.True(succeeded);
        Assert.Equal(
            new[]
            {
                "find", "sort-iterator", "printf", "sort-body", "test",
                "rm", "probe", "echo", "fallback", "inner",
            },
            result.Commands.Select(Verb));
        Assert.Equal(
            new[]
            {
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.Condition,
                CommandOccurrenceRole.Branch,
                CommandOccurrenceRole.Condition,
                CommandOccurrenceRole.Branch,
                CommandOccurrenceRole.Branch,
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
            (ShellSyntaxKind.Conditional, CommandAncestryRegion.Branch, 0),
            (ShellSyntaxKind.ConditionalBranch, CommandAncestryRegion.Condition, null),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
        AssertFrames(
            result.Commands[8],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 1),
            (ShellSyntaxKind.Conditional, CommandAncestryRegion.Branch, 2),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
        AssertFrames(
            result.Commands[9],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 2),
            (ShellSyntaxKind.CommandSubstitution, CommandAncestryRegion.Substitution, 2),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
    }

    [Fact]
    public void Compatibility_projection_preserves_authored_operators_without_synthesizing_relations()
    {
        var condition = ClauseFor("test");
        var thenClause = ClauseFor("rm", CompoundOperator.AndIf);
        var elseClause = ClauseFor("echo");
        var root = Block(
            0,
            new ConditionalSyntax
            {
                Branches = new[]
                {
                    new ConditionalBranchSyntax
                    {
                        Condition = Block(1, Leaf(condition, 1)),
                        Body = Block(2, Leaf(thenClause, 2)),
                    },
                },
                Else = Block(3, Leaf(elseClause, 3)),
            });

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.True(succeeded);
        Assert.Equal(3, result.Clauses.Count);
        Assert.Same(condition, result.Clauses[0]);
        Assert.Same(thenClause, result.Clauses[1]);
        Assert.Same(elseClause, result.Clauses[2]);
        Assert.Equal(
            new[] { CompoundOperator.None, CompoundOperator.AndIf, CompoundOperator.None },
            result.Clauses.Select(clause => clause.Operator));
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
    public void Cyclic_syntax_discards_partial_projections()
    {
        var statements = new List<ShellSyntaxNode>();
        var root = new ShellBlockSyntax { Statements = statements };
        statements.Add(Leaf("first", 0));
        statements.Add(root);

        var succeeded = ShellSyntaxProjection.TryProject(root, out var result);

        Assert.False(succeeded);
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
    public void Group_and_command_list_retain_condition_and_body_roles()
    {
        var condition = Leaf("condition", 4);
        var body = Leaf("body", 12);
        var root = Block(
            0,
            new ConditionLoopSyntax
            {
                LoopKind = ConditionLoopKind.While,
                Condition = Block(
                    2,
                    new GroupSyntax
                    {
                        GroupKind = ShellGroupKind.CurrentScope,
                        Body = Block(3, condition),
                    }),
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
        Assert.Equal(CommandOccurrenceRole.Condition, result.Commands[0].ImmediateRole);
        Assert.Equal(CommandOccurrenceRole.LoopBody, result.Commands[1].ImmediateRole);
        AssertFrames(
            result.Commands[0],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.ConditionLoop, CommandAncestryRegion.Condition, null),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0),
            (ShellSyntaxKind.Group, CommandAncestryRegion.GroupBody, null),
            (ShellSyntaxKind.Block, CommandAncestryRegion.Statement, 0));
        AssertFrames(
            result.Commands[1],
            (ShellSyntaxKind.Block, CommandAncestryRegion.Root, 0),
            (ShellSyntaxKind.ConditionLoop, CommandAncestryRegion.LoopBody, null),
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
                WorkingDirectory = new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.FiniteSet,
                    Values = new[] { "/a", "/b" },
                },
            },
            () => new CommandOccurrenceFacts
            {
                EffectiveArguments = new[]
                {
                    new EffectiveArgument
                    {
                        ClauseElementIndex = 0,
                        Value = ShellValueDomain.Unknown,
                    },
                },
            },
            () => new CommandOccurrenceFacts
            {
                EffectiveArguments = new[]
                {
                    new EffectiveArgument
                    {
                        ClauseElementIndex = 1,
                        Value = new ShellValueDomain { Kind = ShellValueDomainKind.Exact },
                    },
                },
            },
            () => new CommandOccurrenceFacts
            {
                EffectiveArguments = new[]
                {
                    new EffectiveArgument
                    {
                        ClauseElementIndex = 1,
                        Value = ShellValueDomain.Unknown,
                    },
                    new EffectiveArgument
                    {
                        ClauseElementIndex = 1,
                        Value = ShellValueDomain.Unknown,
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
    public void Value_domain_validator_accepts_only_the_four_locked_shapes()
    {
        var clause = ClauseFor("echo") with
        {
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement { Role = ClauseElementRole.Argument },
            },
        };
        var root = Block(0, Leaf(clause, 0));
        var validDomains = new[]
        {
            ShellValueDomain.Unknown,
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { "one" },
            },
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = new[] { "one", "two" },
            },
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Pattern,
                Pattern = "/work/*.txt",
                CoveringDirectory = "/work",
            },
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
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Unknown,
                Values = new[] { "unexpected" },
            },
            new ShellValueDomain { Kind = ShellValueDomainKind.Exact },
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { "one", "two" },
            },
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = new[] { "one" },
            },
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = new[] { "duplicate", "duplicate" },
            },
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.FiniteSet,
                Values = overLimit,
            },
            new ShellValueDomain
            {
                Kind = ShellValueDomainKind.Pattern,
                Pattern = "/work/*.txt",
            },
            new ShellValueDomain { Kind = (ShellValueDomainKind)999 },
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
            new RedirectAnalysis
            {
                RedirectIndex = 1,
                Source = new RedirectSource { Kind = RedirectSourceKind.Default },
                Operation = RedirectOperation.FileOutput,
                IsPathRelevant = true,
            },
            new RedirectAnalysis
            {
                RedirectIndex = 0,
                Operation = RedirectOperation.FileOutput,
                IsPathRelevant = true,
                IsComplete = true,
            },
            new RedirectAnalysis
            {
                RedirectIndex = 0,
                Source = new RedirectSource
                {
                    Kind = RedirectSourceKind.Default,
                    Descriptor = 1,
                },
                Operation = RedirectOperation.FileOutput,
                IsPathRelevant = true,
            },
            new RedirectAnalysis
            {
                RedirectIndex = 0,
                Source = new RedirectSource { Kind = RedirectSourceKind.Default },
                Operation = RedirectOperation.HereDocument,
            },
            new RedirectAnalysis
            {
                RedirectIndex = 0,
                Source = new RedirectSource { Kind = RedirectSourceKind.Default },
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

    private static CommandOccurrenceFacts FactsForArgument(ShellValueDomain domain) =>
        new()
        {
            EffectiveArguments = new[]
            {
                new EffectiveArgument
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
