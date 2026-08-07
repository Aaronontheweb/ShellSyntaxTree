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

        var result = Parse(source);

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
        Assert.False(occurrence.IsComplete);
        Assert.All(occurrence.EffectiveArguments, argument =>
            Assert.Equal(ShellValueDomainKind.Unknown, argument.Value.Kind));
    }

    [Fact]
    public void Iterator_pipeline_and_body_pipeline_preserve_roles_and_ancestry()
    {
        const string source =
            "foreach ($f in Get-ChildItem C:\\input | Where-Object Name) " +
            "{ Get-Item $f | Remove-Item }";

        var result = Parse(source);

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
        Assert.All(result.Commands.Skip(2), command => Assert.False(command.IsComplete));
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
        Assert.False(result.Commands[1].IsComplete);
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
        Assert.False(occurrence.IsComplete);
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
        Assert.False(Assert.Single(result.Commands).IsComplete);
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
    [InlineData("foreach ($x in 1) { Set-Location C:\\other }")]
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
        var result = Parse("Write-Output x | foreach ($_) ");

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
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = result.Commands[^1];
        Assert.Equal("ForEach-Object", command.Clause.Verb.CanonicalVerb);
        Assert.Equal("(1)", Assert.Single(command.Clause.Args).Raw);
    }

    [Fact]
    public void Foreach_after_call_operator_remains_an_alias()
    {
        var result = Parse("& foreach ($x)");

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
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
        Assert.False(result.Commands[2].IsComplete);
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
        Assert.All(result.Commands, command => Assert.False(command.IsComplete));
    }

    [Fact]
    public void Isolated_child_host_loop_does_not_taint_outer_continuation()
    {
        var result = Parse(
            "pwsh -Command 'foreach ($x in 1) { Write-Output $x }'; Get-Date");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.False(result.Commands[0].IsComplete);
        Assert.True(result.Commands[1].IsComplete);
    }

    [Fact]
    public void Decoded_child_host_pipeline_iterator_stays_visible_without_outer_plan()
    {
        var result = ParseIsolated(
            "pwsh -Command 'foreach ($x in Get-Item C:\\input) " +
            "{ Write-Output $x }'; Get-Date");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(new[] { "Get-Item", "Write-Output", "Get-Date" },
            result.Commands.Select(CommandVerb));
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
        Assert.True(result.Commands[2].IsComplete);
    }

    [Fact]
    public void Foreach_object_alias_remains_an_opaque_script_block_argument()
    {
        var result = Parse("Get-ChildItem | foreach { Write-Output $_ }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal("Get-ChildItem", CommandVerb(result.Commands[0]));
        Assert.Equal("ForEach-Object", result.Commands[1].Clause.Verb.CanonicalVerb);
        Assert.IsType<PipelineSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(2, result.Commands.Count);
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
    public void Default_initial_state_withholds_binding_proof()
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
    public void Same_name_nested_binding_does_not_restore_outer_parser_frame()
    {
        var result = ParseIsolated(
            "foreach ($f in 'outer') { foreach ($F in 'inner') " +
            "{ Write-Output $f }; Write-Output $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.All(result.Commands, command => Assert.False(command.IsComplete));
        Assert.All(result.Commands, command => Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind));
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
    public void Redirect_binding_stays_incomplete_until_redirect_analysis_lands()
    {
        var result = ParseIsolated("foreach ($f in 'out.txt') { Write-Output x > $f }");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.False(Assert.Single(result.Commands).IsComplete);
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
