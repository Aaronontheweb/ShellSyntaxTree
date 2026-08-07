// -----------------------------------------------------------------------
// <copyright file="PwshExecutionRegionStructuralTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class PwshExecutionRegionStructuralTests
{
    [Fact]
    public void Command_script_block_is_exposed_as_an_unknown_incomplete_region()
    {
        const string source = "ForEach-Object { Remove-Item victim.txt }";

        var result = Parse(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionOrigin.CommandArgument, region.Origin);
        Assert.Equal(1, region.HostClauseElementIndex);
        Assert.Equal(ExecutionRegionPhase.Unknown, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Unknown, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Unknown, region.Cardinality);
        Assert.Equal(source.IndexOf('{'), region.SourceStart);
        Assert.Equal("{ Remove-Item victim.txt }".Length, region.SourceLength);

        var body = Assert.IsType<SimpleCommandSyntax>(Assert.Single(region.Body.Statements));
        Assert.Equal("Remove-Item", Assert.Single(body.Clause.Verb.Tokens));
        Assert.Equal(new[] { "ForEach-Object", "Remove-Item" },
            result.Commands.Select(command => command.Clause.Verb.Tokens[0]));
        Assert.All(result.Commands, command => Assert.False(command.IsComplete));
        Assert.Equal(CommandOccurrenceRole.ExecutionRegion, result.Commands[1].ImmediateRole);
        Assert.Contains(result.Commands[1].Ancestry,
            frame => frame.Region == CommandAncestryRegion.ExecutionRegion);
    }

    [Fact]
    public void Multiple_script_blocks_keep_authored_regions_and_host_coordinates()
    {
        const string source =
            "ForEach-Object -Begin { Write-Output begin } " +
            "-Process { Remove-Item one }, { Remove-Item two }";

        var result = Parse(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(3, host.ExecutionRegions.Count);
        Assert.Equal(
            new int?[] { 2, 4, 5 },
            host.ExecutionRegions.Select(region => region.HostClauseElementIndex));
        Assert.True(host.ExecutionRegions
            .Select(region => region.SourceStart)
            .SequenceEqual(host.ExecutionRegions.Select(region => region.SourceStart)
                .OrderBy(start => start)));
        Assert.All(host.ExecutionRegions, region =>
        {
            Assert.Equal(ExecutionRegionPhase.Unknown, region.Phase);
            Assert.Single(region.Body.Statements);
        });
        Assert.Equal(4, result.Commands.Count);
    }

    [Theory]
    [InlineData("Write-Output { Remove-Item victim.txt }")]
    [InlineData("Invoke-Custom { Remove-Item victim.txt }")]
    public void Unproved_receiver_identity_never_hides_a_script_block(string source)
    {
        var result = Parse(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Single(host.ExecutionRegions);
        Assert.Equal(2, result.Commands.Count);
        Assert.False(result.Commands[1].IsComplete);
    }

    [Theory]
    [InlineData("Where-Object { $_.Length -gt 0 }")]
    [InlineData("ForEach-Object { $_ }")]
    public void Pure_output_expressions_do_not_invent_command_occurrences(string source)
    {
        var result = Parse(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Empty(Assert.Single(host.ExecutionRegions).Body.Statements);
        Assert.Single(result.Commands);
        Assert.False(result.Commands[0].IsComplete);
    }

    [Theory]
    [InlineData("ForEach-Object { $_.Delete() }", "execution-bearing")]
    [InlineData(
        "ForEach-Object { $value = Remove-Item victim.txt }",
        "assignment statement")]
    [InlineData(
        "ForEach-Object { $value ??= Remove-Item victim.txt }",
        "assignment statement")]
    [InlineData(
        "ForEach-Object { $_ -eq \"$(Remove-Item victim.txt)\" }",
        "execution-bearing")]
    [InlineData(
        "ForEach-Object { $_ -eq @\"\n$(Remove-Item victim.txt)\n\"@ }",
        "execution-bearing")]
    public void Unsupported_execution_bearing_expression_fails_atomically(
        string source,
        string expectedReason)
    {
        var result = new PwshParser().Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains(expectedReason, result.UnparseableReason);
    }

    [Fact]
    public void Unknown_region_poisons_later_cwd_and_command_resolution_facts()
    {
        var location = Parse(
            "Invoke-Custom { Set-Location /tmp }; Remove-Item relative.txt");
        var alias = Parse(
            "ForEach-Object { Set-Alias ri Write-Output }; ri victim.txt");

        Assert.Equal(
            ShellValueDomainKind.Unknown,
            location.Commands.Last().WorkingDirectory.Kind);
        Assert.False(location.Commands.Last().IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            alias.Commands.Last().WorkingDirectory.Kind);
        Assert.False(alias.Commands.Last().IsComplete);
    }

    [Fact]
    public void Unknown_region_poisons_later_variable_facts()
    {
        var result = ParseIsolated(
            "Invoke-Custom { Set-Variable x value }; Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.False(continuation.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(continuation.EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Unknown_region_in_substitution_poisons_the_containing_host()
    {
        var result = ParseIsolated(
            "Get-Item relative.txt $(Invoke-Custom { Set-Location /tmp })");

        var host = result.Commands.Last();
        Assert.Equal("Get-Item", Assert.Single(host.Clause.Verb.Tokens));
        Assert.False(host.IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, host.WorkingDirectory.Kind);
    }

    [Fact]
    public void Unknown_region_in_current_scope_wrapper_poisons_inner_and_outer_continuations()
    {
        var result = ParseIsolated(
            "Invoke-Expression \"Invoke-Custom { Set-Location /tmp }; " +
            "Get-Item inner.txt\"; Get-Item outer.txt");

        var continuations = result.Commands
            .Where(command => command.Clause.Verb.Tokens.Contains("Get-Item"))
            .ToArray();
        Assert.Equal(2, continuations.Length);
        Assert.All(continuations, continuation =>
        {
            Assert.False(continuation.IsComplete);
            Assert.Equal(
                ShellValueDomainKind.Unknown,
                continuation.WorkingDirectory.Kind);
        });
    }

    [Fact]
    public void Unknown_region_in_child_wrapper_does_not_poison_outer_continuation()
    {
        var result = ParseIsolated(
            "pwsh -NoProfile -Command \"Invoke-Custom { Set-Location /tmp }; " +
            "Get-Item inner.txt\"; Get-Item outer.txt");

        var continuations = result.Commands
            .Where(command => command.Clause.Verb.Tokens.Contains("Get-Item"))
            .ToArray();
        Assert.Equal(2, continuations.Length);
        Assert.False(continuations[0].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            continuations[0].WorkingDirectory.Kind);
        Assert.True(continuations[1].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Exact,
            continuations[1].WorkingDirectory.Kind);
    }

    [Fact]
    public void Unknown_region_in_child_wrapper_pipeline_does_not_poison_outer_continuation()
    {
        var result = ParseIsolated(
            "pwsh -NoProfile -Command \"Invoke-Custom { Set-Location /tmp }; " +
            "Get-Item inner.txt\" | Write-Output passthrough; Get-Item outer.txt");

        var continuations = result.Commands
            .Where(command => command.Clause.Verb.Tokens.Contains("Get-Item"))
            .ToArray();
        Assert.Equal(2, continuations.Length);
        Assert.False(continuations[0].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            continuations[0].WorkingDirectory.Kind);
        Assert.True(continuations[1].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Exact,
            continuations[1].WorkingDirectory.Kind);
    }

    [Fact]
    public void Unknown_region_poisons_later_pipeline_stages()
    {
        var result = ParseIsolated(
            "Write-Output content | Invoke-Custom { Set-Location /tmp } | " +
            "Set-Content relative-probe.txt -WhatIf");

        var continuation = result.Commands.Last();
        Assert.Equal("Set-Content", Assert.Single(continuation.Clause.Verb.Tokens));
        Assert.False(continuation.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            continuation.WorkingDirectory.Kind);
        Assert.Contains(
            continuation.Clause.Args,
            argument => argument.IsCwdAttribution && argument.Raw == "<dynamic-cwd>");
    }

    [Theory]
    [InlineData(
        "Invoke-Expression 'pwsh -Command \"Invoke-Custom { Write-Output hi }\"; " +
        "Set-Location /tmp' | Write-Output later")]
    [InlineData(
        "Invoke-Expression 'pwsh -Command \"Invoke-Custom { Write-Output hi }\"; " +
        "Set-Alias ri Write-Output' | Write-Output later")]
    [InlineData(
        "Invoke-Expression 'Invoke-Custom { Write-Output hi }; " +
        "Set-Location /tmp' | Write-Output later")]
    [InlineData(
        "Invoke-Expression 'Set-Location /tmp; " +
        "Invoke-Custom { Write-Output hi }' | Write-Output later")]
    [InlineData(
        "Set-Location C:/work; Invoke-Expression 'Set-Alias ri Write-Output; " +
        "Invoke-Custom { Write-Output hi }' | Write-Output later")]
    [InlineData(
        "Set-Location C:/work; Invoke-Expression 'Set-Location /tmp; " +
        "Invoke-Custom { Write-Output hi }' | Write-Output later")]
    public void Execution_region_does_not_mask_sibling_pipeline_mutation(
        string source)
    {
        var result = new PwshParser(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
        }).Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    private static ParsedCommand Parse(string source)
    {
        var result = new PwshParser().Parse(source);
        Assert.False(result.IsUnparseable, result.UnparseableReason);
        return result;
    }

    private static ParsedCommand ParseIsolated(string source)
    {
        var result = new PwshParser(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
        }).Parse(source);
        Assert.False(result.IsUnparseable, result.UnparseableReason);
        return result;
    }
}
