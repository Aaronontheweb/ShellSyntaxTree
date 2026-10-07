// -----------------------------------------------------------------------
// <copyright file="BashChildEnvironmentTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using ShellSyntaxTree.Internal.Bash.Parsing;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// <see cref="ShellVariableAssignment.MayAffectProcessEnvironment"/> is false
/// for a Bash shell-state assignment only when the caller declares the
/// complete launch environment and the parser proves that the name has no
/// export attribute. Each case runs in real Bash, launched with
/// <c>env -i</c> and the exact set of environment names, when Bash is
/// available.
/// </summary>
public class BashChildEnvironmentTests
{
    private static BashParser CreateParser(
        IEnumerable<string>? completeNames,
        BashInitialStateMode mode = BashInitialStateMode.FreshNonInteractiveNoStartup) => new(new BashParserOptions
    {
        HomeDirectory = "/home/test",
        WorkingDirectory = "/work",
        InitialStateMode = mode,
        LaunchEnvironment = completeNames is null
            ? null
            : ShellLaunchEnvironment.FromCompleteEnvironmentNames(completeNames),
    });

    [Theory]
    // The F3 sources from Netclaw: a name not in the environment.
    [InlineData("b=$(echo new); env", "PATH", false)]
    [InlineData("st=$(echo new); env", "PATH", false)]
    // An inherited name keeps its export attribute.
    [InlineData("b=$(echo new); env", "PATH,b", true)]
    // Export on every path, on one path, or later.
    [InlineData("b=1; export b; env", "PATH", true)]
    [InlineData("export b=1; env", "PATH", true)]
    [InlineData("if true; then export b; fi; b=1; env", "PATH", true)]
    [InlineData("b=1; true && export b; env", "PATH", true)]
    [InlineData("b=1; false || export b; env", "PATH", true)]
    // An export that one branch can run counts, also when it does not run.
    [InlineData("if false; then export b; fi; b=1; env", "PATH", true)]
    [InlineData("b=1; while false; do export b; done; env", "PATH", true)]
    // An export in a pipeline stage or a background job runs in a subshell.
    [InlineData("b=1; export b | true; env", "PATH", false)]
    [InlineData("b=1; export b & wait; env", "PATH", false)]
    // The command runs in a pipeline or in a command substitution.
    [InlineData("b=1; env | cat", "PATH", false)]
    [InlineData("b=1; echo \"$(env)\"", "PATH", false)]
    [InlineData("b=1; for i in 1; do env; done", "PATH", false)]
    // A name that differs only in case keeps the fact true (Windows).
    [InlineData("b=1; env", "PATH,B", true)]
    // More export forms and commands.
    [InlineData("read b <<< x; env", "PATH", false)]
    [InlineData("b=1; export -p; env", "PATH", false)]
    [InlineData("b=1; export b=2 c; env", "PATH", true)]
    [InlineData("b=1; if export b; then env; fi", "PATH", true)]
    [InlineData("b=1; case x in x) export b;; esac; env", "PATH", true)]
    [InlineData("b=1; until export b; do :; done; env", "PATH", true)]
    [InlineData("b=1; nohup env", "PATH", false)]
    // A loop: the export of the first pass reaches env in the second pass.
    [InlineData("b=1; for i in 1 2; do env; export b; done", "PATH", true)]
    public void Shell_state_assignment_reaches_a_child_only_when_bash_exports_it(
        string source,
        string launchNames,
        bool mayAffect)
    {
        var names = launchNames.Split(',');
        var reaches = RunInExactEnvironment(source, names, "b", "st");
        if (reaches is not null)
        {
            // The fact must never be false when Bash passes the variable.
            Assert.True(mayAffect || !reaches.Value, "Bash passes the variable to env");
            if (!source.Contains("if false", StringComparison.Ordinal) &&
                !source.Contains("while false", StringComparison.Ordinal) &&
                !launchNames.Contains('B'))
            {
                Assert.Equal(mayAffect, reaches.Value);
            }
        }

        var parsed = CreateParser(names).Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = parsed.Commands.Single(
            c => c.Clause.Elements.Any(e => e.Value == "env"));
        var assignment = Assert.Single(
            command.Assignments,
            a => a.Scope == ShellVariableAssignmentScope.ShellState);
        Assert.Equal(mayAffect, assignment.MayAffectProcessEnvironment);
    }

