// -----------------------------------------------------------------------
// <copyright file="PwshStructuralProjectionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>Pins the v0.3 PowerShell syntax and authorization projections.</summary>
public class PwshStructuralProjectionTests
{
    [Fact]
    public void Simple_command_populates_all_projections_with_shared_leaf_identity()
    {
        const string source = "Get-Item child.txt";

        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal(0, result.Syntax.SourceStart);
        Assert.Equal(source.Length, result.Syntax.SourceLength);
        var simple = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(0, simple.SourceStart);
        Assert.Equal(source.Length, simple.SourceLength);

        var occurrence = Assert.Single(result.Commands);
        var clause = Assert.Single(result.Clauses);
        Assert.Same(clause, simple.Clause);
        Assert.Same(clause, occurrence.Clause);
        Assert.True(occurrence.IsComplete);
        Assert.Equal(CommandOccurrenceRole.Ordinary, occurrence.ImmediateRole);
    }

    [Fact]
    public void Mixed_pipeline_and_list_preserve_authored_structure_and_roles()
    {
        var result = Parse("Get-Item x | Select-Object Name && Get-Date");

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(CompoundOperator.AndIf, list.Items[1].Operator);
        var pipeline = Assert.IsType<PipelineSyntax>(list.Items[0].Command);
        Assert.Equal(2, pipeline.Stages.Count);
        Assert.IsType<SimpleCommandSyntax>(list.Items[1].Command);

        Assert.Equal(3, result.Commands.Count);
        Assert.Equal(
            new[]
            {
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.Ordinary,
            },
            result.Commands.Select(command => command.ImmediateRole));
        Assert.Equal(result.Clauses, result.Commands.Select(command => command.Clause));
    }

    [Fact]
    public void Nested_groups_are_current_scope_and_keep_exact_source_ranges()
    {
        const string source = "((Get-Item x | Select-Object Name))";

        var result = Parse(source);

        var outer = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        var inner = Assert.IsType<GroupSyntax>(Assert.Single(outer.Body.Statements));
        Assert.Equal(ShellGroupKind.CurrentScope, outer.GroupKind);
        Assert.Equal(ShellGroupKind.CurrentScope, inner.GroupKind);
        Assert.Equal(0, outer.SourceStart);
        Assert.Equal(source.Length, outer.SourceLength);
        Assert.Equal(1, inner.SourceStart);
        Assert.Equal(source.Length - 2, inner.SourceLength);
        Assert.IsType<PipelineSyntax>(Assert.Single(inner.Body.Statements));
        Assert.All(result.Clauses, clause => Assert.True(clause.IsSubshell));
    }

    [Fact]
    public void Group_collapses_leading_and_trailing_newlines_without_admitting_a_list()
    {
        var accepted = Parse("(\nGet-Date\n)");
        var rejected = Parse("(\nGet-Date\nGet-Process\n)");

        Assert.False(accepted.IsUnparseable);
        Assert.IsType<GroupSyntax>(Assert.Single(accepted.Syntax.Statements));
        Assert.True(rejected.IsUnparseable);
        Assert.Empty(rejected.Commands);
        Assert.Empty(rejected.Clauses);
    }

    [Fact]
    public void Set_location_propagates_out_of_a_current_scope_group()
    {
        var result = Parse("(Set-Location C:\\sensitive); Remove-Item child.txt");

        var remove = result.Clauses.Last();
        Assert.Contains(
            remove.Args,
            arg => arg.IsCwdAttribution && arg.Resolved == "C:/sensitive");
    }

