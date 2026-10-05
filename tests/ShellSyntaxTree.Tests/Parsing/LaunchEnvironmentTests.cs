// -----------------------------------------------------------------------
// <copyright file="LaunchEnvironmentTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins <see cref="ShellParserOptions.LaunchEnvironment"/> (#200). The
/// caller proves environment values for a new shell process. The parser uses
/// them only while no statement can have changed them, and only under a
/// startup-free initial-state mode.
/// </summary>
public class LaunchEnvironmentTests
{
    private const string Start = "/work";
    private const string Temp = "/tmp/netclaw-1";
    private const string Home = "/home/agent";

    private static readonly ShellLaunchEnvironment NetclawLaunch = new(
        new Dictionary<string, string>
        {
            ["TMPDIR"] = Temp,
            ["TMP"] = Temp,
            ["TEMP"] = Temp,
            ["HOME"] = Home,
            ["SUB"] = "push",
            ["SPACED"] = "a b",
            ["STAR"] = "*",
            ["REL"] = "rel",
            ["tmp"] = "/launch/tmp",
        },
        new[] { "CDPATH" });

    private static readonly ShellLaunchEnvironment LaunchWithoutCdPath = new(
        new Dictionary<string, string> { ["TMPDIR"] = Temp },
        Array.Empty<string>());

    // ------------------------------------------------------------ positive

