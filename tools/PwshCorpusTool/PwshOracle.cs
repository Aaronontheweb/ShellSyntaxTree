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

namespace ShellSyntaxTree.Tools.PwshCorpus;

/// <summary>
/// Ground-truth validation oracle (SPEC.POWERSHELL.md §13): feeds command
/// strings to the real PowerShell parser
/// (<c>[System.Management.Automation.Language.Parser]::ParseInput</c>) via a
/// batched child-process <c>pwsh</c> invocation and reports the parse-error
/// count for each. Both the corpus tool's <c>check</c> mode and the
/// <c>PwshOracleTests</c> CI gate consume this.
/// </summary>
public static class PwshOracle
{
    // A batched ParseInput driver: reads a JSON array of command strings,
    // emits a JSON array of parse-error counts.
    private const string OracleScript = @"
param([string]$Path)
$inputs = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
$counts = foreach ($s in @($inputs)) {
    $errs = $null
    $toks = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput([string]$s, [ref]$toks, [ref]$errs)
    $errs.Count
}
,@($counts) | ConvertTo-Json -Compress
";

    /// <summary>True when a <c>pwsh</c> executable is on PATH.</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = "-NoProfile -NoLogo -Command \"exit 0\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Parse-error count from real <c>pwsh</c> for each input — 0 means the
    /// input is valid PowerShell. Returns null when <c>pwsh</c> is not
    /// available.
    /// </summary>
    public static IReadOnlyList<int>? CountParseErrors(IReadOnlyList<string> inputs)
    {
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

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -NoLogo -File \"{scriptPath}\" \"{inputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            });

            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(120000);

            var counts = JsonSerializer.Deserialize<int[]>(stdout.Trim());
            if (counts is null || counts.Length != inputs.Count)
            {
                throw new InvalidOperationException(
                    $"pwsh oracle returned {counts?.Length ?? -1} counts for {inputs.Count} inputs. Output: {stdout}");
            }

            return counts;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return null;
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(inputPath);
        }
    }

    /// <summary>
    /// The names of every alias the running <c>pwsh</c> defines — the live
    /// <c>Get-Alias</c> set, for the <c>PwshAliases</c> completeness gate
    /// (SPEC.POWERSHELL.md §6.3). Returns null when <c>pwsh</c> is absent.
    /// </summary>
    public static IReadOnlyList<string>? GetAliasNames()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = "-NoProfile -NoLogo -Command \"Get-Alias | ForEach-Object Name\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            });

            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(60000);
            return stdout.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
