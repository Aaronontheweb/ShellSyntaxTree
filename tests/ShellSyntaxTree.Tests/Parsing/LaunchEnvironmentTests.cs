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
    [InlineData("git -p \"$SUB\"", "git push")]
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
    [InlineData("cat \"$FOO/x\"")]
    [InlineData("cd \"$FOO\" && ls")]
    [InlineData("TMPDIR=/x; cd \"$TMPDIR\"")]
    [InlineData("unset TMPDIR; cd \"$TMPDIR\"")]
    [InlineData("read TMPDIR; cd \"$TMPDIR\"")]
    [InlineData("export -n TMPDIR; cd \"$TMPDIR\"")]
    [InlineData("declare TMPDIR=/x; cd \"$TMPDIR\"")]
    [InlineData("local TMPDIR=/x; cd \"$TMPDIR\"")]
    [InlineData("for TMPDIR in a; do cd \"$TMPDIR\"; done")]
    [InlineData("cat \"$TMPDIR/x\" \"$FOO\"")]
    [InlineData("cd \"$CDPATH\" && ls")]
    [InlineData("cat \"${TMPDIR:-/x}\"")]
    [InlineData("bash -c 'cat \"$TMPDIR/x\"'")]
    [InlineData("cat <<EOF\n$TMPDIR\nEOF")]
    [InlineData("cat \"$TMPDIR/x\" - <<EOF\n$FOO\nEOF")]
    [InlineData("wait -p TMPDIR; cat \"$TMPDIR/x\"")]
    public void Unsupplied_or_changed_variables_behave_as_without_launch_facts(string source)
    {
        var withLaunch = Bash().Parse(source);
        var withoutLaunch = Bash(launch: null).Parse(source);

        Assert.True(withoutLaunch.IsUnparseable);
        Assert.True(withLaunch.IsUnparseable);
        Assert.Equal(withoutLaunch.UnparseableReason, withLaunch.UnparseableReason);
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
    [InlineData("cat \"$HOME/x\"")]
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

    private static string Words(CommandOccurrence occurrence) =>
        occurrence.CommandWords is ShellCommandWords.Known known
            ? string.Join(" ", known.Words)
            : "<unknown>";
}
