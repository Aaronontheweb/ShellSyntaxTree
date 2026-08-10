// -----------------------------------------------------------------------
// <copyright file="PwshExecutionRegionBindingCatalogTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Linq;
using ShellSyntaxTree.Internal.Pwsh.Verbs;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class PwshExecutionRegionBindingCatalogTests
{
    private static readonly PwshParser Parser = new(new PwshParserOptions
    {
        HomeDirectory = "C:/Users/user",
        WorkingDirectory = "C:/work",
    });

    private static readonly PwshParser IsolatedParser = new(new PwshParserOptions
    {
        HomeDirectory = "C:/Users/user",
        WorkingDirectory = "C:/work",
        InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
    });

    [Theory]
    [InlineData("% { Get-Date }", "ForEachObject",
        "ForEachScriptBlock", ExecutionRegionPhase.Process,
        ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.OncePerInputObject)]
    [InlineData("? { Get-Date }", "WhereObject",
        "WhereScriptBlock", ExecutionRegionPhase.Filter,
        ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.OncePerInputObject)]
    [InlineData("icm { Get-Date }", "InvokeCommand",
        "InvokeInProcess", ExecutionRegionPhase.Main,
        ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.Once)]
    [InlineData("Measure-Command { Get-Date }", "MeasureCommand",
        "MeasureExpression", ExecutionRegionPhase.Main,
        ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.Once)]
    [InlineData("Trace-Command * { Get-Date }", "TraceCommand",
        "TraceExpression", ExecutionRegionPhase.Main,
        ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.Once)]
    [InlineData("sajb { Get-Date }", "StartJob",
        "StartJobScriptBlock", ExecutionRegionPhase.Main,
        ExecutionRegionTiming.Concurrent, ExecutionRegionCardinality.Once)]
    [InlineData("nmo { Get-Date }", "NewModule",
        "NewModuleScriptBlock", ExecutionRegionPhase.Initialization,
        ExecutionRegionTiming.Synchronous, ExecutionRegionCardinality.Once)]
    [InlineData("sbp -Variable x -Action { Get-Date }",
        "SetPSBreakpoint",
        "Breakpoint", ExecutionRegionPhase.Action,
        ExecutionRegionTiming.Deferred, ExecutionRegionCardinality.ZeroOrMore)]
    [InlineData("Register-ObjectEvent $source Changed subscription { Get-Date }",
        "RegisterObjectEvent",
        "ObjectEvent", ExecutionRegionPhase.Action,
        ExecutionRegionTiming.Deferred, ExecutionRegionCardinality.ZeroOrMore)]
    [InlineData("Register-EngineEvent source { Get-Date }",
        "RegisterEngineEvent",
        "EngineEvent", ExecutionRegionPhase.Action,
        ExecutionRegionTiming.Deferred, ExecutionRegionCardinality.ZeroOrMore)]
    [InlineData("Register-ArgumentCompleter -CommandName git -ParameterName x -ScriptBlock { Get-Date }",
        "RegisterArgumentCompleter",
        "ArgumentCompleter", ExecutionRegionPhase.Completion,
        ExecutionRegionTiming.Deferred, ExecutionRegionCardinality.ZeroOrMore)]
    public void Pinned_receivers_bind_their_script_block(
        string source,
        string receiver,
        string parameterSet,
        ExecutionRegionPhase phase,
        ExecutionRegionTiming timing,
        ExecutionRegionCardinality cardinality)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.Equal(receiver, result.Receiver.ToString());
        Assert.Equal(parameterSet, result.ParameterSet.ToString());
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(phase, binding.Phase);
        Assert.Equal(timing, binding.Timing);
        Assert.Equal(cardinality, binding.Cardinality);
        Assert.True(binding.IsComplete);
    }

    [Theory]
    [InlineData("& 'ForEach-Object' { Get-Date }")]
    [InlineData("& '%' { Get-Date }")]
    public void Static_command_spellings_share_the_canonical_receiver(string source)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.Equal(PwshExecutionRegionReceiver.ForEachObject, result.Receiver);
        Assert.Equal("ForEach-Object", result.CanonicalCommandName);
        Assert.Equal(ExecutionRegionPhase.Process, Assert.Single(result.Bindings).Phase);
    }

    [Fact]
    public void Module_qualified_catalog_lookup_uses_authored_identity()
    {
        var resolved = PwshExecutionRegionBindingCatalog.TryResolveStaticCommandName(
            "Microsoft.PowerShell.Core\\ForEach-Object",
            out var canonical,
            PwshDialect.PowerShell7);
        var conservative = Parser.Parse(
            "Microsoft.PowerShell.Core\\ForEach-Object { Remove-Item victim.txt }");
        var parsed = IsolatedParser.Parse(
            "Microsoft.PowerShell.Core\\ForEach-Object { Remove-Item victim.txt }");

        Assert.True(resolved);
        Assert.Equal("ForEach-Object", canonical);
        Assert.Equal(
            ExecutionRegionPhase.Process,
            Assert.Single(
                Assert.IsType<SimpleCommandSyntax>(
                    Assert.Single(conservative.Syntax.Statements))
                .ExecutionRegions).Phase);
        Assert.All(conservative.Commands, command => Assert.True(command.IsComplete));
        Assert.False(parsed.IsUnparseable);
        Assert.Equal(2, parsed.Commands.Count);
        Assert.Equal(2, parsed.Clauses.Count);
        var host = Assert.IsType<SimpleCommandSyntax>(Assert.Single(parsed.Syntax.Statements));
        var region = Assert.Single(host.ExecutionRegions);
        Assert.Equal(ExecutionRegionPhase.Process, region.Phase);
        Assert.All(parsed.Commands, command => Assert.True(command.IsComplete));
    }

    [Theory]
    [InlineData("ForEach-Object -Proc { Get-Date }")]
    [InlineData("ForEach-Object -Process:{ Get-Date }")]
    [InlineData("ForEach-Object -PROCESS { Get-Date }")]
    public void Exact_prefix_inline_and_case_forms_share_process_binding(string source)
    {
        var binding = Assert.Single(Bind(source).Bindings);

        Assert.Equal("Process", binding.CanonicalParameterName);
        Assert.Equal(ExecutionRegionPhase.Process, binding.Phase);
    }

    [Theory]
    [InlineData("ForEach-Object -ov captured { Get-Date }")]
    [InlineData("ForEach-Object -db { Get-Date }")]
    public void Common_parameter_aliases_preserve_the_positional_process_block(string source)
    {
        var binding = Assert.Single(Bind(source).Bindings);

        Assert.Equal(ExecutionRegionPhase.Process, binding.Phase);
    }

    [Theory]
    [InlineData("Where-Object -InputObject value -FilterScript { Get-Date }")]
    [InlineData("ForEach-Object -Inp value -Process { Get-Date }")]
    public void Explicit_input_object_binding_is_retained_for_cardinality_analysis(
        string source)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.True(result.HasExplicitInputObject);
    }

    [Fact]
    public void Parameter_alias_prefix_can_bind_the_script_block_parameter()
    {
        var result = Bind("Invoke-Command -Comm { Get-Date }");

        Assert.Equal(PwshExecutionRegionParameterSet.InvokeInProcess, result.ParameterSet);
        Assert.Equal("ScriptBlock", Assert.Single(result.Bindings).CanonicalParameterName);
    }

    [Fact]
    public void Ambiguous_parameter_prefix_keeps_every_block_unknown()
    {
        var result = Bind("ForEach-Object -Pro { Get-Date }");

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(ExecutionRegionPhase.Unknown, binding.Phase);
        Assert.False(binding.IsComplete);
    }

    [Fact]
    public void Positional_for_each_script_block_array_gets_begin_process_end_semantics()
    {
        var result = Bind(
            "ForEach-Object { Write-Output begin } { Write-Output process } " +
            "{ Write-Output remaining } { Write-Output end }");

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.Equal(
            new[]
            {
                ExecutionRegionPhase.Begin,
                ExecutionRegionPhase.Process,
                ExecutionRegionPhase.Process,
                ExecutionRegionPhase.End,
            },
            result.Bindings.Select(binding => binding.Phase));
        Assert.Equal(
            new[]
            {
                ExecutionRegionCardinality.Once,
                ExecutionRegionCardinality.OncePerInputObject,
                ExecutionRegionCardinality.OncePerInputObject,
                ExecutionRegionCardinality.Once,
            },
            result.Bindings.Select(binding => binding.Cardinality));
    }

    [Fact]
    public void Explicit_process_script_block_array_promotes_unbound_edges()
    {
        var result = Bind(
            "ForEach-Object -Process { Write-Output one }, { Write-Output two }, " +
            "{ Write-Output three }");

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.Equal(
            new[]
            {
                ExecutionRegionPhase.Begin,
                ExecutionRegionPhase.Process,
                ExecutionRegionPhase.End,
            },
            result.Bindings.Select(binding => binding.Phase));
    }

    [Theory]
    [InlineData(
        "ForEach-Object { Write-Output first } { Write-Output second }",
        "Begin,Process")]
    [InlineData(
        "ForEach-Object -Begin { Write-Output begin } " +
        "-Process { Write-Output first }, { Write-Output second }",
        "Begin,Process,End")]
    [InlineData(
        "ForEach-Object -End { Write-Output end } " +
        "-Process { Write-Output first }, { Write-Output second }",
        "End,Begin,Process")]
    [InlineData(
        "ForEach-Object -Begin { Write-Output begin } -End { Write-Output end } " +
        "-Process { Write-Output first }, { Write-Output second }",
        "Begin,End,Process,Process")]
    public void For_each_edge_promotion_accounts_for_explicit_begin_and_end(
        string source,
        string expectedPhases)
    {
        var result = Bind(source);

        Assert.Equal(
            expectedPhases.Split(','),
            result.Bindings.Select(binding => binding.Phase.ToString()));
    }

    [Fact]
    public void Process_and_remaining_scripts_share_one_authored_edge_promotion_sequence()
    {
        var result = Bind(
            "ForEach-Object -RemainingScripts { Write-Output first }, " +
            "{ Write-Output second } -Process { Write-Output third }");

        Assert.Equal(
            new[]
            {
                ExecutionRegionPhase.Begin,
                ExecutionRegionPhase.Process,
                ExecutionRegionPhase.End,
            },
            result.Bindings.Select(binding => binding.Phase));
    }

    [Fact]
    public void Named_for_each_blocks_keep_authored_order_and_semantic_phases()
    {
        var result = Bind(
            "ForEach-Object -End { Write-Output end } " +
            "-Begin { Write-Output begin } -Process { Write-Output process }");

        Assert.Equal(
            new[]
            {
                ExecutionRegionPhase.End,
                ExecutionRegionPhase.Begin,
                ExecutionRegionPhase.Process,
            },
            result.Bindings.Select(binding => binding.Phase));
        Assert.True(result.Bindings.Select(binding => binding.HostClauseElementIndex)
            .SequenceEqual(result.Bindings.Select(binding => binding.HostClauseElementIndex)
                .OrderBy(index => index)));
    }

    [Fact]
    public void Parallel_parameter_selects_concurrent_parameter_set()
    {
        var result = Bind("ForEach-Object -Parallel { Get-Date } -AsJob");

        Assert.Equal(PwshExecutionRegionParameterSet.ForEachParallel, result.ParameterSet);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(ExecutionRegionTiming.Concurrent, binding.Timing);
        Assert.Equal(ExecutionRegionCardinality.OncePerInputObject, binding.Cardinality);
        Assert.False(result.HasUseNewRunspace);

        var fresh = Bind(
            "ForEach-Object -Parallel { Get-Date } -UseNewRunspace");
        Assert.True(fresh.HasUseNewRunspace);
    }

    [Fact]
    public void Invoke_command_distinguishes_in_process_and_remote_parameter_sets()
    {
        var local = Bind("Invoke-Command -ScriptBlock { Get-Date } -NoNewScope");
        var remote = Bind(
            "Invoke-Command -ComputerName server -ScriptBlock { Get-Date } -AsJob");
        var inlineRemote = Bind(
            "Invoke-Command -ComputerName:server -ScriptBlock:{ Get-Date }");
        var aliasRemote = Bind("Invoke-Command -Cn server -Command { Get-Date }");
        var positionalRemote = Bind("Invoke-Command server { Get-Date }");

        Assert.Equal(PwshExecutionRegionParameterSet.InvokeInProcess, local.ParameterSet);
        Assert.True(Assert.Single(local.Bindings).IsComplete);
        Assert.True(local.HasNoNewScope);
        var positionalLocal = Bind("Invoke-Command { Get-Date }");
        Assert.Equal(
            PwshExecutionRegionParameterSet.InvokeInProcess,
            positionalLocal.ParameterSet);
        Assert.False(positionalLocal.HasNoNewScope);
        Assert.False(Bind(
            "Invoke-Command -NoNewScope:$false { Get-Date }").HasNoNewScope);
        Assert.True(Bind(
            "Invoke-Command -NoNewScope:$true { Get-Date }").HasNoNewScope);
        Assert.False(Bind(
            "Invoke-Command -NoNewScope:0 { Get-Date }").HasNoNewScope);
        Assert.True(Bind(
            "Invoke-Command -NoNewScope:1 { Get-Date }").HasNoNewScope);
        Assert.Equal(PwshExecutionRegionParameterSet.InvokeRemote, remote.ParameterSet);
        var remoteBinding = Assert.Single(remote.Bindings);
        Assert.Equal(ExecutionRegionTiming.Concurrent, remoteBinding.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, remoteBinding.Cardinality);
        Assert.True(remoteBinding.IsComplete);
        Assert.Equal(PwshExecutionRegionParameterSet.InvokeRemote, inlineRemote.ParameterSet);
        Assert.True(Assert.Single(inlineRemote.Bindings).IsComplete);
        Assert.Equal(PwshExecutionRegionParameterSet.InvokeRemote, aliasRemote.ParameterSet);
        Assert.True(Assert.Single(aliasRemote.Bindings).IsComplete);
        Assert.Equal(PwshExecutionRegionParameterSet.InvokeRemote, positionalRemote.ParameterSet);
        Assert.True(Assert.Single(positionalRemote.Bindings).IsComplete);
    }

    [Theory]
    [InlineData("Invoke-Command server -ScriptBlock { Get-Date }")]
    [InlineData("Invoke-Command -Command { Get-Date } server")]
    public void Mixed_named_and_positional_remote_targets_bind_as_single_remote_targets(
        string source)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionParameterSet.InvokeRemote, result.ParameterSet);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(ExecutionRegionTiming.Synchronous, binding.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, binding.Cardinality);
        Assert.True(binding.IsComplete);
    }

    [Theory]
    [InlineData(
        "Invoke-Command -ComputerName server -ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Synchronous,
        ExecutionRegionCardinality.Once)]
    [InlineData(
        "Invoke-Command -ComputerName server -AsJob -ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Concurrent,
        ExecutionRegionCardinality.Once)]
    [InlineData(
        "Invoke-Command -ComputerName server -InDisconnectedSession " +
        "-ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Concurrent,
        ExecutionRegionCardinality.Once)]
    [InlineData(
        "Invoke-Command -ComputerName server -AsJob:$false " +
        "-ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Synchronous,
        ExecutionRegionCardinality.Once)]
    [InlineData(
        "Invoke-Command -ComputerName server -InDisconnectedSession:$false " +
        "-ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Synchronous,
        ExecutionRegionCardinality.Once)]
    [InlineData(
        "Invoke-Command -ComputerName 'server1,server2' " +
        "-ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Synchronous,
        ExecutionRegionCardinality.Once)]
    [InlineData(
        "Invoke-Command -ComputerName server1`,server2 " +
        "-ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Synchronous,
        ExecutionRegionCardinality.Once)]
    [InlineData(
        "Invoke-Command -ComputerName server1,server2 -ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Concurrent,
        ExecutionRegionCardinality.Unknown)]
    [InlineData(
        "Invoke-Command -ComputerName server1, server2 -ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Concurrent,
        ExecutionRegionCardinality.Unknown)]
    [InlineData(
        "Invoke-Command server1, server2 -ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Concurrent,
        ExecutionRegionCardinality.Unknown)]
    [InlineData(
        "Invoke-Command -ComputerName:server1, server2 " +
        "-ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Concurrent,
        ExecutionRegionCardinality.Unknown)]
    [InlineData(
        "Invoke-Command -ComputerName $servers -ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Unknown,
        ExecutionRegionCardinality.Unknown)]
    [InlineData(
        "Invoke-Command -Session $session -ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Unknown,
        ExecutionRegionCardinality.Unknown)]
    [InlineData(
        "Invoke-Command -Session $session -AsJob -ScriptBlock { Get-Date }",
        ExecutionRegionTiming.Concurrent,
        ExecutionRegionCardinality.Unknown)]
    public void Remote_targets_publish_only_proved_scheduling_facts(
        string source,
        ExecutionRegionTiming expectedTiming,
        ExecutionRegionCardinality expectedCardinality)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.Equal(PwshExecutionRegionParameterSet.InvokeRemote, result.ParameterSet);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(expectedTiming, binding.Timing);
        Assert.Equal(expectedCardinality, binding.Cardinality);
        Assert.True(binding.IsComplete);
    }

    [Theory]
    [InlineData(
        "Invoke-Command -ConnectionUri https://example.invalid/wsman " +
        "-ScriptBlock { Get-Date }")]
    [InlineData(
        "Invoke-Command -HostName example.invalid -ScriptBlock { Get-Date }")]
    [InlineData(
        "Invoke-Command -VMId 8a9d9e75-0ec0-4e0d-948a-a0d876ccf995 " +
        "-Credential $credential -ScriptBlock { Get-Date }")]
    [InlineData(
        "Invoke-Command -VMName vm01 -Credential $credential " +
        "-ScriptBlock { Get-Date }")]
    [InlineData(
        "Invoke-Command -ContainerId container01 -ScriptBlock { Get-Date }")]
    [InlineData(
        "Invoke-Command -SSHConnection @{HostName='example.invalid'} " +
        "-ScriptBlock { Get-Date }")]
    [InlineData(
        "Invoke-Command -SSHConnection @{HostName='server,corp'} " +
        "-ScriptBlock { Get-Date }")]
    public void Remote_target_families_share_the_isolated_single_target_contract(
        string source)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.Equal(PwshExecutionRegionParameterSet.InvokeRemote, result.ParameterSet);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(ExecutionRegionTiming.Synchronous, binding.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, binding.Cardinality);
        Assert.True(binding.IsComplete);
    }

    [Fact]
    public void Missing_value_before_another_parameter_keeps_script_block_binding_unknown()
    {
        var result = Bind("ForEach-Object -Begin -Process { Get-Date }");

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        Assert.False(Assert.Single(result.Bindings).IsComplete);
    }

    [Theory]
    [InlineData("Invoke-Command -Com server -ScriptBlock { Get-Date }")]
    [InlineData("Invoke-Command -Port 22 -ScriptBlock { Get-Date }")]
    [InlineData("Where-Object -FilterScript { Get-Date } -Property Name")]
    [InlineData("ForEach-Object -Process { Get-Date } -Process { Get-Item }")]
    [InlineData("ForEach-Object -Parallel { Get-Date } -Begin { Get-Item }")]
    [InlineData("Trace-Command -Expression { Get-Date } -Command Get-Date")]
    public void Ambiguous_aliases_and_parameter_set_conflicts_remain_unknown(string source)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        Assert.All(result.Bindings, binding => Assert.False(binding.IsComplete));
    }

    [Theory]
    [InlineData("Where-Object -FilterScript { Get-Date } -Value x")]
    [InlineData("Trace-Command -Name * -Expression { Get-Date } -ArgumentList x")]
    [InlineData("Start-Job -ScriptBlock { Get-Date } -ConnectingTimeout 1")]
    [InlineData("Set-PSBreakpoint -Action { Get-Date }")]
    [InlineData("Register-ObjectEvent -Action { Get-Date }")]
    [InlineData("Register-EngineEvent -Action { Get-Date }")]
    [InlineData(
        "Register-ArgumentCompleter -NativeFallback -CommandName git " +
        "-ScriptBlock { Get-Date }")]
    public void Incompatible_or_incomplete_parameter_sets_remain_unknown(string source)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        Assert.All(result.Bindings, binding => Assert.False(binding.IsComplete));
    }

    [Theory]
    [InlineData("ForEach-Object --Process { Get-Date }")]
    [InlineData("ForEach-Object -Begin { Get-Date }, { Get-Item }")]
    [InlineData("ForEach-Object -End { Get-Date }, { Get-Item }")]
    [InlineData("Start-Job { Write-Output main }, { Write-Output init }")]
    [InlineData("Start-Job -ScriptBlock { Write-Output main } not-a-scriptblock")]
    [InlineData(
        "Start-Job -ScriptBlock { Write-Output main } " +
        "-InitializationScript not-a-scriptblock")]
    [InlineData("ForEach-Object -Process { Get-Date } -Debug:notbool")]
    [InlineData("ForEach-Object -Parallel { Get-Date } -TimeoutSeconds:notint")]
    [InlineData(
        "Trace-Command -Name ParameterBinding -Expression { Get-Date } " +
        "-Option:notoption -PSHost")]
    [InlineData("ForEach-Object -Parallel { Get-Date } -TimeoutSeconds notint")]
    [InlineData("ForEach-Object -Process { Get-Date } -Debug:true")]
    [InlineData("ForEach-Object -Process { Get-Date } -Debug:false")]
    [InlineData("ForEach-Object -Process { Get-Date } -Debug:")]
    [InlineData(
        "Trace-Command -Name ParameterBinding -Expression { Get-Date } " +
        "-Option \"\" -PSHost")]
    [InlineData("Trace-Command ParameterBinding { Get-Date } \"\" -PSHost")]
    [InlineData(
        "Trace-Command -Name ParameterBinding -Expression { Get-Date } " +
        "-Option \"Error,\" -PSHost")]
    [InlineData("ForEach-Object -Parallel { Get-Date } -TimeoutSeconds:-1")]
    [InlineData("ForEach-Object -Parallel { Get-Date } -ThrottleLimit:0")]
    [InlineData("ForEach-Object -Parallel { Get-Date } -ThrottleLimit:-1")]
    [InlineData(
        "Set-PSBreakpoint -Script ./script.ps1 -Line 0 -Action { Get-Date }")]
    [InlineData("Start-Job -ScriptBlock { Get-Date } -PSVersion 7.6.4")]
    [InlineData("ForEach-Object -Process { Get-Date } -ErrorAction Suspend")]
    [InlineData("ForEach-Object -Process { Get-Date } -ErrorVariable \"\"")]
    [InlineData("Start-Job -WorkingDirectory \" \" -ScriptBlock { Get-Date }")]
    [InlineData("New-Module -ScriptBlock { Get-Date } -Cmdlet $null")]
    [InlineData(
        "Invoke-Command -HostName server -Options @{} -ScriptBlock { Get-Date }")]
    [InlineData(
        "Invoke-Command -HostName example.invalid -ScriptBlock { Get-Date } " +
        "-SSHTransport:$false")]
    [InlineData("Start-Job -ScriptBlock { Get-Date } -Authentication Basic")]
    [InlineData("Start-Job -ScriptBlock { Get-Date } -RunAs32")]
    [InlineData(
        "ForEach-Object -Parallel { Get-Date } -AsJob -TimeoutSeconds 1")]
    [InlineData(
        "Register-EngineEvent -SourceIdentifier source -Action { Get-Date } -Forward")]
    [InlineData(
        "Register-ObjectEvent -InputObject $source -EventName Changed " +
        "-Action { Get-Date } -Forward")]
    public void Unsupported_parameter_spelling_and_scalar_arrays_remain_unknown(string source)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        Assert.All(result.Bindings, binding => Assert.False(binding.IsComplete));
    }

    [Theory]
    [InlineData("ForEach-Object -Process { Get-Date } -Debug:$false")]
    [InlineData("ForEach-Object -Parallel { Get-Date } -TimeoutSeconds:5")]
    [InlineData(
        "Trace-Command -Name ParameterBinding -Expression { Get-Date } " +
        "-Option:ExecutionFlow -PSHost")]
    [InlineData("Trace-Command ParameterBinding { Get-Date } ExecutionFlow -PSHost")]
    [InlineData(
        "ForEach-Object -Parallel { Get-Date } -TimeoutSeconds:0 -ThrottleLimit:1")]
    [InlineData(
        "Set-PSBreakpoint -Script ./script.ps1 -Line 1 -Action { Get-Date }")]
    [InlineData("Start-Job -ScriptBlock { Get-Date } -PSVersion 5.1")]
    [InlineData("Start-Job -ScriptBlock { Get-Date } -Authentication Default")]
    public void Proved_literal_value_conversions_preserve_execution_binding(string source)
    {
        var result = Bind(source);

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.All(result.Bindings, binding => Assert.True(binding.IsComplete));
    }

    [Fact]
    public void Ssh_transport_true_value_preserves_remote_binding()
    {
        var result = Bind(
            "Invoke-Command -HostName example.invalid -ScriptBlock { Get-Date } " +
            "-SSHTransport:$true");

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
        Assert.Equal(PwshExecutionRegionParameterSet.InvokeRemote, result.ParameterSet);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(ExecutionRegionTiming.Synchronous, binding.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, binding.Cardinality);
        Assert.True(binding.IsComplete);
    }

    [Fact]
    public void As_job_without_remote_target_does_not_invent_local_job_semantics()
    {
        var result = Bind("Invoke-Command -AsJob -ScriptBlock { Get-Date }");

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        Assert.False(Assert.Single(result.Bindings).IsComplete);
    }

    [Fact]
    public void Start_job_binds_main_and_initialization_positions_independently()
    {
        var result = Bind("Start-Job { Write-Output main } { Write-Output init }");

        Assert.Equal(
            new[] { ExecutionRegionPhase.Main, ExecutionRegionPhase.Initialization },
            result.Bindings.Select(binding => binding.Phase));
        Assert.All(result.Bindings, binding =>
            Assert.Equal(ExecutionRegionTiming.Concurrent, binding.Timing));
    }

    [Theory]
    [InlineData("Start-Job -WorkingDirectory /tmp -ScriptBlock { Get-Date }", "/tmp")]
    [InlineData("Start-Job -WorkingD /var/tmp -ScriptBlock { Get-Date }", "/var/tmp")]
    [InlineData("Start-Job -WorkingDirectory:/opt -ScriptBlock { Get-Date }", "/opt")]
    [InlineData(
        "Start-Job -WorkingDirectory:' /tmp ' -ScriptBlock { Get-Date }",
        " /tmp ")]
    [InlineData(
        "Start-Job -WorkingDirectory:\"x$HOME\" -ScriptBlock { Get-Date }",
        "x$HOME")]
    public void Start_job_retains_the_bound_working_directory_coordinate(
        string source,
        string expectedValue)
    {
        var clause = ParseClause(source);
        var result = PwshExecutionRegionBindingCatalog.Bind(
            clause,
            commandIdentityProven: true,
            dialect: PwshDialect.PowerShell7);

        var elementIndex = Assert.IsType<int>(result.WorkingDirectoryElementIndex);
        Assert.EndsWith(expectedValue, clause.Elements[elementIndex].Value);
        Assert.Equal(
            expectedValue,
            clause.Elements[elementIndex].Value.Substring(
                result.WorkingDirectoryValueOffset));
        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
    }

    [Fact]
    public void Non_job_receivers_do_not_publish_a_working_directory_coordinate()
    {
        var result = Bind("Invoke-Command -ScriptBlock { Get-Date }");

        Assert.Null(result.WorkingDirectoryElementIndex);
        Assert.Equal(0, result.WorkingDirectoryValueOffset);
    }

    [Theory]
    [InlineData("Start-Job -PSVersion 5.1 -ScriptBlock { Get-Date }")]
    [InlineData("Start-Job -PSVersion:5.1 -ScriptBlock { Get-Date }")]
    public void Start_job_retains_an_explicit_child_version_boundary(string source)
    {
        var result = Bind(source);

        Assert.True(result.HasExplicitPSVersion);
        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, result.Status);
    }

    [Fact]
    public void Default_start_job_does_not_invent_an_explicit_child_version()
    {
        var result = Bind("Start-Job -ScriptBlock { Get-Date }");

        Assert.False(result.HasExplicitPSVersion);
    }

    [Fact]
    public void Named_primary_parameters_shift_positional_script_block_slots()
    {
        var trace = Bind("Trace-Command -Name * { Get-Date }");
        var job = Bind("Start-Job -ScriptBlock { Get-Date } { Write-Output init }");
        var fileJob = Bind("Start-Job -FilePath script.ps1 { Write-Output init }");
        var engineEvent = Bind("Register-EngineEvent -SourceIdentifier source { Get-Date }");
        var objectEvent = Bind(
            "Register-ObjectEvent -InputObject $source -EventName Changed " +
            "-SourceIdentifier subscription { Get-Date }");

        Assert.Equal(ExecutionRegionPhase.Main, Assert.Single(trace.Bindings).Phase);
        Assert.Equal(
            new[] { ExecutionRegionPhase.Main, ExecutionRegionPhase.Initialization },
            job.Bindings.Select(binding => binding.Phase));
        Assert.Equal(
            ExecutionRegionPhase.Initialization,
            Assert.Single(fileJob.Bindings).Phase);
        Assert.Equal(ExecutionRegionPhase.Action, Assert.Single(engineEvent.Bindings).Phase);
        Assert.Equal(ExecutionRegionPhase.Action, Assert.Single(objectEvent.Bindings).Phase);
    }

    [Fact]
    public void New_module_name_parameter_does_not_displace_its_script_block()
    {
        var result = Bind("New-Module -Name demo { Get-Date }");

        Assert.Equal(ExecutionRegionPhase.Initialization, Assert.Single(result.Bindings).Phase);
    }

    [Fact]
    public void Canonical_write_output_script_block_is_proved_opaque_data()
    {
        var result = Bind("echo { Remove-Item victim.txt }");

        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedData, result.Status);
        Assert.Equal(PwshExecutionRegionReceiver.WriteOutput, result.Receiver);
        Assert.Empty(result.Bindings);
    }

    [Fact]
    public void Unknown_receiver_keeps_body_visible_with_unknown_binding_facts()
    {
        var result = Bind("Invoke-CustomAction { Remove-Item victim.txt }");

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        Assert.Equal(PwshExecutionRegionReceiver.Unknown, result.Receiver);
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(ExecutionRegionTiming.Unknown, binding.Timing);
        Assert.False(binding.IsComplete);
    }

    [Fact]
    public void Unproved_command_identity_cannot_claim_receiver_semantics_or_data()
    {
        var executingClause = ParseClause("ForEach-Object { Remove-Item victim.txt }");
        var dataClause = ParseClause("Write-Output { Remove-Item victim.txt }");

        var executing = PwshExecutionRegionBindingCatalog.Bind(
            executingClause,
            commandIdentityProven: false,
            dialect: PwshDialect.PowerShell7);
        var data = PwshExecutionRegionBindingCatalog.Bind(
            dataClause,
            commandIdentityProven: false,
            dialect: PwshDialect.PowerShell7);

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, executing.Status);
        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, data.Status);
        Assert.False(Assert.Single(executing.Bindings).IsComplete);
        Assert.False(Assert.Single(data.Bindings).IsComplete);
    }

    [Fact]
    public void Thread_job_requires_the_explicit_pinned_module_baseline()
    {
        var clause = ParseClause("Start-ThreadJob { Get-Date }");

        var unpinned = PwshExecutionRegionBindingCatalog.Bind(
            clause,
            commandIdentityProven: true,
            dialect: PwshDialect.PowerShell7,
            threadJobModuleProven: false);
        var pinned = PwshExecutionRegionBindingCatalog.Bind(
            clause,
            commandIdentityProven: true,
            dialect: PwshDialect.PowerShell7,
            threadJobModuleProven: true);

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, unpinned.Status);
        Assert.False(Assert.Single(unpinned.Bindings).IsComplete);
        Assert.Equal(PwshExecutionRegionBindingStatus.ProvedExecution, pinned.Status);
        Assert.Equal(PwshExecutionRegionReceiver.StartThreadJob, pinned.Receiver);
        var binding = Assert.Single(pinned.Bindings);
        Assert.Equal(ExecutionRegionTiming.Concurrent, binding.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, binding.Cardinality);
    }

    [Fact]
    public void Pinned_thread_job_still_requires_a_main_script_or_file()
    {
        var clause = ParseClause(
            "Start-ThreadJob -InitializationScript { Write-Output init }");

        var result = PwshExecutionRegionBindingCatalog.Bind(
            clause,
            commandIdentityProven: true,
            dialect: PwshDialect.PowerShell7,
            threadJobModuleProven: true);

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        Assert.False(Assert.Single(result.Bindings).IsComplete);
    }

    [Theory]
    [InlineData("Start-ThreadJob -Name \"\" -ScriptBlock { Get-Date }")]
    [InlineData(
        "Start-ThreadJob -FilePath \"\" " +
        "-InitializationScript { Write-Output init }")]
    public void Pinned_thread_job_rejects_invalid_required_string_values(string source)
    {
        var result = PwshExecutionRegionBindingCatalog.Bind(
            ParseClause(source),
            commandIdentityProven: true,
            dialect: PwshDialect.PowerShell7,
            threadJobModuleProven: true);

        Assert.Equal(PwshExecutionRegionBindingStatus.Ambiguous, result.Status);
        Assert.All(result.Bindings, binding => Assert.False(binding.IsComplete));
    }

    [Fact]
    public void Wrong_module_qualification_remains_atomically_unparseable()
    {
        var parsed = Parser.Parse("Contoso.Tools\\ForEach-Object { Get-Date }");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
        Assert.Contains("module-qualified cmdlet", parsed.UnparseableReason);
    }

    private static PwshExecutionRegionBindingResult Bind(string source)
    {
        var clause = ParseClause(source);
        var result = PwshExecutionRegionBindingCatalog.Bind(
            clause,
            commandIdentityProven: true,
            dialect: PwshDialect.PowerShell7);
        Assert.All(result.Bindings, binding =>
        {
            Assert.InRange(binding.HostClauseElementIndex, 0, clause.Elements.Count - 1);
            var host = clause.Elements[binding.HostClauseElementIndex];
            Assert.Equal(ClauseElementRole.Argument, host.Role);
            Assert.Equal(ArgKind.DynamicSkip, host.Kind);
        });
        return result;
    }

    private static Clause ParseClause(string source)
    {
        var parsed = Parser.Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        return parsed.Clauses.Last(clause => clause.Elements.Any(element =>
            element.Kind == ArgKind.DynamicSkip && element.Raw.Contains('{')));
    }
}