    [Theory]
    [InlineData("(Get-Date; Get-Process)")]
    [InlineData("Get-Date | (Get-Process)")]
    [InlineData("Write-Output (Get-Date)")]
    [InlineData("& (Get-Command Get-Date)")]
    [InlineData("& { Get-Date }")]
    public void Unsupported_execution_bearing_expression_shapes_fail_closed(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Operator_entering_group_is_carried_by_its_first_compatibility_leaf()
    {
        var result = Parse("Get-Date || (Get-Item x | Select-Object Name)");

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        var group = Assert.IsType<GroupSyntax>(list.Items[1].Command);
        Assert.Equal(ShellGroupKind.CurrentScope, group.GroupKind);
        Assert.Equal(
            new[]
            {
                CompoundOperator.None,
                CompoundOperator.OrIf,
                CompoundOperator.Pipe,
            },
            result.Clauses.Select(clause => clause.Operator));
    }

    [Fact]
    public void Exact_structural_depth_limit_remains_parseable()
    {
        var source = new string('(', ShellAnalysisLimits.MaxStructuralNesting)
            + "Get-Date"
            + new string(')', ShellAnalysisLimits.MaxStructuralNesting);

        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Commands);
    }

    [Fact]
    public void Structural_depth_overflow_fails_before_recursive_descent()
    {
        const int hostileDepth = 4096;
        var source = new string('(', hostileDepth)
            + "Get-Date"
            + new string(')', hostileDepth);

        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("nesting depth", result.UnparseableReason!);
    }

    [Fact]
    public void PowerShell_host_wrapper_preserves_inner_structure_in_an_isolated_group()
    {
        var result = Parse(
            "pwsh -Command \"Get-Item x | Select-Object Name; Get-Date\"");

        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(ShellGroupKind.IsolatedScope, wrapper.GroupKind);
        Assert.Equal(0, wrapper.SourceStart);
        Assert.Equal(result.Source.Length, wrapper.SourceLength);
        Assert.Null(wrapper.Body.SourceStart);
        Assert.Null(wrapper.Body.SourceLength);
        var list = Assert.IsType<CommandListSyntax>(Assert.Single(wrapper.Body.Statements));
        Assert.IsType<PipelineSyntax>(list.Items[0].Command);
        Assert.Equal(3, result.Commands.Count);
        Assert.All(result.Clauses, clause => Assert.True(clause.IsCommandStringWrapped));
        Assert.All(Descendants(wrapper.Body), node =>
        {
            Assert.Null(node.SourceStart);
            Assert.Null(node.SourceLength);
        });

        var leaves = Descendants(wrapper.Body).OfType<SimpleCommandSyntax>().ToArray();
        Assert.Equal(result.Commands.Count, leaves.Length);
        for (var index = 0; index < leaves.Length; index++)
        {
            Assert.Same(leaves[index].Clause, result.Commands[index].Clause);
            Assert.Same(leaves[index].Clause, result.Clauses[index]);
        }
    }

    [Fact]
    public void Invoke_expression_wrapper_preserves_inner_structure_in_current_scope()
    {
        var result = Parse("iex 'Get-Date; Get-Process'");

        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(ShellGroupKind.CurrentScope, wrapper.GroupKind);
        Assert.IsType<CommandListSyntax>(Assert.Single(wrapper.Body.Statements));
        Assert.Equal(2, result.Commands.Count);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Theory]
    [InlineData("Get-Date || pwsh -Command \"Get-Item x; Get-Process\"")]
    [InlineData("Get-Date || iex 'Get-Item x; Get-Process'")]
    public void Operator_entering_static_wrapper_is_carried_by_its_first_leaf(string source)
    {
        var result = Parse(source);

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.IsType<GroupSyntax>(list.Items[1].Command);
        Assert.Equal(
            new[]
            {
                CompoundOperator.None,
                CompoundOperator.OrIf,
                CompoundOperator.Sequence,
            },
            result.Clauses.Select(clause => clause.Operator));
    }

    [Fact]
    public void Invoke_expression_exports_location_changes_to_following_commands()
    {
        var result = Parse(
            "iex 'Set-Location C:\\sensitive'; Remove-Item child.txt");

        var remove = result.Clauses.Last();
        Assert.Contains(
            remove.Args,
            arg => arg.IsCwdAttribution && arg.Resolved == "C:/sensitive");
    }

