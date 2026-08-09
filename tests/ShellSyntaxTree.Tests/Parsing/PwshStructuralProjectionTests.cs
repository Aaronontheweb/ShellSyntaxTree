// -----------------------------------------------------------------------
// <copyright file="PwshStructuralProjectionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    [InlineData("pwsh -Command 'Get-Date'")]
    [InlineData("pwsh -EncodedCommand RwBlAHQALQBEAGEAdABlAA==")]
    public void Joined_cwd_keeps_outer_wrapper_redirect_target_unknown(
        string invocation)
    {
        var result = Parse($"Set-Location C:\\maybe; {invocation} > relative.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands[1];
        Assert.Equal(ShellValueDomainKind.Unknown, command.WorkingDirectory.Kind);
        var compatibility = Assert.Single(command.Clause.Redirects);
        Assert.True(compatibility.IsDynamicSkip);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.True(redirect.IsComplete);
        Assert.True(command.IsComplete);
    }

    [Theory]
    [InlineData("pwsh -Command 'Get-Date'")]
    [InlineData("pwsh -EncodedCommand RwBlAHQALQBEAGEAdABlAA==")]
    public void Success_only_cwd_resolves_outer_wrapper_redirect_target(
        string invocation)
    {
        var result = Parse($"Set-Location C:\\maybe && {invocation} > relative.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands[1];
        Assert.Equal("C:/maybe", Assert.Single(command.WorkingDirectory.Values));
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.Exact, redirect.Target.Kind);
        Assert.Equal("C:/maybe/relative.txt", Assert.Single(redirect.Target.Values));
        Assert.True(command.IsComplete);
    }

    [Theory]
    [InlineData("Get-Item child.txt > out.txt")]
    [InlineData("pwsh -Command 'Get-Item child.txt > out.txt'")]
    [InlineData("pwsh -EncodedCommand RwBlAHQALQBJAHQAZQBtACAAYwBoAGkAbABkAC4AdAB4AHQAIAA+ACAAbwB1AHQALgB0AHgAdAA=")]
    public void Dynamic_provider_failure_promotes_static_paths_and_redirect_facts(
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
        Assert.True(command.IsComplete);
        var redirectFact = Assert.Single(command.Redirects);
        Assert.Equal(RedirectOperation.FileOutput, redirectFact.Operation);
        Assert.Equal("C:/work/out.txt", Assert.Single(redirectFact.Target.Values));
        Assert.True(redirectFact.IsComplete);
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
    public void PowerShell_host_wrapper_pipeline_retains_static_authored_commands()
    {
        var result = Parse(
            "pwsh -Command \"Get-Item x | Select-Object Name; Get-Date\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(
            new[] { "Get-Item", "Select-Object", "Get-Date" },
            result.Commands.Select(CommandVerb));
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Unknown_receiver_body_pipeline_fails_atomically()
    {
        var result = Parse(
            "Invoke-Custom { Get-Item x | Select-Object Name }");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
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
    [InlineData("Get-Content ~", "iex 'Get-Content ~'", "~")]
    [InlineData("Get-Content *.txt", "iex 'Get-Content *.txt'", "*.txt")]
    [InlineData("curl ~", "iex 'curl ~'", "C:/Users/test")]
    public void Invoke_expression_preserves_current_scope_argument_binding_provenance(
        string directSource,
        string wrappedSource,
        string expectedValue)
    {
        var direct = Parse(directSource);
        var wrapped = Parse(wrappedSource);

        var directArgument = Assert.Single(
            Assert.Single(direct.Commands).EffectiveArguments);
        var wrappedArgument = Assert.Single(
            Assert.Single(wrapped.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, directArgument.Value.Kind);
        Assert.Equal(directArgument.Value.Kind, wrappedArgument.Value.Kind);
        Assert.Equal(
            Assert.Single(directArgument.Value.Values),
            Assert.Single(wrappedArgument.Value.Values));
        Assert.Equal(expectedValue, Assert.Single(wrappedArgument.Value.Values));
        Assert.True(Assert.Single(wrapped.Commands).IsComplete);
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
    public void Invoke_expression_location_changes_remap_current_scope_state()
    {
        var result = Parse(
            "iex 'Set-Location C:\\sensitive' && Remove-Item child.txt");

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
        Assert.True(result.Commands[1].IsComplete);
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
    public void Redirect_subexpression_is_visible_while_outer_target_stays_unknown()
    {
        var result = Parse("Get-Content > $(Join-Path C:\\temp out.txt)");

        Assert.False(result.IsUnparseable);
        Assert.Equal(new[] { "Join-Path", "Get-Content" }, result.Commands.Select(CommandVerb));
        Assert.True(result.Commands[0].IsComplete);
        Assert.True(result.Commands[1].IsComplete);
        var redirectFact = Assert.Single(result.Commands[1].Redirects);
        Assert.Equal(RedirectOperation.FileOutput, redirectFact.Operation);
        Assert.Equal(ShellValueDomainKind.Unknown, redirectFact.Target.Kind);
        Assert.True(redirectFact.IsComplete);
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

    [Fact]
    public void Ordinary_literal_argument_does_not_add_a_redundant_effective_value()
    {
        var result = Parse("Write-Output plain");

        Assert.Empty(Assert.Single(result.Commands).EffectiveArguments);
    }

    [Fact]
    public void Escaped_and_quoted_native_flag_fragments_form_one_exact_effective_value()
    {
        var result = Parse(
            "curl --data=@`$HOME\".json\" https://example.invalid/api");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(1, effective.ClauseElementIndex);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("--data=@$HOME.json", Assert.Single(effective.Value.Values));
    }

    [Fact]
    public void Isolated_home_variable_composition_preserves_separator_bytes()
    {
        var result = ParseIsolatedWithHome("Write-Output \"$HOME/x\"", "/tmp/");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("/tmp//x", Assert.Single(effective.Value.Values));
    }

    [Theory]
    [InlineData("/", "~", "/")]
    [InlineData("/", "~/x", "//x")]
    [InlineData("/tmp/", "~", "/tmp/")]
    [InlineData("/tmp/", "~/x", "/tmp//x")]
    public void Native_tilde_effective_value_preserves_configured_home_bytes(
        string homeDirectory,
        string argument,
        string expected)
    {
        var result = ParseIsolatedWithHome(
            $"curl --output {argument} https://example.invalid",
            homeDirectory);

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(2, effective.ClauseElementIndex);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal(expected, Assert.Single(effective.Value.Values));
    }

    [Fact]
    public void Quoted_native_tilde_remains_literal_in_the_effective_value()
    {
        var result = ParseWithHome(
            "curl --output \"~\" https://example.invalid",
            "/tmp/");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("~", Assert.Single(effective.Value.Values));
    }

    [Theory]
    [InlineData("$^")]
    [InlineData("$$")]
    public void Runtime_automatic_parameter_path_remains_unknown(string argument)
    {
        var result = Parse($"Get-Content \"{argument}\"");

        var command = Assert.Single(result.Commands);
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
        Assert.True(command.IsComplete);
    }

    [Fact]
    public void Userprofile_mutation_invalidates_the_configured_home_effective_value()
    {
        var result = ParseIsolatedWithHome(
            "Set-Item Env:USERPROFILE X; Get-Content \"$env:USERPROFILE/x\"",
            "C:/Users/test");

        var command = result.Commands.Last();
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
        Assert.False(command.IsComplete);
    }

    [Fact]
    public void Process_environment_mutation_does_not_change_read_only_current_runspace_home()
    {
        var result = ParseIsolatedWithHome(
            "Set-Item Env:USERPROFILE X; Get-Content \"$HOME/x\"",
            "C:/Users/test");

        var command = result.Commands.Last();
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("C:/Users/test/x", Assert.Single(effective.Value.Values));
    }

    [Fact]
    public void Remote_execution_region_does_not_reuse_the_local_configured_home()
    {
        var result = ParseIsolatedWithHome(
            "Invoke-Command -ComputerName server { Get-Content \"$HOME/x\" }",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => occurrence.Clause.Verb.Tokens[0] == "Get-Content");
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Fact]
    public void Repeated_parallel_child_does_not_reuse_home_after_process_environment_mutation()
    {
        var result = ParseIsolatedWithHome(
            "1,2 | ForEach-Object -Parallel { Set-Item Env:HOME X; " +
            "Get-Content \"$HOME/x\" } -UseNewRunspace",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => occurrence.Clause.Verb.Tokens[0] == "Get-Content");
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Theory]
    [InlineData(
        "Start-Job { Get-Content \"$HOME/x\" }",
        ShellValueDomainKind.Exact,
        "C:/Users/test/x")]
    [InlineData(
        "Set-Item Env:HOME X; Start-Job { Get-Content \"$HOME/x\" }",
        ShellValueDomainKind.Unknown,
        null)]
    public void Child_process_home_depends_on_proved_process_environment(
        string source,
        ShellValueDomainKind expectedKind,
        string? expectedValue)
    {
        var result = ParseIsolatedWithHome(source, "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => occurrence.Clause.Verb.Tokens[0] == "Get-Content");
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(expectedKind, effective.Value.Kind);
        if (expectedValue is not null)
        {
            Assert.Equal(expectedValue, Assert.Single(effective.Value.Values));
        }
    }

    [Theory]
    [InlineData("Set-Variable HOME X -Force; Write-Output hi > \"$HOME/out\"")]
    [InlineData("Set-Item Env:USERPROFILE X; Write-Output hi > \"$env:USERPROFILE/out\"")]
    [InlineData("Invoke-Command -ComputerName server { Write-Output hi > \"$HOME/out\" }")]
    [InlineData("Invoke-Command -ComputerName server { Write-Output hi > \"~\" }")]
    [InlineData("Set-Item Env:HOME X; Start-Job { Write-Output hi > \"~\" }")]
    public void Redirect_home_values_use_occurrence_local_state(string source)
    {
        var result = ParseIsolatedWithHome(source, "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Write-Output");
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
    }

    [Theory]
    [InlineData("Set-Item Env:HOME X; Start-Job { Write-Output hi > \"~\" }")]
    [InlineData("Invoke-Command -ComputerName server { Write-Output hi > \"~\" }")]
    [InlineData("Set-Variable HOME X -Force; Write-Output hi > \"$HOME/out\"")]
    public void Encoded_wrapper_inner_redirect_uses_inner_execution_state(
        string payload)
    {
        var result = ParseIsolatedWithHome(
            $"pwsh -EncodedCommand {Encode(payload)}",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Write-Output");
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
    }

    [Theory]
    [InlineData("Set-Variable HOME X -Force; Get-Content \"$HOME/x\"")]
    [InlineData("Set-Item Env:HOME X; pwsh -EncodedCommand {0}")]
    public void Encoded_wrapper_reconstructs_inner_argument_provenance(
        string source)
    {
        var inner = Encode("Get-Content \"$HOME/x\"");
        var commandText = source.IndexOf("{0}", StringComparison.Ordinal) >= 0
            ? string.Format(source, inner)
            : $"pwsh -EncodedCommand {Encode(source)}";
        var result = ParseIsolatedWithHome(commandText, "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Get-Content");
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Encoded_wrapper_profile_can_mutate_automatic_home()
    {
        var result = ParseIsolatedWithHome(
            $"pwsh -EncodedCommand {Encode("Get-Content \"$HOME/x\"")}",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Get-Content");
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Fact]
    public void Encoded_wrapper_does_not_reuse_parent_automatic_home_proof()
    {
        var result = ParseIsolatedWithHome(
            "Set-Variable HOME X -Force; " +
            $"pwsh -EncodedCommand {Encode("Get-Content \"$HOME/x\"")}",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Get-Content");
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Fact]
    public void Encoded_wrapper_profile_can_mutate_userprofile_environment()
    {
        var result = ParseIsolatedWithHome(
            $"pwsh -EncodedCommand {Encode("Get-Content \"$env:USERPROFILE/x\"")}",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Get-Content");
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Encoded_wrapper_does_not_inherit_parent_command_binding_assertion()
    {
        var result = ParseIsolatedWithHome(
            $"pwsh -EncodedCommand {Encode("curl ~")}",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "curl");
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Nested_encoded_wrapper_redirect_uses_intermediate_invocation_scope()
    {
        var deepest = Encode("Set-Location /tmp/inner && Write-Output hi");
        var middle = Encode(
            $"pwsh -NoProfile -EncodedCommand {deepest} > relative.txt");
        var result = ParseIsolatedWithHome(
            $"pwsh -NoProfile -EncodedCommand {middle}",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Write-Output");
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.Exact, redirect.Target.Kind);
        Assert.Equal("C:/work/relative.txt", Assert.Single(redirect.Target.Values));
    }

    [Theory]
    [InlineData("/usr/bin/my-tool ~", "C:/Users/test")]
    [InlineData("./tool.ps1 ~", "~")]
    public void Encoded_wrapper_retains_authored_path_shaped_command_binding(
        string payload,
        string expected)
    {
        var result = ParseIsolatedWithHome(
            $"pwsh -EncodedCommand {Encode(payload)}",
            "C:/Users/test");

        var command = result.Commands.Last();
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal(expected, Assert.Single(effective.Value.Values));
        Assert.True(command.IsComplete);
    }

    [Theory]
    [InlineData("/usr/bin/my-tool ~", "C:/Users/test")]
    [InlineData("./tool.ps1 ~", "~")]
    public void Default_state_retains_authored_path_shaped_command_binding(
        string source,
        string expected)
    {
        var result = ParseWithHome(source, "C:/Users/test");

        var command = Assert.Single(result.Commands);
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal(expected, Assert.Single(effective.Value.Values));
        Assert.True(command.IsComplete);
    }

    [Theory]
    [InlineData("Set-Alias '/usr/bin/my-tool' Write-Output; /usr/bin/my-tool ~")]
    [InlineData("Set-Alias './tool.ps1' Write-Output; ./tool.ps1 ~")]
    public void Path_shaped_alias_mutation_invalidates_argument_binding(string source)
    {
        var result = ParseIsolatedWithHome(source, "C:/Users/test");

        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("foo.ps1 ~")]
    [InlineData("Set-Alias foo.ps1 Write-Output; foo.ps1 ~")]
    public void Unqualified_ps1_name_does_not_prove_script_binding(string source)
    {
        var result = source.StartsWith("Set-Alias", StringComparison.Ordinal)
            ? ParseIsolatedWithHome(source, "C:/Users/test")
            : ParseWithHome(source, "C:/Users/test");

        var command = result.Commands.Last();
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Encoded_unqualified_ps1_name_keeps_binding_unknown()
    {
        var result = ParseIsolatedWithHome(
            $"pwsh -EncodedCommand {Encode("foo.ps1 ~")}",
            "C:/Users/test");

        var command = result.Commands.Last();
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Rebound_state_mutator_invalidates_all_later_state_proofs()
    {
        var result = ParseIsolatedWithHome(
            "Set-Alias Set-Variable Invoke-Expression; " +
            "Set-Variable 'Set-Alias evil Write-Output'; evil ~",
            "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "evil");
        Assert.False(command.IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, command.WorkingDirectory.Kind);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Ambient_receiver_uncertainty_does_not_poison_later_authored_facts()
    {
        var result = ParseUnknown("Invoke-Custom; Get-Content relative.txt");

        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
        var command = result.Commands.Last();
        Assert.Equal(ShellValueDomainKind.Exact, command.WorkingDirectory.Kind);
        Assert.Contains(command.Clause.Args, argument =>
            argument.Raw == "relative.txt" &&
            argument.Resolved == "C:/work/relative.txt");
        Assert.DoesNotContain(command.Clause.Args, argument =>
            argument.IsCwdAttribution);
    }

    [Fact]
    public void Initial_state_contract_does_not_control_authored_completeness()
    {
        var unknown = ParseUnknown("Write-Output victim.txt");
        var isolated = Parse("Write-Output victim.txt");

        Assert.Single(unknown.Clauses);
        Assert.Single(isolated.Clauses);
        Assert.True(Assert.Single(unknown.Commands).IsComplete);
        Assert.True(Assert.Single(isolated.Commands).IsComplete);
    }

    [Fact]
    public void Encoded_unknown_receiver_keeps_later_structure_visible()
    {
        var result = ParseIsolatedWithHome(
            $"pwsh -EncodedCommand {Encode("foo; Write-Output hi > relative.txt")}",
            "C:/Users/test");

        var command = result.Commands.Last();
        Assert.True(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Exact,
            Assert.Single(command.Redirects).Target.Kind);
        Assert.Equal(
            "C:/work/relative.txt",
            Assert.Single(Assert.Single(command.Redirects).Target.Values));
        Assert.False(Assert.Single(command.Clause.Redirects).IsDynamicSkip);
    }

    [Theory]
    [InlineData("Set-Variable HOME X -Force; Get-Content \"$HOME/x\"")]
    [InlineData("Set-Item Variable:HOME X; Get-Content \"$HOME/x\"")]
    [InlineData("Invoke-Expression $code; Get-Content \"$HOME/x\"")]
    [InlineData("Set-Alias sh Set-Variable; sh HOME X -Force; Get-Content \"$HOME/x\"")]
    [InlineData("& { Set-Variable HOME X -Force }; Get-Content \"$HOME/x\"")]
    [InlineData(". { Set-Variable HOME X -Force }; Get-Content \"$HOME/x\"")]
    [InlineData("./mutate.ps1; Get-Content \"$HOME/x\"")]
    [InlineData("& ./mutate.ps1; Get-Content \"$HOME/x\"")]
    [InlineData("Set-Location Variable:; Set-Item HOME X -Force; Get-Content \"$HOME/x\"")]
    [InlineData("Push-Location Variable:; Set-Item HOME X -Force; Pop-Location; Get-Content \"$HOME/x\"")]
    public void Observable_home_variable_mutation_invalidates_automatic_home(
        string source)
    {
        var result = ParseIsolatedWithHome(source, "C:/Users/test");

        var command = result.Commands.Last();
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Theory]
    [InlineData("./mutate-env.ps1; Get-Content \"$env:USERPROFILE/x\"")]
    [InlineData("./mutate-env.ps1; Start-Job { Get-Content \"$HOME/x\" }")]
    public void External_script_process_mutation_invalidates_later_home_values(
        string source)
    {
        var result = ParseIsolatedWithHome(source, "C:/Users/test");

        var command = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Get-Content");
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Fact]
    public void External_script_invalidates_location_and_command_resolution()
    {
        var result = ParseIsolatedWithHome(
            "./mutate.ps1; Get-Content relative.txt; curl ~",
            "C:/Users/test");

        var getContent = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "Get-Content");
        Assert.Equal(ShellValueDomainKind.Unknown, getContent.WorkingDirectory.Kind);
        Assert.Contains(
            getContent.Clause.Args,
            argument => argument.IsCwdAttribution &&
                argument.Kind == ArgKind.DynamicSkip);

        var curl = Assert.Single(
            result.Commands,
            occurrence => CommandVerb(occurrence) == "curl");
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(curl.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Automatic_home_mutation_does_not_invalidate_tilde_home()
    {
        var result = ParseIsolatedWithHome(
            "Set-Variable HOME X -Force; " +
            "curl --output ~ https://example.invalid",
            "C:/Users/test");

        var command = result.Commands.Last();
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("C:/Users/test", Assert.Single(effective.Value.Values));
    }

    [Fact]
    public void Proved_data_script_block_does_not_leak_into_host_analysis()
    {
        var result = Parse("Write-Output { Remove-Item victim.txt }");

        var host = Assert.Single(
            result.Commands,
            command => CommandVerb(command) == "Write-Output");
        Assert.Empty(host.EffectiveArguments);
        Assert.DoesNotContain(
            result.Commands,
            command => CommandVerb(command) == "Remove-Item");
    }

    [Fact]
    public void Filesystem_drive_path_does_not_add_a_redundant_effective_value()
    {
        var result = Parse("Get-Content C:\\input");

        Assert.Empty(Assert.Single(result.Commands).EffectiveArguments);
    }

    [Theory]
    [InlineData("Get-Content ~")]
    [InlineData("Get-Content -LiteralPath ~")]
    [InlineData("Write-Output ~")]
    public void Cmdlet_tilde_is_the_exact_authored_argument(string source)
    {
        var result = ParseIsolatedWithHome(source, "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("~", Assert.Single(effective.Value.Values));
    }

    [Theory]
    [InlineData("Get-Content -Path *.txt")]
    [InlineData("Get-Content -LiteralPath *.txt")]
    public void Cmdlet_glob_is_an_exact_argument_before_parameter_semantics(
        string source)
    {
        var result = ParseIsolatedWithHome(source, "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("*.txt", Assert.Single(effective.Value.Values));
    }

    [Theory]
    [InlineData("--output=~", true)]
    [InlineData("prefix~", false)]
    [InlineData("~suffix", true)]
    public void Native_tilde_only_expands_at_a_whole_argument_path_prefix(
        string argument,
        bool hasIndependentEffectiveValue)
    {
        var result = ParseWithHome(
            $"curl {argument} https://example.invalid",
            "C:/Users/test");

        var effectiveArguments = Assert.Single(result.Commands).EffectiveArguments;
        if (!hasIndependentEffectiveValue)
        {
            Assert.Empty(effectiveArguments);
            return;
        }

        var effective = Assert.Single(effectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal(argument, Assert.Single(effective.Value.Values));
    }

    [Theory]
    [InlineData("/usr/bin/my-tool ~")]
    [InlineData("C:/tools/my-tool.exe ~")]
    public void Explicit_native_path_preserves_native_binding_with_hyphens(
        string source)
    {
        var result = ParseIsolatedWithHome(source, "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("C:/Users/test", Assert.Single(effective.Value.Values));
    }

    [Theory]
    [InlineData("git-lfs ~")]
    [InlineData("docker-compose *.txt")]
    public void Unqualified_hyphenated_command_binding_remains_unknown(string source)
    {
        var result = ParseWithHome(source, "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Theory]
    [InlineData("curl ~", "C:/Users/test")]
    [InlineData("Get-Content ~", "~")]
    public void Default_state_uses_parser_owned_command_binding(
        string source,
        string expected)
    {
        var result = ParseWithHome(source, "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal(expected, Assert.Single(effective.Value.Values));
    }

    [Fact]
    public void Unknown_initial_state_does_not_assume_automatic_home_value()
    {
        var result = ParseWithHome(
            "Write-Output \"$HOME/x\"",
            "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Fact]
    public void Isolated_initial_state_proves_automatic_home_value()
    {
        var result = ParseIsolatedWithHome(
            "Write-Output \"$HOME/x\"",
            "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("C:/Users/test/x", Assert.Single(effective.Value.Values));
    }

    [Fact]
    public void Unknown_initial_state_does_not_assume_userprofile_environment_value()
    {
        var result = ParseWithHome(
            "Write-Output \"$env:USERPROFILE/x\"",
            "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Fact]
    public void Isolated_initial_state_proves_userprofile_environment_value()
    {
        var result = ParseIsolatedWithHome(
            "Write-Output \"$env:USERPROFILE/x\"",
            "C:/Users/test");

        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Exact, effective.Value.Kind);
        Assert.Equal("C:/Users/test/x", Assert.Single(effective.Value.Values));
    }

    private static ParsedCommand Parse(string source) => new PwshParser(
        new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
        }).Parse(source);

    private static ParsedCommand ParseUnknown(string source) => new PwshParser(
        new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
        }).Parse(source);

    private static ParsedCommand ParseWithHome(string source, string homeDirectory) =>
        new PwshParser(
            new PwshParserOptions
            {
                HomeDirectory = homeDirectory,
                WorkingDirectory = "C:/work",
            }).Parse(source);

    private static ParsedCommand ParseIsolatedWithHome(string source, string homeDirectory) =>
        new PwshParser(
            new PwshParserOptions
            {
                HomeDirectory = homeDirectory,
                WorkingDirectory = "C:/work",
                InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
            }).Parse(source);

    private static string CommandVerb(CommandOccurrence command) => command.Clause.Verb.Joined;

    private static string Encode(string source) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(source));

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
