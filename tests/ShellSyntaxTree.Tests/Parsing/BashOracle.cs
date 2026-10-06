// -----------------------------------------------------------------------
// <copyright file="BashOracle.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Runs a source in real Bash (<c>bash --noprofile --norc -c</c>) with
/// <c>HOME=/home/test</c>, so a test proves the Bash behavior that it pins.
/// Without native Bash (Windows), each check returns at once and the parser
/// assertions still run.
/// </summary>
internal static class BashOracle
{
    internal const string Home = "/home/test";

    // Every program and `echo`/`printf` only log their argv. PATH names a
    // missing folder, so no external program runs.
    private const string LoggingPrelude =
        "__log(){ local IFS=$'\\x1f'; builtin printf '%s\\x1e' \"$*\" >>\"$SST_ORACLE_LOG\"; }\n" +
        "command_not_found_handle(){ __log \"$@\"; return 0; }\n" +
        "echo(){ __log echo \"$@\"; }\n" +
        "printf(){ __log printf \"$@\"; }\n";

    internal static bool IsAvailable() =>
        !OperatingSystem.IsWindows() && Run("true") is { ExitCode: 0 };

    internal static void AssertPrints(string source, string expected)
    {
        if (!IsAvailable())
        {
            return;
        }

        var result = Run(source)!.Value;
        Assert.True(result.ExitCode == 0, $"bash failed: {result.StandardError}");
        Assert.True(string.IsNullOrEmpty(result.StandardError), result.StandardError);
        Assert.Equal(expected, result.StandardOutput.TrimEnd('\n'));
    }

    internal static void AssertWritesToStandardError(string source, string expected)
    {
        if (!IsAvailable())
        {
            return;
        }

        var result = Run(source)!.Value;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(expected, result.StandardError);
    }

    internal static void AssertSucceeds(string source)
    {
        if (!IsAvailable())
        {
            return;
        }

        var result = Run(source)!.Value;
        Assert.True(result.ExitCode == 0, $"bash failed: {result.StandardError}");
    }

    /// <summary>
    /// The argv of each command that Bash runs for <paramref name="source"/>,
    /// in order. Nothing real runs: every program and <c>echo</c>/<c>printf</c>
    /// only log. Null without native Bash.
    /// </summary>
    internal static IReadOnlyList<string[]>? LoggedCommands(string source)
    {
        if (!IsAvailable())
        {
            return null;
        }

        var folder = Path.Combine(Path.GetTempPath(), "sst-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = Path.Combine(folder, "log");
            File.WriteAllText(log, string.Empty);
            Run(
                LoggingPrelude + source,
                folder,
                new Dictionary<string, string>
                {
                    ["PATH"] = Path.Combine(folder, "missing"),
                    ["SST_ORACLE_LOG"] = log,
                });
            var commands = new List<string[]>();
            foreach (var record in File.ReadAllText(log).Split('\u001e'))
            {
                if (record.Length > 0)
                {
                    commands.Add(record.Split('\u001f'));
                }
            }

            return commands;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ProcessResult? Run(
        string source,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--noprofile");
        startInfo.ArgumentList.Add("--norc");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(source);
        startInfo.Environment["HOME"] = Home;
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "bash oracle timed out");
            return new ProcessResult(process.ExitCode, standardOutput, standardError);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No bash on this machine: the parser assertions still run.
            return null;
        }
    }

    internal readonly record struct ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