    [Fact]
    public void Terminal_outer_redirect_keeps_exact_element_span_on_last_wrapped_leaf()
    {
        const string source = "pwsh -Command \"Get-Date; Get-Process\" > out.txt";

        var result = Parse(source);

        Assert.Equal(2, result.Commands.Count);
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
        var last = result.Clauses[1];
        Assert.Single(last.Redirects);
        var redirect = last.Elements.Last();
        Assert.NotNull(redirect.SourceStart);
        Assert.Equal("> out.txt", source.Substring(
            redirect.SourceStart!.Value,
            redirect.SourceLength!.Value));
        Assert.All(last.Elements.Take(last.Elements.Count - 1), element =>
        {
            Assert.Null(element.SourceStart);
            Assert.Null(element.SourceLength);
        });
    }

    [Theory]
    [InlineData("& $exe arg")]
    [InlineData("Invoke-Expression $code")]
    [InlineData("pwsh $flag payload")]
    [InlineData("pwsh $flag -Command \"Get-Date\"")]
    [InlineData("pwsh \"-NoProfile\" -Command \"Get-Date\"")]
    [InlineData("pwsh \"-Command\" \"Get-Date\"")]
    [InlineData("pwsh --% -Command Get-Date")]
    [InlineData("pwsh -CommandWithArgs 'Get-Date'")]
    [InlineData("pwsh -cwa 'Get-Date'")]
    [InlineData("pwsh -Command -")]
    public void Undiscovered_command_identity_or_payload_is_incomplete(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.False(Assert.Single(result.Commands).IsComplete);
    }

    [Fact]
    public void Exact_static_host_prefix_still_allows_structural_recursion()
    {
        var result = Parse("pwsh -NoProfile -Command \"Get-Date\"");

        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(ShellGroupKind.IsolatedScope, wrapper.GroupKind);
        Assert.True(Assert.Single(result.Commands).IsComplete);
        Assert.Equal("Get-Date", Assert.Single(result.Clauses).Verb.Tokens[0]);
    }

    [Fact]
    public void Undiscovered_subexpression_is_incomplete_but_ordinary_variable_data_is_not()
    {
        var hidden = Parse("Write-Output $(Get-Date)");
        var data = Parse("Write-Output $value");

        Assert.False(Assert.Single(hidden.Commands).IsComplete);
        Assert.True(Assert.Single(data.Commands).IsComplete);
    }

    [Fact]
    public void Empty_wrapper_body_with_redirect_preserves_the_compatibility_leaf()
    {
        var result = Parse("pwsh -Command \"\" > out.txt");

        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        var leaf = Assert.IsType<SimpleCommandSyntax>(Assert.Single(wrapper.Body.Statements));
        Assert.Same(leaf.Clause, Assert.Single(result.Clauses));
        Assert.False(Assert.Single(result.Commands).IsComplete);
        Assert.Single(leaf.Clause.Redirects);
    }

    private static ParsedCommand Parse(string source) => new PwshParser(
        new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
        }).Parse(source);

    private static IEnumerable<ShellSyntaxNode> Descendants(ShellSyntaxNode node)
    {
        yield return node;
        switch (node)
        {
            case ShellBlockSyntax block:
                foreach (var statement in block.Statements)
                {
                    foreach (var descendant in Descendants(statement))
                    {
                        yield return descendant;
                    }
                }

                break;
            case CommandListSyntax list:
                foreach (var item in list.Items)
                {
                    foreach (var descendant in Descendants(item.Command))
                    {
                        yield return descendant;
                    }
                }

                break;
            case PipelineSyntax pipeline:
                foreach (var stage in pipeline.Stages)
                {
                    foreach (var descendant in Descendants(stage))
                    {
                        yield return descendant;
                    }
                }

                break;
            case GroupSyntax group:
                foreach (var descendant in Descendants(group.Body))
                {
                    yield return descendant;
                }

                break;
        }
    }
}
