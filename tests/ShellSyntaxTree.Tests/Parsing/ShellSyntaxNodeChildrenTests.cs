// -----------------------------------------------------------------------
// <copyright file="ShellSyntaxNodeChildrenTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests;

public class ShellSyntaxNodeChildrenTests
{
    [Fact]
    public void GetChildren_returns_direct_children_for_each_node_kind()
    {
        var leaf = new SimpleCommandSyntax();
        var block = new ShellBlockSyntax { Statements = new[] { leaf } };
        var substitution = new CommandSubstitutionSyntax { Body = block };
        var executionRegion = new ExecutionRegionSyntax { Body = block };
        var simple = new SimpleCommandSyntax
        {
            Substitutions = new[] { substitution },
            ExecutionRegions = new[] { executionRegion },
        };
        var assignment = new ShellAssignmentSyntax
        {
            Substitutions = new[] { substitution },
        };
        var pipeline = new PipelineSyntax { Stages = new ShellSyntaxNode[] { leaf, simple } };
        var listItem = new CommandListItemSyntax { Command = pipeline };
        var commandList = new CommandListSyntax { Items = new[] { listItem } };
        var group = new GroupSyntax { Body = block };
        var redirected = new RedirectedCompoundSyntax { Command = group };
        var forEach = new ForEachSyntax
        {
            IteratorCommands = block,
            Body = block,
        };
        var conditionLoop = new ConditionLoopSyntax
        {
            Condition = block,
            Body = group.Body,
        };
        var branch = new ConditionalBranchSyntax
        {
            Condition = block,
            Body = group.Body,
        };
        var conditional = new ConditionalSyntax
        {
            Branches = new[] { branch },
            Else = block,
        };
        var caseItem = new CaseItemSyntax { Body = block };
        var caseSyntax = new CaseSyntax { Items = new[] { caseItem } };

        AssertChildren(block, leaf);
        AssertChildren(substitution, block);
        AssertChildren(executionRegion, block);
        AssertChildren(simple, substitution, executionRegion);
        AssertChildren(assignment, substitution);
        AssertChildren(pipeline, leaf, simple);
        AssertChildren(commandList, pipeline);
        AssertChildren(group, block);
        AssertChildren(redirected, group);
        AssertChildren(forEach, block, block);
        AssertChildren(conditionLoop, block, block);
        AssertChildren(branch, block, block);
        AssertChildren(conditional, branch, block);
        AssertChildren(caseItem, block);
        AssertChildren(caseSyntax, caseItem);
        AssertChildren(leaf);
    }

    [Fact]
    public void Recursive_traversal_reaches_parser_owned_assignment_substitution_commands()
    {
        var parsed = new BashParser(new BashParserOptions
        {
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        }).Parse("VALUE=$(sudo cat secret); echo done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var nodes = Descendants(parsed.Syntax).ToArray();
        var assignment = Assert.Single(nodes.OfType<ShellAssignmentSyntax>());
        var substitution = Assert.Single(assignment.Substitutions);
        Assert.Same(substitution, Assert.Single(assignment.GetChildren()));
        var nestedCommand = Descendants(substitution)
            .OfType<SimpleCommandSyntax>()
            .Single(command => command.Clause.Verb.Tokens.FirstOrDefault() == "sudo");
        Assert.Equal("sudo", nestedCommand.Clause.Verb.Tokens[0]);
    }

    [Fact]
    public void Recursive_traversal_reaches_redirected_compound_in_assignment_substitution()
    {
        var parsed = new BashParser(new BashParserOptions
        {
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            ParseCompoundRedirects = true,
        }).Parse("VALUE=$(while false; do :; done < input.txt)");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var assignment = Assert.Single(Descendants(parsed.Syntax).OfType<ShellAssignmentSyntax>());
        var wrapper = Assert.Single(Descendants(parsed.Syntax).OfType<RedirectedCompoundSyntax>());
        Assert.Contains(Descendants(assignment), node => ReferenceEquals(node, wrapper));
        Assert.Same(wrapper.Command, Assert.Single(wrapper.GetChildren()));
    }

    [Fact]
    public void Command_list_children_are_the_parser_owned_item_commands()
    {
        var parsed = new BashParser().Parse("echo first; echo second");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var list = Assert.IsType<CommandListSyntax>(Assert.Single(parsed.Syntax.Statements));
        var children = list.GetChildren();

        Assert.Equal(list.Items.Count, children.Count);
        for (var index = 0; index < children.Count; index++)
        {
            Assert.Same(list.Items[index].Command, children[index]);
        }
    }

    [Fact]
    public void GetChildren_fails_when_a_required_child_is_missing()
    {
        var group = new GroupSyntax { Body = null! };
        var commandList = new CommandListSyntax
        {
            Items = new[] { new CommandListItemSyntax { Command = null! } },
        };
        var assignment = new ShellAssignmentSyntax { Substitutions = null! };

        Assert.Throws<InvalidOperationException>(() => group.GetChildren());
        Assert.Throws<InvalidOperationException>(() => commandList.GetChildren());
        Assert.Throws<InvalidOperationException>(() => assignment.GetChildren());
    }

    [Fact]
    public void GetChildren_returns_a_read_only_list_of_the_same_child_objects()
    {
        var child = new SimpleCommandSyntax();
        var parent = new GroupSyntax { Body = new ShellBlockSyntax { Statements = new[] { child } } };

        var children = parent.GetChildren();
        var collection = parent.GetChildren() as ICollection<ShellSyntaxNode>;

        Assert.NotNull(collection);
        Assert.True(collection!.IsReadOnly);
        Assert.Same(parent.Body, children[0]);
        Assert.Same(child, Assert.Single(children[0].GetChildren()));
    }

    [Fact]
    public void GetChildren_keeps_a_read_only_snapshot_of_assignment_substitutions()
    {
        var original = new CommandSubstitutionSyntax { Body = new ShellBlockSyntax() };
        var substitutions = new[] { original };
        var assignment = new ShellAssignmentSyntax { Substitutions = substitutions };

        var children = assignment.GetChildren();
        substitutions[0] = new CommandSubstitutionSyntax { Body = new ShellBlockSyntax() };

        Assert.Same(original, Assert.Single(children));
        var list = Assert.IsAssignableFrom<IList<ShellSyntaxNode>>(children);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = substitutions[0]);
    }

    [Fact]
    public void Diagnostic_partial_syntax_remains_separate_from_authorization_projection()
    {
        var diagnosticNode = new SimpleCommandSyntax();
        var result = new ParsedCommand
        {
            Syntax = new ShellBlockSyntax { Statements = new[] { diagnosticNode } },
            IsUnparseable = true,
            Commands = Array.Empty<CommandOccurrence>(),
            Clauses = Array.Empty<Clause>(),
            UnparseableReason = "diagnostic fixture",
        };

        Assert.Same(diagnosticNode, Assert.Single(result.Syntax.GetChildren()));
        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    private static IEnumerable<ShellSyntaxNode> Descendants(ShellSyntaxNode root)
    {
        var stack = new Stack<ShellSyntaxNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            var children = node.GetChildren();
            for (var index = children.Count - 1; index >= 0; index--)
            {
                stack.Push(children[index]);
            }
        }
    }

    private static void AssertChildren(
        ShellSyntaxNode parent,
        params ShellSyntaxNode[] expected)
    {
        var actual = parent.GetChildren();
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < actual.Count; index++)
        {
            Assert.Same(expected[index], actual[index]);
        }
    }
}
