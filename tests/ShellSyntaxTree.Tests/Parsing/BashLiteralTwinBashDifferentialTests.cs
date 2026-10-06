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

    private static readonly string[] Programs = { "cat", "cmd", "cp", "echo", "gh", "git", "rm" };

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

    private static string SstArgv(BashLiteralTwin twin) =>
        string.Join("\u001f", twin.Occurrence.Clause.Elements
            .Where(element => element.Role != ClauseElementRole.Redirect)
            .Select(element => element.Value));

    private static IReadOnlyList<string> RunArgv(string source, string program)
    {
        var script = new StringBuilder("exec 3>&1\n");
        foreach (var name in Programs)
        {
            script.Append(name)
                .Append("() { builtin printf '%s\\037' ")
                .Append(name)
                .Append(" \"$@\" >&3; builtin printf '\\036' >&3; }\n");
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
            start.Environment["HOME"] = Home;
            start.Environment["PATH"] = "/nonexistent-shellsyntaxtree";

            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Bash did not exit.");
            Assert.Equal(string.Empty, error.Result);

            return output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries)
                .Select(record => record.EndsWith('\u001f') ? record[..^1] : record)
                .Where(record => record.Split('\u001f')[0] == program)
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
