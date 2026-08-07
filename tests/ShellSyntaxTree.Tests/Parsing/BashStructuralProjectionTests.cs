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

    [Fact]
    public void Command_substitution_is_attached_and_projected_before_its_consumer()
    {
        const string source = "rm \"$(find /tmp)\"";

        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        var outer = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var substitution = Assert.Single(outer.Substitutions);
        Assert.Equal(4, substitution.SourceStart);
        Assert.Equal(12, substitution.SourceLength);
        Assert.Equal(6, substitution.Body.SourceStart);
        Assert.Equal(9, substitution.Body.SourceLength);
        var inner = Assert.IsType<SimpleCommandSyntax>(Assert.Single(substitution.Body.Statements));
        Assert.Equal(6, inner.SourceStart);
        Assert.Equal(9, inner.SourceLength);

        Assert.Equal(new[] { "find", "rm" }, result.Commands.Select(CommandVerb));
        Assert.Equal(result.Clauses, result.Commands.Select(command => command.Clause));
        Assert.Same(inner.Clause, result.Commands[0].Clause);
        Assert.Same(outer.Clause, result.Commands[1].Clause);
        Assert.Equal(CommandOccurrenceRole.Substitution, result.Commands[0].ImmediateRole);
        Assert.Equal(CommandOccurrenceRole.Ordinary, result.Commands[1].ImmediateRole);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));

        Assert.Equal(3, result.Commands[0].Ancestry.Count);
        Assert.Equal(CommandAncestryRegion.Root, result.Commands[0].Ancestry[0].Region);
        Assert.Equal(ShellSyntaxKind.CommandSubstitution, result.Commands[0].Ancestry[1].AncestorKind);
        Assert.Equal(CommandAncestryRegion.Substitution, result.Commands[0].Ancestry[1].Region);
        Assert.Equal(0, result.Commands[0].Ancestry[1].ChildIndex);
        Assert.Equal(ShellSyntaxKind.Block, result.Commands[0].Ancestry[2].AncestorKind);
        Assert.Equal(CommandAncestryRegion.Statement, result.Commands[0].Ancestry[2].Region);

        var authored = Assert.Single(outer.Clause.Args);
        Assert.Equal("\"$(find /tmp)\"", authored.Raw);
        Assert.Equal(ArgKind.DynamicSkip, authored.Kind);
    }

    [Fact]
    public void Multiple_and_nested_substitutions_preserve_parentage_and_order()
    {
        var siblings = Parse("printf '%s %s' \"$(first)\" \"$(second)\"");

        var siblingOuter = Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(siblings.Syntax.Statements));
        Assert.Equal(2, siblingOuter.Substitutions.Count);
        Assert.Equal(new[] { "first", "second", "printf" },
            siblings.Commands.Select(CommandVerb));
        Assert.Equal(0, siblings.Commands[0].Ancestry[1].ChildIndex);
        Assert.Equal(1, siblings.Commands[1].Ancestry[1].ChildIndex);

        var nested = Parse("printf '%s' \"$(echo \"$(whoami)\")\"");

        var nestedOuter = Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(nested.Syntax.Statements));
        var outerSubstitution = Assert.Single(nestedOuter.Substitutions);
        var echo = Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(outerSubstitution.Body.Statements));
        Assert.Single(echo.Substitutions);
        Assert.Equal(new[] { "whoami", "echo", "printf" },
            nested.Commands.Select(CommandVerb));
        Assert.Equal(5, nested.Commands[0].Ancestry.Count);
        Assert.Equal(3, nested.Commands[1].Ancestry.Count);
    }

    [Theory]
    [InlineData("printf '%s' $(id)")]
    [InlineData("printf '%s' pre$(id)post")]
    [InlineData("printf '%s' \"pre$(id)post\"")]
    public void Supported_argument_forms_discover_substitution_and_preserve_dynamic_outer_value(
        string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "id", "printf" }, result.Commands.Select(CommandVerb));
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
        var outer = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Single(outer.Substitutions);
        Assert.Contains(outer.Clause.Args, argument => argument.Kind == ArgKind.DynamicSkip);
    }

    [Fact]
    public void Redirect_substitution_is_visible_while_redirect_analysis_remains_incomplete()
    {
        var result = Parse("cat > \"$(mktemp)\"");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "mktemp", "cat" }, result.Commands.Select(CommandVerb));
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
        Assert.Single(result.Clauses[1].Redirects);
        Assert.True(result.Clauses[1].Redirects[0].IsDynamicSkip);
    }

    [Theory]
    [InlineData("echo \"$(printf x # )\nid)\"")]
    [InlineData("echo \"$(printf x \\\n# )\nid)\"")]
    [InlineData("echo \"$(printf x \\\r\n# )\r\nid)\"")]
    public void Comment_parenthesis_does_not_close_command_substitution_early(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "printf x", "id", "echo" }, result.Commands.Select(CommandVerb));
        Assert.Equal(
            new[] { CompoundOperator.None, CompoundOperator.Sequence, CompoundOperator.None },
            result.Clauses.Select(clause => clause.Operator));
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Empty_command_substitution_is_preserved_without_inventing_a_command()
    {
        var result = Parse("printf '%s' \"$()\"");

        Assert.False(result.IsUnparseable);
        Assert.Equal("printf", CommandVerb(Assert.Single(result.Commands)));
        var command = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var substitution = Assert.Single(command.Substitutions);
        Assert.Empty(substitution.Body.Statements);
        Assert.Contains(command.Clause.Args, argument => argument.Kind == ArgKind.DynamicSkip);
    }

    [Fact]
    public void Substitution_state_is_sequential_inside_and_isolated_from_outer_commands()
    {
        var result = Parse("printf '%s' \"$(cd /tmp; pwd)\"; cat relative.txt");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "cd", "pwd", "printf", "cat" },
            result.Commands.Select(CommandVerb));
        Assert.Equal(
            new[]
            {
                CompoundOperator.None,
                CompoundOperator.Sequence,
                CompoundOperator.None,
                CompoundOperator.Sequence,
            },
            result.Clauses.Select(clause => clause.Operator));
        Assert.Contains(result.Clauses[1].Args,
            argument => argument.IsCwdAttribution && argument.Resolved == "/tmp");
        Assert.DoesNotContain(result.Clauses[2].Args, argument => argument.IsCwdAttribution);
        Assert.Equal("/work/relative.txt", Assert.Single(result.Clauses[3].Args).Resolved);
    }

    [Fact]
    public void Substitution_in_later_list_item_does_not_invent_a_compound_operator()
    {
        var result = Parse("echo ok && rm $(find /tmp)");

        Assert.Equal(new[] { "echo ok", "find", "rm" }, result.Commands.Select(CommandVerb));
        Assert.Equal(
            new[] { CompoundOperator.None, CompoundOperator.None, CompoundOperator.AndIf },
            result.Clauses.Select(clause => clause.Operator));
    }

    [Fact]
    public void Outer_and_inner_wrapper_substitutions_retain_distinct_provenance()
    {
        var outer = Parse("bash -c \"echo $(id)\"");

        Assert.False(outer.IsUnparseable);
        Assert.Equal(new[] { "id", "bash" }, outer.Commands.Select(CommandVerb));
        Assert.True(outer.Commands[0].IsComplete);
        Assert.False(outer.Commands[1].IsComplete);
        Assert.NotNull(outer.Commands[0].Clause.Elements[0].SourceStart);
        Assert.False(outer.Commands[0].Clause.IsCommandStringWrapped);

        var inner = Parse("bash -c 'echo $(id)'");

        Assert.False(inner.IsUnparseable);
        Assert.Equal(new[] { "id", "echo" }, inner.Commands.Select(CommandVerb));
        Assert.All(inner.Commands, command => Assert.True(command.Clause.IsCommandStringWrapped));
        Assert.All(inner.Commands.SelectMany(command => command.Clause.Elements),
            element => Assert.Null(element.SourceStart));
    }

    [Theory]
    [InlineData("$(printf rm) target")]
    [InlineData("r$(printf m) target")]
    [InlineData("\"$(printf rm)\" target")]
    public void Command_name_substitution_fails_closed(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("command", result.UnparseableReason!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("echo `whoami`")]
    [InlineData("echo \"pre`whoami`post\"")]
    [InlineData("echo pre`whoami`post")]
    public void Executable_legacy_backticks_fail_closed(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("backtick", result.UnparseableReason!);
    }

    [Theory]
    [InlineData("printf '%s %s' '$(whoami)' \"\\$(id)\"")]
    [InlineData("printf '%s %s' '`whoami`' \"\\`id\\`\"")]
    public void Literal_substitution_spellings_do_not_create_occurrences(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.Equal("printf", CommandVerb(command));
        Assert.True(command.IsComplete);
        Assert.Empty(Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(result.Syntax.Statements)).Substitutions);
    }

    [Theory]
    [InlineData("id & evil")]
    [InlineData("id&evil")]
    [InlineData("echo \"$(id & evil)\"")]
    [InlineData("echo \"$(id&evil)\"")]
    public void Background_lists_fail_closed_without_partial_projections(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("background", result.UnparseableReason!);
    }

    [Theory]
    [InlineData("printf '%s' '&' \\&")]
    [InlineData("echo \"$(printf '%s' '&')\"")]
    public void Quoted_and_escaped_ampersands_remain_literal_data(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Theory]
    [InlineData("X=1 rm -rf /tmp/x")]
    [InlineData("X+=1 rm -rf /tmp/x")]
    [InlineData("X[0]=1 rm -rf /tmp/x")]
    [InlineData("X[key]+=1 rm -rf /tmp/x")]
    [InlineData("echo \"$(X=1 rm -rf /tmp/x)\"")]
    [InlineData("echo \"$(X\\\n=1 rm -rf /tmp/x)\"")]
    public void Assignment_prefix_commands_fail_closed(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("assignment", result.UnparseableReason!);
    }

    [Fact]
    public void Line_continuation_inside_command_name_is_removed_before_analysis()
    {
        var result = Parse("r\\\nm target");

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.Equal("rm", CommandVerb(command));
        Assert.Equal("/work/target", Assert.Single(command.Clause.Args).Resolved);
    }

    [Fact]
    public void Hash_after_command_name_continuation_remains_part_of_the_word()
    {
        var result = Parse("echo \"$(r\\\n#suffix)\"");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "r#suffix", "echo" }, result.Commands.Select(CommandVerb));
    }

    [Fact]
    public void Escaped_equals_does_not_create_an_assignment_prefix()
    {
        var result = Parse("X\\=1 value");

        Assert.False(result.IsUnparseable);
        Assert.StartsWith("X=1", CommandVerb(Assert.Single(result.Commands)));
    }

    [Theory]
    [InlineData("rm \"$(echo ok &&)\"")]
    [InlineData("rm \"$(diff <(id))\"")]
    [InlineData("rm \"$(echo `id`)\"")]
    [InlineData("rm \"$(cat <<EOF\nvalue\nEOF\n)\"")]
    public void Unsupported_substitution_interior_fails_the_whole_result(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Substitution_structural_depth_limit_is_checked_before_recursive_descent()
    {
        var exact = Parse(NestedSubstitutions(ShellAnalysisLimits.MaxStructuralNesting));

        Assert.False(exact.IsUnparseable);
        Assert.Equal(ShellAnalysisLimits.MaxStructuralNesting + 1, exact.Commands.Count);

        var overflow = Parse(NestedSubstitutions(
            ShellAnalysisLimits.MaxStructuralNesting + 1));

        Assert.True(overflow.IsUnparseable);
        Assert.Empty(overflow.Commands);
        Assert.Empty(overflow.Clauses);
        Assert.Contains("nesting depth", overflow.UnparseableReason!);

        var hostile = Parse(NestedSubstitutions(4096));

        Assert.True(hostile.IsUnparseable);
        Assert.Empty(hostile.Commands);
        Assert.Empty(hostile.Clauses);
    }

    [Fact]
    public void Subshell_and_substitution_share_the_structural_depth_budget()
    {
        var exactSource = new string('(', ShellAnalysisLimits.MaxStructuralNesting - 1)
            + "echo $(id)"
            + new string(')', ShellAnalysisLimits.MaxStructuralNesting - 1);
        Assert.False(Parse(exactSource).IsUnparseable);

        var overflowSource = "(" + exactSource + ")";
        var overflow = Parse(overflowSource);

        Assert.True(overflow.IsUnparseable);
        Assert.Empty(overflow.Commands);
        Assert.Empty(overflow.Clauses);
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

    private static string CommandVerb(CommandOccurrence command) => command.Clause.Verb.Joined;

    private static string NestedSubstitutions(int depth)
    {
        var command = "id";
        for (var index = 0; index < depth; index++)
        {
            command = "echo $(" + command + ")";
        }

        return command;
    }
}
