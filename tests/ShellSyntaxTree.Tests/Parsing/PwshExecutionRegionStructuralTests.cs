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

    [Theory]
    [InlineData("Invoke-Command { Get-Item child.txt }")]
    [InlineData("icm -ScriptBlock { Get-Item child.txt }")]
    [InlineData("Microsoft.PowerShell.Core\\Invoke-Command { Get-Item child.txt }")]
    public void In_process_invoke_command_publishes_a_synchronous_once_region(
        string source)
    {
        var result = ParseIsolated(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionOrigin.CommandArgument, region.Origin);
        Assert.Equal(ExecutionRegionPhase.Main, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Synchronous, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, region.Cardinality);
        Assert.Equal(2, result.Commands.Count);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void In_process_invoke_command_isolates_bindings_but_shares_location()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; Invoke-Command { " +
            "foreach ($x in 'inner') { }; Set-Location /tmp }; " +
            "Write-Output $x; Get-Item child.txt");

        var continuation = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] is "Write-Output" or "Get-Item")
            .TakeLast(2)
            .ToArray();
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(continuation[0].EffectiveArguments).Value.Values);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            continuation[1].WorkingDirectory.Kind);
        Assert.All(continuation, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void In_process_invoke_command_no_new_scope_shares_supported_state()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; Invoke-Command -NoNewScope { " +
            "foreach ($x in 'inner') { }; Set-Location /tmp }; " +
            "Write-Output $x; Get-Item child.txt");

        var continuation = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] is "Write-Output" or "Get-Item")
            .TakeLast(2)
            .ToArray();
        Assert.Equal(
            new[] { "inner" },
            Assert.Single(continuation[0].EffectiveArguments).Value.Values);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            continuation[1].WorkingDirectory.Kind);
        Assert.All(continuation, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void In_process_invoke_command_explicit_false_no_new_scope_isolates_state()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            "Invoke-Command -NoNewScope:$false { foreach ($x in 'inner') { } }; " +
            "Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(continuation.EffectiveArguments).Value.Values);
        Assert.True(continuation.IsComplete);
    }

    [Theory]
    [InlineData("Invoke-Command -ComputerName server -ScriptBlock { Get-Date }")]
    [InlineData("Invoke-Command -AsJob -ScriptBlock { Get-Date }")]
    [InlineData("Invoke-Command -NoNewScope:$scope -ScriptBlock { Get-Date }")]
    public void Unproved_invoke_command_shapes_remain_unknown(string source)
    {
        var result = ParseIsolated(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Unknown, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Unknown, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Unknown, region.Cardinality);
        Assert.All(result.Commands, command => Assert.False(command.IsComplete));
    }

    [Fact]
    public void In_process_invoke_command_child_scope_isolates_alias_mutation()
    {
        var result = ParseIsolated(
            "Invoke-Command { Set-Alias Measure-Command Write-Output }; " +
            "Measure-Command { Get-Date }");

        var host = result.Syntax.Statements
            .OfType<CommandListSyntax>()
            .SelectMany(list => list.Items)
            .Select(item => item.Command)
            .OfType<SimpleCommandSyntax>()
            .Last();
        Assert.Equal(
            ExecutionRegionPhase.Main,
            Assert.Single(host.ExecutionRegions).Phase);
        Assert.True(result.Commands.Last().IsComplete);
    }

    [Fact]
    public void In_process_invoke_command_no_new_scope_propagates_alias_mutation()
    {
        var result = ParseIsolated(
            "Invoke-Command -NoNewScope { Set-Alias Measure-Command Write-Output }; " +
            "Measure-Command { Get-Date }");

        var host = result.Syntax.Statements
            .OfType<CommandListSyntax>()
            .SelectMany(list => list.Items)
            .Select(item => item.Command)
            .OfType<SimpleCommandSyntax>()
            .Last();
        Assert.Equal(
            ExecutionRegionPhase.Unknown,
            Assert.Single(host.ExecutionRegions).Phase);
        Assert.False(result.Commands.Last().IsComplete);
    }

    [Fact]
    public void In_process_invoke_command_joins_body_outcomes_before_host_continuation()
    {
        var result = ParseIsolated(
            "Invoke-Command { Set-Location /maybe } && Get-Item child.txt");

        var continuation = result.Commands.Last();
        Assert.Equal("Get-Item", continuation.Clause.Verb.Tokens[0]);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            continuation.WorkingDirectory.Kind);
        Assert.True(continuation.IsComplete);
    }

    [Theory]
    [InlineData("Start-Job -ScriptBlock { Get-Item child.txt }")]
    [InlineData("sajb { Get-Item child.txt }")]
    [InlineData("Microsoft.PowerShell.Core\\Start-Job { Get-Item child.txt }")]
    public void Start_job_publishes_a_concurrent_once_main_region(string source)
    {
        var result = ParseIsolated(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionOrigin.CommandArgument, region.Origin);
        Assert.Equal(ExecutionRegionPhase.Main, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Concurrent, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, region.Cardinality);
        Assert.Equal(2, result.Commands.Count);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Start_job_keeps_authored_order_but_initializes_child_before_main()
    {
        var result = ParseIsolated(
            "Start-Job -ScriptBlock { Measure-Command { Get-Date } } " +
            "-InitializationScript { Set-Alias Measure-Command Write-Output }");

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal(
            new[] { ExecutionRegionPhase.Main, ExecutionRegionPhase.Initialization },
            host.ExecutionRegions.Select(region => region.Phase));
        Assert.Equal(
            new[] { "Start-Job", "Measure-Command", "Get-Date", "Set-Alias" },
            result.Commands.Select(command => command.Clause.Verb.Tokens[0]));
        var main = result.Commands[1];
        Assert.False(main.IsComplete);
    }

    [Theory]
    [InlineData("-WorkingDirectory /tmp")]
    [InlineData("-WorkingD:/tmp")]
    public void Start_job_applies_working_directory_before_initialization_and_isolates_exit(
        string workingDirectoryArgument)
    {
        var result = ParseIsolated(
            $"Start-Job {workingDirectoryArgument} " +
            "-InitializationScript { Get-Item init.txt } " +
            "-ScriptBlock { Get-Item main.txt; Set-Location / }; " +
            "Get-Item host.txt");

        var items = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Get-Item")
            .ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal("/tmp", Assert.Single(items[0].WorkingDirectory.Values));
        Assert.Equal("/tmp", Assert.Single(items[1].WorkingDirectory.Values));
        Assert.Equal("C:/work", Assert.Single(items[2].WorkingDirectory.Values));
        Assert.All(items, command => Assert.True(command.IsComplete));
    }

    [Theory]
    [InlineData("-WorkingDirectory $target")]
    [InlineData("-WorkingDirectory:$target")]
    public void Start_job_accepts_a_bounded_host_working_directory_value(
        string workingDirectoryArgument)
    {
        var result = ParseIsolated(
            "foreach ($target in '/tmp') { }; " +
            $"Start-Job {workingDirectoryArgument} " +
            "-ScriptBlock { Get-Item child.txt }");

        var child = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Tokens[0] == "Get-Item");
        Assert.Equal(
            new[] { "/tmp" },
            child.WorkingDirectory.Values);
        Assert.True(child.IsComplete);
    }

    [Theory]
    [InlineData("~/jobs", "C:/Users/test/jobs")]
    [InlineData("$HOME/jobs", "C:/Users/test/jobs")]
    public void Start_job_resolves_static_host_working_directory_forms(
        string target,
        string expected)
    {
        var result = ParseIsolated(
            $"Start-Job -WorkingDirectory {target} " +
            "-ScriptBlock { Get-Item child.txt }");

        var child = result.Commands.Last();
        Assert.Equal(
            new[] { expected },
            child.WorkingDirectory.Values);
        Assert.True(child.IsComplete);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("jobs")]
    [InlineData("child*")]
    [InlineData("HKLM:/Software")]
    [InlineData("\"x$HOME\"")]
    [InlineData("\" $HOME \"")]
    [InlineData("\"prefix${HOME}/x\"")]
    public void Start_job_rejects_non_independently_rooted_working_directory_forms(
        string target)
    {
        var result = ParseIsolated(
            $"Start-Job -WorkingDirectory {target} " +
            "-ScriptBlock { Get-Item child.txt }");

        var child = result.Commands.Last();
        Assert.Equal(ShellValueDomainKind.Unknown, child.WorkingDirectory.Kind);
    }

    [Fact]
    public void Start_job_preserves_significant_inline_working_directory_whitespace()
    {
        var result = ParseIsolated(
            "Start-Job -WorkingDirectory:' /tmp ' " +
            "-ScriptBlock { Get-Item child.txt }");

        var child = result.Commands.Last();
        Assert.Equal(ShellValueDomainKind.Unknown, child.WorkingDirectory.Kind);
        Assert.True(child.IsComplete);
    }

    [Theory]
    [InlineData("-PSVersion 5.1")]
    [InlineData("-PSVersion:5.1")]
    public void Start_job_alternate_child_version_remains_visible_but_incomplete(
        string versionArgument)
    {
        var result = ParseIsolated(
            $"Start-Job {versionArgument} -ScriptBlock {{ Get-Item child.txt }}");

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Unknown, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Unknown, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Unknown, region.Cardinality);
        Assert.All(result.Commands, command => Assert.False(command.IsComplete));
    }

    [Theory]
    [InlineData(
        "Start-Job -PSVersion 5.1 -ScriptBlock { Set-Location / }")]
    [InlineData(
        "Start-Job -FilePath script.ps1 " +
        "-InitializationScript { Set-Location / }")]
    [InlineData(
        "Start-Job -RunAs32 -ScriptBlock { Set-Location / }")]
    public void Incomplete_start_job_variants_preserve_host_location_boundary(
        string invocation)
    {
        var result = ParseIsolated(invocation + "; Get-Item host.txt");

        var continuation = result.Commands.Last();
        Assert.Equal("Get-Item", continuation.Clause.Verb.Tokens[0]);
        Assert.Equal(
            new[] { "C:/work" },
            continuation.WorkingDirectory.Values);
        Assert.True(continuation.IsComplete);
    }

    [Theory]
    [InlineData(
        "Start-Job -PSVersion 5.1 -ScriptBlock { " +
        "Set-Alias Measure-Command Write-Output }")]
    [InlineData(
        "Start-Job -FilePath script.ps1 -InitializationScript { " +
        "Set-Alias Measure-Command Write-Output }")]
    [InlineData(
        "Start-Job -RunAs32 -ScriptBlock { " +
        "Set-Alias Measure-Command Write-Output }")]
    public void Incomplete_start_job_variants_preserve_host_command_resolution_boundary(
        string invocation)
    {
        var result = ParseIsolated(invocation + "; Measure-Command { Get-Date }");

        var continuation = result.Commands
            .Last(command => command.Clause.Verb.Tokens[0] == "Measure-Command");
        Assert.True(continuation.IsComplete);
    }

    [Fact]
    public void Start_job_does_not_inherit_host_bindings_or_export_child_bindings()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; Start-Job " +
            "-InitializationScript { foreach ($x in 'init') { }; Write-Output $x } " +
            "-ScriptBlock { Write-Output $x; foreach ($x in 'main') { } }; " +
            "Write-Output $x");

        var writes = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Write-Output")
            .ToArray();
        Assert.Equal(3, writes.Length);
        Assert.All(writes.Take(2), write =>
        {
            Assert.Equal(
                ShellValueDomainKind.Unknown,
                Assert.Single(write.EffectiveArguments).Value.Kind);
            Assert.True(write.IsComplete);
        });
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(writes[2].EffectiveArguments).Value.Values);
        Assert.True(writes[2].IsComplete);
    }

    [Fact]
    public void Start_job_initialization_mutation_invalidates_main_but_not_host_resolution()
    {
        var result = ParseIsolated(
            "Start-Job -InitializationScript { " +
            "Set-Alias Measure-Command Write-Output } -ScriptBlock { " +
            "Measure-Command { Get-Date } }; Measure-Command { Get-Date }");

        var measurements = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Measure-Command")
            .ToArray();
        Assert.Equal(2, measurements.Length);
        Assert.False(measurements[0].IsComplete);
        Assert.True(measurements[1].IsComplete);
    }

    [Fact]
    public void Dynamic_start_job_working_directory_fails_closed_only_in_the_child()
    {
        var result = ParseIsolated(
            "Start-Job -WorkingDirectory $target " +
            "-ScriptBlock { Get-Item child.txt }; Get-Item host.txt");

        var items = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Get-Item")
            .ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(ShellValueDomainKind.Unknown, items[0].WorkingDirectory.Kind);
        Assert.True(items[0].IsComplete);
        Assert.Equal(
            new[] { "C:/work" },
            items[1].WorkingDirectory.Values);
        Assert.True(items[1].IsComplete);
    }

    [Fact]
    public void Start_job_file_path_keeps_initialization_visible_but_incomplete()
    {
        var result = ParseIsolated(
            "Start-Job -FilePath script.ps1 " +
            "-InitializationScript { Get-Date }");

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var initialization = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Unknown, initialization.Phase);
        Assert.All(result.Commands, command => Assert.False(command.IsComplete));
    }

    [Theory]
    [InlineData("New-Module { Get-Item child.txt }")]
    [InlineData("nmo -ScriptBlock { Get-Item child.txt }")]
    [InlineData("Microsoft.PowerShell.Core\\New-Module { Get-Item child.txt }")]
    public void New_module_publishes_a_synchronous_initialization_region(string source)
    {
        var result = ParseIsolated(source);

        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(result.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionOrigin.CommandArgument, region.Origin);
        Assert.Equal(ExecutionRegionPhase.Initialization, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Synchronous, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, region.Cardinality);
        Assert.Equal(2, result.Commands.Count);
        Assert.All(result.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void New_module_body_reads_caller_state_but_host_invalidates_continuation()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; New-Module { " +
            "Write-Output $x; foreach ($x in 'inner') { } }; " +
            "Write-Output $x");

        var writes = result.Commands
            .Where(command => command.Clause.Verb.Tokens[0] == "Write-Output")
            .ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(writes[0].EffectiveArguments).Value.Values);
        Assert.True(writes[0].IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(writes[1].EffectiveArguments).Value.Kind);
        Assert.False(writes[1].IsComplete);
    }

    [Theory]
    [InlineData("OutVariable")]
    [InlineData("PipelineVariable")]
    [InlineData("ErrorVariable")]
    [InlineData("WarningVariable")]
    [InlineData("InformationVariable")]
    public void New_module_variable_writers_invalidate_body_input(string parameter)
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            $"New-Module -ReturnResult {{ Write-Output $x }} -{parameter} x");

        Assert.False(result.IsUnparseable);
        var write = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Tokens[0] == "Write-Output");
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(write.EffectiveArguments).Value.Kind);
        Assert.False(write.IsComplete);
    }

    [Fact]
    public void New_module_writer_target_invalidates_body_command_resolution()
    {
        var result = ParseIsolated(
            "New-Module -ReturnResult { Compress-Archive a b } " +
            "-OutVariable PSModuleAutoLoadingPreference");

        Assert.False(result.IsUnparseable);
        var body = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Tokens[0] == "Compress-Archive");
        Assert.False(body.IsComplete);
    }

    [Fact]
    public void Module_qualified_new_module_invalidates_exported_function_continuation()
    {
        var result = ParseIsolated(
            "Microsoft.PowerShell.Core\\New-Module { " +
            "Set-Item Function:\\git -Value 'Remove-Item child.txt' }; " +
            "git child.txt");

        Assert.False(result.IsUnparseable);
        var continuation = result.Commands.Last();
        Assert.Equal("git", continuation.Clause.Verb.Tokens[0]);
        Assert.False(continuation.IsComplete);
    }

    [Fact]
    public void New_module_joins_body_outcomes_before_host_continuation()
    {
        var result = ParseIsolated(
            "New-Module { Set-Location /maybe } && Get-Item child.txt");

        var continuation = result.Commands.Last();
        Assert.Equal("Get-Item", continuation.Clause.Verb.Tokens[0]);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            continuation.WorkingDirectory.Kind);
        Assert.False(continuation.IsComplete);
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

    [Theory]
    [InlineData("Invoke-Command", "")]
    [InlineData("Invoke-Command -NoNewScope", "")]
    [InlineData(".", "")]
    [InlineData("&", "")]
    [InlineData("Measure-Command", "")]
    [InlineData("Trace-Command -Name ParameterBinding -Expression", " -PSHost")]
    public void Pipeline_stage_effects_make_synchronous_region_state_unknown(
        string invocation,
        string trailingArguments)
    {
        var result = ParseIsolated(
            "foreach ($x in 'start') { }; " + invocation + " { " +
            "Write-Output $x; Write-Output $x; foreach ($x in 'end') { } }" +
            trailingArguments + " | " +
            "Write-Output -OutVariable x");

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
    public void New_module_pipeline_state_mutation_fails_atomically()
    {
        var result = new PwshParser(new PwshParserOptions
        {
            WorkingDirectory = "C:/work",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
        }).Parse(
            "New-Module { Write-Output value } | Write-Output -OutVariable x");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
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
