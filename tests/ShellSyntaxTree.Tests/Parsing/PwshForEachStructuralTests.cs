// -----------------------------------------------------------------------
// <copyright file="PwshForEachStructuralTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>Pins the bounded PowerShell foreach structural grammar.</summary>
public class PwshForEachStructuralTests
{
    [Fact]
    public void Literal_array_preserves_binding_iterable_body_and_exact_spans()
    {
        const string source =
            "foreach ($f in @('a.txt', 'b.txt')) { Remove-Item -LiteralPath $f }";

        var result = ParseIsolated(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var loop = Assert.IsType<ForEachSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal("f", loop.Binding.Name);
        Assert.Equal("$f", loop.Binding.Source.Raw);
        Assert.Equal(source.IndexOf("$f", StringComparison.Ordinal), loop.Binding.Source.SourceStart);
        Assert.Equal(2, loop.Binding.Source.SourceLength);
        Assert.Equal("@('a.txt', 'b.txt')", loop.Iterable.Raw);
        Assert.Equal(
            source.IndexOf("@(", StringComparison.Ordinal),
            loop.Iterable.SourceStart);
        Assert.Equal(loop.Iterable.Raw.Length, loop.Iterable.SourceLength);
        Assert.Empty(loop.IteratorCommands.Statements);
        Assert.Equal(0, loop.SourceStart);
        Assert.Equal(source.Length, loop.SourceLength);

        var body = Assert.IsType<SimpleCommandSyntax>(Assert.Single(loop.Body.Statements));
        var occurrence = Assert.Single(result.Commands);
        Assert.Same(body.Clause, occurrence.Clause);
        Assert.Same(body.Clause, Assert.Single(result.Clauses));
        Assert.Equal(CommandOccurrenceRole.LoopBody, occurrence.ImmediateRole);
        Assert.True(occurrence.IsComplete);
        var effective = Assert.Single(occurrence.EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.FiniteSet, effective.Value.Kind);
        Assert.Equal(new[] { "a.txt", "b.txt" }, effective.Value.Values);
    }

    [Fact]
    public void Iterator_pipeline_and_body_pipeline_preserve_roles_and_ancestry()
    {
        const string source =
            "foreach ($f in Get-ChildItem C:\\input | Where-Object Name) " +
            "{ Get-Item $f | Remove-Item }";

        var result = ParseIsolated(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var loop = Assert.IsType<ForEachSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.IsType<PipelineSyntax>(Assert.Single(loop.IteratorCommands.Statements));
        Assert.IsType<PipelineSyntax>(Assert.Single(loop.Body.Statements));
        Assert.Equal(
            new[] { "Get-ChildItem", "Where-Object", "Get-Item", "Remove-Item" },
            result.Commands.Select(CommandVerb));
        Assert.Equal(
            new[]
            {
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
                CommandOccurrenceRole.PipelineStage,
            },
            result.Commands.Select(command => command.ImmediateRole));
        Assert.All(result.Commands.Take(2), command =>
            Assert.Contains(command.Ancestry, frame =>
                frame.Region == CommandAncestryRegion.Iterator));
        Assert.All(result.Commands.Skip(2), command =>
            Assert.Contains(command.Ancestry, frame =>
                frame.Region == CommandAncestryRegion.LoopBody));
        Assert.All(result.Commands.Take(2), command => Assert.True(command.IsComplete));
        Assert.All(result.Commands.Skip(2), command => Assert.True(command.IsComplete));
        Assert.Equal(result.Clauses, result.Commands.Select(command => command.Clause));
    }

    [Fact]
    public void Direct_subexpression_iterator_discovers_inner_command()
    {
        var result = Parse(
            "foreach ($item in $(Get-ChildItem C:\\input)) { Write-Output $item }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var loop = Assert.IsType<ForEachSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.IsType<CommandSubstitutionSyntax>(
            Assert.Single(loop.IteratorCommands.Statements));
        Assert.Equal(new[] { "Get-ChildItem", "Write-Output" },
            result.Commands.Select(CommandVerb));
        Assert.Equal(CommandOccurrenceRole.Substitution, result.Commands[0].ImmediateRole);
        Assert.Contains(result.Commands[0].Ancestry, frame =>
            frame.Region == CommandAncestryRegion.Iterator);
        Assert.Equal(CommandOccurrenceRole.LoopBody, result.Commands[1].ImmediateRole);
        Assert.True(result.Commands[1].IsComplete);
    }

    [Fact]
    public void Default_state_keeps_loop_command_complete_without_promoting_value()
    {
        var result = Parse(
            "foreach ($f in @('a','b')) { Write-Output $f }");

        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Default_state_keeps_static_pipeline_stages_complete()
    {
        var result = Parse("Get-ChildItem | Remove-Item");

        Assert.Equal(2, result.Commands.Count);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Nested_foreach_statements_preserve_distinct_loop_ancestry()
    {
        var result = Parse(
            "foreach ($outer in 1) { foreach ($inner in 2) { Write-Output $inner } }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var outer = Assert.IsType<ForEachSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.IsType<ForEachSyntax>(Assert.Single(outer.Body.Statements));
        var occurrence = Assert.Single(result.Commands);
        Assert.Equal(CommandOccurrenceRole.LoopBody, occurrence.ImmediateRole);
        Assert.Equal(
            2,
            occurrence.Ancestry.Count(frame =>
                frame.AncestorKind == ShellSyntaxKind.ForEach &&
                frame.Region == CommandAncestryRegion.LoopBody));
        Assert.True(occurrence.IsComplete);
    }

    [Fact]
    public void Decoded_host_wrapper_retains_foreach_structure_without_outer_spans()
    {
        var result = Parse(
            "pwsh -Command 'foreach ($x in 1) { Write-Output $x }'");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        var loop = Assert.IsType<ForEachSyntax>(Assert.Single(wrapper.Body.Statements));
        Assert.Null(loop.SourceStart);
        Assert.Null(loop.SourceLength);
        Assert.Null(loop.Binding.Source.SourceStart);
        Assert.Null(loop.Binding.Source.SourceLength);
        Assert.Null(loop.Iterable.SourceStart);
        Assert.Null(loop.Iterable.SourceLength);
        Assert.True(Assert.Single(result.Commands).IsComplete);
    }

    [Theory]
    [InlineData("foreach ($x in $items) { Write-Output $x }")]
    [InlineData("foreach ($x in @items) { Write-Output $x }")]
    [InlineData("foreach ($x in & $producer) { Write-Output $x }")]
    [InlineData("foreach ($x in 1, 2, 3) { Write-Output $x }")]
    [InlineData("foreach (${x} in 1) { Write-Output $x }")]
    [InlineData("foreach ($global:x in 1) { Write-Output $x }")]
    [InlineData("foreach ($x 1) { Write-Output $x }")]
    [InlineData("foreach ($x in) { Write-Output $x }")]
    [InlineData("foreach ($x in 1) Write-Output $x")]
    [InlineData("foreach ($x in 1) { Write-Output $x")]
    [InlineData("foreach ($x in 1) { & $command $x }")]
    public void Dynamic_or_malformed_foreach_fails_atomically(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Theory]
    [InlineData("foreach ($x in 1) { Push-Location C:\\other }")]
    [InlineData("foreach ($x in 1) { Pop-Location }")]
    [InlineData("foreach ($x in 1) { Set-Variable x 2 }")]
    [InlineData("foreach ($x in 1) { Set-Alias wipe Remove-Item }; wipe file.txt")]
    [InlineData("foreach ($x in Set-Alias wipe Remove-Item) { Write-Output $x }; wipe file.txt")]
    [InlineData("foreach ($x in New-Alias wipe Remove-Item) { Write-Output $x }; wipe file.txt")]
    [InlineData("foreach ($x in Import-Module ./commands.psm1) { Write-Output $x }; Invoke-Thing")]
    [InlineData("foreach ($x in 1) { Set-Item Alias:wipe Remove-Item }; wipe file.txt")]
    [InlineData("foreach ($x in 1) { Set-Item 'Alias:wipe' Remove-Item }; wipe file.txt")]
    [InlineData("foreach ($x in 1) { New-Item Function:wipe -Value { Remove-Item $args } }; wipe file.txt")]
    [InlineData("foreach ($x in 1) { Set-Item Env:PATH C:\\tools }; tool")]
    [InlineData("foreach ($x in 1) { Set-Item \"Env:PATH\" C:\\tools }; tool")]
    [InlineData("foreach ($x in $(Set-Item 'Microsoft.PowerShell.Core\\Alias::wipe' Remove-Item; wipe victim)) { }")]
    public void Unmodeled_foreach_state_transfer_fails_atomically(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("state mutation", result.UnparseableReason!);
    }

    [Theory]
    [InlineData("foreach ($x in 1) { Write-Output $x } | Select-Object")]
    [InlineData("Get-Date && foreach ($x in 1) { Write-Output $x }")]
    [InlineData("foreach ($x in 1) { Write-Output $x } || Get-Date")]
    public void Foreach_rejects_pipeline_and_chain_boundaries(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Foreach_with_parenthesized_argument_in_pipeline_slot_remains_an_alias()
    {
        var result = ParseIsolated("Write-Output x | foreach ($_) ");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.IsType<PipelineSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal("ForEach-Object", result.Commands[1].Clause.Verb.CanonicalVerb);
        Assert.Equal(2, result.Commands.Count);
    }

    [Theory]
    [InlineData("Write-Output x | foreach (1)")]
    [InlineData("& foreach (1)")]
    public void Foreach_with_literal_parenthesized_argument_remains_an_alias(string source)
    {
        var result = ParseIsolated(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands[^1];
        Assert.Equal("ForEach-Object", command.Clause.Verb.CanonicalVerb);
        Assert.Equal("(1)", Assert.Single(command.Clause.Args).Raw);
    }

    [Fact]
    public void Foreach_after_call_operator_remains_an_alias()
    {
        var result = ParseIsolated("& foreach ($x)");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var command = Assert.Single(result.Commands);
        Assert.Equal("ForEach-Object", command.Clause.Verb.CanonicalVerb);
        Assert.True(command.IsComplete);
    }

    [Fact]
    public void Semicolon_remains_a_valid_foreach_statement_boundary()
    {
        var result = Parse(
            "Get-Date; foreach ($x in 1) { Write-Output $x }; Get-Process");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(3, list.Items.Count);
        Assert.IsType<ForEachSyntax>(list.Items[1].Command);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Separate_and_or_pipeline_after_foreach_statement_remains_valid()
    {
        var result = Parse(
            "foreach ($x in 1) { Write-Output $x }; Get-Date && Get-Process");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(CompoundOperator.Sequence, list.Items[1].Operator);
        Assert.Equal(CompoundOperator.AndIf, list.Items[2].Operator);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Isolated_child_host_loop_does_not_taint_outer_continuation()
    {
        var result = ParseIsolated(
            "pwsh -Command 'foreach ($x in 1) { Write-Output $x }'; Get-Date");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.True(result.Commands[0].IsComplete);
        Assert.True(result.Commands[1].IsComplete);
    }

    [Fact]
    public void Decoded_child_host_iterator_stays_visible_and_complete()
    {
        var result = ParseIsolated(
            "pwsh -Command 'foreach ($x in Get-Item C:\\input) " +
            "{ Write-Output $x }'; Get-Date");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(new[] { "Get-Item", "Write-Output", "Get-Date" },
            result.Commands.Select(CommandVerb));
        Assert.True(result.Commands[0].IsComplete);
        Assert.True(result.Commands[1].IsComplete);
        Assert.True(result.Commands[2].IsComplete);
    }

    [Fact]
    public void Foreach_object_alias_exposes_an_unknown_script_block_region()
    {
        var result = ParseIsolated("Get-ChildItem | foreach { Write-Output $_ }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal("Get-ChildItem", CommandVerb(result.Commands[0]));
        Assert.Equal("ForEach-Object", result.Commands[1].Clause.Verb.CanonicalVerb);
        Assert.IsType<PipelineSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(3, result.Commands.Count);
        var pipeline = Assert.IsType<PipelineSyntax>(Assert.Single(result.Syntax.Statements));
        var host = Assert.IsType<SimpleCommandSyntax>(pipeline.Stages[1]);
        Assert.Single(host.ExecutionRegions);
        Assert.Equal(CommandOccurrenceRole.ExecutionRegion, result.Commands[2].ImmediateRole);
    }

    [Fact]
    public void Foreach_shares_the_structural_depth_budget()
    {
        var exact = Parse(NestedLoops(ShellAnalysisLimits.MaxStructuralNesting));
        var overflow = Parse(NestedLoops(ShellAnalysisLimits.MaxStructuralNesting + 1));

        Assert.False(exact.IsUnparseable, exact.UnparseableReason);
        Assert.True(overflow.IsUnparseable);
        Assert.Empty(overflow.Commands);
        Assert.Empty(overflow.Clauses);
        Assert.Contains("nesting depth", overflow.UnparseableReason!);
    }

    [Fact]
    public void Isolated_literal_scalar_publishes_exact_binding_value()
    {
        var result = ParseIsolated(
            "foreach ($f in 'a.txt') { Remove-Item -LiteralPath $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var effective = Assert.Single(command.EffectiveArguments);
        Assert.Equal("$f", command.Clause.Elements[effective.ClauseElementIndex].Raw);
        AssertDomain(effective.Value, ShellValueDomainKind.Exact, "a.txt");
    }

    [Fact]
    public void Isolated_literal_array_publishes_distinct_finite_binding_values()
    {
        var result = ParseIsolated(
            "foreach ($f in @('a.txt', 'b.txt', 'a.txt')) { Write-Output $F }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        AssertDomain(
            Assert.Single(command.EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet,
            "a.txt",
            "b.txt");
    }

    [Fact]
    public void Default_initial_state_keeps_policy_sensitive_binding_strict()
    {
        var result = Parse(
            "foreach ($f in @('a.txt', 'b.txt')) { Remove-Item -LiteralPath $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.False(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Pipeline_objects_remain_unknown_without_making_body_structure_incomplete()
    {
        var result = ParseIsolated(
            "foreach ($f in Get-ChildItem C:\\input) { Write-Output $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.True(result.Commands[0].IsComplete);
        Assert.True(result.Commands[1].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(result.Commands[1].EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Nested_distinct_bindings_compose_case_insensitively()
    {
        var result = ParseIsolated(
            "foreach ($outer in 'left') { foreach ($inner in @('a','b')) " +
            "{ Write-Output \"$OUTER/$Inner\" } }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        AssertDomain(
            Assert.Single(command.EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet,
            "left/a",
            "left/b");
    }

    [Fact]
    public void Same_name_nested_binding_persists_the_inner_assignment()
    {
        var result = ParseIsolated(
            "foreach ($f in 'outer') { foreach ($F in 'inner') " +
            "{ Write-Output $f }; Write-Output $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
        Assert.All(result.Commands, command => AssertDomain(
            Assert.Single(command.EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "inner"));
    }

    [Fact]
    public void Ordered_visits_leave_the_last_binding_visible_after_foreach()
    {
        var result = ParseIsolated(
            "foreach ($f in @('a','b','a')) { Write-Output $f }; Write-Output $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        AssertDomain(
            Assert.Single(result.Commands[0].EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet,
            "a",
            "b");
        AssertDomain(
            Assert.Single(result.Commands[1].EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "a");
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
        Assert.All(result.Commands, command => AssertDomain(
            command.WorkingDirectory,
            ShellValueDomainKind.Exact,
            "C:/work"));
    }

    [Fact]
    public void Empty_foreach_preserves_cwd_and_does_not_invent_a_binding()
    {
        var result = ParseIsolated(
            "foreach ($f in @()) { Set-Location C:\\other }; Write-Output $f; Get-Date");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(3, result.Commands.Count);
        Assert.True(result.Commands[0].IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, result.Commands[0].WorkingDirectory.Kind);
        Assert.True(result.Commands[1].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(result.Commands[1].EffectiveArguments).Value.Kind);
        Assert.True(result.Commands[2].IsComplete);
        AssertDomain(
            result.Commands[2].WorkingDirectory,
            ShellValueDomainKind.Exact,
            "C:/work");
        Assert.DoesNotContain(
            result.Clauses[2].Args,
            argument => argument.IsCwdAttribution);
    }

    [Fact]
    public void Set_location_success_partition_flows_through_and_if_inside_foreach()
    {
        var result = ParseIsolated(
            "foreach ($f in 'C:\\target') { Set-Location $f && Get-Item child.txt }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        AssertDomain(
            result.Commands[0].WorkingDirectory,
            ShellValueDomainKind.Exact,
            "C:/work");
        AssertDomain(
            result.Commands[1].WorkingDirectory,
            ShellValueDomainKind.Exact,
            "C:/target");
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Set_location_failure_path_makes_post_loop_cwd_unknown()
    {
        var result = ParseIsolated(
            "foreach ($f in 'C:\\target') { Set-Location $f }; Get-Item child.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.Equal(ShellValueDomainKind.Unknown, result.Commands[1].WorkingDirectory.Kind);
        Assert.Contains(
            result.Clauses[1].Args,
            argument => argument.IsCwdAttribution &&
                argument.Kind == ArgKind.DynamicSkip);
        Assert.DoesNotContain(
            result.Clauses[1].Args,
            argument => argument.Raw == "child.txt" && argument.Resolved is not null);
    }

    [Fact]
    public void Set_location_sequence_sanitizes_false_exact_loop_body_compatibility_path()
    {
        var result = ParseIsolated(
            "foreach ($f in 'x') { Set-Location C:\\target; Get-Item child.txt }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(ShellValueDomainKind.Unknown, result.Commands[1].WorkingDirectory.Kind);
        var clause = result.Clauses[1];
        Assert.Same(clause, result.Commands[1].Clause);
        Assert.Contains(
            clause.Args,
            argument => argument.Raw == "child.txt" && argument.Resolved is null);
        Assert.Contains(
            clause.Elements,
            element => element.Value == "child.txt" && element.Resolved is null);
        Assert.Contains(
            clause.Args,
            argument => argument.IsCwdAttribution &&
                argument.Kind == ArgKind.DynamicSkip &&
                argument.Raw == "<dynamic-cwd>");
    }

    [Fact]
    public void Unknown_cwd_does_not_clear_an_absolute_compatibility_path()
    {
        var result = ParseIsolated(
            "foreach ($f in 'x') { Set-Location C:\\target; Get-Item C:\\fixed.txt }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(ShellValueDomainKind.Unknown, result.Commands[1].WorkingDirectory.Kind);
        Assert.Contains(
            result.Clauses[1].Args,
            argument => argument.Raw == "C:\\fixed.txt" &&
                argument.Resolved == "C:/fixed.txt");
    }

    [Fact]
    public void Fixed_point_repeated_value_retains_exact_post_loop_binding()
    {
        var values = string.Join(",", Enumerable.Repeat("'same'", 33));
        var result = ParseIsolated(
            $"foreach ($f in @({values})) {{ Write-Output $f }}; Write-Output $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.All(result.Commands, command => AssertDomain(
            Assert.Single(command.EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "same"));
    }

    [Fact]
    public void Runtime_iterator_uses_zero_or_more_post_loop_binding_state()
    {
        var result = ParseIsolated(
            "foreach ($f in Get-ChildItem C:\\input) { Write-Output $f }; Write-Output $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(3, result.Commands.Count);
        Assert.All(result.Commands.Skip(1), command => Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind));
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Prior_variable_mutation_keeps_static_occurrence_complete()
    {
        var result = ParseIsolated(
            "Set-Variable f seeded; foreach ($f in 'value') { Write-Output $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.True(result.Commands[1].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(result.Commands[1].EffectiveArguments).Value.Kind);
        AssertDomain(
            result.Commands[1].WorkingDirectory,
            ShellValueDomainKind.Exact,
            "C:/work");
    }

    [Fact]
    public void Default_state_prior_variable_mutation_keeps_static_occurrence_complete()
    {
        var result = Parse(
            "Set-Variable f seeded; foreach ($f in 'value') { Write-Output $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.True(result.Commands[1].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(result.Commands[1].EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("-OutVariable f")]
    [InlineData("-ov f")]
    [InlineData("-ov:f")]
    [InlineData("-ov +f")]
    [InlineData("-OutV f")]
    [InlineData("-PipelineVariable f")]
    [InlineData("-pv f")]
    [InlineData("-Pi f")]
    [InlineData("-ErrorVariable f")]
    [InlineData("-ev f")]
    [InlineData("-ErrorV f")]
    [InlineData("-WarningVariable f")]
    [InlineData("-wv f")]
    [InlineData("-WarningV f")]
    [InlineData("-InformationVariable f")]
    [InlineData("-iv f")]
    [InlineData("-InformationV f")]
    public void Common_variable_writer_invalidates_a_proved_binding(
        string parameter)
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            $"Write-Output C:/sensitive.txt {parameter}; Remove-Item $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Set_location_error_variable_invalidates_failure_continuation()
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            "Set-Location Z:/missing -ErrorVariable f || Write-Output $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        AssertDomain(
            command.WorkingDirectory,
            ShellValueDomainKind.Exact,
            "C:/work");
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Set_location_out_variable_invalidates_success_continuation()
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            "Set-Location C:\\target -OutVariable f && Write-Output $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, command.WorkingDirectory.Kind);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData('\u2013')]
    [InlineData('\u2014')]
    [InlineData('\u2015')]
    public void Alternate_parameter_dash_fails_state_analysis_atomically(char dash)
    {
        var writer = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            $"Write-Output C:/sensitive.txt {dash}OutVariable f; Remove-Item $f");
        var location = ParseIsolated(
            $"Set-Location {dash}Path C:/target; Get-Item child.txt");
        var provider = ParseIsolated(
            $"Set-Item {dash}Path Alias:git {dash}Value Remove-Item; git child.txt");

        Assert.All(new[] { writer, location, provider }, result =>
        {
            Assert.True(result.IsUnparseable);
            Assert.Empty(result.Commands);
            Assert.Empty(result.Clauses);
            Assert.Contains($"U+{(int)dash:X4}", result.UnparseableReason);
        });
    }

    [Theory]
    [InlineData("Microsoft.PowerShell.Utility\\Tee-Object -Variable f -InputObject C:/sensitive.txt")]
    [InlineData("Write-Output C:/sensitive.txt | Microsoft.PowerShell.Utility\\Tee-Object -Variable f")]
    [InlineData("& 'Microsoft.PowerShell.Utility\\Tee-Object' -Variable f -InputObject C:/sensitive.txt")]
    [InlineData("Microsoft.PowerShell.Utility\\Set-Variable f C:/sensitive.txt")]
    [InlineData("Microsoft.PowerShell.Management\\Set-Location C:/sensitive")]
    public void Unsupported_module_qualified_mutation_fails_structured_parse_atomically(
        string mutation)
    {
        var result = ParseIsolated(
            $"foreach ($f in 'safe.txt') {{ }}; {mutation}; Remove-Item $f");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("module-qualified cmdlet", result.UnparseableReason);
    }

    [Theory]
    [InlineData("Tee-Object -Variable f -InputObject C:/sensitive.txt")]
    [InlineData("Tee-Object -Vari f -InputObject C:/sensitive.txt")]
    [InlineData("Tee-Object -Va f -InputObject C:/sensitive.txt")]
    [InlineData("Tee-Object -V f -InputObject C:/sensitive.txt")]
    [InlineData("tee -Variable:f -InputObject C:/sensitive.txt")]
    [InlineData("Import-LocalizedData -BindingVariable f")]
    [InlineData("Import-LocalizedData -Bind f")]
    [InlineData("Import-LocalizedData -Bi f")]
    [InlineData("Import-LocalizedData -Variable f")]
    [InlineData("Import-LocalizedData -Vari f")]
    [InlineData("Import-LocalizedData -V f")]
    [InlineData("Invoke-RestMethod -Uri https://example.invalid -SessionVariable f")]
    [InlineData("Invoke-RestMethod -Uri https://example.invalid -SV f")]
    [InlineData("Invoke-RestMethod -Uri https://example.invalid -Se f")]
    [InlineData("irm -Uri https://example.invalid -SV:f")]
    [InlineData("Invoke-RestMethod -Uri https://example.invalid -ResponseHeadersVariable f")]
    [InlineData("Invoke-RestMethod -Uri https://example.invalid -RHV f")]
    [InlineData("Invoke-RestMethod -Uri https://example.invalid -Resp f")]
    [InlineData("Invoke-RestMethod -Uri https://example.invalid -StatusCodeVariable f")]
    [InlineData("Invoke-RestMethod -Uri https://example.invalid -St f")]
    [InlineData("Invoke-WebRequest -Uri https://example.invalid -SessionVariable f")]
    [InlineData("Invoke-WebRequest -Uri https://example.invalid -SV f")]
    [InlineData("Invoke-WebRequest -Uri https://example.invalid -Se f")]
    [InlineData("iwr -Uri https://example.invalid -SV:f")]
    public void Command_specific_variable_writer_invalidates_a_proved_binding(
        string mutation)
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            $"{mutation}; Remove-Item $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Splat_may_supply_a_variable_writer_and_invalidates_a_proved_binding()
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            "Write-Output C:/sensitive.txt @params; Remove-Item $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("Write-Output C:/sensitive.txt -OutBuffer 1")]
    [InlineData("Tee-Object -Verbose -InputObject C:/sensitive.txt")]
    [InlineData("Import-LocalizedData -BaseDirectory C:/safe")]
    public void Nonwriting_parameter_does_not_invalidate_a_proved_binding(
        string commandText)
    {
        var result = ParseIsolated(
            $"foreach ($f in 'safe.txt') {{ }}; {commandText}; Remove-Item $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.True(command.IsComplete);
        AssertDomain(
            Assert.Single(command.EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "safe.txt");
    }

    [Fact]
    public void Pipeline_variable_writer_fails_the_pipeline_atomically()
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            "Write-Output C:/sensitive.txt -PipelineVariable f | Remove-Item $f");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Variable_writer_in_current_runspace_substitution_propagates()
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            "Write-Output $(Write-Output C:/sensitive.txt -OutVariable f); " +
            "Remove-Item $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Variable_writer_in_decoded_child_does_not_escape()
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            "pwsh -Command 'Write-Output C:/sensitive.txt -OutVariable f'; " +
            "Remove-Item $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.True(command.IsComplete);
        AssertDomain(
            Assert.Single(command.EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "safe.txt");
    }

    [Theory]
    [InlineData("Set-Item Alias:foo Remove-Item")]
    [InlineData("Set-Item 'Alias:\\foo' Remove-Item")]
    [InlineData("si 'Function:\\foo' { Remove-Item $args }")]
    [InlineData("Set-Item 'Variable:\\f' seeded")]
    [InlineData("Set-Item Variable:f seeded")]
    [InlineData("Set-Item Env:PATH C:\\tools")]
    [InlineData("Set-Item -Path:Alias:\\foo -Value:Remove-Item")]
    [InlineData("Set-Item -LiteralPath:Function:\\foo -Value:{ Remove-Item $args }")]
    [InlineData("Set-Item -Path:Variable:\\f -Value:seeded")]
    [InlineData("Set-Item -LiteralPath:Env:\\PATH -Value:C:\\tools")]
    [InlineData("Set-Item -Path @('Alias:\\foo') -Value Remove-Item")]
    [InlineData("Set-Item -Path $(Write-Output 'Alias:\\foo') -Value Remove-Item")]
    [InlineData("Set-Item -Path $env:TARGET -Value Remove-Item")]
    [InlineData("Set-Item -LP $env:TARGET -Value Remove-Item")]
    [InlineData("Set-Item -LP:$env:TARGET -Value Remove-Item")]
    [InlineData("Copy-Item -Path C:/safe -Destination @('Alias:\\foo')")]
    public void Prior_provider_mutation_invalidates_later_binding_proof(string mutation)
    {
        var result = ParseIsolated(
            $"{mutation}; foreach ($f in 'value') {{ foo $f }}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("Set-Item -LiteralPath C:/safe/file.txt -Value $env:CONTENT")]
    [InlineData("Set-Item C:/safe/file.txt $env:CONTENT")]
    [InlineData("Set-Item -LiteralPath C:/safe/file.txt -Value Alias:foo")]
    [InlineData("Set-Item -LiteralPath:C:/safe/file.txt -Value:Alias:\\foo")]
    [InlineData("New-Item -Path C:/safe/link -ItemType SymbolicLink -Target $env:TARGET")]
    [InlineData("New-Item -Path C:/safe/link -ItemType SymbolicLink -Target Alias:\\foo")]
    [InlineData("New-Item -Path:C:/safe/link -ItemType:SymbolicLink -Target:$env:TARGET")]
    public void Value_operand_does_not_invalidate_a_proved_filesystem_target(
        string mutation)
    {
        var result = ParseIsolated(
            $"{mutation}; foreach ($f in 'value') {{ Write-Output $f }}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.True(result.Commands[1].IsComplete);
        AssertDomain(
            Assert.Single(result.Commands[1].EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "value");
    }

    [Fact]
    public void Finite_provider_target_invalidates_commands_after_mutating_visit()
    {
        var result = ParseIsolated(
            "foreach ($f in @('C:/safe','Alias:\\foo')) { " +
            "Set-Item -LP $f -Value Remove-Item; foo $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(result.Commands[1].EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("Set-Location Alias:; New-Item -Name foo -Value Remove-Item")]
    [InlineData("Set-Location Function:; New-Item -Name foo -Value Remove-Item")]
    [InlineData("Set-Location Alias:; Set-Item foo Remove-Item")]
    [InlineData("Set-Location $env:TARGET; Set-Item foo Remove-Item")]
    public void Unproved_or_nonfilesystem_location_success_invalidates_later_proofs(
        string mutation)
    {
        var result = ParseIsolated(
            $"{mutation}; foreach ($f in 'value') {{ foo $f }}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, command.WorkingDirectory.Kind);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("Set-Item Alias:git Remove-Item; git child.txt")]
    [InlineData("Set-Location Alias:; New-Item -Name git -Value Remove-Item; git child.txt")]
    [InlineData("Set-Location Function:; New-Item -Name git -Value Remove-Item; git child.txt")]
    [InlineData("Import-PSSession $session -CommandName git -AllowClobber; git child.txt")]
    [InlineData("Import-Alias aliases.csv -Force; git child.txt")]
    [InlineData("New-Module -ScriptBlock $script; git child.txt")]
    public void Observed_command_resolution_mutation_invalidates_plain_continuations(
        string source)
    {
        var result = ParseIsolated(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.False(result.Commands.Last().IsComplete);
    }

    [Theory]
    [InlineData("Import-PSSession $session -CommandName git -AllowClobber")]
    [InlineData("Import-Alias aliases.csv -Force")]
    [InlineData("New-Module -ScriptBlock $script")]
    public void Command_resolution_mutation_propagates_through_current_runspace_substitution(
        string mutation)
    {
        var result = ParseIsolated(
            $"Write-Output $({mutation}); git child.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.False(result.Commands.Last().IsComplete);
    }

    [Theory]
    [InlineData("Import-PSSession $session -CommandName git -AllowClobber")]
    [InlineData("Import-Alias aliases.csv -Force")]
    [InlineData("New-Module -ScriptBlock $script")]
    public void Command_resolution_mutation_in_decoded_child_does_not_escape(
        string mutation)
    {
        var result = ParseIsolated(
            $"pwsh -Command '{mutation}'; git child.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.True(result.Commands.Last().IsComplete);
    }

    [Theory]
    [InlineData("Invoke-Expression $code")]
    [InlineData("iex -Command:$code")]
    [InlineData("& 'iex' $code")]
    [InlineData("Microsoft.PowerShell.Utility\\Invoke-Expression $code")]
    public void Dynamic_invoke_expression_invalidates_current_runspace_state(
        string invocation)
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { }; " +
            $"{invocation}; git $f");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.False(command.IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, command.WorkingDirectory.Kind);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Dynamic_invoke_expression_inside_foreach_fails_atomically()
    {
        var result = ParseIsolated(
            "foreach ($f in 'safe.txt') { Invoke-Expression $code; git $f }");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("state mutation", result.UnparseableReason!);
    }

    [Fact]
    public void Nonfilesystem_location_failure_partition_retains_incoming_state()
    {
        var result = ParseIsolated(
            "Set-Location Alias: || Get-Item child.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands.Last();
        Assert.True(command.IsComplete);
        AssertDomain(command.WorkingDirectory, ShellValueDomainKind.Exact, "C:/work");
        Assert.Contains(command.Clause.Args, argument =>
            argument.Raw == "child.txt" &&
            argument.Resolved == "C:/work/child.txt");
    }

    [Fact]
    public void Prior_directory_stack_mutation_invalidates_binding_and_cwd_proofs()
    {
        var result = ParseIsolated(
            "Push-Location C:\\other; foreach ($f in 'value') { Write-Output $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.False(result.Commands[1].IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, result.Commands[1].WorkingDirectory.Kind);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(result.Commands[1].EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Pipeline_location_transfer_fails_atomically_until_pipeline_state_is_modeled()
    {
        var result = ParseIsolated(
            "foreach ($f in 'C:\\target') { Set-Location $f | Get-Location }");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Nested_concrete_transition_budget_overflow_fails_atomically()
    {
        var a = LiteralArray("a", 17);
        var b = LiteralArray("b", 17);
        var c = LiteralArray("c", 15);
        var result = ParseIsolated(
            $"foreach ($a in {a}) {{ foreach ($b in {b}) {{ " +
            $"foreach ($c in {c}) {{ Write-Output \"$a$b$c\" }} }} }}");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("exceeded limits", result.UnparseableReason!);
    }

    [Fact]
    public void Null_and_candidate_overflow_remain_unknown()
    {
        var nullResult = ParseIsolated(
            "foreach ($f in @($null)) { Write-Output $f }");
        var candidates = string.Join(",", Enumerable.Range(1, 33)
            .Select(index => $"'v{index:00}'"));
        var overflow = ParseIsolated(
            $"foreach ($f in @({candidates})) {{ Write-Output $f }}");

        Assert.False(nullResult.IsUnparseable, nullResult.UnparseableReason);
        Assert.True(Assert.Single(nullResult.Commands).IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(Assert.Single(nullResult.Commands).EffectiveArguments).Value.Kind);
        Assert.False(overflow.IsUnparseable, overflow.UnparseableReason);
        Assert.True(Assert.Single(overflow.Commands).IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(Assert.Single(overflow.Commands).EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("home")]
    [InlineData("PSItem")]
    [InlineData("EnabledExperimentalFeatures")]
    [InlineData("PSStyle")]
    [InlineData("ConfirmPreference")]
    [InlineData("_")]
    public void Isolated_reserved_or_stateful_builtin_binding_fails_atomically(string binding)
    {
        var result = ParseIsolated(
            $"foreach (${binding} in @('x')) {{ Write-Output ${binding} }}");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("built-in", result.UnparseableReason!);
    }

    [Fact]
    public void Literal_variable_spelling_does_not_receive_effective_binding_value()
    {
        var result = ParseIsolated(
            "foreach ($f in 'value') { Write-Output '$f' \"`$f\" }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        Assert.Empty(command.EffectiveArguments);
    }

    [Fact]
    public void Redirect_binding_resolves_to_a_bounded_path_domain()
    {
        var result = ParseIsolated(
            "foreach ($f in @('one.txt', 'two.txt')) { Write-Output x > $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.FiniteSet, redirect.Target.Kind);
        Assert.Equal(
            new[] { "C:/work/one.txt", "C:/work/two.txt" },
            redirect.Target.Values);
        Assert.True(redirect.IsComplete);
    }

    [Fact]
    public void Outer_wrapper_redirect_uses_parent_loop_binding_domain()
    {
        var result = ParseIsolated(
            "foreach ($f in @('one.txt', 'two.txt')) { " +
            "pwsh -Command 'Get-Date' > $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.FiniteSet, redirect.Target.Kind);
        Assert.Equal(
            new[] { "C:/work/one.txt", "C:/work/two.txt" },
            redirect.Target.Values);
        Assert.True(redirect.IsComplete);
    }

    [Theory]
    [InlineData("pwsh -Command \"pwsh -Command 'Get-Date'\"")]
    [InlineData("pwsh -EncodedCommand cAB3AHMAaAAgAC0ARQBuAGMAbwBkAGUAZABDAG8AbQBtAGEAbgBkACAAUgB3AEIAbABBAEgAUQBBAEwAUQBCAEUAQQBHAEUAQQBkAEEAQgBsAEEAQQA9AD0A")]
    public void Nested_wrapper_redirect_uses_outermost_parent_loop_binding_domain(
        string wrapper)
    {
        var result = ParseIsolated(
            "foreach ($f in @('one.txt', 'two.txt')) { " +
            $"{wrapper} > $f }}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.FiniteSet, redirect.Target.Kind);
        Assert.Equal(
            new[] { "C:/work/one.txt", "C:/work/two.txt" },
            redirect.Target.Values);
        Assert.True(redirect.IsComplete);
    }

    [Fact]
    public void Unreachable_relative_redirect_does_not_retain_parse_time_cwd()
    {
        var result = ParseIsolated(
            "foreach ($x in @()) { Write-Output x > relative.txt }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, command.WorkingDirectory.Kind);
        Assert.True(Assert.Single(command.Clause.Redirects).IsDynamicSkip);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.True(redirect.IsComplete);
    }

    [Fact]
    public void Unreachable_absolute_redirect_remains_cwd_independent()
    {
        var result = ParseIsolated(
            "foreach ($x in @()) { Write-Output x > C:\\fixed.txt }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var redirect = Assert.Single(command.Redirects);
        Assert.Equal(ShellValueDomainKind.Exact, redirect.Target.Kind);
        Assert.Equal("C:/fixed.txt", Assert.Single(redirect.Target.Values));
    }

    private static ParsedCommand Parse(string source) => new PwshParser(
        new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
        }).Parse(source);

    private static ParsedCommand ParseIsolated(string source) => new PwshParser(
        new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
        }).Parse(source);

    private static void AssertDomain(
        ShellValueDomain domain,
        ShellValueDomainKind kind,
        params string[] values)
    {
        Assert.Equal(kind, domain.Kind);
        Assert.Equal(values, domain.Values);
    }

    private static string LiteralArray(string prefix, int count) =>
        "@(" + string.Join(",", Enumerable.Range(1, count)
            .Select(index => $"'{prefix}{index:00}'")) + ")";

    private static string CommandVerb(CommandOccurrence command) => command.Clause.Verb.Joined;

    private static string NestedLoops(int depth)
    {
        var source = "Write-Output $x";
        for (var index = 0; index < depth; index++)
        {
            source = $"foreach ($x{index} in 1) {{ {source} }}";
        }

        return source;
    }
}
