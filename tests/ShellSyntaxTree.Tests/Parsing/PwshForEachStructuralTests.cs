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

    private static ParsedCommand Parse(string source) => new PwshParser(
        new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
        }).Parse(source);

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
