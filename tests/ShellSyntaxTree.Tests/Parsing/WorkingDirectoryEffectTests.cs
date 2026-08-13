// -----------------------------------------------------------------------
// <copyright file="WorkingDirectoryEffectTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class WorkingDirectoryEffectTests
{
    [Fact]
    public void Bash_ordinary_command_preserves_working_directory()
    {
        var command = Assert.Single(ParseBash("inspect artifact").Commands);

        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            command.WorkingDirectoryEffect);
    }

    [Theory]
    [InlineData("cd /tmp")]
    [InlineData("command cd /tmp")]
    [InlineData("builtin cd /tmp")]
    public void Bash_static_cd_changes_exactly_on_success(string source)
    {
        var command = Assert.Single(ParseBash(source).Commands);

        AssertExactChange(command, "/tmp");
    }

    [Theory]
    [InlineData("cd /tmp extra")]
    [InlineData("cd -z /tmp")]
    public void Bash_failure_only_cd_shape_is_unchanged(string source)
    {
        var command = Assert.Single(ParseBash(source).Commands);

        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            command.WorkingDirectoryEffect);
    }

    [Theory]
    [InlineData("pushd /tmp")]
    [InlineData("popd")]
    public void Bash_directory_stack_effect_is_unknown(string source)
    {
        var command = Assert.Single(ParseBash(source).Commands);

        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            command.WorkingDirectoryEffect);
    }

    [Fact]
    public void Bash_directory_stack_counterexample_keeps_later_scope_unknown()
    {
        var result = ParseBash("cd /tmp && pushd /other; head file");

        AssertExactChange(result.Commands[0], "/tmp");
        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            result.Commands[1].WorkingDirectoryEffect);
        Assert.IsType<ShellValueDomain.Unknown>(result.Commands[2].WorkingDirectory);
        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            result.Commands[2].WorkingDirectoryEffect);
    }

    [Theory]
    [InlineData("source setup.sh")]
    [InlineData(". setup.sh")]
    [InlineData("eval 'cd /tmp'")]
    public void Bash_hidden_current_scope_execution_stays_atomic(string source)
    {
        var result = ParseBash(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Bash_chdir_is_not_a_parent_shell_transfer()
    {
        var result = ParseBash("chdir /tmp; head file");

        Assert.Equal(2, result.Commands.Count);
        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            result.Commands[0].WorkingDirectoryEffect);
        Assert.Equal(
            "/work",
            Assert.IsType<ShellValueDomain.Exact>(
                result.Commands[1].WorkingDirectory).Value);
    }

    [Fact]
    public void Bash_finite_loop_cd_target_joins_under_isolated_state()
    {
        var result = ParseBash(
            "for d in /work/a /work/b; do cd \"$d\"; done");
        var command = Assert.Single(result.Commands);
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            command.WorkingDirectoryEffect);
        var target = Assert.IsType<ShellValueDomain.FiniteSet>(effect.Target);

        Assert.Equal(new[] { "/work/a", "/work/b" }, target.Values);
    }

    [Fact]
    public void Bash_mixed_finite_cd_targets_join_to_unknown_target()
    {
        var result = ParseBash(
            "for d in /tmp -; do cd \"$d\"; done");
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            Assert.Single(result.Commands).WorkingDirectoryEffect);

        Assert.IsType<ShellValueDomain.Unknown>(effect.Target);
    }

    [Fact]
    public void Bash_dynamic_cd_target_keeps_success_target_unknown()
    {
        var command = Assert.Single(ParseBash("cd \"$1\"").Commands);
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            command.WorkingDirectoryEffect);

        Assert.IsType<ShellValueDomain.Unknown>(effect.Target);
    }

    [Fact]
    public void Bash_pipeline_stage_effect_does_not_claim_parent_transfer()
    {
        var result = ParseBash("printf x | cd /tmp; pwd");

        AssertExactChange(result.Commands[1], "/tmp");
        Assert.IsType<ShellValueDomain.Unknown>(result.Commands[2].WorkingDirectory);
    }

    [Fact]
    public void Bash_first_pipeline_stage_effect_does_not_reach_parent()
    {
        var result = ParseBash("cd /tmp | cat; pwd");

        AssertExactChange(result.Commands[0], "/tmp");
        Assert.Equal(
            "/work",
            Assert.IsType<ShellValueDomain.Exact>(
                result.Commands.Last().WorkingDirectory).Value);
    }

    [Fact]
    public void Bash_decoded_child_shell_keeps_nested_effect_in_child_ancestry()
    {
        var result = ParseBash("bash -c 'cd /tmp; pwd'; pwd");
        var nested = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "cd");

        AssertExactChange(nested, "/tmp");
        Assert.Contains(nested.Ancestry, frame =>
            frame.Region == CommandAncestryRegion.GroupBody &&
            frame.Ancestor is GroupSyntax
            {
                GroupKind: ShellGroupKind.IsolatedScope,
            });
        Assert.Equal(
            "/work",
            Assert.IsType<ShellValueDomain.Exact>(
                result.Commands.Last().WorkingDirectory).Value);
    }

    [Fact]
    public void Bash_proved_empty_loop_has_unknown_body_effect()
    {
        var result = ParseBash("for d in; do cd /tmp; done");

        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            Assert.Single(result.Commands).WorkingDirectoryEffect);
    }

    [Fact]
    public void Bash_substitution_effect_is_local_to_its_ancestry()
    {
        var result = ParseBash("echo \"$(cd /tmp && pwd)\"; pwd");
        var innerCd = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "cd");
        var outerPwd = result.Commands.Last();

        AssertExactChange(innerCd, "/tmp");
        Assert.Contains(innerCd.Ancestry, frame =>
            frame.Region == CommandAncestryRegion.Substitution);
        Assert.Equal(
            "/work",
            Assert.IsType<ShellValueDomain.Exact>(outerPwd.WorkingDirectory).Value);
    }

    [Fact]
    public void Bash_parenthesized_subshell_effect_is_local_to_its_ancestry()
    {
        var result = ParseBash("(cd /tmp; pwd); pwd");
        var innerCd = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "cd");

        AssertExactChange(innerCd, "/tmp");
        Assert.Contains(innerCd.Ancestry, frame =>
            frame.Region == CommandAncestryRegion.GroupBody &&
            frame.Ancestor is GroupSyntax
            {
                GroupKind: ShellGroupKind.IsolatedScope,
            });
        Assert.Equal(
            "/work",
            Assert.IsType<ShellValueDomain.Exact>(
                result.Commands.Last().WorkingDirectory).Value);
    }

    [Fact]
    public void PowerShell_ordinary_command_preserves_location()
    {
        var command = Assert.Single(ParsePowerShell("Get-Content file.txt").Commands);

        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            command.WorkingDirectoryEffect);
    }

    [Theory]
    [InlineData("Set-Location C:\\temp")]
    [InlineData("Set-Location -Lit C:\\temp")]
    [InlineData("Set-Location -LP C:\\temp")]
    [InlineData("Set-Location -PSPath C:\\temp")]
    [InlineData("Set-Location -ps C:\\temp")]
    [InlineData("Set-Location -psp C:\\temp")]
    [InlineData("cd C:\\temp")]
    [InlineData("chdir C:\\temp")]
    [InlineData("sl C:\\temp")]
    public void PowerShell_set_location_and_aliases_change_on_success(string source)
    {
        var command = Assert.Single(ParsePowerShell(source).Commands);

        AssertExactChange(command, "C:/temp");
    }

    [Theory]
    [InlineData("Set-Location -Verbose C:\\temp")]
    [InlineData("Set-Location -ErrorAction Stop C:\\temp")]
    public void PowerShell_common_parameters_preserve_exact_location_target(string source)
    {
        var command = Assert.Single(ParsePowerShell(source).Commands);

        AssertExactChange(command, "C:/temp");
    }

    [Theory]
    [InlineData("Set-Location -Verbose C:\\temp && Get-Location")]
    [InlineData("Set-Location -Verbose:$true C:\\temp && Get-Location")]
    [InlineData("Set-Location -Verbose:$false C:\\temp && Get-Location")]
    [InlineData("Set-Location -ErrorAction Stop C:\\temp && Get-Location")]
    [InlineData("Set-Location -psp C:\\temp && Get-Location")]
    public void PowerShell_effect_and_following_flow_share_parameter_binding(
        string source)
    {
        var result = ParsePowerShell(source);

        AssertExactChange(result.Commands[0], "C:/temp");
        Assert.Equal(
            "C:/temp",
            Assert.IsType<ShellValueDomain.Exact>(
                result.Commands[1].WorkingDirectory).Value);
    }

    [Theory]
    [InlineData("Set-Location C:\\temp C:\\other")]
    [InlineData("Set-Location -Path")]
    [InlineData("Set-Location -Unsupported C:\\temp")]
    public void PowerShell_failure_only_set_location_shape_is_unchanged(string source)
    {
        var command = Assert.Single(ParsePowerShell(source).Commands);

        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            command.WorkingDirectoryEffect);
    }

    [Theory]
    [InlineData("Set-Location -Unsupported C:\\temp; Get-Location")]
    [InlineData("Set-Location -Verbose:no C:\\temp; Get-Location")]
    public void PowerShell_failure_only_binding_preserves_following_flow(string source)
    {
        var result = ParsePowerShell(source);

        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            result.Commands[0].WorkingDirectoryEffect);
        Assert.Equal(
            "C:/work",
            Assert.IsType<ShellValueDomain.Exact>(
                result.Commands[1].WorkingDirectory).Value);
    }

    [Theory]
    [InlineData("Set-Location -Verbose:$switch C:\\temp && Get-Location")]
    [InlineData("Set-Location -PassThru:$switch C:\\temp && Get-Location")]
    [InlineData("Set-Location -Verbose:$(Get-Flag) C:\\temp && Get-Location")]
    [InlineData("Set-Location -Verbose:0 C:\\temp && Get-Location")]
    [InlineData("Set-Location -Verbose:1 C:\\temp && Get-Location")]
    [InlineData("Set-Location -Verbose:2 C:\\temp && Get-Location")]
    [InlineData("Set-Location -Verbose:.0 C:\\temp && Get-Location")]
    [InlineData("Set-Location -Verbose:.5 C:\\temp && Get-Location")]
    public void PowerShell_dynamic_switch_value_keeps_success_transfer_reachable(
        string source)
    {
        var result = ParsePowerShell(source);
        var location = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Set-Location");
        var following = result.Commands.Last();

        AssertExactChange(location, "C:/temp");
        Assert.Equal(
            "C:/temp",
            Assert.IsType<ShellValueDomain.Exact>(following.WorkingDirectory).Value);
    }

    [Fact]
    public void PowerShell_escaped_switch_value_is_static_and_invalid()
    {
        var result = ParsePowerShell(
            "Set-Location -Verbose:`$switch C:\\temp; Get-Location");

        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            result.Commands[0].WorkingDirectoryEffect);
        Assert.Equal(
            "C:/work",
            Assert.IsType<ShellValueDomain.Exact>(
                result.Commands[1].WorkingDirectory).Value);
    }

    [Theory]
    [InlineData("Push-Location C:\\temp")]
    [InlineData("Pop-Location")]
    public void PowerShell_directory_stack_effect_is_unknown(string source)
    {
        var command = Assert.Single(ParsePowerShell(source).Commands);

        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            command.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_non_filesystem_location_has_unknown_success_target()
    {
        var command = Assert.Single(ParsePowerShell("Set-Location Env:").Commands);
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            command.WorkingDirectoryEffect);

        Assert.IsType<ShellValueDomain.Unknown>(effect.Target);
    }

    [Fact]
    public void PowerShell_dynamic_location_has_unknown_success_target()
    {
        var result = ParsePowerShell(
            "Set-Location $target; Get-Location");
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            result.Commands[0].WorkingDirectoryEffect);

        Assert.IsType<ShellValueDomain.Unknown>(effect.Target);
        Assert.IsType<ShellValueDomain.Unknown>(result.Commands[1].WorkingDirectory);
    }

    [Fact]
    public void PowerShell_dynamic_location_failure_retains_incoming_location()
    {
        var result = ParsePowerShell(
            "Set-Location $target || Get-Location");

        Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            result.Commands[0].WorkingDirectoryEffect);
        Assert.Equal(
            "C:/work",
            Assert.IsType<ShellValueDomain.Exact>(
                result.Commands[1].WorkingDirectory).Value);
    }

    [Fact]
    public void PowerShell_dynamic_path_parameter_has_unknown_success_target()
    {
        var command = Assert.Single(
            ParsePowerShell("Set-Location -Path $target").Commands);
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            command.WorkingDirectoryEffect);

        Assert.IsType<ShellValueDomain.Unknown>(effect.Target);
    }

    [Fact]
    public void PowerShell_stack_name_has_unknown_success_target()
    {
        var command = Assert.Single(
            ParsePowerShell("Set-Location -StackName missing").Commands);
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            command.WorkingDirectoryEffect);

        Assert.IsType<ShellValueDomain.Unknown>(effect.Target);
    }

    [Fact]
    public void PowerShell_abbreviated_stack_name_has_unknown_success_target()
    {
        var command = Assert.Single(
            ParsePowerShell("Set-Location -s missing").Commands);
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            command.WorkingDirectoryEffect);

        Assert.IsType<ShellValueDomain.Unknown>(effect.Target);
    }

    [Fact]
    public void PowerShell_preserving_current_runspace_host_is_unchanged()
    {
        var result = ParsePowerShell("Measure-Command { Get-Date }");
        var host = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Measure-Command");
        var nested = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Get-Date");

        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            nested.WorkingDirectoryEffect);
        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            host.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_mutating_current_runspace_host_is_unknown()
    {
        var result = ParsePowerShell(
            "Measure-Command { Set-Location C:\\temp }");
        var host = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Measure-Command");
        var nested = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Set-Location");

        AssertExactChange(nested, "C:/temp");
        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            host.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_same_target_mutation_does_not_look_preserving()
    {
        var result = ParsePowerShell(
            "Measure-Command { Set-Location C:\\work }");
        var host = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Measure-Command");

        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            host.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_unknown_domain_equality_does_not_prove_host_preservation()
    {
        var result = new PwshParser(
            new PwshParserOptions
            {
                HomeDirectory = "C:/Users/test",
                InitialStateMode = PwshInitialStateMode.Unknown,
                Dialect = PwshDialect.PowerShell7,
            }).Parse("Measure-Command { Set-Location $target }");
        var host = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Measure-Command");

        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            host.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_child_process_host_is_unchanged()
    {
        var result = ParsePowerShell(
            "Start-Job -ScriptBlock { Set-Location C:\\temp }");
        var host = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Start-Job");
        var nested = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Set-Location");

        AssertExactChange(nested, "C:/temp");
        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            host.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_proved_data_script_block_does_not_create_nested_effect()
    {
        var result = ParsePowerShell("Write-Output { Set-Location C:\\temp }");
        var host = Assert.Single(result.Commands);

        Assert.Equal("Write-Output", host.Clause.Verb.Joined);
        Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(
            host.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_unmodeled_execution_region_host_is_unknown()
    {
        var result = ParsePowerShell("Invoke-Custom { Get-Date }");
        var host = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Invoke-Custom");

        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            host.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_subexpression_effect_retains_current_runspace_ancestry()
    {
        var result = ParsePowerShell(
            "$(Set-Location C:\\temp); Get-Location");
        var nested = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Set-Location");

        AssertExactChange(nested, "C:/temp");
        Assert.Contains(nested.Ancestry, frame =>
            frame.Region == CommandAncestryRegion.Substitution);
        Assert.IsType<ShellValueDomain.Unknown>(
            result.Commands.Last().WorkingDirectory);
    }

    [Fact]
    public void PowerShell_parenthesized_effect_reaches_containing_runspace()
    {
        var result = ParsePowerShell(
            "(Set-Location C:\\temp); Get-Location");
        var nested = result.Commands.Single(command =>
            command.Clause.Verb.Joined == "Set-Location");

        AssertExactChange(nested, "C:/temp");
        Assert.Contains(nested.Ancestry, frame =>
            frame.Region == CommandAncestryRegion.GroupBody &&
            frame.Ancestor is GroupSyntax
            {
                GroupKind: ShellGroupKind.CurrentScope,
            });
        Assert.IsType<ShellValueDomain.Unknown>(
            result.Commands.Last().WorkingDirectory);
    }

    [Fact]
    public void PowerShell_empty_loop_body_has_no_reusable_effect()
    {
        var result = ParsePowerShell(
            "foreach ($x in @()) { Set-Location C:\\temp }");
        var command = Assert.Single(result.Commands);

        Assert.IsType<ShellWorkingDirectoryEffect.Unknown>(
            command.WorkingDirectoryEffect);
    }

    [Fact]
    public void PowerShell_pipeline_location_transfer_stays_atomic()
    {
        var result = ParsePowerShell("Set-Location C:\\temp | Get-Location");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    private static ParsedCommand ParseBash(string source) => new BashParser(
        new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        }).Parse(source);

    private static ParsedCommand ParsePowerShell(string source) => new PwshParser(
        new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
            Dialect = PwshDialect.PowerShell7,
        }).Parse(source);

    private static void AssertExactChange(CommandOccurrence command, string target)
    {
        var effect = Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            command.WorkingDirectoryEffect);
        Assert.Equal(target, Assert.IsType<ShellValueDomain.Exact>(effect.Target).Value);
    }
}
