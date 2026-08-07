// -----------------------------------------------------------------------
// <copyright file="BashStructuralProjectionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>Pins the v0.3 Bash syntax and authorization projections.</summary>
public class BashStructuralProjectionTests
{
    [Fact]
    public void Simple_command_populates_all_projections_with_shared_leaf_identity()
    {
        const string source = "echo hello";

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

        var root = Assert.Single(occurrence.Ancestry);
        Assert.Equal(ShellSyntaxKind.Block, root.AncestorKind);
        Assert.Equal(CommandAncestryRegion.Root, root.Region);
        Assert.Equal(0, root.ChildIndex);
    }

    [Fact]
    public void Mixed_pipeline_and_list_preserve_authored_structure_and_roles()
    {
        var result = Parse("printf x | grep x && echo ok");

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(CompoundOperator.None, list.Items[0].Operator);
        Assert.Equal(CompoundOperator.AndIf, list.Items[1].Operator);

        var pipeline = Assert.IsType<PipelineSyntax>(list.Items[0].Command);
        Assert.Equal(2, pipeline.Stages.Count);
        Assert.All(pipeline.Stages, stage => Assert.IsType<SimpleCommandSyntax>(stage));
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
    public void Nested_and_sibling_subshells_remain_distinct_isolated_groups()
    {
        const string source = "(echo a && (echo b | grep b)) || (echo c)";

        var result = Parse(source);

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(2, list.Items.Count);
        var firstGroup = Assert.IsType<GroupSyntax>(list.Items[0].Command);
        var secondGroup = Assert.IsType<GroupSyntax>(list.Items[1].Command);
        Assert.NotSame(firstGroup, secondGroup);
        Assert.Equal(ShellGroupKind.IsolatedScope, firstGroup.GroupKind);
        Assert.Equal(ShellGroupKind.IsolatedScope, secondGroup.GroupKind);
        Assert.Equal(0, firstGroup.SourceStart);
        Assert.Equal(29, firstGroup.SourceLength);
        Assert.Equal(33, secondGroup.SourceStart);
        Assert.Equal(8, secondGroup.SourceLength);

        var firstBodyList = Assert.IsType<CommandListSyntax>(Assert.Single(firstGroup.Body.Statements));
        var nestedGroup = Assert.IsType<GroupSyntax>(firstBodyList.Items[1].Command);
        var nestedPipeline = Assert.IsType<PipelineSyntax>(Assert.Single(nestedGroup.Body.Statements));
        Assert.Equal(2, nestedPipeline.Stages.Count);
        Assert.Equal(4, result.Commands.Count);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Bash_command_string_keeps_nested_shape_but_clears_outer_source_ranges()
    {
        var result = Parse("bash -c \"echo a | grep a && echo b\"");

        Assert.False(result.IsUnparseable);
        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(ShellGroupKind.IsolatedScope, wrapper.GroupKind);
        Assert.Equal(0, wrapper.SourceStart);
        Assert.Equal(result.Source.Length, wrapper.SourceLength);
        Assert.Null(wrapper.Body.SourceStart);
        Assert.Null(wrapper.Body.SourceLength);
        var wrapped = Assert.IsType<CommandListSyntax>(Assert.Single(wrapper.Body.Statements));
        Assert.Null(wrapped.SourceStart);
        Assert.Null(wrapped.SourceLength);
        var pipeline = Assert.IsType<PipelineSyntax>(wrapped.Items[0].Command);
        Assert.All(pipeline.Stages, stage =>
        {
            Assert.Null(stage.SourceStart);
            Assert.Null(stage.SourceLength);
        });

        Assert.Equal(3, result.Commands.Count);
        Assert.Equal(3, result.Clauses.Count);
        Assert.All(result.Clauses, clause => Assert.True(clause.IsCommandStringWrapped));
        Assert.Equal(result.Clauses, result.Commands.Select(command => command.Clause));
    }

    [Fact]
    public void Structural_depth_overflow_fails_closed_without_partial_projections()
    {
        var source = new string('(', ShellAnalysisLimits.MaxStructuralNesting + 1)
            + "echo ok"
            + new string(')', ShellAnalysisLimits.MaxStructuralNesting + 1);

        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Hostile_structural_depth_is_rejected_before_recursive_descent()
    {
        const int hostileDepth = 4096;
        var source = new string('(', hostileDepth)
            + "echo ok"
            + new string(')', hostileDepth);

        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("nesting depth", result.UnparseableReason!);
    }

    [Fact]
    public void Exact_structural_depth_limit_remains_parseable()
    {
        var source = new string('(', ShellAnalysisLimits.MaxStructuralNesting)
            + "echo ok"
            + new string(')', ShellAnalysisLimits.MaxStructuralNesting);

        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Commands);
        Assert.Single(result.Clauses);
    }

    [Fact]
    public void Operator_entering_subshell_is_carried_by_its_first_compatibility_leaf()
    {
        var result = Parse("echo a && (echo b | echo c)");

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        var group = Assert.IsType<GroupSyntax>(list.Items[1].Command);
        Assert.Equal(CompoundOperator.AndIf, list.Items[1].Operator);
        Assert.IsType<PipelineSyntax>(Assert.Single(group.Body.Statements));
        Assert.Equal(
            new[]
            {
                CompoundOperator.None,
                CompoundOperator.AndIf,
                CompoundOperator.Pipe,
            },
            result.Clauses.Select(clause => clause.Operator));
        Assert.Equal(result.Clauses, result.Commands.Select(command => command.Clause));
    }

    [Fact]
    public void Operator_entering_wrapper_is_carried_by_its_first_decoded_leaf()
    {
        var result = Parse("echo a || bash -c \"echo b; echo c\"");

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        var wrapper = Assert.IsType<GroupSyntax>(list.Items[1].Command);
        Assert.Equal(CompoundOperator.OrIf, list.Items[1].Operator);
        Assert.IsType<CommandListSyntax>(Assert.Single(wrapper.Body.Statements));
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
    public void Decoded_wrapper_preserves_inner_subshell_shape_with_unavailable_spans()
    {
        var result = Parse("bash -c \"(echo a && echo b)\"");

        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        var innerGroup = Assert.IsType<GroupSyntax>(Assert.Single(wrapper.Body.Statements));
        Assert.Null(innerGroup.SourceStart);
        Assert.Null(innerGroup.SourceLength);
        Assert.Null(innerGroup.Body.SourceStart);
        Assert.Null(innerGroup.Body.SourceLength);
        Assert.All(result.Clauses, clause =>
        {
            Assert.True(clause.IsSubshell);
            Assert.True(clause.IsCommandStringWrapped);
        });
    }

    [Theory]
    [InlineData("bash -c \"echo ok\" argv0")]
    [InlineData("bash -c \"echo ok\" > out.txt")]
    public void Unsupported_wrapper_tail_fails_closed(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("wrapper", result.UnparseableReason!);
    }

    [Fact]
    public void Redirect_leaf_is_structurally_visible_but_incomplete_until_redirect_analysis_lands()
    {
        var result = Parse("echo ok > out.txt");

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.False(command.IsComplete);
        Assert.Single(result.Clauses);
    }

    [Fact]
    public void Dynamic_command_string_preserves_compatibility_but_is_not_complete()
    {
        var result = Parse("bash -c $code");

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.False(command.IsComplete);
        Assert.Same(Assert.Single(result.Clauses), command.Clause);
        Assert.False(command.Clause.IsCommandStringWrapped);
    }

    [Theory]
    [InlineData("bash -c \"$code\"")]
    [InlineData("bash -c \"echo $HOME\"")]
    public void Expanding_quoted_command_string_is_not_treated_as_static(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.False(command.IsComplete);
        Assert.Equal(new[] { "bash" }, command.Clause.Verb.Tokens);
        Assert.False(command.Clause.IsCommandStringWrapped);
        Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.All(command.Clause.Elements, element =>
        {
            Assert.NotNull(element.SourceStart);
            Assert.NotNull(element.SourceLength);
        });
    }

    [Theory]
    [InlineData("bash -c 'echo $HOME'")]
    [InlineData("bash -c \"echo \\$HOME\"")]
    [InlineData("bash -x -c 'echo $HOME'")]
    public void Outer_literal_command_string_is_recursively_analyzed(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        Assert.True(command.Clause.IsCommandStringWrapped);
        Assert.Equal(new[] { "echo" }, command.Clause.Verb.Tokens);
        Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
    }

    [Theory]
    [InlineData("bash -$opts -c 'echo safe'")]
    [InlineData("bash $opts 'echo hidden'")]
    [InlineData("bash \"-c\" 'echo hidden'")]
    [InlineData("bash -ce 'echo hidden'")]
    [InlineData("bash -ec 'echo hidden'")]
    [InlineData("bash -xc 'echo hidden'")]
    [InlineData("bash -O extglob -c 'echo hidden'")]
    public void Noncanonical_command_string_options_remain_outer_and_incomplete(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.False(command.IsComplete);
        Assert.False(command.Clause.IsCommandStringWrapped);
        Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
    }

    [Theory]
    [InlineData("rm $(find /tmp)")]
    [InlineData("printf '%s' \"$(whoami)\"")]
    [InlineData("echo `whoami`")]
    public void Undiscovered_command_substitution_keeps_outer_leaf_incomplete(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.False(command.IsComplete);
        Assert.Same(Assert.Single(result.Clauses), command.Clause);
    }

    [Fact]
    public void Ordinary_variable_value_does_not_make_structure_incomplete()
    {
        var result = Parse("printf '%s' $value");

        Assert.False(result.IsUnparseable);
        Assert.True(Assert.Single(result.Commands).IsComplete);
    }

    private static ParsedCommand Parse(string input)
    {
        var parser = new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
        });
        return parser.Parse(input);
    }
}
