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
        var result = Parse("(Set-Location C:\\sensitive) && Remove-Item child.txt");

        var remove = result.Clauses.Last();
        Assert.Contains(
            remove.Args,
            arg => arg.IsCwdAttribution && arg.Resolved == "C:/sensitive");
    }

    [Fact]
    public void Set_location_failure_partition_rebases_compatibility_to_incoming_cwd()
    {
        var result = Parse("Set-Location C:\\target || Get-Item child.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(
            ShellValueDomainKind.Exact,
            result.Commands[1].WorkingDirectory.Kind);
        Assert.Equal(
            "C:/work",
            Assert.Single(result.Commands[1].WorkingDirectory.Values));
        var clause = result.Clauses[1];
        Assert.Same(clause, result.Commands[1].Clause);
        Assert.Contains(clause.Args, argument =>
            argument.Raw == "child.txt" &&
            argument.Resolved == "C:/work/child.txt");
        Assert.Contains(clause.Elements, element =>
            element.Value == "child.txt" &&
            element.Resolved == "C:/work/child.txt");
        Assert.Contains(clause.Args, argument =>
            argument.IsCwdAttribution && argument.Resolved == "C:/work");
    }

    [Fact]
    public void Exact_failure_rebase_preserves_absolute_path_and_rewrites_redirect()
    {
        var result = Parse(
            "Set-Location C:\\target || Get-Item . C:\\work > result.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var clause = result.Clauses[1];
        Assert.Contains(clause.Args, argument =>
            argument.Raw == "." && argument.Resolved == "C:/work");
        Assert.Contains(clause.Args, argument =>
            argument.Raw == "C:\\work" && argument.Resolved == "C:/work");
        var redirect = Assert.Single(clause.Redirects);
        Assert.False(redirect.IsDynamicSkip);
        Assert.Equal("C:/work/result.txt", redirect.Target);
    }

    [Theory]
    [InlineData("pwsh -Command 'Get-Item child.txt > out.txt'")]
    [InlineData("pwsh -EncodedCommand RwBlAHQALQBJAHQAZQBtACAAYwBoAGkAbABkAC4AdAB4AHQAIAA+ACAAbwB1AHQALgB0AHgAdAA=")]
    public void Exact_failure_rebase_crosses_decoded_child_host_boundary(string invocation)
    {
        var result = Parse($"Set-Location C:\\target || {invocation}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands[1];
        var clause = result.Clauses[1];
        Assert.Same(clause, command.Clause);
        Assert.Equal(ShellValueDomainKind.Exact, command.WorkingDirectory.Kind);
        Assert.Equal("C:/work", Assert.Single(command.WorkingDirectory.Values));
        Assert.Contains(clause.Args, argument =>
            argument.Raw == "child.txt" &&
            argument.Resolved == "C:/work/child.txt");
        Assert.Contains(clause.Elements, element =>
            element.Value == "child.txt" &&
            element.Resolved == "C:/work/child.txt");
        Assert.Contains(clause.Args, argument =>
            argument.IsCwdAttribution && argument.Resolved == "C:/work");
        var redirect = Assert.Single(clause.Redirects);
        Assert.False(redirect.IsDynamicSkip);
        Assert.Equal("C:/work/out.txt", redirect.Target);
    }

    [Theory]
    [InlineData("Get-Item child.txt > out.txt")]
    [InlineData("pwsh -Command 'Get-Item child.txt > out.txt'")]
    [InlineData("pwsh -EncodedCommand RwBlAHQALQBJAHQAZQBtACAAYwBoAGkAbABkAC4AdAB4AHQAIAA+ACAAbwB1AHQALgB0AHgAdAA=")]
    public void Dynamic_provider_failure_promotes_only_static_compatibility_paths(
        string invocation)
    {
        var result = Parse($"Set-Location Alias: || {invocation}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands[1];
        var clause = result.Clauses[1];
        Assert.Same(clause, command.Clause);
        Assert.Equal(ShellValueDomainKind.Exact, command.WorkingDirectory.Kind);
        Assert.Equal("C:/work", Assert.Single(command.WorkingDirectory.Values));
        Assert.Contains(clause.Args, argument =>
            argument.Raw == "child.txt" &&
            argument.Kind == ArgKind.Literal &&
            argument.Resolved == "C:/work/child.txt");
        Assert.Contains(clause.Elements, element =>
            element.Value == "out.txt" &&
            element.Kind == ArgKind.Literal &&
            element.Resolved == "C:/work/out.txt");
        var redirect = Assert.Single(clause.Redirects);
        Assert.False(redirect.IsDynamicSkip);
        Assert.Equal("C:/work/out.txt", redirect.Target);
        Assert.False(command.IsComplete);
    }

    [Theory]
    [InlineData("Get-Item $name > $out")]
    [InlineData("pwsh -Command 'Get-Item $name > $out'")]
    public void Dynamic_provider_failure_does_not_promote_runtime_values(
        string invocation)
    {
        var result = Parse($"Set-Location Alias: || {invocation}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var clause = result.Clauses[1];
        Assert.Contains(clause.Args, argument =>
            argument.Raw == "$name" && argument.Resolved is null);
        Assert.Contains(clause.Elements, element =>
            element.Value == "$out" && element.Resolved is null);
        Assert.True(Assert.Single(clause.Redirects).IsDynamicSkip);
    }

    [Theory]
    [InlineData("Get-Item a,b")]
    [InlineData("pwsh -Command 'Get-Item a,b'")]
    [InlineData("Remove-Item safe.txt,C:/sensitive.txt")]
    [InlineData("pwsh -Command 'Remove-Item safe.txt,C:/sensitive.txt'")]
    [InlineData("Get-Item 'a','b'")]
    [InlineData("Get-Item \"a\",\"b\"")]
    [InlineData("pwsh -Command \"Get-Item 'a','b'\"")]
    [InlineData("pwsh -EncodedCommand RwBlAHQALQBJAHQAZQBtACAAJwBhACcALAAnAGIAJwA=")]
    [InlineData("Remove-Item 'safe.txt','C:/sensitive.txt'")]
    public void Dynamic_provider_failure_does_not_collapse_argument_lists_to_one_path(
        string invocation)
    {
        var result = Parse($"Set-Location Alias: || {invocation}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands[1];
        Assert.False(command.IsComplete);
        Assert.DoesNotContain(command.Clause.Args, argument =>
            argument.Raw.IndexOf(',') >= 0 && argument.Resolved is not null);
        Assert.DoesNotContain(command.Clause.Elements, element =>
            element.Value.IndexOf(',') >= 0 && element.Resolved is not null);
    }

    [Theory]
    [InlineData("Get-Item 'a,b'")]
    [InlineData("pwsh -Command \"Get-Item 'a,b'\"")]
    public void Dynamic_provider_failure_promotes_a_quoted_comma_filename(
        string invocation)
    {
        var result = Parse($"Set-Location Alias: || {invocation}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands[1];
        Assert.True(command.IsComplete);
        Assert.Contains(command.Clause.Args, argument =>
            argument.Resolved == "C:/work/a,b");
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
    public void Exact_substitution_depth_limit_remains_parseable()
    {
        var result = Parse(NestedSubstitution(ShellAnalysisLimits.MaxStructuralNesting));

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Commands.Count);
    }

    [Fact]
    public void Substitution_depth_overflow_fails_before_recursive_descent()
    {
        var result = Parse(NestedSubstitution(ShellAnalysisLimits.MaxStructuralNesting + 1));

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("nesting depth", result.UnparseableReason!);
    }

    [Fact]
    public void Group_and_substitution_share_the_exact_structural_depth_budget()
    {
        var result = Parse("(" + NestedSubstitution(
            ShellAnalysisLimits.MaxStructuralNesting - 1) + ")");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Commands.Count);
    }

    [Fact]
    public void Mixed_group_and_substitution_depth_overflow_fails_closed()
    {
        var result = Parse("(" + NestedSubstitution(
            ShellAnalysisLimits.MaxStructuralNesting) + ")");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
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
    public void Invoke_expression_location_changes_remain_conservative_until_remapping()
    {
        var result = Parse(
            "iex 'Set-Location C:\\sensitive' && Remove-Item child.txt");

        var remove = result.Clauses.Last();
        Assert.Contains(
            remove.Args,
            arg => arg.IsCwdAttribution && arg.Kind == ArgKind.DynamicSkip);
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
    public void Discovered_subexpression_completes_the_outer_command_but_variable_data_is_ordinary()
    {
        var discovered = Parse("Write-Output $(Get-Date)");
        var data = Parse("Write-Output $value");

        Assert.Equal(new[] { "Get-Date", "Write-Output" },
            discovered.Commands.Select(CommandVerb));
        Assert.All(discovered.Commands, command => Assert.True(command.IsComplete));
        Assert.True(Assert.Single(data.Commands).IsComplete);
    }

    [Fact]
    public void Simple_subexpression_preserves_exact_structure_identity_and_spans()
    {
        const string source = "Remove-Item $(Get-Item target.txt)";
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Get-Item", "Remove-Item" }, result.Commands.Select(CommandVerb));
        var outer = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var substitution = Assert.Single(outer.Substitutions);
        Assert.Equal(source.IndexOf("$(", System.StringComparison.Ordinal), substitution.SourceStart);
        Assert.Equal("$(Get-Item target.txt)".Length, substitution.SourceLength);
        var inner = Assert.IsType<SimpleCommandSyntax>(Assert.Single(substitution.Body.Statements));
        Assert.Same(inner.Clause, result.Commands[0].Clause);
        Assert.Same(outer.Clause, result.Commands[1].Clause);
        Assert.Contains(outer.Clause.Args, argument => argument.Kind == ArgKind.DynamicSkip);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Multiple_and_nested_subexpressions_emit_innermost_first()
    {
        var siblings = Parse("Write-Output $(Get-Date) $(Get-Location)");
        Assert.Equal(new[] { "Get-Date", "Get-Location", "Write-Output" },
            siblings.Commands.Select(CommandVerb));
        Assert.Equal(2, Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(siblings.Syntax.Statements)).Substitutions.Count);

        var nested = Parse("Write-Output $(Get-Item $(Get-Location))");
        Assert.Equal(new[] { "Get-Location", "Get-Item", "Write-Output" },
            nested.Commands.Select(CommandVerb));
        Assert.Equal(5, nested.Commands[0].Ancestry.Count);
        Assert.Equal(3, nested.Commands[1].Ancestry.Count);
    }

    [Theory]
    [InlineData("Write-Output pre$(Get-Date)post")]
    [InlineData("Write-Output \"pre$(Get-Date)post\"")]
    [InlineData("native --output=$(Get-Date)")]
    public void Supported_word_forms_discover_subexpressions(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal("Get-Date", CommandVerb(result.Commands[0]));
        Assert.True(result.Commands[0].IsComplete);
        Assert.Contains(result.Clauses.Last().Args,
            argument => argument.Kind == ArgKind.DynamicSkip);
    }

    [Fact]
    public void Expandable_here_string_discovers_subexpression()
    {
        var result = Parse("Write-Output @\"\nvalue $(Get-Date)\n\"@");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Get-Date", "Write-Output" }, result.Commands.Select(CommandVerb));
    }

    [Theory]
    [InlineData("Write-Output '$(Get-Date)'")]
    [InlineData("Write-Output \"literal `$(Get-Date)\"")]
    [InlineData("Write-Output @'\n$(Get-Date)\n'@")]
    public void Literal_subexpression_spellings_do_not_create_occurrences(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Commands);
        Assert.Empty(Assert.IsType<SimpleCommandSyntax>(
            Assert.Single(result.Syntax.Statements)).Substitutions);
    }

    [Fact]
    public void Standalone_subexpression_exposes_only_its_body_commands()
    {
        var result = Parse("$(Write-Output Get-Date)");

        Assert.False(result.IsUnparseable);
        Assert.Equal("Write-Output", CommandVerb(Assert.Single(result.Commands)));
        Assert.IsType<CommandSubstitutionSyntax>(Assert.Single(result.Syntax.Statements));
    }

    [Theory]
    [InlineData("$(Get-Date) argument")]
    [InlineData("$(Get-Date) | (Get-Process)")]
    public void Unsupported_standalone_subexpression_shapes_fail_closed(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Call_operator_subexpression_retains_one_incomplete_dynamic_outer_occurrence()
    {
        var result = Parse("& $(Write-Output Get-Date) argument");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Write-Output", "$(Write-Output Get-Date)" },
            result.Commands.Select(CommandVerb));
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
        Assert.True(result.Commands[1].Clause.Verb.IsDynamic);
    }

    [Fact]
    public void Interpolated_command_word_remains_dynamic_while_exposing_its_subexpression()
    {
        var result = Parse("Get-$(Write-Output Content) /etc/passwd");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Commands.Count);
        Assert.Equal("Write-Output", CommandVerb(result.Commands[0]));
        Assert.True(result.Commands[1].Clause.Verb.IsDynamic);
        Assert.False(result.Commands[1].IsComplete);
    }

    [Fact]
    public void Subexpression_location_failure_joins_sanitize_outer_compatibility()
    {
        var result = Parse(
            "Write-Output $(Set-Location C:\\sensitive; Get-Location); Get-Item child.txt");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Set-Location", "Get-Location", "Write-Output", "Get-Item" },
            result.Commands.Select(CommandVerb));
        Assert.All(
            result.Clauses.Skip(1),
            clause => Assert.Contains(
                clause.Args,
                argument => argument.IsCwdAttribution &&
                    argument.Kind == ArgKind.DynamicSkip));
        Assert.Contains(result.Clauses[3].Args,
            argument => argument.Raw == "child.txt" && argument.Resolved is null);
        Assert.Equal(
            "C:/work",
            Assert.Single(result.Commands[0].WorkingDirectory.Values));
        Assert.All(
            result.Commands.Skip(1),
            command => Assert.Equal(
                ShellValueDomainKind.Unknown,
                command.WorkingDirectory.Kind));
    }

    [Fact]
    public void Subexpression_and_if_exposes_exact_success_cwd_without_leaking_it_to_outer_flow()
    {
        var result = Parse(
            "Write-Output $(Set-Location C:\\sensitive && Get-Location); Get-Item child.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(4, result.Commands.Count);
        Assert.Equal(
            "C:/work",
            Assert.Single(result.Commands[0].WorkingDirectory.Values));
        Assert.Equal(
            "C:/sensitive",
            Assert.Single(result.Commands[1].WorkingDirectory.Values));
        Assert.All(
            result.Commands.Skip(2),
            command => Assert.Equal(
                ShellValueDomainKind.Unknown,
                command.WorkingDirectory.Kind));
    }

    [Fact]
    public void Dynamic_subexpression_location_poisons_inner_consumer_and_continuation()
    {
        var result = Parse(
            "Write-Output $(Set-Location $target; Get-Item child.txt); Get-Item sibling.txt");

        Assert.False(result.IsUnparseable);
        Assert.All(result.Clauses.Skip(1), clause => Assert.Contains(
            clause.Args,
            argument => argument.IsCwdAttribution && argument.Kind == ArgKind.DynamicSkip));
        Assert.DoesNotContain(result.Clauses.SelectMany(clause => clause.Args),
            argument => argument.Raw is "child.txt" or "sibling.txt" &&
                argument.Resolved is not null);
    }

    [Fact]
    public void Redirect_subexpression_is_visible_while_outer_redirect_stays_incomplete()
    {
        var result = Parse("Get-Content > $(Join-Path C:\\temp out.txt)");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Join-Path", "Get-Content" }, result.Commands.Select(CommandVerb));
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
        Assert.True(Assert.Single(result.Clauses[1].Redirects).IsDynamicSkip);
    }

    [Theory]
    [InlineData("Write-Output @(Get-Date)")]
    [InlineData("Write-Output @(1; Get-Date)")]
    [InlineData("Write-Output @{x=$(Get-Date)}")]
    [InlineData("Write-Output @{x=(Get-Date)}")]
    [InlineData("Write-Output $(1+1)")]
    [InlineData("Write-Output $(1)")]
    [InlineData("Write-Output $(1-1)")]
    [InlineData("Write-Output $(-1)")]
    [InlineData("Write-Output $(1kb)")]
    [InlineData("Write-Output $(0x10)")]
    [InlineData("Write-Output $(1e3)")]
    [InlineData("Write-Output $(1L)")]
    [InlineData("Write-Output $(1s)")]
    [InlineData("Write-Output $(1us)")]
    [InlineData("Write-Output $(1y)")]
    [InlineData("Write-Output $(1uy)")]
    [InlineData("Write-Output $(0x10s)")]
    [InlineData("Write-Output $(,$x)")]
    [InlineData("Write-Output $(!$true)")]
    [InlineData("Write-Output $(++$x)")]
    [InlineData("Write-Output $(-not $true)")]
    [InlineData("Write-Output $(-bnot 1)")]
    [InlineData("Write-Output $(-$x)")]
    [InlineData("Write-Output $(+$x)")]
    [InlineData("Write-Output $(-[int]'1')")]
    [InlineData("Write-Output $(+[int]'1')")]
    [InlineData("Write-Output $(-join @('a','b'))")]
    [InlineData("Write-Output $(@(1,2,3))")]
    [InlineData("Write-Output $(@{x=1})")]
    [InlineData("Write-Output $(@args)")]
    [InlineData("Write-Output $({ Get-Date })")]
    [InlineData("Write-Output $(Get-Date; 1kb)")]
    [InlineData("Write-Output $(Get-Date; ,$x)")]
    [InlineData("Write-Output $(Get-Date; @('a'))")]
    [InlineData("Write-Output $((1kb))")]
    [InlineData("Write-Output $($x)")]
    [InlineData("Write-Output $(Get-Date).Property")]
    [InlineData("Write-Output $(Get-Date).ToString()")]
    public void Unsupported_execution_bearing_expressions_fail_whole(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Theory]
    [InlineData("Write-Output $(7z)", "7z")]
    [InlineData("Write-Output $(7zip)", "7zip")]
    [InlineData("Write-Output $(1.0f)", "1.0f")]
    [InlineData("Write-Output $(1.0m)", "1.0m")]
    [InlineData("Write-Output $(-foo)", "-foo")]
    public void Expression_like_command_names_remain_executable_identities(
        string source,
        string expectedVerb)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal(expectedVerb, CommandVerb(result.Commands[0]));
    }

    [Theory]
    [InlineData("Write-Output @(1, 2, 3)")]
    [InlineData("New-Item -Path C:\\x @{ Force = $true }")]
    public void Proved_literal_at_expressions_remain_opaque_data(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Commands);
        Assert.False(Assert.Single(result.Commands).IsComplete);
    }

    [Theory]
    [InlineData("Write-Output $(Write-Output x # )\nGet-Date)")]
    [InlineData("Write-Output $(Write-Output x <# ) #>; Get-Date)")]
    [InlineData("Write-Output $(Write-Output abc#def; Get-Date)")]
    public void Comment_parentheses_do_not_hide_later_subexpression_commands(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Write-Output", "Get-Date", "Write-Output" },
            result.Commands.Select(CommandVerb));
    }

    [Theory]
    [InlineData("Write-Output $(Write-Output 'x'# )\nGet-Date)")]
    [InlineData("Write-Output $($(Write-Output x)# )\nGet-Date)")]
    public void Direct_subexpression_comments_after_closed_regions_are_recognized(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.Equal("Get-Date", CommandVerb(result.Commands[^2]));
    }

    [Theory]
    [InlineData("Write-Output \"$($(Write-Output x)# )\nGet-Date)\"")]
    [InlineData("Write-Output @\"\n$($(Write-Output x)# )\nGet-Date)\n\"@")]
    public void Comment_bearing_subexpressions_in_expandable_data_fail_closed(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Expanding_host_payload_keeps_parent_substitution_and_incomplete_outer_host()
    {
        var result = Parse("pwsh -Command \"Write-Output $(Get-Date)\"");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Get-Date", "pwsh" }, result.Commands.Select(CommandVerb));
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
        Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
    }

    [Fact]
    public void Literal_host_payload_recurses_and_clears_decoded_substitution_spans()
    {
        var result = Parse("pwsh -Command 'Write-Output $(Get-Date)'");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Get-Date", "Write-Output" }, result.Commands.Select(CommandVerb));
        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        var outer = Assert.Single(
            Descendants(wrapper.Body).OfType<SimpleCommandSyntax>(),
            command => command.Clause.Verb.Joined == "Write-Output");
        var substitution = Assert.Single(outer.Substitutions);
        Assert.Null(substitution.SourceStart);
        Assert.Null(substitution.SourceLength);
        Assert.All(Descendants(substitution.Body), node =>
        {
            Assert.Null(node.SourceStart);
            Assert.Null(node.SourceLength);
        });
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

    private static string CommandVerb(CommandOccurrence command) => command.Clause.Verb.Joined;

    private static string NestedSubstitution(int depth) =>
        "Write-Output " + string.Concat(Enumerable.Repeat("$(", depth)) + "Get-Date" +
        new string(')', depth);

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
            case SimpleCommandSyntax simple:
                foreach (var substitution in simple.Substitutions)
                {
                    foreach (var descendant in Descendants(substitution))
                    {
                        yield return descendant;
                    }
                }

                break;
            case CommandSubstitutionSyntax substitution:
                foreach (var descendant in Descendants(substitution.Body))
                {
                    yield return descendant;
                }

                break;
        }
    }
}
