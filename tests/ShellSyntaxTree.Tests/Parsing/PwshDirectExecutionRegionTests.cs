// -----------------------------------------------------------------------
// <copyright file="PwshDirectExecutionRegionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class PwshDirectExecutionRegionTests
{
    [Theory]
    [InlineData("& { Remove-Item target.txt }", ExecutionRegionOrigin.DirectCall)]
    [InlineData(". { Remove-Item target.txt }", ExecutionRegionOrigin.DotSource)]
    public void Direct_block_is_a_typed_region_without_a_synthetic_host(
        string source,
        ExecutionRegionOrigin origin)
    {
        var result = Parse(source);

        var region = Assert.IsType<ExecutionRegionSyntax>(
            Assert.Single(result.Syntax.Statements));
        Assert.Equal(origin, region.Origin);
        Assert.Null(region.HostClauseElementIndex);
        Assert.Equal(ExecutionRegionPhase.Main, region.Phase);
        Assert.Equal(ExecutionRegionTiming.Synchronous, region.Timing);
        Assert.Equal(ExecutionRegionCardinality.Once, region.Cardinality);
        Assert.Equal(source.IndexOf('{'), region.SourceStart);
        Assert.Equal("{ Remove-Item target.txt }".Length, region.SourceLength);

        var occurrence = Assert.Single(result.Commands);
        Assert.Equal("Remove-Item", Assert.Single(occurrence.Clause.Verb.Tokens));
        Assert.Equal(CommandOccurrenceRole.ExecutionRegion, occurrence.ImmediateRole);
        Assert.True(occurrence.IsComplete);
        Assert.Single(result.Clauses);
    }

    [Theory]
    [InlineData("& { Write-Output $args } alpha")]
    [InlineData(". { Write-Output $args } alpha")]
    [InlineData("& { param($x) Write-Output $x }")]
    [InlineData(". { param($x) Write-Output $x }")]
    [InlineData("& { $x = 'changed' }")]
    [InlineData(". { $x = 'changed'; Set-Location /tmp }")]
    public void Unsupported_direct_block_binding_fails_atomically(string source)
    {
        var result = Parser().Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Direct_call_isolates_binding_mutation_but_dot_source_shares_it()
    {
        var direct = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            "& { Write-Output inner -OutVariable x }; Write-Output $x");
        var dotSource = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            ". { Write-Output inner -OutVariable x }; Write-Output $x");

        var directContinuation = direct.Commands.Last();
        Assert.True(directContinuation.IsComplete);
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(directContinuation.EffectiveArguments).Value.Values);

        var dotSourceContinuation = dotSource.Commands.Last();
        Assert.False(dotSourceContinuation.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(dotSourceContinuation.EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("& { Set-Location /tmp }; Get-Item relative.txt")]
    [InlineData(". { Set-Location /tmp }; Get-Item relative.txt")]
    public void Direct_regions_propagate_location_outcomes(string source)
    {
        var result = ParseIsolated(source);

        var continuation = result.Commands.Last();
        Assert.True(continuation.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            continuation.WorkingDirectory.Kind);
        Assert.Contains(
            continuation.Clause.Args,
            argument => argument.IsCwdAttribution && argument.Raw == "<dynamic-cwd>");
    }

    [Fact]
    public void Dynamic_direct_call_body_invalidates_possible_escaping_state()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; & { & $command }; " +
            "Write-Output $x; Get-Item relative.txt");

        var variableContinuation = result.Commands[^2];
        Assert.False(variableContinuation.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(variableContinuation.EffectiveArguments).Value.Kind);

        var pathContinuation = result.Commands[^1];
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            pathContinuation.WorkingDirectory.Kind);
        Assert.Contains(
            pathContinuation.Clause.Args,
            argument => argument.IsCwdAttribution && argument.Raw == "<dynamic-cwd>");
    }

    [Fact]
    public void Direct_call_isolates_alias_mutation_but_dot_source_shares_it()
    {
        var direct = ParseIsolated(
            "& { Set-Alias zz Write-Output }; zz value");
        var dotSource = ParseIsolated(
            ". { Set-Alias zz Write-Output }; zz value");

        Assert.True(direct.Commands.Last().IsComplete);
        Assert.False(dotSource.Commands.Last().IsComplete);
    }

    [Theory]
    [InlineData("& { Set-Alias zz Write-Output -Scope Global }; zz value")]
    [InlineData("& { Set-Item Env:PATH C:/tools }; git status")]
    [InlineData("& { Set-Item Function:global:zz -Value 'Write-Output marker' }; zz")]
    [InlineData("& { Set-Item Function:prompt -Value 'Write-Output marker' }; prompt")]
    [InlineData("& { Set-Item -Path Alias:zz,Env:SST_SCOPE_TEST -Value changed }; git status")]
    [InlineData("& { Remove-Item Variable:x }; Write-Output $x")]
    [InlineData("& { Rename-Item Variable:x renamed }; Write-Output $x")]
    [InlineData("& { Remove-Item Alias:where }; where")]
    [InlineData("& { Rename-Item Alias:where renamed }; where")]
    [InlineData("& { Clear-Item Alias:where }; where")]
    [InlineData("& { Remove-Alias erase }; erase target.txt")]
    public void Explicit_child_scope_escape_invalidates_the_outer_continuation(
        string source)
    {
        var result = ParseIsolated(source);

        Assert.False(result.Commands.Last().IsComplete);
    }

    [Theory]
    [InlineData("Set-Item Variable:x changed")]
    [InlineData("New-Item Variable:x -Value changed -Force")]
    [InlineData("Clear-Item Variable:x")]
    [InlineData("Set-Content Variable:x changed")]
    public void Proved_child_local_variable_provider_mutation_does_not_escape(
        string mutation)
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            $"& {{ {mutation} }}; Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.True(continuation.IsComplete);
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(continuation.EffectiveArguments).Value.Values);
    }

    [Theory]
    [InlineData("Global")]
    [InlineData("Script")]
    [InlineData("1")]
    public void Escaping_variable_scope_invalidates_the_outer_binding(
        string scope)
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            $"& {{ Set-Variable x changed -Scope {scope} }}; Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.False(continuation.IsComplete);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(continuation.EffectiveArguments).Value.Kind);
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Private")]
    [InlineData("0")]
    [InlineData(":Local")]
    public void Child_local_variable_scope_does_not_escape(
        string scope)
    {
        var separator = scope.StartsWith(':') ? string.Empty : " ";
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            $"& {{ Set-Variable x changed -Scope{separator}{scope} }}; Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.True(continuation.IsComplete);
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(continuation.EffectiveArguments).Value.Values);
    }

    [Fact]
    public void Escaping_mutation_in_a_nested_child_host_remains_isolated()
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            "& { pwsh -Command 'Set-Variable x changed -Scope Global' }; " +
            "Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.True(continuation.IsComplete);
        Assert.Equal(
            new[] { "outer" },
            Assert.Single(continuation.EffectiveArguments).Value.Values);
    }

    [Theory]
    [InlineData("global:x", false)]
    [InlineData("script:x", false)]
    [InlineData("x", true)]
    public void Analyzed_dynamic_variable_name_respects_its_effective_scope(
        string name,
        bool expectedComplete)
    {
        var result = ParseIsolated(
            "foreach ($x in 'outer') { }; " +
            $"foreach ($name in '{name}') {{ " +
            "& { Set-Variable -Name $name -Value changed } }; " +
            "Write-Output $x");

        var continuation = result.Commands.Last();
        Assert.Equal(expectedComplete, continuation.IsComplete);
        if (expectedComplete)
        {
            Assert.Equal(
                new[] { "outer" },
                Assert.Single(continuation.EffectiveArguments).Value.Values);
        }
        else
        {
            Assert.Equal(
                ShellValueDomainKind.Unknown,
                Assert.Single(continuation.EffectiveArguments).Value.Kind);
        }
    }

    private static ParsedCommand Parse(string source)
    {
        var result = Parser(PwshInitialStateMode.IsolatedNonInteractiveNoProfile)
            .Parse(source);
        Assert.False(result.IsUnparseable, result.UnparseableReason);
        return result;
    }

    private static ParsedCommand ParseIsolated(string source)
    {
        var result = Parser(PwshInitialStateMode.IsolatedNonInteractiveNoProfile)
            .Parse(source);
        Assert.False(result.IsUnparseable, result.UnparseableReason);
        return result;
    }

    private static PwshParser Parser(
        PwshInitialStateMode initialStateMode = PwshInitialStateMode.Unknown) =>
        new(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
            InitialStateMode = initialStateMode,
        });
}
