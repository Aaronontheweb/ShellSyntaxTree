// -----------------------------------------------------------------------
// <copyright file="BashLiteralTwinBashDifferentialTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Compares each literal twin with the argv that GNU Bash really passes. Each
/// program of the corpus is a shell function that only prints its argv, and
/// <c>PATH</c> names no directory, so no external program runs.
/// </summary>
public class BashLiteralTwinBashDifferentialTests
{
    private const string Home = "/home/agent";

    private static readonly string[] Programs = { "cat", "cmd", "cp", "echo", "gh", "git", "rm", "tool" };

    private static readonly BashParser Parser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        HomeDirectory = Home,
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
        LaunchEnvironment = new ShellLaunchEnvironment(
            new[] { new KeyValuePair<string, string>("HOME", Home) },
            Array.Empty<string>()),
    });

    [BashTheory]
    [InlineData("for n in 8250 8244 8223 8208 8187; do gh api -X PATCH repos/akkadotnet/akka.net/issues/$n -f milestone=157 >/dev/null && echo \"moved $n\"; done")]
    [InlineData("for f in a.txt b.txt; do cat \"$f\"; done")]
    [InlineData("for f in 'a b.txt' plain; do cat \"$f\"; done")]
    [InlineData("for f in '*.txt' plain; do cat \"$f\"; done")]
    [InlineData("for f in \"it's\" plain; do cat \"$f\"; done")]
    [InlineData("for a in x y; do for b in 1 2; do cp \"src/$a\" \"dst/$b\"; done; done")]
    [InlineData("for a in x y; do for b in 1 2; do echo \"$a-$b\"; done; done")]
    [InlineData("for v in push fetch; do git $v origin; done")]
    [InlineData("for n in -rf build; do rm \"$n\"; done")]
    [InlineData("for n in a b; do cmd -o\"$n\" x; done")]
    [InlineData("for n in a b; do cat \"$n\" 2>&1 >/dev/null <<< x; done")]
    [InlineData("n=8250; gh api repos/x/$n")]
    [InlineData("for n in 1 2; do gh api repos/x/$n; done")]
    [InlineData("for n in a b; do cat ~/x \"$n\"; done")]
    [InlineData("for n in a b; do cat \"$HOME/x\" \"$n\"; done")]
    [InlineData("for r in 100001 100002; do echo -n \"$r: \"; gh run view $r --json headSha 2>/dev/null; echo; done")]
    [InlineData("for n in a x; do tool '$n' -o\"$n\"; done")]
    [InlineData("for n in a x; do git -C\"$n\" status; done")]
    [InlineData("for n in a x; do tool -xo\"$n\"; done")]
    [InlineData("for n in a x; do tool -o\"${n}\"; done")]
    [InlineData("for n in a x; do tool -o\"$n\"x; done")]
    public void Each_twin_has_the_argv_that_bash_passes(string source)
    {
        Assert.True(Parser.TryProjectLiteralTwins(source, out var projection));

        var checkedCommands = 0;
        foreach (var command in projection!.Commands)
        {
            var program = command.SourceOccurrence.Clause.Elements[0].Value;
            // Bash output names the program only, so check an occurrence
            // whose program is unique in the source.
            if (projection.Parsed.Commands.Count(occurrence =>
                    occurrence.Clause.Elements[0].Value == program) != 1)
            {
                continue;
            }

            var twinArgv = command.Twins.Select(SstArgv).ToArray();
            var runs = RunArgv(source, program);
            Assert.NotEmpty(runs);
            // Every argv of the real run is the argv of a twin.
            Assert.All(runs, run => Assert.Contains(run, twinArgv));

            foreach (var twin in command.Twins)
            {
                var twinRuns = RunArgv(twin.Source, program);
                Assert.NotEmpty(twinRuns);
                Assert.All(twinRuns, run => Assert.Equal(SstArgv(twin), run));
            }

            checkedCommands++;
        }

        Assert.True(checkedCommands > 0);
    }

    // Bash expands the tilde in a loop list item with `=~` or `:~`. Before
    // 0.4.0-beta.22 the parser kept the text. Since then it gives Unknown or
    // the value that Bash passes, never the text. The projection gives no
    // twins for such a source.
    [BashTheory]
    [InlineData("for n in x=~/y; do cat \"$n\"; done", "x=~/y", "x=/home/agent/y")]
    [InlineData("for n in x=~; do cat \"$n\"; done", "x=~", "x=/home/agent")]
    [InlineData("for n in a=b:~/y; do cat \"$n\"; done", "a=b:~/y", "a=b:/home/agent/y")]
    [InlineData("for n in a; do for m in x=~/y; do cat \"$n\" \"$m\"; done; done", "x=~/y", "x=/home/agent/y")]
    public void Bash_changes_a_list_item_that_gives_no_twins(
        string source,
        string parserValue,
        string bashValue)
    {
        Assert.False(Parser.TryProjectLiteralTwins(source, out _));

        var argument = Assert.Single(Parser.Parse(source).Commands).Arguments[^1];
        if (argument.Value is ShellValueDomain.Exact exact)
        {
            Assert.Equal(bashValue, exact.Value);
        }
        else
        {
            Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        }

        var run = Assert.Single(RunArgv(source, "cat"));
        Assert.Equal(bashValue, run.Split('\u001f')[^1]);
        Assert.NotEqual(parserValue, bashValue);
    }

    // With an unknown initial state, the parser reports `-o$n` as an exact
    // value, but Bash passes `-oa`. The projection gives no twins in that mode.
    [BashTheory]
    [InlineData("for n in a; do tool -o\"$n\"; done", "-o$n", "-oa")]
    [InlineData("for n in a; do tool -o\"${n}\"; done", "-o${n}", "-oa")]
    public void Bash_changes_a_word_that_the_unknown_state_reports_as_exact(
        string source,
        string parserValue,
        string bashValue)
    {
        var unknown = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            HomeDirectory = Home,
            PublishAuthoredSourceFacts = true,
        });

        Assert.False(unknown.TryProjectLiteralTwins(source, out _));
        var argument = Assert.Single(Assert.Single(unknown.Parse(source).Commands).Arguments);
        Assert.Equal(parserValue, Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value);
        Assert.Equal("tool\u001f" + bashValue, Assert.Single(RunArgv(source, "tool")));
    }

    // A `cd` without an operand goes to $HOME. With a live launch HOME, the
    // twin directory is the directory where Bash runs the command. Without
    // it, the parser uses the HomeDirectory assumption, so the projection
    // gives no twins.
    [BashTheory]
    [InlineData("cd && for n in a; do cmd \"$n\"; done")]
    [InlineData("cd -L && for n in a; do cmd \"$n\"; done")]
    [InlineData("cd -- && for n in a; do cmd \"$n\"; done")]
    [InlineData("builtin cd && for n in a; do cmd \"$n\"; done")]
    [InlineData("command cd && for n in a; do cmd \"$n\"; done")]
    [InlineData("cd >/dev/null && for n in a; do cmd \"$n\"; done")]
    [InlineData("if cd; then for n in a; do cmd \"$n\"; done; fi")]
    [InlineData("for d in x; do cd && cmd \"$d\"; done")]
    public void A_cd_to_home_gives_the_directory_that_bash_uses(string source)
    {
        var home = Directory.CreateTempSubdirectory("sst-twin-home-").FullName;
        try
        {
            var proved = new BashParser(new BashParserOptions
            {
                WorkingDirectory = "/work",
                HomeDirectory = home,
                InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
                PublishAuthoredSourceFacts = true,
                LaunchEnvironment = new ShellLaunchEnvironment(
                    new[] { new KeyValuePair<string, string>("HOME", home) },
                    Array.Empty<string>()),
            });
            Assert.True(proved.TryProjectLiteralTwins(source, out var projection));
            var twin = Assert.Single(Assert.Single(projection!.Commands).Twins);
            var run = Assert.Single(RunRecords(source, "cmd", home));
            Assert.Equal(SstArgv(twin), run.Argv);
            Assert.Equal(twin.WorkingDirectory, run.Directory);

            var assumed = new BashParser(new BashParserOptions
            {
                WorkingDirectory = "/work",
                HomeDirectory = "/home/assumed",
                InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
                PublishAuthoredSourceFacts = true,
            });
            Assert.False(assumed.TryProjectLiteralTwins(source, out _));
            var occurrence = assumed.Parse(source).Commands.Single(command =>
                command.Clause.Elements[0].Value == "cmd");
            Assert.Equal("/home/assumed",
                Assert.IsType<ShellValueDomain.Exact>(occurrence.WorkingDirectory).Value);
            Assert.NotEqual("/home/assumed", run.Directory);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static string SstArgv(BashLiteralTwin twin) =>
        string.Join("\u001f", twin.Occurrence.Clause.Elements
            .Where(element => element.Role != ClauseElementRole.Redirect)
            .Select(element => element.Value));

    private static IReadOnlyList<string> RunArgv(string source, string program) =>
        RunRecords(source, program, Home).Select(record => record.Argv).ToArray();

    private static IReadOnlyList<(string Argv, string Directory)> RunRecords(
        string source,
        string program,
        string home)
    {
        var script = new StringBuilder("exec 3>&1\n");
        foreach (var name in Programs)
        {
            script.Append(name)
                .Append("() { builtin printf '%s\\037' ")
                .Append(name)
                .Append(" \"$@\" >&3; builtin printf '\\035%s\\036' \"$PWD\" >&3; }\n");
        }

        script.Append(source).Append('\n');
        var directory = Directory.CreateTempSubdirectory("sst-twin-").FullName;
        try
        {
            var start = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = directory,
            };
            start.ArgumentList.Add("--norc");
            start.ArgumentList.Add("--noprofile");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script.ToString());
            start.Environment.Clear();
            start.Environment["HOME"] = home;
            start.Environment["PATH"] = "/nonexistent-shellsyntaxtree";

            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Bash did not exit.");
            Assert.Equal(string.Empty, error.Result);

            return output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries)
                .Select(record => record.Split('\u001d'))
                .Select(parts => (Argv: parts[0].EndsWith('\u001f') ? parts[0][..^1] : parts[0],
                    Directory: parts[1]))
                .Where(record => record.Argv.Split('\u001f')[0] == program)
                .ToArray();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>
/// A theory that needs GNU Bash at <c>/bin/bash</c> on Linux. On another
/// host, the runner reports the theory as skipped with the reason.
/// </summary>
public sealed class BashTheoryAttribute : TheoryAttribute
{
    public BashTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "The Bash differential test runs only on Linux.";
        }
        else if (!File.Exists("/bin/bash"))
        {
            Skip = "The Bash differential test needs /bin/bash.";
        }
    }
}