    [Theory]
    [InlineData("b=$(echo new); env")]
    [InlineData("b=1; export b | true; env")]
    public void Without_a_complete_environment_the_fact_stays_true(string source)
    {
        foreach (var launch in new[]
                 {
                     null,
                     new ShellLaunchEnvironment(
                         new Dictionary<string, string> { ["HOME"] = "/home/test" },
                         new[] { "CDPATH" }),
                 })
        {
            var parsed = new BashParser(new BashParserOptions
            {
                WorkingDirectory = "/work",
                InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
                LaunchEnvironment = launch,
            }).Parse(source);

            Assert.All(
                parsed.Commands.SelectMany(c => c.Assignments),
                a => Assert.True(a.MayAffectProcessEnvironment));
        }
    }

    [Fact]
    public void Exported_launch_value_is_part_of_the_complete_environment()
    {
        var parsed = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            LaunchEnvironment = new ShellLaunchEnvironment(
                    new Dictionary<string, string> { ["b"] = "old" },
                    Array.Empty<string>())
                .WithCompleteEnvironmentNames(new[] { "PATH" }),
        }).Parse("b=1; env");

        Assert.True(parsed.Commands.Single().Assignments.Single().MayAffectProcessEnvironment);
    }

    [Fact]
    public void Prefix_assignment_always_reaches_the_command()
    {
        const string source = "b=1 env";
        var reaches = RunInExactEnvironment(source, new[] { "PATH" }, "b");
        Assert.True(reaches ?? true);

        var assignment = CreateParser(new[] { "PATH" }).Parse(source)
            .Commands.Single().Assignments.Single();
        Assert.Equal(ShellVariableAssignmentScope.CommandEnvironment, assignment.Scope);
        Assert.True(assignment.MayAffectProcessEnvironment);
    }

    [Theory]
    // Unexported: the decoded child does not get the assignment at all.
    [InlineData("b=1; bash -c 'env'", false)]
    // Exported: the child gets it, and the fact stays true.
    [InlineData("b=1; export b; bash -c 'env'", true)]
    // Exported on one path: the child lists the assignment with an Unknown
    // value. Before 0.4.0-beta.24 it listed nothing.
    [InlineData("if true; then export b; fi; b=1; bash -c 'env'", true)]
    [InlineData("b=1; if true; then export b; fi; bash -c 'env'", true)]
    public void Decoded_child_shell_keeps_the_fact_true(string source, bool reachesChild)
    {
        var reaches = RunInExactEnvironment(source, new[] { "PATH" }, "b");
        if (reaches is not null)
        {
            Assert.Equal(reachesChild, reaches.Value);
        }

        var parsed = CreateParser(new[] { "PATH" }).Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var child = parsed.Commands.Last();
        Assert.All(child.Assignments, a => Assert.True(a.MayAffectProcessEnvironment));
        Assert.Equal(reachesChild, child.Assignments.Count == 1);
        if (source.StartsWith("if", System.StringComparison.Ordinal) || source.Contains("fi;", System.StringComparison.Ordinal))
        {
            Assert.IsType<ShellValueDomain.Unknown>(child.Assignments.Single().EffectiveValue);
        }
    }

    [Theory]
    // Bash exports every name that it imports, so a child gets the new
    // value of an inherited name (review of #248).
    [InlineData("GIT_DIR=/tmp/nope; bash -c 'env'", "PATH,GIT_DIR", "GIT_DIR", "/tmp/nope", true)]
    [InlineData("b=1; bash -c 'env'", "PATH,b", "b", "1", true)]
    // Absent from the declared environment and not exported: no fact.
    [InlineData("b=1; bash -c 'env'", "PATH", "b", null, false)]
    // `B` is another name on Linux; the child does not get `b`, but the
    // parser cannot prove which name the shell sees on Windows.
    [InlineData("b=1; bash -c 'env'", "PATH,B", "b", null, true)]
    public void Decoded_child_gets_an_inherited_name(
        string source,
        string launchNames,
        string name,
        string? value,
        bool listed)
    {
        var names = launchNames.Split(',');
        var reaches = RunInExactEnvironment(source, names, name);
        if (reaches is not null && !launchNames.Contains('B'))
        {
            Assert.Equal(listed, reaches.Value);
        }

        var parsed = CreateParser(names).Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var child = parsed.Commands.Last();
        var assignment = child.Assignments.SingleOrDefault(a => a.Name == name);
        Assert.Equal(listed, assignment is not null);
        if (assignment is not null)
        {
            Assert.True(assignment.MayAffectProcessEnvironment);
            if (value is null)
            {
                Assert.IsType<ShellValueDomain.Unknown>(assignment.EffectiveValue);
            }
            else
            {
                Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(assignment.EffectiveValue).Value);
            }
        }
    }

    [Theory]
    // A startup override in the declared environment can export every
    // variable: `SHELLOPTS=allexport`, a `BASH_ENV` file with `set -a`, or
    // an imported function. The fact stays true (review of #248).
    [InlineData("SHELLOPTS")]
    [InlineData("BASH_ENV")]
    [InlineData("BASH_FUNC_env%%")]
    [InlineData("BASHOPTS")]
    [InlineData("ENV")]
    [InlineData("POSIXLY_CORRECT")]
    [InlineData("BASH_COMPAT")]
    [InlineData("bash_func_x%%")]
    public void Startup_override_in_the_environment_keeps_the_fact_true(string overrideName)
    {
        var parsed = CreateParser(new[] { "PATH", overrideName }).Parse("b=1; env");

        Assert.True(parsed.Commands.Single().Assignments.Single().MayAffectProcessEnvironment);
    }

    [Theory]
    [InlineData("SHELLOPTS=allexport")]
    [InlineData("BASH_ENV=SETA")]
    [InlineData("BASH_FUNC_env%%=() { export b; command env; }")]
    public void Startup_override_exports_the_variable_in_bash(string entry)
    {
        if (!BashOracle.IsAvailable())
        {
            return;
        }

        var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sst-seta-" + Guid.NewGuid().ToString("N"));
        System.IO.File.WriteAllText(scratch, "set -a\n");
        try
        {
            var startInfo = new ProcessStartInfo("env")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add("PATH=/usr/bin:/bin");
            startInfo.ArgumentList.Add(entry.Replace("SETA", scratch, StringComparison.Ordinal));
            startInfo.ArgumentList.Add("bash");
            startInfo.ArgumentList.Add("--noprofile");
            startInfo.ArgumentList.Add("--norc");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("b=1; env");
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(10_000));
            Assert.Contains("\nb=1\n", "\n" + output, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(scratch);
        }
    }

    [Theory]
    // Bash exports the variable, or the parser cannot see it. Each source
    // fails closed, so it has no assignment fact.
    [InlineData("set -a; b=1; env")]
    [InlineData("set -o allexport; b=1; env")]
    [InlineData("b=1; declare -x b; env")]
    [InlineData("b=1; typeset -x b; env")]
    [InlineData("readonly b=1; export b; env")]
    [InlineData("b=1; eval 'export b'; env")]
    [InlineData("b=1; . ./x; env")]
    [InlineData("b=1; source ./x; env")]
    [InlineData("f() { export b; }; b=1; f; env")]
    [InlineData("f() { local -x b=1; env; }; f")]
    [InlineData("b=1; { export b; }; env")]
    [InlineData("b=1; (export b; env)")]
    [InlineData("b=1; export -n b; env")]
    [InlineData("b=1; export -- b; env")]
    [InlineData("b=1; command export b; env")]
    [InlineData("b=1; command -p export b; env")]
    [InlineData("b=1; builtin export b; env")]
    [InlineData("b=1; \\export b; env")]
    [InlineData("b=1; 'export' b; env")]
    [InlineData("b=1; ex\"port\" b; env")]
    [InlineData("b=1; x=$(export b); env")]
    [InlineData("b=1; ! export b; env")]
    [InlineData("b=1; exec env")]
    public void Unmodeled_export_fails_closed(string source)
    {
        Assert.True(CreateParser(new[] { "PATH" }).Parse(source).IsUnparseable);
    }

    [Theory]
    [InlineData("PWD")]
    [InlineData("OLDPWD")]
    [InlineData("SHLVL")]
    [InlineData("_")]
    public void Bash_exports_its_own_variables(string name)
    {
        // Bash 5.2 gives these names the export attribute, also when they are
        // not in its environment. The fact stays true for them; the parser
        // also rejects assignments to these names.
        if (name != "_")
        {
            var reaches = RunInExactEnvironment(name + "=zz; env", new[] { "PATH" }, name);
            Assert.True(reaches ?? true);
        }

        Assert.True(BashChildEnvironment.IsExportedByBash(name));
        Assert.True(CreateParser(new[] { "PATH" }).Parse(name + "=zz; env").IsUnparseable);
    }

    [Fact]
    public void Ordinary_name_is_not_exported_by_bash()
    {
        Assert.False(BashChildEnvironment.IsExportedByBash("b"));
        Assert.False(BashChildEnvironment.IsExportedByBash("pwd"));
    }

    [Theory]
    [InlineData(BashInitialStateMode.Unknown)]
    [InlineData(BashInitialStateMode.IsolatedNonInteractive)]
    public void Other_initial_state_modes_give_no_false_fact(BashInitialStateMode mode)
    {
        var parsed = CreateParser(new[] { "PATH" }, mode).Parse("b=1; env");
        Assert.All(
            parsed.Commands.SelectMany(c => c.Assignments),
            a => Assert.True(a.MayAffectProcessEnvironment));
    }

    [Fact]
    public void Complete_environment_names_are_validated()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ShellLaunchEnvironment.FromCompleteEnvironmentNames(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ShellLaunchEnvironment.FromCompleteEnvironmentNames(new string[] { null! }));
        Assert.Throws<ArgumentException>(() =>
            ShellLaunchEnvironment.FromCompleteEnvironmentNames(new[] { "" }));
        Assert.Throws<ArgumentException>(() =>
            ShellLaunchEnvironment.FromCompleteEnvironmentNames(new[] { "a\0b" }));
        Assert.Throws<ArgumentException>(() =>
            new ShellLaunchEnvironment(
                    Array.Empty<KeyValuePair<string, string>>(),
                    new[] { "CDPATH" })
                .WithCompleteEnvironmentNames(new[] { "CDPATH" }));

        // Windows keeps hidden names such as `=C:`; they are allowed.
        var facts = ShellLaunchEnvironment.FromCompleteEnvironmentNames(new[] { "=C:", "PATH", "Path" });
        Assert.Equal(new[] { "=C:", "PATH", "Path" }, facts.CompleteEnvironmentNames);

        // Review of #248: on Linux, `B` and `b` are two names. A launch with
        // `B` set and `b` unset is valid.
        var mixed = new ShellLaunchEnvironment(
                Array.Empty<KeyValuePair<string, string>>(),
                new[] { "b" })
            .WithCompleteEnvironmentNames(new[] { "PATH", "B" });
        Assert.Equal(new[] { "B", "PATH" }, mixed.CompleteEnvironmentNames);
        Assert.Null(new ShellLaunchEnvironment(
            Array.Empty<KeyValuePair<string, string>>(),
            Array.Empty<string>()).CompleteEnvironmentNames);
        var withValue = new ShellLaunchEnvironment(
                new Dictionary<string, string> { ["HOME"] = "/home/test" },
                Array.Empty<string>())
            .WithCompleteEnvironmentNames(new[] { "PATH" });
        Assert.Equal(new[] { "HOME", "PATH" }, withValue.CompleteEnvironmentNames);
    }

    /// <summary>
    /// Runs the source in Bash launched with <c>env -i</c> and exactly the
    /// given environment names. True when the output of <c>env</c> holds one
    /// of <paramref name="watched"/>. Null without native Bash.
    /// </summary>
    private static bool? RunInExactEnvironment(
        string source,
        IReadOnlyList<string> names,
        params string[] watched)
    {
        if (!BashOracle.IsAvailable())
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "env",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-i");
        foreach (var name in names)
        {
            startInfo.ArgumentList.Add(name + "=" + (name == "PATH" ? "/usr/bin:/bin" : "old"));
        }

        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("--noprofile");
        startInfo.ArgumentList.Add("--norc");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(source);
        try
        {
            using var process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "bash timed out");
            return output.Split('\n').Any(line => watched.Any(w => line.StartsWith(w + "=", StringComparison.Ordinal)));
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
