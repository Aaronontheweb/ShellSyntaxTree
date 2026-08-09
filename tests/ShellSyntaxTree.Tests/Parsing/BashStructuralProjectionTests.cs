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
    public void Literal_file_redirect_has_complete_explicit_analysis()
    {
        var result = Parse("echo ok > out.txt");

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(0, redirect.RedirectIndex);
        Assert.Equal(RedirectSourceKind.Default, redirect.Source.Kind);
        Assert.Null(redirect.Source.Descriptor);
        Assert.Equal(RedirectOperation.FileOutput, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Exact, redirect.Target.Kind);
        Assert.Equal("/work/out.txt", Assert.Single(redirect.Target.Values));
        Assert.True(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
        Assert.Single(result.Clauses);
    }

    [Theory]
    [InlineData("dotnet test 2>&1", RedirectSourceKind.Descriptor, 2, RedirectOperation.DescriptorDuplicate, 1)]
    [InlineData("command 2>&-", RedirectSourceKind.Descriptor, 2, RedirectOperation.DescriptorClose, null)]
    [InlineData("command 2>&1-", RedirectSourceKind.Descriptor, 2, RedirectOperation.DescriptorMove, 1)]
    [InlineData("command <&0", RedirectSourceKind.Default, null, RedirectOperation.DescriptorDuplicate, 0)]
    [InlineData("command <&-", RedirectSourceKind.Default, null, RedirectOperation.DescriptorClose, null)]
    [InlineData("command <&0-", RedirectSourceKind.Default, null, RedirectOperation.DescriptorMove, 0)]
    [InlineData("command 3>&1", RedirectSourceKind.Descriptor, 3, RedirectOperation.DescriptorDuplicate, 1)]
    [InlineData("command 10>&2-", RedirectSourceKind.Descriptor, 10, RedirectOperation.DescriptorMove, 2)]
    public void Literal_descriptor_redirects_are_complete_and_not_paths(
        string source,
        RedirectSourceKind sourceKind,
        int? sourceDescriptor,
        RedirectOperation operation,
        int? targetDescriptor)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(sourceKind, redirect.Source.Kind);
        Assert.Equal(sourceDescriptor, redirect.Source.Descriptor);
        Assert.Equal(operation, redirect.Operation);
        Assert.Equal(targetDescriptor, redirect.TargetDescriptor);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
    }

    [Theory]
    [InlineData("command 3> out.log", 3, RedirectOperation.FileOutput)]
    [InlineData("command 10>> out.log", 10, RedirectOperation.FileAppend)]
    [InlineData("command 4< input.txt", 4, RedirectOperation.FileInput)]
    public void Numeric_source_file_redirect_preserves_descriptor(
        string source,
        int sourceDescriptor,
        RedirectOperation operation)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(RedirectSourceKind.Descriptor, redirect.Source.Kind);
        Assert.Equal(sourceDescriptor, redirect.Source.Descriptor);
        Assert.Equal(operation, redirect.Operation);
        Assert.True(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
    }

    [Fact]
    public void Quoted_adjacent_digits_are_an_argument_not_a_source_descriptor()
    {
        var result = Parse("command \"\"3> out.log");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(RedirectSourceKind.Default, redirect.Source.Kind);
        Assert.Equal(RedirectOperation.FileOutput, redirect.Operation);
        Assert.Contains(command.Clause.Args, argument => argument.Raw == "\"\"3");
    }

    [Theory]
    [InlineData("command 3\\\n> out.log", 3, RedirectOperation.FileOutput, null)]
    [InlineData("command 3\\\r\n>&1", 3, RedirectOperation.DescriptorDuplicate, 1)]
    [InlineData("command 1\\\n0>&2-", 10, RedirectOperation.DescriptorMove, 2)]
    public void Continued_numeric_source_preserves_descriptor_semantics(
        string source,
        int sourceDescriptor,
        RedirectOperation operation,
        int? targetDescriptor)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        Assert.DoesNotContain(command.Clause.Args, argument => argument.Raw is "3" or "10");
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(RedirectSourceKind.Descriptor, redirect.Source.Kind);
        Assert.Equal(sourceDescriptor, redirect.Source.Descriptor);
        Assert.Equal(operation, redirect.Operation);
        Assert.Equal(targetDescriptor, redirect.TargetDescriptor);
        Assert.True(redirect.IsComplete);
    }

    [Fact]
    public void Overflow_numeric_source_descriptor_remains_incomplete()
    {
        var result = Parse("command 999999999999999999999> out.log");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.False(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(RedirectSourceKind.Unknown, redirect.Source.Kind);
        Assert.Equal(RedirectOperation.Unknown, redirect.Operation);
        Assert.False(redirect.IsComplete);
    }

    [Fact]
    public void Malformed_numeric_descriptor_target_is_unparseable()
    {
        var result = Parse("command 3>&1bad");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Multiple_redirects_preserve_authored_coordinates_and_independent_operations()
    {
        var result = Parse("command > out.log 2>&1");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        Assert.Collection(
            command.Redirects,
            redirect =>
            {
                Assert.Equal(0, redirect.RedirectIndex);
                Assert.Equal(RedirectOperation.FileOutput, redirect.Operation);
                Assert.Equal("/work/out.log", Assert.Single(redirect.Target.Values));
            },
            redirect =>
            {
                Assert.Equal(1, redirect.RedirectIndex);
                Assert.Equal(RedirectOperation.DescriptorDuplicate, redirect.Operation);
                Assert.Equal(1, redirect.TargetDescriptor);
            });
    }

    [Theory]
    [InlineData("command 2>&$FD")]
    [InlineData("command >&${FD}")]
    public void Computed_descriptor_target_remains_incomplete(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.False(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(RedirectOperation.DescriptorDuplicate, redirect.Operation);
        Assert.Null(redirect.TargetDescriptor);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.False(redirect.IsPathRelevant);
        Assert.False(redirect.IsComplete);
    }

    [Theory]
    [InlineData("command &> out.log", RedirectOperation.CombinedOutput)]
    [InlineData("command &>> out.log", RedirectOperation.CombinedOutputAppend)]
    public void Combined_output_redirects_have_explicit_operations(
        string source,
        RedirectOperation operation)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(RedirectSourceKind.Default, redirect.Source.Kind);
        Assert.Equal(operation, redirect.Operation);
        Assert.Equal("/work/out.log", Assert.Single(redirect.Target.Values));
        Assert.True(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
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
            argument => argument.IsCwdAttribution &&
                argument.Kind == ArgKind.DynamicSkip &&
                argument.Resolved is null);
        Assert.DoesNotContain(result.Clauses[2].Args, argument => argument.IsCwdAttribution);
        Assert.Equal("/work/relative.txt", Assert.Single(result.Clauses[3].Args).Resolved);
    }

    [Fact]
    public void Cwd_flow_uses_command_outcomes_instead_of_parse_order()
    {
        var sequence = Parse("cd /maybe; cat relative.txt");
        var andIf = Parse("cd /maybe && cat relative.txt");
        var orIf = Parse("cd /maybe || cat relative.txt");

        Assert.Equal(ShellValueDomainKind.Unknown, sequence.Commands[1].WorkingDirectory.Kind);
        Assert.Null(sequence.Clauses[1].Args[0].Resolved);
        Assert.Null(sequence.Clauses[1].Elements[1].Resolved);
        Assert.Contains(sequence.Clauses[1].Args, argument =>
            argument.IsCwdAttribution && argument.Kind == ArgKind.DynamicSkip);

        Assert.Equal("/maybe", Assert.Single(andIf.Commands[1].WorkingDirectory.Values));
        Assert.Equal("/maybe/relative.txt", andIf.Clauses[1].Args[0].Resolved);

        Assert.Equal("/work", Assert.Single(orIf.Commands[1].WorkingDirectory.Values));
        Assert.Equal("/work/relative.txt", orIf.Clauses[1].Args[0].Resolved);
        Assert.DoesNotContain(orIf.Clauses[1].Args, argument => argument.IsCwdAttribution);

        var absolute = Parse("cd /outer && cat /work relative.txt");
        Assert.Equal("/work", absolute.Clauses[1].Args[0].Resolved);
        Assert.Equal("/outer/relative.txt", absolute.Clauses[1].Args[1].Resolved);
    }

    [Fact]
    public void Cwd_rebasing_uses_resolver_provenance()
    {
        var parentTraversal = Parse("cd /maybe/deep; cat ../secret.txt");
        var homeExpansion = Parse("cd /home/test || cat $HOME/secret.txt");
        var escapedRelative = Parse(@"cd /maybe; cat \file.txt");

        Assert.Null(parentTraversal.Clauses[1].Args[0].Resolved);
        Assert.Null(parentTraversal.Clauses[1].Elements[1].Resolved);
        Assert.Equal("/home/test/secret.txt", homeExpansion.Clauses[1].Args[0].Resolved);
        Assert.Equal("/home/test/secret.txt", homeExpansion.Clauses[1].Elements[1].Resolved);
        Assert.Null(escapedRelative.Clauses[1].Args[0].Resolved);
        Assert.Null(escapedRelative.Clauses[1].Elements[1].Resolved);
    }

    [Theory]
    [InlineData("cd /maybe/deep || curl -d @../request.json https://example.invalid", "/request.json")]
    [InlineData("cd /maybe/deep || curl --data=@../request.json https://example.invalid", "/request.json")]
    [InlineData("cd /outer/deep && bash -c 'curl -d @../request.json https://example.invalid'", "/outer/request.json")]
    public void Cwd_rebasing_preserves_per_verb_logical_path_operands(
        string source,
        string expected)
    {
        var result = Parse(source);
        var curl = Assert.Single(result.Commands, command => CommandVerb(command) == "curl");

        Assert.Equal(expected, Assert.Single(curl.Clause.Args,
            argument => argument.IsPath).Resolved);
        Assert.Equal(expected, Assert.Single(curl.Clause.Elements,
            element => element.IsPath).Resolved);
    }

    [Fact]
    public void Exact_failure_partition_promotes_paths_blocked_only_by_parse_order_cwd()
    {
        var argumentOr = Parse("cd \"$TARGET\" || cat rel.txt");
        var argumentAnd = Parse("cd \"$TARGET\" && cat rel.txt");
        var redirectOr = Parse("cd \"$TARGET\" || printf x > out.txt");
        var redirectAnd = Parse("cd \"$TARGET\" && printf x > out.txt");
        var transformedOr = Parse(
            "cd \"$TARGET\" || curl --data=@payload.json https://example.invalid");

        var exactArgument = Assert.Single(argumentOr.Clauses[1].Args);
        Assert.Equal(ArgKind.Literal, exactArgument.Kind);
        Assert.True(exactArgument.IsPath);
        Assert.Equal("/work/rel.txt", exactArgument.Resolved);
        Assert.Equal("/work/rel.txt", argumentOr.Clauses[1].Elements[1].Resolved);

        Assert.Null(argumentAnd.Clauses[1].Args[0].Resolved);
        Assert.Equal(ArgKind.DynamicSkip, argumentAnd.Clauses[1].Args[0].Kind);

        var exactRedirect = Assert.Single(redirectOr.Clauses[1].Redirects);
        Assert.False(exactRedirect.IsDynamicSkip);
        Assert.Equal("/work/out.txt", exactRedirect.Target);
        var exactRedirectElement = Assert.Single(redirectOr.Clauses[1].Elements,
            element => element.Role == ClauseElementRole.Redirect);
        Assert.Equal(ArgKind.Literal, exactRedirectElement.Kind);
        Assert.True(exactRedirectElement.IsPath);
        Assert.Equal("/work/out.txt", exactRedirectElement.Resolved);

        Assert.True(Assert.Single(redirectAnd.Clauses[1].Redirects).IsDynamicSkip);

        Assert.Equal("/work/payload.json", Assert.Single(transformedOr.Clauses[1].Args,
            argument => argument.IsPath).Resolved);
        Assert.Equal("/work/payload.json", Assert.Single(transformedOr.Clauses[1].Elements,
            element => element.IsPath).Resolved);
    }

    [Theory]
    [InlineData("command command cd /outer && cat relative.txt")]
    [InlineData("builtin builtin cd /outer && cat relative.txt")]
    [InlineData("command builtin cd /outer && cat relative.txt")]
    [InlineData("builtin command cd /outer && cat relative.txt")]
    public void Nested_dispatch_wrapped_cwd_uses_the_effective_argv(string source)
    {
        var result = Parse(source);

        Assert.Equal(
            "/outer",
            Assert.Single(result.Commands[1].WorkingDirectory.Values));
        Assert.Equal("/outer/relative.txt", result.Clauses[1].Args[0].Resolved);
    }

    [Theory]
    [InlineData("command cd /outer && cat relative.txt")]
    [InlineData("builtin cd /outer && cat relative.txt")]
    public void Dispatch_wrapped_cwd_uses_the_effective_argv(string source)
    {
        var result = Parse(source);

        Assert.Equal(
            "/outer",
            Assert.Single(result.Commands[1].WorkingDirectory.Values));
        Assert.Equal("/outer/relative.txt", result.Clauses[1].Args[0].Resolved);
    }

    [Fact]
    public void Cd_operand_forms_only_publish_exact_cwd_when_lexically_safe()
    {
        var cdPath = Parse("cd /a && cd sub && pwd");
        var explicitRelative = Parse("cd /a && cd ./sub && pwd");
        var physical = Parse("cd -P /a && pwd");
        var dashOperand = Parse("cd -- -foo && pwd");

        Assert.Null(cdPath.Clauses[1].Args[0].Resolved);
        Assert.Equal(ShellValueDomainKind.Unknown, cdPath.Commands[2].WorkingDirectory.Kind);
        Assert.Equal("/a/sub", Assert.Single(explicitRelative.Commands[2].WorkingDirectory.Values));
        Assert.Equal(ShellValueDomainKind.Unknown, physical.Commands[1].WorkingDirectory.Kind);
        Assert.Equal(ShellValueDomainKind.Unknown, dashOperand.Commands[1].WorkingDirectory.Kind);
    }

    [Theory]
    [InlineData("cd ~ && pwd", "/home/test")]
    [InlineData("cd $HOME && pwd", "/home/test")]
    [InlineData("cd . && pwd", "/work")]
    [InlineData("cd .. && pwd", "/")]
    public void Cd_operands_that_bypass_search_retain_exact_cwd(
        string source,
        string expected)
    {
        var result = Parse(source);

        Assert.Equal(expected, Assert.Single(result.Commands[1].WorkingDirectory.Values));
        Assert.Equal(expected, Assert.Single(result.Clauses[1].Args).Resolved);
    }

    [Fact]
    public void Cd_extended_attribute_option_fails_closed()
    {
        var result = Parse("cd -@ /a && pwd");

        Assert.Equal(ShellValueDomainKind.Unknown, result.Commands[1].WorkingDirectory.Kind);
        Assert.Contains(result.Clauses[1].Args, argument =>
            argument.IsCwdAttribution && argument.Kind == ArgKind.DynamicSkip);
    }

    [Theory]
    [InlineData("> out.txt; cat relative.txt")]
    [InlineData("> out.txt && cat relative.txt")]
    [InlineData("> out.txt || cat relative.txt")]
    public void Redirect_only_commands_preserve_cwd_state(string source)
    {
        var result = Parse(source);

        var cat = Assert.Single(result.Commands, command => CommandVerb(command) == "cat");
        Assert.Equal("/work", Assert.Single(cat.WorkingDirectory.Values));
        Assert.Equal("/work/relative.txt", result.Clauses[1].Args[0].Resolved);
        Assert.DoesNotContain(result.Clauses[1].Args, argument =>
            argument.IsCwdAttribution);
    }

    [Fact]
    public void Redirect_paths_follow_the_same_outcome_sensitive_cwd()
    {
        var sequence = Parse("cd /maybe; printf x > relative.txt");
        var andIf = Parse("cd /maybe && printf x > relative.txt");

        var unknownRedirect = Assert.Single(sequence.Clauses[1].Redirects);
        Assert.True(unknownRedirect.IsDynamicSkip);
        Assert.Equal("relative.txt", unknownRedirect.Target);
        Assert.Null(Assert.Single(sequence.Clauses[1].Elements,
            element => element.Role == ClauseElementRole.Redirect).Resolved);

        Assert.Equal("/maybe/relative.txt",
            Assert.Single(andIf.Clauses[1].Redirects).Target);

        var quoted = Parse("cd /maybe; printf x > \"relative file.txt\"");
        var quotedRedirect = Assert.Single(quoted.Clauses[1].Redirects);
        Assert.True(quotedRedirect.IsDynamicSkip);
        Assert.Equal("\"relative file.txt\"", quotedRedirect.Target);
    }

    [Fact]
    public void Pipeline_options_join_last_stage_state_conservatively()
    {
        var result = Parse("printf x | cd /tmp; pwd");

        Assert.All(result.Commands.Take(2), command =>
            Assert.Equal("/work", Assert.Single(command.WorkingDirectory.Values)));
        Assert.Equal(ShellValueDomainKind.Unknown, result.Commands[2].WorkingDirectory.Kind);
        Assert.Contains(result.Clauses[2].Args, argument =>
            argument.IsCwdAttribution && argument.Kind == ArgKind.DynamicSkip);
    }

    [Fact]
    public void Decoded_wrapper_inherits_invocation_cwd_and_isolates_exit_state()
    {
        var result = Parse("cd /outer && bash -c 'cd /inner && pwd' && pwd");

        Assert.Equal(new[] { "/work", "/outer", "/inner", "/outer" },
            result.Commands.Select(command =>
                Assert.Single(command.WorkingDirectory.Values)));
        Assert.DoesNotContain(result.Clauses[1].Args, argument =>
            argument.IsCwdAttribution && argument.Resolved == "/outer");
        Assert.Contains(result.Clauses[2].Args, argument =>
            argument.IsCwdAttribution && argument.Resolved == "/inner");
        Assert.Contains(result.Clauses[3].Args, argument =>
            argument.IsCwdAttribution && argument.Resolved == "/outer");
    }

    [Theory]
    [InlineData("cd /maybe; echo \"$(cat rel.txt)\"")]
    [InlineData("pushd /maybe && echo \"$(cat rel.txt)\"")]
    [InlineData("pushd /maybe && bash -c 'cat rel.txt'")]
    public void Isolated_child_retains_dynamic_cwd_signal(string source)
    {
        var result = Parse(source);
        var cat = Assert.Single(result.Commands, command => CommandVerb(command) == "cat");

        Assert.Equal(ShellValueDomainKind.Unknown, cat.WorkingDirectory.Kind);
        Assert.Null(Assert.Single(cat.Clause.Args,
            argument => !argument.IsCwdAttribution).Resolved);
        Assert.Contains(cat.Clause.Args, argument =>
            argument.IsCwdAttribution && argument.Kind == ArgKind.DynamicSkip);
    }

    [Fact]
    public void Substitution_on_cd_failure_uses_failure_partition_cwd()
    {
        var result = Parse("cd /maybe || echo \"$(cat rel.txt)\"");
        var cat = Assert.Single(result.Commands, command => CommandVerb(command) == "cat");

        Assert.Equal("/work", Assert.Single(cat.WorkingDirectory.Values));
        Assert.Equal("/work/rel.txt", Assert.Single(cat.Clause.Args).Resolved);
        Assert.DoesNotContain(cat.Clause.Args, argument => argument.IsCwdAttribution);
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
    public void Expanding_heredoc_substitution_is_attached_before_its_consumer()
    {
        const string source = "cat <<EOF\n$(printf body)\nEOF";
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "printf body", "cat" }, result.Commands.Select(CommandVerb));
        Assert.True(result.Commands[0].IsComplete);
        Assert.True(result.Commands[1].IsComplete);
        var outer = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(0, outer.SourceStart);
        Assert.Equal(source.Length, outer.SourceLength);
        var substitution = Assert.Single(outer.Substitutions);
        Assert.Equal(source.IndexOf("$(", System.StringComparison.Ordinal), substitution.SourceStart);
        Assert.Equal("$(printf body)".Length, substitution.SourceLength);
        Assert.Equal(CommandOccurrenceRole.Substitution, result.Commands[0].ImmediateRole);
    }

    [Fact]
    public void Literal_heredoc_publishes_authored_data_and_retains_compatibility_redirect()
    {
        const string source = "cat > output.txt <<'EOF'\nhello\nEOF\n";

        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.True(occurrence.IsComplete);
        Assert.Equal(2, occurrence.Redirects.Count);

        var redirect = occurrence.Redirects[1];
        Assert.Equal(1, redirect.RedirectIndex);
        Assert.Equal(RedirectSourceKind.Default, redirect.Source.Kind);
        Assert.Null(redirect.Source.Descriptor);
        Assert.Equal(RedirectOperation.HereDocument, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);

        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal("'EOF'", hereDocument.Delimiter.Raw);
        Assert.Equal(source.IndexOf("'EOF'", System.StringComparison.Ordinal),
            hereDocument.Delimiter.SourceStart);
        Assert.Equal("'EOF'".Length, hereDocument.Delimiter.SourceLength);
        Assert.Equal("hello\n", hereDocument.Body.Raw);
        Assert.Equal(source.IndexOf("hello", System.StringComparison.Ordinal),
            hereDocument.Body.SourceStart);
        Assert.Equal("hello\n".Length, hereDocument.Body.SourceLength);
        Assert.Equal(HereDocumentExpansionMode.Literal, hereDocument.ExpansionMode);
        Assert.False(hereDocument.StripLeadingTabs);
        Assert.True(hereDocument.IsComplete);

        var compatibility = occurrence.Clause.Redirects[1];
        Assert.Equal(RedirectDirection.In, compatibility.Direction);
        Assert.Equal("<<EOF>", compatibility.Target);
        Assert.False(compatibility.IsDynamicSkip);
        var element = occurrence.Clause.Elements.Last(item =>
            item.Role == ClauseElementRole.Redirect);
        Assert.Equal("<<'EOF'", element.Raw);
        Assert.Equal("EOF", element.Value);
        Assert.False(element.IsPath);
    }

    [Theory]
    [InlineData("cat <<< \"hello\"", "hello\n")]
    [InlineData("cat <<< \"\"", "\n")]
    [InlineData("cat 3<<<payload", "payload\n")]
    [InlineData("cat <<< *.txt", "*.txt\n")]
    [InlineData("cat <<< ~", "/home/test\n")]
    [InlineData("cat <<< $HOME", "/home/test\n")]
    public void Literal_here_string_publishes_complete_non_path_data(
        string source,
        string expectedData)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.True(occurrence.IsComplete);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(RedirectOperation.HereString, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Exact, redirect.Target.Kind);
        Assert.Equal(new[] { expectedData }, redirect.Target.Values);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
        Assert.Null(redirect.HereDocument);

        var expectedDescriptor = source.Contains("3<<<", System.StringComparison.Ordinal)
            ? 3
            : (int?)null;
        Assert.Equal(
            expectedDescriptor is null
                ? RedirectSourceKind.Default
                : RedirectSourceKind.Descriptor,
            redirect.Source.Kind);
        Assert.Equal(expectedDescriptor, redirect.Source.Descriptor);

        var compatibility = Assert.Single(occurrence.Clause.Redirects);
        Assert.Equal(RedirectDirection.In, compatibility.Direction);
        Assert.False(compatibility.IsDynamicSkip);
        var element = occurrence.Clause.Elements.Last(item =>
            item.Role == ClauseElementRole.Redirect);
        Assert.False(element.IsPath);
    }

    [Fact]
    public void Dynamic_here_string_data_is_unknown_but_structurally_complete()
    {
        var result = Parse("cat <<< \"$value\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.True(occurrence.IsComplete);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(RedirectOperation.HereString, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
        Assert.True(Assert.Single(occurrence.Clause.Redirects).IsDynamicSkip);
    }

    [Fact]
    public void Here_string_substitution_is_visible_and_leaves_data_unknown()
    {
        var result = Parse("cat <<< \"$(printf payload)\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(new[] { "printf payload", "cat" }, result.Commands.Select(CommandVerb));
        Assert.Equal(CommandOccurrenceRole.Substitution, result.Commands[0].ImmediateRole);
        var consumer = result.Commands[1];
        Assert.True(consumer.IsComplete);
        var redirect = Assert.Single(consumer.Redirects);
        Assert.Equal(RedirectOperation.HereString, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.True(redirect.IsComplete);
    }

    [Theory]
    [InlineData("cat <<<")]
    [InlineData("cat <<<< payload")]
    public void Malformed_here_string_fails_the_whole_parse(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Expanding_heredoc_publishes_complete_body_and_substitution_facts()
    {
        const string source = "cat <<EOF\nbefore $(id) after\nEOF";

        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(new[] { "id", "cat" }, result.Commands.Select(CommandVerb));
        var consumer = result.Commands[1];
        Assert.True(consumer.IsComplete);
        var redirect = Assert.Single(consumer.Redirects);
        Assert.Equal(RedirectOperation.HereDocument, redirect.Operation);
        Assert.True(redirect.IsComplete);
        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal("EOF", hereDocument.Delimiter.Raw);
        Assert.Equal("before $(id) after\n", hereDocument.Body.Raw);
        Assert.Equal(HereDocumentExpansionMode.Expand, hereDocument.ExpansionMode);
        Assert.True(hereDocument.IsComplete);
        Assert.Equal(CommandOccurrenceRole.Substitution, result.Commands[0].ImmediateRole);
    }

    [Fact]
    public void Simple_parameter_in_expanding_heredoc_remains_complete_data()
    {
        const string source = "cat <<EOF\n${value}\nEOF";

        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var redirect = Assert.Single(Assert.Single(result.Commands).Redirects);
        Assert.True(redirect.IsComplete);
        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal("${value}\n", hereDocument.Body.Raw);
        Assert.Equal(HereDocumentExpansionMode.Expand, hereDocument.ExpansionMode);
        Assert.True(hereDocument.IsComplete);
    }

    [Fact]
    public void Tab_stripping_heredoc_retains_authored_tabs_in_body_provenance()
    {
        const string source = "cat <<-EOF\n\tvalue\n\tEOF";

        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var redirect = Assert.Single(Assert.Single(result.Commands).Redirects);
        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal(RedirectOperation.HereDocument, redirect.Operation);
        Assert.Equal("\tvalue\n", hereDocument.Body.Raw);
        Assert.Equal(source.IndexOf("\tvalue", System.StringComparison.Ordinal),
            hereDocument.Body.SourceStart);
        Assert.True(hereDocument.StripLeadingTabs);
        Assert.Equal(HereDocumentExpansionMode.Expand, hereDocument.ExpansionMode);
        Assert.True(hereDocument.IsComplete);
    }

    [Theory]
    [InlineData("cat 2<<EOF\nbody\nEOF", 2, false)]
    [InlineData("cat 10<<-EOF\n\tbody\n\tEOF", 10, true)]
    [InlineData("cat 1\\\n0<<EOF\nbody\nEOF", 10, false)]
    public void Numeric_source_heredoc_preserves_descriptor_and_body_semantics(
        string source,
        int sourceDescriptor,
        bool stripLeadingTabs)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.True(occurrence.IsComplete);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(RedirectSourceKind.Descriptor, redirect.Source.Kind);
        Assert.Equal(sourceDescriptor, redirect.Source.Descriptor);
        Assert.Equal(RedirectOperation.HereDocument, redirect.Operation);
        Assert.True(redirect.IsComplete);
        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal("EOF", hereDocument.Delimiter.Raw);
        Assert.Equal(stripLeadingTabs ? "\tbody\n" : "body\n", hereDocument.Body.Raw);
        Assert.Equal(stripLeadingTabs, hereDocument.StripLeadingTabs);
        Assert.True(hereDocument.IsComplete);

        var compatibility = Assert.Single(occurrence.Clause.Redirects);
        Assert.Equal(RedirectDirection.In, compatibility.Direction);
        Assert.Equal("<<EOF>", compatibility.Target);
        Assert.False(compatibility.IsDynamicSkip);
    }

    [Fact]
    public void Overflow_numeric_source_heredoc_preserves_body_but_remains_incomplete()
    {
        var result = Parse("cat 999999999999999999999<<EOF\nbody\nEOF");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.False(occurrence.IsComplete);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(RedirectSourceKind.Unknown, redirect.Source.Kind);
        Assert.Equal(RedirectOperation.HereDocument, redirect.Operation);
        Assert.False(redirect.IsComplete);
        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal("body\n", hereDocument.Body.Raw);
        Assert.True(hereDocument.IsComplete);
    }

    [Theory]
    [InlineData("cat <<EOF\nEOF", "")]
    [InlineData("cat <<EOF\r\nbody\r\nEOF", "body\r\n")]
    public void Heredoc_body_fragment_preserves_empty_and_crlf_source(
        string source,
        string expectedBody)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var redirect = Assert.Single(Assert.Single(result.Commands).Redirects);
        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal(expectedBody, hereDocument.Body.Raw);
        Assert.Equal(expectedBody.Length, hereDocument.Body.SourceLength);
        Assert.Equal(source.IndexOf('\n') + 1, hereDocument.Body.SourceStart);
        Assert.True(redirect.IsComplete);
        Assert.True(hereDocument.IsComplete);
    }

    [Theory]
    [InlineData("E\\OF", "E\\OF")]
    [InlineData("E\"OF\"", "E\"OF\"")]
    public void Escaped_or_mixed_quoted_delimiter_publishes_literal_mode(
        string delimiter,
        string expectedRaw)
    {
        var source = "cat <<" + delimiter + "\n$value\nEOF";

        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var redirect = Assert.Single(Assert.Single(result.Commands).Redirects);
        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal(expectedRaw, hereDocument.Delimiter.Raw);
        Assert.Equal("$value\n", hereDocument.Body.Raw);
        Assert.Equal(HereDocumentExpansionMode.Literal, hereDocument.ExpansionMode);
        Assert.True(hereDocument.IsComplete);
    }

    [Fact]
    public void Decoded_wrapper_heredoc_retains_raw_data_without_outer_source_offsets()
    {
        const string source = "bash -c \"cat <<'EOF'\nhello\nEOF\"";

        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.True(occurrence.Clause.IsCommandStringWrapped);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.True(redirect.IsComplete);
        var hereDocument = Assert.IsType<HereDocumentAnalysis>(redirect.HereDocument);
        Assert.Equal("'EOF'", hereDocument.Delimiter.Raw);
        Assert.Equal("hello\n", hereDocument.Body.Raw);
        Assert.Null(hereDocument.Delimiter.SourceStart);
        Assert.Null(hereDocument.Delimiter.SourceLength);
        Assert.Null(hereDocument.Body.SourceStart);
        Assert.Null(hereDocument.Body.SourceLength);
    }

    [Theory]
    [InlineData("cat <<'EOF'\n$(id)\nEOF")]
    [InlineData("cat <<E\"OF\"\n$(id)\nEOF")]
    [InlineData("cat <<E\\OF\n$(id)\nEOF")]
    public void Quoted_or_escaped_heredoc_delimiters_keep_body_literal(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal("cat", CommandVerb(Assert.Single(result.Commands)));
        Assert.Empty(Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(result.Syntax.Statements)).Substitutions);
    }

    [Theory]
    [InlineData("'$(id)'")]
    [InlineData("\"$(id)\"")]
    public void Quote_characters_in_expanding_heredoc_body_do_not_suppress_execution(
        string body)
    {
        var result = Parse("cat <<EOF\n" + body + "\nEOF");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "id", "cat" }, result.Commands.Select(CommandVerb));
    }

    [Theory]
    [InlineData("\\$(id)", false)]
    [InlineData("\\\\$(id)", true)]
    [InlineData("\\\\\\$(id)", false)]
    [InlineData("\\\\\\\\$(id)", true)]
    public void Expanding_heredoc_backslash_parity_controls_substitution(
        string body,
        bool executes)
    {
        var result = Parse("cat <<EOF\n" + body + "\nEOF");

        Assert.False(result.IsUnparseable);
        Assert.Equal(executes ? 2 : 1, result.Commands.Count);
        Assert.Equal("cat", CommandVerb(result.Commands.Last()));
    }

    [Fact]
    public void Multiple_and_nested_heredoc_substitutions_preserve_authored_order()
    {
        var multiple = Parse("cat <<EOF\n$(first)\n$(second)\nEOF");
        Assert.Equal(new[] { "first", "second", "cat" },
            multiple.Commands.Select(CommandVerb));
        var outer = Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(multiple.Syntax.Statements));
        Assert.Equal(2, outer.Substitutions.Count);
        Assert.Equal(0, multiple.Commands[0].Ancestry[1].ChildIndex);
        Assert.Equal(1, multiple.Commands[1].Ancestry[1].ChildIndex);

        var nested = Parse("cat <<EOF\n$(echo $(whoami))\nEOF");
        Assert.Equal(new[] { "whoami", "echo", "cat" },
            nested.Commands.Select(CommandVerb));
    }

    [Fact]
    public void Heredoc_substitution_cwd_is_isolated_from_its_consumer_and_continuation()
    {
        var result = Parse("cat <<EOF\n$(cd /tmp; pwd)\nEOF\ncat relative.txt");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "cd", "pwd", "cat", "cat" },
            result.Commands.Select(CommandVerb));
        Assert.Contains(result.Clauses[1].Args,
            argument => argument.IsCwdAttribution &&
                argument.Kind == ArgKind.DynamicSkip &&
                argument.Resolved is null);
        Assert.DoesNotContain(result.Clauses[2].Args,
            argument => argument.IsCwdAttribution);
        Assert.Equal("/work/relative.txt", Assert.Single(result.Clauses[3].Args).Resolved);
    }

    [Fact]
    public void Tab_stripping_heredoc_discovers_substitution_with_authored_span()
    {
        const string source = "cat <<-EOF\n\t$(id)\n\tEOF";
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "id", "cat" }, result.Commands.Select(CommandVerb));
        var substitution = Assert.Single(Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(result.Syntax.Statements)).Substitutions);
        Assert.Equal(source.IndexOf("$(", System.StringComparison.Ordinal), substitution.SourceStart);
    }

    [Fact]
    public void Heredoc_header_comment_does_not_disable_body_discovery()
    {
        var result = Parse("cat <<EOF # body follows\n$(id)\nEOF");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "id", "cat" }, result.Commands.Select(CommandVerb));
    }

    [Fact]
    public void Heredoc_substitutions_share_the_structural_depth_budget()
    {
        var exact = Parse("cat <<EOF\n" +
            NestedSubstitutions(ShellAnalysisLimits.MaxStructuralNesting) +
            "\nEOF");
        Assert.False(exact.IsUnparseable);

        var overflow = Parse("cat <<EOF\n" +
            NestedSubstitutions(ShellAnalysisLimits.MaxStructuralNesting + 1) +
            "\nEOF");
        Assert.True(overflow.IsUnparseable);
        Assert.Empty(overflow.Commands);
        Assert.Empty(overflow.Clauses);
    }

    [Theory]
    [InlineData("cat <<EOF >out\nbody\nEOF")]
    [InlineData("cat <<EOF; evil\nbody\nEOF")]
    [InlineData("cat <<EOF | sh\nbody\nEOF")]
    [InlineData("cat <<A <<B\na\nA\nb\nB")]
    [InlineData("cat <<EOF\n`id`\nEOF")]
    [InlineData("cat <<EOF\n$((1+1))\nEOF")]
    [InlineData("cat <<EOF\n$[x]\nEOF")]
    [InlineData("cat <<EOF\n${x@P}\nEOF")]
    [InlineData("cat <<EOF\n${x:-$(id)}\nEOF")]
    [InlineData("cat <<EOF\n$\\\n(id)\nEOF")]
    [InlineData("cat <<EOF\n$(id\nEOF")]
    [InlineData("cat <<EOF\n$(id)\n")]
    public void Unsupported_or_hidden_heredoc_execution_fails_whole(string source)
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

    [Fact]
    public void Policy_relevant_static_path_publishes_exact_effective_value()
    {
        var command = Assert.Single(Parse("cat \\$HOME").Commands);

        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(1, effective.ClauseElementIndex);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("$HOME", Assert.Single(effective.Value.Values));
    }

    [Fact]
    public void Runtime_parameter_publishes_unknown_effective_value()
    {
        var command = Assert.Single(Parse("cat \"$?\"").Commands);

        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(1, effective.ClauseElementIndex);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
        Assert.True(command.IsComplete);
    }

    [Fact]
    public void Plain_literal_non_path_argument_does_not_add_effective_overlay()
    {
        var command = Assert.Single(Parse("printf '%s' value").Commands);

        Assert.Empty(command.EffectiveArguments);
    }

    [Theory]
    [InlineData("cat ~/x")]
    [InlineData("cat ~\\\n/x")]
    [InlineData("cat ~\\\r\n/x")]
    [InlineData("cat ~/\\x")]
    [InlineData("cat ~/\"x\"")]
    public void Proved_home_path_does_not_publish_unknown_overlay(string source)
    {
        var result = Parse(source);
        var argument = Assert.Single(Assert.Single(result.Clauses).Args);
        var command = Assert.Single(result.Commands);

        Assert.Equal(ArgKind.Tilde, argument.Kind);
        Assert.Equal("/home/test/x", argument.Resolved);
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("/home/test/x", Assert.Single(effective.Value.Values));
    }

    [Theory]
    [InlineData("cat ~\\/x", "~/x", "/work/~/x")]
    [InlineData("cat ~\"/x\"", "~/x", "/work/~/x")]
    [InlineData("cat ~'/x'", "~/x", "/work/~/x")]
    [InlineData("cat ~''", "~", "/work/~")]
    [InlineData("cat ~''/x", "~/x", "/work/~/x")]
    [InlineData("cat ~\"x\"", "~x", "/work/~x")]
    [InlineData("cat ~'x'", "~x", "/work/~x")]
    [InlineData("cat ~\\x", "~x", "/work/~x")]
    [InlineData("cat ~\"root\"/x", "~root/x", "/work/~root/x")]
    [InlineData("cat ~\\\n\"x\"", "~x", "/work/~x")]
    public void Quoted_or_escaped_tilde_prefix_remains_literal(
        string source,
        string effectiveValue,
        string resolvedPath)
    {
        var result = Parse(source);
        var argument = Assert.Single(Assert.Single(result.Clauses).Args);
        var command = Assert.Single(result.Commands);

        Assert.Equal(ArgKind.Literal, argument.Kind);
        Assert.Equal(resolvedPath, argument.Resolved);
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal(effectiveValue, Assert.Single(effective.Value.Values));
    }

    [Theory]
    [InlineData("cat ~root/x")]
    [InlineData("cat ~\\\nroot/x")]
    public void Unquoted_named_tilde_prefix_remains_unknown(string source)
    {
        var result = Parse(source);
        var argument = Assert.Single(Assert.Single(result.Clauses).Args);
        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);

        Assert.Equal(ArgKind.DynamicSkip, argument.Kind);
        Assert.Null(argument.Resolved);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Theory]
    [InlineData("/", "~", "/")]
    [InlineData("/", "~/x", "//x")]
    [InlineData("/home/test/", "~", "/home/test/")]
    [InlineData("/home/test/", "~/x", "/home/test//x")]
    public void Effective_tilde_value_preserves_configured_home_bytes(
        string homeDirectory,
        string authoredValue,
        string effectiveValue)
    {
        var parser = new BashParser(new BashParserOptions
        {
            HomeDirectory = homeDirectory,
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        });
        var command = Assert.Single(parser.Parse("cat " + authoredValue).Commands);

        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal(effectiveValue, Assert.Single(effective.Value.Values));
    }

    private static ParsedCommand Parse(string input)
    {
        var parser = new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
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
