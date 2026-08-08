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
    [Theory]
    [InlineData(
        "Measure-Command { Get-Item child.txt }",
        "Measure-Command")]
    [InlineData(
        "Trace-Command -Name ParameterBinding -Expression { Get-Item child.txt } -PSHost",
        "Trace-Command")]
    public void Current_scope_once_receivers_publish_typed_regions(
        string source,
        string expectedHost)
    {
        var result = ParseIsolated(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionOrigin.CommandArgument, region.Origin);
        Assert.Equal(ExecutionRegionPhase.Main, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Synchronous, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, region.Cardinality);
        Assert.Equal(
            new[] { expectedHost, "Get-Item" },
            result.Commands.Select(command => command.Clause.Verb.Tokens[0]));
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Current_scope_once_receiver_propagates_binding_state()
    {
        var result = ParseIsolated(
            "Measure-Command { foreach ($x in 'inner') { } }; Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.True(continuation.IsComplete);
        Assert.Equal(
            new[] { "inner" },
            Assert.Single(continuation.EffectiveArguments).Value.Values);
    }

    [Fact]
    public void Current_scope_receiver_joins_inner_outcomes_before_host_continuation()
    {
        var result = ParseIsolated(
            "Measure-Command { Set-Location /tmp } && Get-Item child.txt");

        var continuation = result.Commands.Last();
        Assert.Equal("Get-Item", continuation.Clause.Verb.Tokens[0]);
        Assert.Equal(ShellValueDomainKind.Unknown, continuation.WorkingDirectory.Kind);
    }

    [Fact]
    public void Receiver_identity_is_bound_before_its_own_common_parameter_mutation()
    {
        var result = ParseIsolated(
            "Measure-Command { Get-Date } -OutVariable measurement");

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Main, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Synchronous, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, region.Cardinality);
        Assert.True(result.Commands[0].IsComplete);
        Assert.False(result.Commands[1].IsComplete);
    }

    [Fact]
    public void Conflicting_receiver_identity_across_loop_visits_stays_unknown()
    {
        var result = ParseIsolated(
            "foreach ($x in @('first','second')) { " +
            "Measure-Command { Set-Alias Measure-Command Write-Output } }");

        var loop = Assert.IsType<ForEachSyntax>(Assert.Single(result.Syntax.Statements));
        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(loop.Body.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Unknown, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Unknown, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Unknown, region.Cardinality);
        Assert.All(result.Commands, command => Assert.False(command.IsComplete));
    }

    [Fact]
    public void Observed_command_mutation_downgrades_a_later_receiver()
    {
        var result = ParseIsolated(
            "Set-Alias Measure-Command Write-Output; " +
            "Measure-Command { Remove-Item victim.txt }; Get-Item later.txt");

        var host = result.Syntax.Statements
            .OfType<CommandListSyntax>()
            .SelectMany(list => list.Items)
            .Select(item => item.Command)
            .OfType<SimpleCommandSyntax>()
            .Single(command => command.Clause.Verb.Tokens[0] == "Measure-Command");
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Unknown, region.Phase);
        Assert.False(result.Commands.Last().IsComplete);
    }

    [Fact]
    public void Module_qualified_receiver_remains_proved_after_alias_mutation()
    {
        var result = ParseIsolated(
            "Set-Alias Measure-Command Write-Output; " +
            "Microsoft.PowerShell.Utility\\Measure-Command { Get-Date }");

        Assert.False(result.IsUnparseable);
        var host = result.Syntax.Statements
            .OfType<CommandListSyntax>()
            .SelectMany(list => list.Items)
            .Select(item => item.Command)
            .OfType<SimpleCommandSyntax>()
            .Last();
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Main, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Synchronous, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, region.Cardinality);
        Assert.True(result.Commands[1].IsComplete);
        Assert.False(result.Commands[2].IsComplete);
    }

    [Fact]
    public void Unknown_command_script_block_is_exposed_as_an_incomplete_region()
    {
        const string source = "Invoke-Custom { Remove-Item victim.txt }";

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
        Assert.Equal(new[] { "Invoke-Custom", "Remove-Item" },
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
        Assert.Equal(
            new[]
            {
                ExecutionRegionPhase.Begin,
                ExecutionRegionPhase.Process,
                ExecutionRegionPhase.End,
            },
            host.ExecutionRegions.Select(region => region.Phase));
        Assert.Equal(
            new[]
            {
                ExecutionRegionCardinality.Once,
                ExecutionRegionCardinality.OncePerInputObject,
                ExecutionRegionCardinality.Once,
            },
            host.ExecutionRegions.Select(region => region.Cardinality));
        Assert.All(host.ExecutionRegions, region =>
        {
            Assert.Equal(ExecutionRegionTiming.Synchronous, region.Timing);
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
        Assert.True(result.Commands[0].IsComplete);
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
            ShellValueDomainKind.Exact,
            alias.Commands.Last().WorkingDirectory.Kind);
        Assert.False(alias.Commands.Last().IsComplete);
    }

    [Fact]
    public void Standalone_for_each_object_uses_semantic_phase_order()
    {
        var result = ParseIsolated(
            "ForEach-Object " +
            "-End { Write-Output $x } " +
            "-Begin { foreach ($x in 'begin') { } } " +
            "-Process { Write-Output $x; foreach ($x in 'process') { } }; " +
            "Write-Output $x");

        var writes = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Write-Output")
            .ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Equal("process", Assert.Single(writes[0].EffectiveArguments).Value.Values[0]);
        Assert.Equal("begin", Assert.Single(writes[1].EffectiveArguments).Value.Values[0]);
        Assert.Equal("process", Assert.Single(writes[2].EffectiveArguments).Value.Values[0]);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Pipeline_for_each_object_joins_zero_and_repeated_process_visits()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; Write-Output input | ForEach-Object " +
            "-End { Write-Output $x } " +
            "-Begin { foreach ($x in 'begin') { } } " +
            "-Process { Write-Output $x; foreach ($x in 'process') { } }; " +
            "Write-Output $x");

        var writes = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Write-Output")
            .ToArray();
        Assert.Equal(4, writes.Length);
        Assert.Equal(
            new[] { "begin", "process" },
            Assert.Single(writes[1].EffectiveArguments).Value.Values
                .OrderBy(value => value));
        Assert.Equal(
            new[] { "begin", "process" },
            Assert.Single(writes[2].EffectiveArguments).Value.Values
                .OrderBy(value => value));
        Assert.False(writes[3].IsComplete);
    }

    [Fact]
    public void Standalone_where_object_records_but_does_not_apply_filter_state()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            "Where-Object { foreach ($x in 'filter') { } }; Write-Output $x");

        var host = result.Syntax.Statements
            .OfType<CommandListSyntax>()
            .SelectMany(list => list.Items)
            .Select(item => item.Command)
            .OfType<SimpleCommandSyntax>()
            .Single(command => command.Clause.Verb.Tokens[0] == "Where-Object");
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Filter, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Synchronous, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.OncePerInputObject, region.Cardinality);
        var continuation = result.Commands.Last();
        Assert.Equal("outer", Assert.Single(continuation.EffectiveArguments).Value.Values[0]);
        Assert.True(continuation.IsComplete);
    }

    [Fact]
    public void Explicit_where_input_conservatively_applies_filter_state()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            "Where-Object -InputObject value -FilterScript { " +
            "foreach ($x in 'filter') { } }; Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.Equal(
            new[] { "filter", "outer" },
            Assert.Single(continuation.EffectiveArguments).Value.Values
                .OrderBy(value => value));
        Assert.True(continuation.IsComplete);
    }

    [Fact]
    public void First_pipeline_stage_for_each_processes_once_without_upstream_input()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            "ForEach-Object { Write-Output $x; foreach ($x in 'process') { } } | " +
            "Out-Null");

        var processWrite = result.Commands
            .Single(command => command.Clause.Verb.Tokens[0] == "Write-Output");
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(processWrite.EffectiveArguments).Value.Values);
    }

    [Fact]
    public void First_pipeline_stage_where_has_no_filter_input()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            "Where-Object { foreach ($x in 'filter') { } } | Out-Null; " +
            "Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(continuation.EffectiveArguments).Value.Values);
        Assert.True(continuation.IsComplete);
    }

    [Fact]
    public void Interleaved_pipeline_callbacks_never_publish_stale_upstream_state()
    {
        var result = ParseIsolated(
            "foreach ($x in 'start') { }; Write-Output 1 2 | " +
            "ForEach-Object { Write-Output $x } | " +
            "ForEach-Object { foreach ($x in 'down') { }; Write-Output $_ }");

        var upstream = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Write-Output")
            .ElementAt(1);
        Assert.False(upstream.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(upstream.EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData(". { Write-Output $x; Write-Output $x }")]
    [InlineData("& { Write-Output $x; Write-Output $x }")]
    public void Interleaved_direct_regions_never_publish_stale_upstream_state(
        string upstreamStage)
    {
        var result = ParseIsolated(
            "foreach ($x in 'start') { }; " + upstreamStage + " | " +
            "ForEach-Object { foreach ($x in 'down') { }; Write-Output $_ }");

        var upstream = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Write-Output")
            .Take(2)
            .ToArray();
        Assert.Equal(2, upstream.Length);
        Assert.All(upstream, command =>
        {
            Assert.False(command.IsComplete);
            Assert.Equal(
                ShellValueDomainKind.Unknown,
                Assert.Single(command.EffectiveArguments).Value.Kind);
        });
    }

    [Fact]
    public void Interleaved_common_parameter_writer_invalidates_callback_state()
    {
        var result = ParseIsolated(
            "foreach ($x in 'start') { }; Write-Output 1 2 | " +
            "ForEach-Object { Write-Output $x } | " +
            "Write-Output -OutVariable x");

        var callbackWrite = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Write-Output")
            .ElementAt(1);
        Assert.False(callbackWrite.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(callbackWrite.EffectiveArguments).Value.Kind);
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
    public void Unknown_region_in_direct_child_can_escape_command_resolution_state()
    {
        var result = Parse(
            "& { Invoke-Custom { Set-Alias erase Get-Date -Scope Global } }; " +
            "erase target.txt");

        var continuation = result.Commands.Last();
        Assert.Equal("erase", continuation.Clause.Verb.Tokens[0]);
        Assert.False(continuation.IsComplete);
        Assert.Equal(ShellValueDomainKind.Unknown, continuation.WorkingDirectory.Kind);
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