    [Fact]
    public void Cd_to_launch_temp_directory_then_sed_runs_in_the_exact_temp_path()
    {
        const string source = "cd \"$TMPDIR/ilspy_out\"; sed -n '1,2p' f";
        var parser = Bash(publishAuthored: true);

        var parsed = parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cd = parsed.Commands[0];
        Assert.Equal(Temp + "/ilspy_out", ExactTarget(cd.WorkingDirectoryEffect));
        Assert.Equal(Temp + "/ilspy_out", ExactValue(cd.Arguments.Single()));

        // `;` joins the success and failure paths. The finite projection
        // keeps both exact directories: a failed cd stays in the start.
        Assert.True(parser.TryProjectFiniteScopes(source, out var projection));
        var sedDirectories = projection!.Commands
            .Where(command => command.Source.StartsWith("sed", StringComparison.Ordinal))
            .Select(command => command.WorkingDirectory)
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { Start, Temp + "/ilspy_out" }.OrderBy(d => d, StringComparer.Ordinal), sedDirectories);
        var inTemp = projection.Commands.Single(command =>
            command.WorkingDirectory == Temp + "/ilspy_out");
        Assert.Equal(
            Temp + "/ilspy_out/f",
            inTemp.ScopedOccurrence.Clause.Args.Single(arg => arg.Raw == "f").Resolved);
    }

    [Theory]
    [InlineData("cd \"$TMPDIR/ilspy_out\" && sed -n '1,2p' f")]
    [InlineData("cd $TMPDIR/ilspy_out && sed -n '1,2p' f")]
    [InlineData("cd \"${TMPDIR}\"/ilspy_out && sed -n '1,2p' f")]
    public void Cd_to_launch_value_sets_the_exact_directory_of_the_next_command(string source)
    {
        var parsed = Bash().Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(Temp + "/ilspy_out", ExactValue(parsed.Commands[1].WorkingDirectory));
        Assert.Equal(
            Temp + "/ilspy_out/f",
            parsed.Commands[1].Clause.Args.Single(arg => arg.Raw == "f").Resolved);
    }

    [Theory]
    [InlineData("cat \"$HOME/x\"", Home + "/x")]
    [InlineData("cat $HOME/x", Home + "/x")]
    [InlineData("cat ~/x", Home + "/x")]
    [InlineData("cat \"$TMPDIR/x\"", Temp + "/x")]
    [InlineData("cat ${TMPDIR}/x", Temp + "/x")]
    [InlineData("cat \"$TMP/x\"", Temp + "/x")]
    [InlineData("cat \"$TEMP\"", Temp)]
    public void Launch_value_gives_an_exact_path_fact(string source, string expected)
    {
        // No HomeDirectory: the launch HOME is the only home fact.
        var parsed = Bash(homeDirectory: null).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var argument = parsed.Commands.Single().Arguments.Single();
        Assert.Equal(expected, ExactValue(argument));
        Assert.Equal(expected, argument.Argument.Resolved);
        Assert.True(argument.Argument.IsPath);
    }

    [Fact]
    public void Launch_value_keeps_the_env_var_kind_with_the_substituted_path()
    {
        var argument = Bash().Parse("cat \"$TMPDIR/x\"").Commands.Single().Arguments.Single();

        Assert.Equal(ArgKind.EnvVar, argument.Argument.Kind);
        Assert.Equal(Temp + "/x", argument.Argument.Resolved);
    }

    [Fact]
    public void Launch_value_gives_an_exact_redirect_target()
    {
        var command = Bash().Parse("echo hi > \"$TMPDIR/f\"").Commands.Single();

        var redirect = Assert.IsType<FileRedirectAnalysis>(command.Redirects.Single());
        Assert.Equal(Temp + "/f", Assert.IsType<ShellValueDomain.Exact>(redirect.Target).Value);
    }

    [Theory]
    [InlineData("cd src && make build", Start + "/src", "make build")]
    [InlineData("cd src/x && dotnet publish", Start + "/src/x", "dotnet publish")]
    [InlineData("cd -- src && ls", Start + "/src", "ls")]
    [InlineData("cd -L src && ls", Start + "/src", "ls")]
    public void Relative_cd_resolves_against_the_supplied_start_directory(
        string source,
        string expectedDirectory,
        string expectedWords)
    {
        var parsed = Bash().Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(expectedDirectory, ExactTarget(parsed.Commands[0].WorkingDirectoryEffect));
        Assert.Equal(expectedDirectory, ExactValue(parsed.Commands[1].WorkingDirectory));
        Assert.Equal(expectedWords, Words(parsed.Commands[1]));
    }

    [Fact]
    public void Relative_cd_chain_resolves_each_step()
    {
        var parsed = Bash().Parse("cd a && cd b && ls");

        Assert.Equal(Start + "/a", ExactValue(parsed.Commands[1].WorkingDirectory));
        Assert.Equal(Start + "/a/b", ExactTarget(parsed.Commands[1].WorkingDirectoryEffect));
        Assert.Equal(Start + "/a/b", ExactValue(parsed.Commands[2].WorkingDirectory));
    }

    [Fact]
    public void Relative_cd_finite_projection_gives_exact_scopes()
    {
        Assert.True(Bash(publishAuthored: true).TryProjectFiniteScopes(
            "cd src && make build",
            out var projection));

        Assert.Equal(
            new[] { ("cd src", Start), ("make build", Start + "/src") },
            projection!.Commands.Select(c => (c.Source, c.WorkingDirectory)).ToArray());
    }

    [Theory]
    [InlineData("cd sub && cat f", BashInitialStateMode.FreshNonInteractiveNoStartup)]
    [InlineData("cd sub && cat f", BashInitialStateMode.IsolatedNonInteractive)]
    [InlineData("cd sub; cat f", BashInitialStateMode.FreshNonInteractiveNoStartup)]
    [InlineData("cd -- sub && cat f", BashInitialStateMode.FreshNonInteractiveNoStartup)]
    [InlineData("cd -e sub && cat f", BashInitialStateMode.FreshNonInteractiveNoStartup)]
    public void Relative_cd_operand_gets_the_computed_directory_as_its_resolved_path(
        string source,
        BashInitialStateMode mode)
    {
        // #203: before the fix the directory was exact, but the operand
        // `sub` had an empty resolved path, so a consumer saw it as unknown.
        var parsed = Bash(mode: mode).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cd = parsed.Commands[0];
        Assert.Equal(Start + "/sub", ExactTarget(cd.WorkingDirectoryEffect));
        Assert.Equal(Start + "/sub", CdOperand(cd, "sub").Resolved);
        Assert.Equal(Start + "/sub", CdOperandElement(cd, "sub").Resolved);
    }

    [Theory]
    [InlineData("cd a/b && ls", new[] { "a/b" }, new[] { Start + "/a/b" })]
    [InlineData("cd a && cd b && ls", new[] { "a", "b" }, new[] { Start + "/a", Start + "/a/b" })]
    [InlineData("cd a && cd b/c; ls", new[] { "a", "b/c" }, new[] { Start + "/a", Start + "/a/b/c" })]
    [InlineData("cd /abs && cd rel && ls", new[] { "/abs", "rel" }, new[] { "/abs", "/abs/rel" })]
    public void Each_relative_cd_operand_in_a_chain_resolves_against_its_input_directory(
        string source,
        string[] operands,
        string[] expected)
    {
        var parsed = Bash().Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        for (var index = 0; index < operands.Length; index++)
        {
            var cd = parsed.Commands[index];
            Assert.Equal(expected[index], ExactTarget(cd.WorkingDirectoryEffect));
            Assert.Equal(expected[index], CdOperand(cd, operands[index]).Resolved);
            Assert.Equal(expected[index], CdOperandElement(cd, operands[index]).Resolved);
        }
    }

    [Fact]
    public void Relative_cd_operand_resolves_in_the_finite_projection_slice()
    {
        Assert.True(Bash(publishAuthored: true).TryProjectFiniteScopes(
            "cd a && cd b && ls",
            out var projection));

        var cdB = projection!.Commands.Single(c => c.Source == "cd b");
        Assert.Equal(Start + "/a", cdB.WorkingDirectory);
        Assert.Equal(Start + "/a/b", CdOperand(cdB.ScopedOccurrence, "b").Resolved);
    }

    [Fact]
    public void Cd_without_operand_goes_to_the_launch_home()
    {
        var parsed = Bash(homeDirectory: null).Parse("cd && ls");

        Assert.Equal(Home, ExactValue(parsed.Commands[1].WorkingDirectory));
    }

    [Theory]
    [InlineData("\"$TMPDIR/tool\" arg", Temp + "/tool arg")]
    [InlineData("$TMPDIR/tool build -c Release", Temp + "/tool build")]
    [InlineData("\"${HOME}/bin/tool\" status", Home + "/bin/tool status")]
    [InlineData("git \"$SUB\" origin", "git push origin")]
    [InlineData("git $SUB origin", "git push origin")]
    // `"$SUB"` is one static word, so it is the value of `-p` (#237).
    [InlineData("git -p \"$SUB\"", "git")]
    [InlineData("git -C . \"$SUB\"", "git push")]
    [InlineData("git \"$TMPDIR\" status", "git status")]
    [InlineData("git \"$SPACED\" status", "git status")]
    [InlineData("git \"$STAR\" status", "git status")]
    public void Launch_values_in_the_verb_slot_and_program_word_resolve(
        string source,
        string expected)
    {
        var parsed = Bash().Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(expected, Words(parsed.Commands.Single()));
    }

    // ------------------------------------------------------------ fail closed

    [Theory]
    [InlineData("TMPDIR=/x; cd \"$TMPDIR\"")]
    [InlineData("unset TMPDIR; cd \"$TMPDIR\"")]
    [InlineData("read TMPDIR; cd \"$TMPDIR\"")]
    [InlineData("export -n TMPDIR; cd \"$TMPDIR\"")]
    [InlineData("declare TMPDIR=/x; cd \"$TMPDIR\"")]
    [InlineData("local TMPDIR=/x; cd \"$TMPDIR\"")]
    [InlineData("for TMPDIR in a; do cd \"$TMPDIR\"; done")]
    [InlineData("cat \"${TMPDIR:-/x}\"")]
    public void Unmodeled_variable_changes_behave_as_without_launch_facts(string source)
    {
        var withLaunch = Bash().Parse(source);
        var withoutLaunch = Bash(launch: null).Parse(source);

        Assert.True(withoutLaunch.IsUnparseable);
        Assert.True(withLaunch.IsUnparseable);
        Assert.Equal(withoutLaunch.UnparseableReason, withLaunch.UnparseableReason);
    }

    [Theory]
    [InlineData("cat \"$FOO/x\"", "\"$FOO/x\"")]
    [InlineData("cat \"$TMPDIR/x\" \"$FOO\"", "\"$FOO\"")]
    [InlineData("wait -p TMPDIR; cat \"$TMPDIR/x\"", "\"$TMPDIR/x\"")]
    [InlineData("bash -c 'cat \"$TMPDIR/x\"'", "\"$TMPDIR/x\"")]
    public void Unsupplied_or_changed_variable_is_an_unknown_value(string source, string raw)
    {
        // Fresh mode reads an unsupplied or revoked name as an unknown value
        // (#221). A launch fact never gives it a value.
        foreach (var parsed in new[] { Bash().Parse(source), Bash(launch: null).Parse(source) })
        {
            Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
            var argument = parsed.Commands[^1].Arguments.Single(a => a.Argument.Raw == raw);
            Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
            Assert.Null(argument.Argument.Resolved);
        }
    }

    [Theory]
    [InlineData("cd \"$FOO\" && ls")]
    [InlineData("cd \"$CDPATH\" && ls")]
    public void Directory_change_to_an_unknown_value_gives_an_unknown_directory(string source)
    {
        foreach (var parsed in new[] { Bash().Parse(source), Bash(launch: null).Parse(source) })
        {
            Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
            Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[^1].WorkingDirectory);
        }
    }

    [Theory]
    [InlineData("cat <<EOF\n$TMPDIR\nEOF")]
    [InlineData("cat \"$TMPDIR/x\" - <<EOF\n$FOO\nEOF")]
    public void Expanding_heredoc_body_keeps_its_raw_text(string source)
    {
        // A launch value never goes into the body. The consumer gets the
        // raw body and the expansion mode, as before.
        foreach (var parsed in new[] { Bash().Parse(source), Bash(launch: null).Parse(source) })
        {
            Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
            var heredoc = Assert.IsType<HereDocumentRedirectAnalysis>(parsed.Commands.Single().Redirects.Single());
            Assert.Equal(HereDocumentExpansionMode.Expand, heredoc.Document.ExpansionMode);
            Assert.StartsWith("$", heredoc.Document.Body.Raw);
        }
    }

    [Fact]
    public void Shell_state_assignment_replaces_a_launch_value_with_the_same_name()
    {
        var parsed = Bash().Parse("tmp=/x; cat \"$tmp\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        // The bounded assignment rules own the value now. They publish the
        // exact value, and the launch value never appears.
        var argument = parsed.Commands.Single().Arguments.Single();
        Assert.Equal("/x", ExactValue(argument));
        Assert.NotEqual("/launch/tmp", argument.Argument.Resolved);
        Assert.Equal(
            ExactValue(Bash(launch: null).Parse("tmp=/x; cat \"$tmp\"").Commands.Single().Arguments.Single()),
            ExactValue(argument));
    }

    [Fact]
    public void Loop_binding_replaces_a_launch_value_with_the_same_name()
    {
        var parsed = Bash(publishAuthored: true).Parse("for tmp in a; do cat \"$tmp\"; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var argument = parsed.Commands.Single().Arguments.Single();
        Assert.NotEqual("/launch/tmp", ExactValue(argument));
        Assert.NotEqual("/launch/tmp", argument.Argument.Resolved);
        Assert.NotEqual("/launch/tmp", ExactAuthoredValue(argument));
    }

    [Theory]
    [InlineData("wait -p TMPDIR && cat \"$TMPDIR/x\"")]
    [InlineData("wait -p TMPDIR; cat \"$TMPDIR/x\"")]
    public void Wait_with_an_option_revokes_launch_values(string source)
    {
        // The isolated mode admits unknown variables, so the result parses.
        var parser = Bash(mode: BashInitialStateMode.IsolatedNonInteractive, publishAuthored: true);
        var parsed = parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cat = parsed.Commands[1];
        Assert.IsType<ShellValueDomain.Unknown>(cat.Arguments.Single().Value);
        Assert.Null(cat.Clause.Args.Single(arg => !arg.IsCwdAttribution).Resolved);
        if (parser.TryProjectFiniteScopes(source, out var projection))
        {
            // A slice must not get back a value that an earlier command revoked.
            var slice = projection!.Commands.Single(c => c.Source.StartsWith("cat", StringComparison.Ordinal));
            Assert.Null(slice.ScopedOccurrence.Clause.Args.Single(arg => !arg.IsCwdAttribution).Resolved);
            Assert.IsType<ShellValueDomain.Unknown>(slice.ScopedOccurrence.Arguments.Single().Value);
        }
    }

    [Theory]
    [InlineData("wait -p HOME; cat ~/x")]
    [InlineData("wait -p HOME; cat \"$HOME/x\"")]
    public void Revoked_launch_home_does_not_fall_back_to_a_default(string source)
    {
        var parsed = Bash(mode: BashInitialStateMode.IsolatedNonInteractive).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cat = parsed.Commands[1];
        Assert.IsType<ShellValueDomain.Unknown>(cat.Arguments.Single().Value);
        Assert.Null(cat.Clause.Args.Single(arg => !arg.IsCwdAttribution).Resolved);
    }

    [Fact]
    public void Revoked_launch_home_makes_cd_without_operand_unknown()
    {
        var parsed = Bash(mode: BashInitialStateMode.IsolatedNonInteractive)
            .Parse("wait -p HOME; cd && ls");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[2].WorkingDirectory);
    }

    [Fact]
    public void Variable_change_inside_a_loop_with_live_launch_facts_is_rejected()
    {
        var parsed = Bash(mode: BashInitialStateMode.IsolatedNonInteractive)
            .Parse("for x in a b; do cat \"$TMPDIR\"; wait -p y; done");

        Assert.True(parsed.IsUnparseable);
    }

    [Theory]
    [InlineData(BashInitialStateMode.Unknown)]
    public void Launch_facts_are_ignored_when_startup_content_can_run(BashInitialStateMode mode)
    {
        var parser = Bash(mode: mode);

        Assert.True(parser.Parse("cat \"$TMPDIR/x\"").IsUnparseable);
        var cd = parser.Parse("cd src && ls");
        Assert.IsType<ShellValueDomain.Unknown>(cd.Commands[1].WorkingDirectory);
    }

    [Fact]
    public void Relative_cd_without_a_supplied_start_directory_is_unknown()
    {
        var parsed = Bash(workingDirectory: null).Parse("cd src && ls");

        Assert.IsType<ShellValueDomain.Unknown>(
            Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
                parsed.Commands[0].WorkingDirectoryEffect).Target);
        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[1].WorkingDirectory);
    }

    [Theory]
    [InlineData("cd src && make build")]
    [InlineData("cd /a && cd sub && ls")]
    public void Finite_projection_without_a_supplied_start_directory_keeps_relative_cd_unknown(
        string source)
    {
        // The projection starts from the process directory when the caller
        // supplies none. That default must not prove a relative cd.
        var parser = Bash(workingDirectory: null, publishAuthored: true);

        if (!parser.TryProjectFiniteScopes(source, out var projection))
        {
            return;
        }

        foreach (var command in projection!.Commands)
        {
            Assert.DoesNotContain("/src", command.WorkingDirectory, StringComparison.Ordinal);
            Assert.DoesNotContain("/sub", command.WorkingDirectory, StringComparison.Ordinal);
            if (command.Source.StartsWith("cd s", StringComparison.Ordinal))
            {
                Assert.Null(ExactTarget(command.ScopedOccurrence.WorkingDirectoryEffect));
            }
        }
    }

    [Fact]
    public void Relative_cd_without_a_proved_unset_cdpath_is_unknown()
    {
        var parsed = Bash(launch: LaunchWithoutCdPath).Parse("cd src && ls");

        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[1].WorkingDirectory);
    }

    [Fact]
    public void Relative_cd_operand_without_a_proved_unset_cdpath_has_no_resolved_path()
    {
        var parsed = Bash(launch: LaunchWithoutCdPath).Parse("cd sub && cat f");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Null(CdOperand(parsed.Commands[0], "sub").Resolved);
        Assert.Null(CdOperandElement(parsed.Commands[0], "sub").Resolved);
    }

    [Theory]
    [InlineData("cd sub && cat f")]
    [InlineData("cd sub; cat f")]
    [InlineData("cd a && cd b && ls")]
    public void Relative_cd_operand_without_launch_facts_has_no_resolved_path(string source)
    {
        var parsed = Bash(launch: null).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cd = parsed.Commands[0];
        Assert.Null(cd.Clause.Args.First(arg => !arg.IsCwdAttribution).Resolved);
        Assert.Null(cd.Clause.Elements.First(e => e.Role == ClauseElementRole.Argument).Resolved);
    }

    [Theory]
    [InlineData(BashInitialStateMode.Unknown, Start)]
    [InlineData(BashInitialStateMode.FreshNonInteractiveNoStartup, null)]
    public void Relative_cd_operand_without_a_startup_free_mode_or_start_directory_has_no_resolved_path(
        BashInitialStateMode mode,
        string? workingDirectory)
    {
        var parsed = Bash(mode: mode, workingDirectory: workingDirectory).Parse("cd sub && cat f");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Null(CdOperand(parsed.Commands[0], "sub").Resolved);
    }

    [Theory]
    [InlineData(BashInitialStateMode.FreshNonInteractiveNoStartup)]
    [InlineData(BashInitialStateMode.IsolatedNonInteractive)]
    public void Cdpath_change_before_cd_keeps_the_operand_unresolved(BashInitialStateMode mode)
    {
        // `wait -p` can assign CDPATH, so the launch fact is revoked.
        var parsed = Bash(mode: mode).Parse("wait -p CDPATH; cd sub && cat f");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cd = parsed.Commands[1];
        Assert.IsType<ShellValueDomain.Unknown>(
            Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(cd.WorkingDirectoryEffect).Target);
        Assert.Null(CdOperand(cd, "sub").Resolved);
        Assert.Null(CdOperandElement(cd, "sub").Resolved);
    }

    [Theory]
    [InlineData("CDPATH=/x; cd sub && cat f")]
    [InlineData("CDPATH=/x cd sub && cat f")]
    [InlineData("export CDPATH=/x; cd sub && cat f")]
    public void Cdpath_assignment_in_the_command_never_resolves_the_operand(string source)
    {
        var parsed = Bash(mode: BashInitialStateMode.IsolatedNonInteractive).Parse(source);

        if (parsed.IsUnparseable)
        {
            return;
        }

        foreach (var command in parsed.Commands)
        {
            foreach (var argument in command.Clause.Args.Where(arg => arg.Raw == "sub"))
            {
                Assert.Null(argument.Resolved);
            }
        }
    }

    [Theory]
    [InlineData("cd -P sub && cat f", "sub")]
    [InlineData("cd -@ sub && cat f", "sub")]
    [InlineData("cd - && cat f", "-")]
    [InlineData("bash -c 'cd sub && cat f'", "sub")]
    public void Cd_operand_that_needs_runtime_state_has_no_resolved_path(string source, string operand)
    {
        var parsed = Bash().Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Null(CdOperand(parsed.Commands[0], operand).Resolved);
    }

    [Fact]
    public void Dynamic_cd_operand_has_no_resolved_path()
    {
        var parsed = Bash(mode: BashInitialStateMode.IsolatedNonInteractive)
            .Parse("cd \"$FOO\" && cat f");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Null(parsed.Commands[0].Clause.Args.First(arg => !arg.IsCwdAttribution).Resolved);
        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[1].WorkingDirectory);
    }

    [Fact]
    public void Cd_without_operand_and_unknown_home_stays_unknown()
    {
        var parsed = Bash(
            launch: LaunchWithoutCdPath,
            BashInitialStateMode.FreshNonInteractiveNoStartup,
            homeDirectory: null).Parse("cd && cat f");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[1].WorkingDirectory);
    }

    [Theory]
    [InlineData("cd - && ls")]
    [InlineData("cd -P src && ls")]
    [InlineData("cd -P \"$TMPDIR\" && ls")]
    public void Cd_rules_that_need_runtime_state_stay_unknown(string source)
    {
        var parsed = Bash().Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[1].WorkingDirectory);
    }

    [Theory]
    [InlineData("cd .. && ls", "/")]
    [InlineData("cd ../x && ls", "/x")]
    [InlineData("cd /a/.. && ls", "/")]
    [InlineData("cd ./x && ls", Start + "/x")]
    public void Dot_dot_and_dot_operands_keep_their_existing_logical_rules(
        string source,
        string expected)
    {
        var withLaunch = Bash().Parse(source);
        var withoutLaunch = Bash(launch: null).Parse(source);

        Assert.Equal(expected, ExactValue(withLaunch.Commands[1].WorkingDirectory));
        Assert.Equal(
            ExactValue(withoutLaunch.Commands[1].WorkingDirectory),
            ExactValue(withLaunch.Commands[1].WorkingDirectory));

        // These operands never search CDPATH, so they resolve with or
        // without launch facts (#203).
        Assert.Equal(expected, FirstOperand(withoutLaunch.Commands[0]).Resolved);
        Assert.Equal(expected, FirstOperand(withLaunch.Commands[0]).Resolved);
    }

    [Fact]
    public void Tilde_cd_operand_does_not_need_a_cdpath_fact()
    {
        // Bash expands `~/x` to an absolute path before the CDPATH search,
        // so the operand resolves with or without the CDPATH fact (#203).
        var withoutCdPathFact = Bash(launch: LaunchWithoutCdPath).Parse("cd ~/x && ls");
        var withoutLaunch = Bash(launch: null).Parse("cd ~/x && ls");

        Assert.Equal(Home + "/x", CdOperand(withoutCdPathFact.Commands[0], "~/x").Resolved);
        Assert.Equal(Home + "/x", CdOperand(withoutLaunch.Commands[0], "~/x").Resolved);
    }

    [Fact]
    public void Relative_launch_value_is_not_a_path_but_cd_applies_the_cdpath_rule()
    {
        var cat = Bash().Parse("cat \"$REL/x\"").Commands.Single().Arguments.Single();
        Assert.Null(cat.Argument.Resolved);
        Assert.False(cat.Argument.IsPath);

        var cd = Bash().Parse("cd \"$REL\" && ls");
        Assert.Equal(Start + "/rel", ExactValue(cd.Commands[1].WorkingDirectory));

        var noCdPathFact = Bash(launch: new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["REL"] = "rel" },
            Array.Empty<string>())).Parse("cd \"$REL\" && ls");
        Assert.IsType<ShellValueDomain.Unknown>(noCdPathFact.Commands[1].WorkingDirectory);
    }

    [Theory]
    [InlineData("cat $SPACED")]
    [InlineData("cat $STAR")]
    public void Unquoted_launch_value_that_can_split_or_glob_is_not_resolved(string source)
    {
        var argument = Bash().Parse(source).Commands.Single().Arguments.Single();

        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        Assert.Null(argument.Argument.Resolved);
    }

    [Theory]
    [InlineData("git $SPACED status")]
    [InlineData("git $STAR")]
    public void Unquoted_launch_value_that_can_split_keeps_the_verb_slot_unknown(string source)
    {
        var parsed = Bash().Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsType<ShellCommandWords.Unknown>(parsed.Commands.Single().CommandWords);
    }

    [Theory]
    [InlineData("$SUB arg")]
    [InlineData("\"$SUB\" arg")]
    [InlineData("$SPACED/tool arg")]
    public void Program_word_without_a_slash_or_that_can_split_stays_unparseable(string source)
    {
        // A bare name can select a builtin, a function, or an alias.
        Assert.True(Bash().Parse(source).IsUnparseable);
    }

    // ------------------------------------------------------------ no caller option

    [Theory]
    [InlineData("cd src && make build")]
    [InlineData("cd \"$TMPDIR/ilspy_out\"; sed -n '1,2p' f")]
    [InlineData("\"$TMPDIR/tool\" arg")]
    public void Without_launch_facts_the_result_is_unchanged(string source)
    {
        var parsed = Bash(launch: null).Parse(source);

        // Before #200 these inputs either fail closed or give an unknown
        // directory. Without the caller option, they still do.
        if (parsed.IsUnparseable)
        {
            return;
        }

        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[^1].WorkingDirectory);
    }

    [Fact]
    public void Without_launch_facts_a_home_read_follows_the_tilde_rule()
    {
        // `$HOME` is the documented exception: it expands like `~` from the
        // HomeDirectory option. Fresh mode now reads it, as isolated mode
        // did before (#221).
        var home = Bash(launch: null).Parse("cat \"$HOME/x\"");
        var tilde = Bash(launch: null).Parse("cat ~/x");

        Assert.False(home.IsUnparseable, home.UnparseableReason);
        Assert.Equal(Home + "/x", ExactValue(home.Commands.Single().Arguments.Single()));
        Assert.Equal(
            ExactValue(tilde.Commands.Single().Arguments.Single()),
            ExactValue(home.Commands.Single().Arguments.Single()));
    }

    // ------------------------------------------------------------ PowerShell

    [Theory]
    [InlineData("Get-Content \"$env:TEMP/x\"")]
    [InlineData("Get-Content \"${env:TEMP}/x\"")]
    [InlineData("Get-Content $ENV:TEMP/x")]
    [InlineData("git status; Get-Content \"$env:TEMP/x\"")]
    public void PowerShell_launch_environment_value_gives_an_exact_path(string source)
    {
        var command = Pwsh().Parse(source).Commands[^1];

        var argument = command.Arguments.Single();
        Assert.True(command.IsComplete);
        Assert.Equal(ArgKind.EnvVar, argument.Argument.Kind);
        Assert.Equal("C:/Temp/nc/x", argument.Argument.Resolved);
        Assert.Equal("C:/Temp/nc/x", ExactValue(argument));
    }

    [Fact]
    public void PowerShell_env_home_resolves_from_the_launch_value()
    {
        var argument = Pwsh().Parse("Get-Content \"$env:HOME/x\"").Commands.Single().Arguments.Single();

        Assert.Equal("C:/Users/agent/x", ExactValue(argument));
    }

    [Theory]
    [InlineData("Get-Content $env:temp/x")]
    [InlineData("Get-Content \"$env:FOO/x\"")]
    public void PowerShell_unsupplied_or_differently_cased_name_is_not_resolved(string source)
    {
        var argument = Pwsh().Parse(source).Commands.Single().Arguments.Single();

        Assert.Equal(ArgKind.DynamicSkip, argument.Argument.Kind);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
    }

    [Fact]
    public void PowerShell_launch_value_is_not_trusted_after_process_wide_change()
    {
        var command = Pwsh().Parse("Import-Module foo; Get-Content \"$env:TEMP/x\"").Commands[1];

        Assert.False(command.IsComplete);
        Assert.IsType<ShellValueDomain.Unknown>(command.Arguments.Single().Value);
    }

    [Fact]
    public void PowerShell_launch_facts_are_ignored_without_the_no_profile_mode()
    {
        var argument = Pwsh(PwshInitialStateMode.Unknown)
            .Parse("Get-Content \"$env:TEMP/x\"").Commands.Single().Arguments.Single();

        Assert.Equal(ArgKind.DynamicSkip, argument.Argument.Kind);
    }

    // ------------------------------------------------------------ validation

    [Fact]
    public void Launch_environment_rejects_invalid_facts()
    {
        Assert.Throws<ArgumentException>(() => new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["1X"] = "a" }, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["X"] = "a\0b" }, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["X"] = "a" }, new[] { "X" }));
        Assert.Throws<ArgumentException>(() => new ShellLaunchEnvironment(
            new[] { new KeyValuePair<string, string>("X", "a"), new KeyValuePair<string, string>("X", "b") },
            Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new ShellLaunchEnvironment(
            new Dictionary<string, string>(), new[] { "X", "X" }));
        Assert.Throws<ArgumentNullException>(() => new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["X"] = null! }, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("PWD")]
    [InlineData("OLDPWD")]
    [InlineData("IFS")]
    [InlineData("BASH_ENV")]
    [InlineData("SHLVL")]
    [InlineData("LD_PRELOAD")]
    [InlineData("BASH_FUNC_x%%")]
    public void Bash_parser_rejects_names_that_bash_owns(string name)
    {
        if (!ShellLaunchEnvironmentNameIsIdentifier(name))
        {
            Assert.Throws<ArgumentException>(() => new ShellLaunchEnvironment(
                new Dictionary<string, string> { [name] = "/x" }, Array.Empty<string>()));
            return;
        }

        var launch = new ShellLaunchEnvironment(
            new Dictionary<string, string> { [name] = "/x" }, Array.Empty<string>());
        Assert.Throws<ArgumentException>(() => new BashParser(new BashParserOptions
        {
            LaunchEnvironment = launch,
        }));
    }

    [Fact]
    public void Bash_parser_rejects_an_empty_launch_home()
    {
        Assert.Throws<ArgumentException>(() => new BashParser(new BashParserOptions
        {
            LaunchEnvironment = new ShellLaunchEnvironment(
                new Dictionary<string, string> { ["HOME"] = string.Empty },
                Array.Empty<string>()),
        }));
    }

    [Fact]
    public void Bash_parser_rejects_a_launch_home_that_disagrees_with_home_directory()
    {
        Assert.Throws<ArgumentException>(() => new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/other",
            LaunchEnvironment = NetclawLaunch,
        }));
    }

    [Theory]
    [InlineData("PSModulePath")]
    [InlineData("USERPROFILE")]
    [InlineData("Path")]
    public void PowerShell_parser_rejects_names_that_powershell_owns(string name)
    {
        var launch = new ShellLaunchEnvironment(
            new Dictionary<string, string> { [name] = "C:/x" }, Array.Empty<string>());

        Assert.Throws<ArgumentException>(() => new PwshParser(new PwshParserOptions
        {
            LaunchEnvironment = launch,
        }));
    }

    [Fact]
    public void PowerShell_parser_rejects_names_that_differ_only_in_case()
    {
        var launch = new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["TEMP"] = "C:/a", ["Temp"] = "C:/b" },
            Array.Empty<string>());

        Assert.Throws<ArgumentException>(() => new PwshParser(new PwshParserOptions
        {
            LaunchEnvironment = launch,
        }));
    }

    // ------------------------------------------------------------ helpers

    private static BashParser Bash(
        ShellLaunchEnvironment? launch = null,
        BashInitialStateMode mode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        string? workingDirectory = Start,
        string? homeDirectory = Home,
        bool publishAuthored = false) => new(new BashParserOptions
        {
            HomeDirectory = homeDirectory,
            WorkingDirectory = workingDirectory,
            InitialStateMode = mode,
            PublishAuthoredSourceFacts = publishAuthored,
            LaunchEnvironment = launch ?? NetclawLaunch,
        });

    // `launch: null` in a test means "no caller option". The default above
    // supplies the Netclaw facts, so the helper keeps a separate overload.
    private static BashParser Bash(ShellLaunchEnvironment? launch) =>
        launch is null
            ? new BashParser(new BashParserOptions
            {
                HomeDirectory = Home,
                WorkingDirectory = Start,
                InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            })
            : Bash(launch, BashInitialStateMode.FreshNonInteractiveNoStartup);

    private static PwshParser Pwsh(
        PwshInitialStateMode mode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile) =>
        new(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/agent",
            WorkingDirectory = "C:/work",
            InitialStateMode = mode,
            LaunchEnvironment = new ShellLaunchEnvironment(
                new Dictionary<string, string>
                {
                    ["TEMP"] = "C:/Temp/nc",
                    ["HOME"] = "C:/Users/agent",
                },
                Array.Empty<string>()),
        });

    private static bool ShellLaunchEnvironmentNameIsIdentifier(string name) =>
        name.All(c => c == '_' || char.IsAsciiLetterOrDigit(c)) && !char.IsAsciiDigit(name[0]);

    private static string? ExactValue(ShellValueDomain domain) =>
        domain is ShellValueDomain.Exact exact ? exact.Value : null;

    private static string? ExactValue(AnalyzedArgument argument) => ExactValue(argument.Value);

    private static string? ExactAuthoredValue(AnalyzedArgument argument) =>
        ExactValue(argument.AuthoredValue);

    private static string? ExactTarget(ShellWorkingDirectoryEffect effect) =>
        effect is ShellWorkingDirectoryEffect.ChangesOnSuccess change
            ? ExactValue(change.Target)
            : null;

    private static Arg CdOperand(CommandOccurrence occurrence, string raw) =>
        occurrence.Clause.Args.Single(arg => !arg.IsCwdAttribution && arg.Raw == raw);

    private static ClauseElement CdOperandElement(CommandOccurrence occurrence, string raw) =>
        occurrence.Clause.Elements.Single(element =>
            element.Role == ClauseElementRole.Argument && element.Raw == raw);

    private static Arg FirstOperand(CommandOccurrence occurrence) =>
        occurrence.Clause.Args.First(arg => !arg.IsCwdAttribution);

    private static string Words(CommandOccurrence occurrence) =>
        occurrence.CommandWords is ShellCommandWords.Known known
            ? string.Join(" ", known.Words)
            : "<unknown>";
}
