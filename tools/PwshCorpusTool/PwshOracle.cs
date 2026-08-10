// -----------------------------------------------------------------------
// <copyright file="PwshOracle.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShellSyntaxTree.Tools.PwshCorpus;

/// <summary>
/// Ground-truth validation oracle (SPEC.POWERSHELL.md §13): feeds command
/// strings to the real PowerShell parser
/// (<c>[System.Management.Automation.Language.Parser]::ParseInput</c>) via a
/// batched child-process invocation of the dialect-selected executable and
/// reports the parse-error count for each. Both the corpus tool's
/// <c>check</c> mode and the
/// <c>PwshOracleTests</c> CI gate consume this.
/// </summary>
public static class PwshOracle
{
    // A batched ParseInput driver: reads a JSON array of command strings,
    // emits a JSON array of parse-error counts.
    private const string OracleScript = @"
param([string]$Path)
$inputs = Get-Content -Raw -Encoding UTF8 -LiteralPath $Path | ConvertFrom-Json
$counts = foreach ($s in @($inputs)) {
    $errs = $null
    $toks = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput([string]$s, [ref]$toks, [ref]$errs)
    $errs.Count
}
,@($counts) | ConvertTo-Json -Compress
";

    /// <summary>
    /// True when the selected executable is on PATH and its version matches
    /// the requested dialect contract.
    /// </summary>
    public static bool IsAvailable(PwshDialect dialect)
    {
        var probe = VersionProbeArguments(dialect);
        return probe is not null && TryRunPowerShell(
            Executable(dialect),
            probe,
            15000,
            out _);
    }

    /// <summary>
    /// Parse-error count from the selected real PowerShell for each input —
    /// 0 means the input is valid PowerShell. Returns null when the selected
    /// executable is absent or does not satisfy the dialect's version contract.
    /// </summary>
    public static IReadOnlyList<int>? CountParseErrors(
        IReadOnlyList<string> inputs,
        PwshDialect dialect)
    {
        if (!IsAvailable(dialect))
        {
            return null;
        }

        if (inputs.Count == 0)
        {
            return Array.Empty<int>();
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"sst-oracle-{Guid.NewGuid():N}.ps1");
        var inputPath = Path.Combine(Path.GetTempPath(), $"sst-oracle-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(scriptPath, OracleScript);
            File.WriteAllText(inputPath, JsonSerializer.Serialize(inputs));

            if (!TryRunPowerShell(
                    Executable(dialect),
                    $"-NoProfile -NoLogo -NonInteractive -File \"{scriptPath}\" \"{inputPath}\"",
                    120000,
                    out var stdout))
            {
                return null;
            }

            var counts = JsonSerializer.Deserialize<int[]>(stdout.Trim());
            if (counts is null || counts.Length != inputs.Count)
            {
                throw new InvalidOperationException(
                    $"{Executable(dialect)} oracle returned {counts?.Length ?? -1} counts " +
                    $"for {inputs.Count} inputs. Output: {stdout}");
            }

            return counts;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _ = TryDelete(scriptPath);
            _ = TryDelete(inputPath);
        }
    }

    /// <summary>
    /// Every alias and definition the selected PowerShell defines — the live
    /// <c>Get-Alias</c> set for the <c>PwshAliases</c> completeness and
    /// definition gate (SPEC.POWERSHELL.md §6.3). Returns null when the
    /// selected executable is absent or incompatible.
    /// </summary>
    public static IReadOnlyList<PwshAliasDefinition>? GetAliasDefinitions(
        PwshDialect dialect)
    {
        if (!IsAvailable(dialect) ||
            !TryRunPowerShell(
                Executable(dialect),
                "-NoProfile -NoLogo -NonInteractive -Command \"@(Get-Alias | Select-Object Name,Definition) | ConvertTo-Json -Compress\"",
                60000,
                out var stdout))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PwshAliasDefinition[]>(stdout.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The names of every variable a fresh no-profile <c>pwsh</c> process
    /// defines. Returns null when the selected executable is absent or incompatible.
    /// </summary>
    public static IReadOnlyList<string>? GetVariableNames(
        PwshDialect dialect)
    {
        if (!IsAvailable(dialect) ||
            !TryRunPowerShell(
                Executable(dialect),
                "-NoProfile -NoLogo -NonInteractive -Command \"Get-Variable | ForEach-Object Name\"",
                60000,
                out var stdout))
        {
            return null;
        }

        return stdout.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Run the selected executable with <paramref name="arguments"/> and capture stdout.
    /// Returns false when the executable is absent, the run times out, or it
    /// exits non-zero. stderr is drained concurrently so a chatty child can
    /// never deadlock on a full pipe buffer; a timed-out child is killed.
    /// </summary>
    private static bool TryRunPowerShell(
        string? executable,
        string arguments,
        int timeoutMs,
        out string stdout)
    {
        stdout = string.Empty;
        if (executable is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            });

            if (process is null)
            {
                return false;
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                _ = TryKill(process);
                _ = process.WaitForExit(5000);
                _ = Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5000);
                return false;
            }

            if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5000))
            {
                _ = TryKill(process);
                return false;
            }

            stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                                   FileNotFoundException or
                                   IOException or
                                   AggregateException)
        {
            return false;
        }
    }

    private static string? VersionProbeArguments(PwshDialect dialect) => dialect switch
    {
        PwshDialect.PowerShell7 =>
            "-NoProfile -NoLogo -NonInteractive -Command \"" +
            "$v = $PSVersionTable.PSVersion; " +
            "if ($v -lt [version]'7.6.4' -or $v -ge [version]'7.7') { exit 2 }; exit 0\"",
        PwshDialect.WindowsPowerShell51 =>
            "-NoProfile -NoLogo -NonInteractive -Command \"" +
            "$v = $PSVersionTable.PSVersion; " +
            "if ($v.Major -ne 5 -or $v.Minor -ne 1) { exit 2 }; exit 0\"",
        _ => null,
    };

    private static string? Executable(PwshDialect dialect) => dialect switch
    {
        PwshDialect.PowerShell7 => "pwsh",
        PwshDialect.WindowsPowerShell51 => "powershell.exe",
        _ => null,
    };

    private static bool TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

/// <summary>An alias name and its canonical definition from real PowerShell.</summary>
public sealed record PwshAliasDefinition(string Name, string Definition);
