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
using System.Threading;
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
# Windows PowerShell 5.1's pipeline binder can adapt the nested array into a
# { value, Count } object here. The values are parser-produced integers, so
# emitting the JSON array directly is both dialect-neutral and unambiguous.
[Console]::Out.Write('[' + (($counts | ForEach-Object { [string]$_ }) -join ',') + ']')
";

    /// <summary>
    /// True when the selected executable is on PATH and its version matches
    /// the requested dialect contract. Other native failures throw with context.
    /// </summary>
    public static Task<bool> IsAvailableAsync(PwshDialect dialect, CancellationToken cancellationToken) =>
        IsAvailableAsync(CreateVersionProbe(dialect), cancellationToken);

    internal static async Task<bool> IsAvailableAsync(
        ProcessStartInfo? probe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (probe is null)
            return false;

        var result = await RunPowerShellAsync(probe, 15000, cancellationToken).ConfigureAwait(false);
        if (result is null || result.ExitCode == 2)
            return false;

        RequireSuccessfulExit(probe.FileName, "version probe", result);
        return true;
    }

    /// <summary>
    /// Parse-error count from the selected real PowerShell for each input —
    /// 0 means the input is valid PowerShell. Returns null when the selected
    /// executable is absent or does not satisfy the dialect's version contract.
    /// Throws when a compatible executable starts but the oracle invocation or
    /// response fails.
    /// </summary>
    public static async Task<IReadOnlyList<int>?> CountParseErrorsAsync(
        IReadOnlyList<string> inputs,
        PwshDialect dialect,
        CancellationToken cancellationToken)
    {
        if (!await IsAvailableAsync(dialect, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (inputs.Count == 0)
        {
            return Array.Empty<int>();
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"sst-oracle-{Guid.NewGuid():N}.ps1");
        var inputPath = Path.Combine(Path.GetTempPath(), $"sst-oracle-{Guid.NewGuid():N}.json");
        var stdout = string.Empty;
        try
        {
            File.WriteAllText(scriptPath, OracleScript);
            File.WriteAllText(inputPath, JsonSerializer.Serialize(inputs));

            stdout = await RunRequiredAsync(
                CreateInvocation(dialect,
                    "-NoProfile -NoLogo -NonInteractive -ExecutionPolicy Bypass " +
                    $"-File \"{scriptPath}\" \"{inputPath}\""),
                120000, cancellationToken).ConfigureAwait(false);

            var counts = ReadJsonResponse<int[]>(Executable(dialect), stdout);
            if (counts.Length != inputs.Count)
            {
                throw new InvalidOperationException(
                    $"{Executable(dialect)} oracle returned {counts.Length} counts " +
                    $"for {inputs.Count} inputs. Output: {Excerpt(stdout)}");
            }

            return counts;
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
    /// selected executable is absent or incompatible. Invocation and JSON failures throw with context.
    /// </summary>
    public static async Task<IReadOnlyList<PwshAliasDefinition>?> GetAliasDefinitionsAsync(
        PwshDialect dialect, CancellationToken cancellationToken)
    {
        if (!await IsAvailableAsync(dialect, cancellationToken).ConfigureAwait(false))
            return null;

        var stdout = await RunRequiredAsync(
            CreateInvocation(dialect,
                "-NoProfile -NoLogo -NonInteractive -Command \"@(Get-Alias | Select-Object Name,Definition) | ConvertTo-Json -Compress\""),
            60000, cancellationToken).ConfigureAwait(false);
        return ReadJsonResponse<PwshAliasDefinition[]>(Executable(dialect), stdout);
    }

    /// <summary>
    /// The names of every variable a fresh no-profile <c>pwsh</c> process
    /// defines. Returns null when the selected executable is absent or incompatible.
    /// Invocation failures throw with context.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> GetVariableNamesAsync(
        PwshDialect dialect, CancellationToken cancellationToken)
    {
        if (!await IsAvailableAsync(dialect, cancellationToken).ConfigureAwait(false))
            return null;

        var stdout = await RunRequiredAsync(
            CreateInvocation(dialect,
                "-NoProfile -NoLogo -NonInteractive -Command \"Get-Variable | ForEach-Object Name\""),
            60000, cancellationToken).ConfigureAwait(false);
        return stdout.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static T ReadJsonResponse<T>(string executable, string stdout) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(stdout.Trim())
                ?? throw new InvalidOperationException($"{executable} oracle returned JSON null. Output: {Excerpt(stdout)}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{executable} oracle returned invalid JSON. Output: {Excerpt(stdout)}", ex);
        }
    }

    internal static async Task<string> RunRequiredAsync(
        ProcessStartInfo invocation, int timeoutMs, CancellationToken cancellationToken)
    {
        var result = await RunPowerShellAsync(invocation, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (result is null)
            throw new InvalidOperationException($"{invocation.FileName} oracle executable disappeared after its compatible-host probe.");
        RequireSuccessfulExit(invocation.FileName, "invocation", result);
        return result.Stdout;
    }

    private static void RequireSuccessfulExit(string executable, string phase, NativeOracleResult result)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{executable} oracle {phase} exited with code {result.ExitCode}. " +
                $"stdout: {Excerpt(result.Stdout)}; stderr: {Excerpt(result.Stderr)}");
    }

    private static ProcessStartInfo? CreateVersionProbe(PwshDialect dialect) =>
        VersionProbeArguments(dialect) is { } arguments ? CreateInvocation(dialect, arguments) : null;

    private static ProcessStartInfo CreateInvocation(PwshDialect dialect, string arguments) =>
        new(Executable(dialect), arguments);

    // The public library remains synchronous. Only this dev-only native oracle owns child processes.
    internal static async Task<NativeOracleResult?> RunPowerShellAsync(
        ProcessStartInfo startInfo, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        var elapsed = Stopwatch.StartNew();
        Process? child;
        try
        {
            child = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2 || OperatingSystem.IsWindows() && ex.NativeErrorCode == 3)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{startInfo.FileName} oracle start failed: {ex.Message}", ex);
        }

        using var process = child ?? throw new InvalidOperationException($"{startInfo.FileName} oracle process did not start.");
        using var readCancellation = new CancellationTokenSource();
        Task<string> stdout = Task.FromResult(string.Empty);
        Task<string> stderr = Task.FromResult(string.Empty);
        var phase = "pipe setup";
        try
        {
            stdout = process.StandardOutput.ReadToEndAsync(readCancellation.Token);
            stderr = process.StandardError.ReadToEndAsync(readCancellation.Token);
            var readers = Task.WhenAll(stdout, stderr);
            phase = $"process deadline {timeoutMs} ms";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            phase = "stream deadline 5000 ms";
            await readers.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return new NativeOracleResult(process.ExitCode,
                await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or InvalidOperationException)
        {
            var readers = Task.WhenAll(stdout, stderr);
            var states = $"stdout={stdout.Status}, stderr={stderr.Status}";
            var cleanup = await CleanupAsync(process, readers, readCancellation).ConfigureAwait(false);
            var output = stdout.IsCompletedSuccessfully ? Excerpt(await stdout.ConfigureAwait(false)) : "<incomplete>";
            var error = stderr.IsCompletedSuccessfully ? Excerpt(await stderr.ConfigureAwait(false)) : "<incomplete>";
            var cause = cancellationToken.IsCancellationRequested ? "caller cancellation" : "failure";
            var detail = $"{startInfo.FileName} oracle {phase}: {cause} after {elapsed.ElapsedMilliseconds} ms; " +
                         $"{states}; {cleanup}; stdout: {output}; stderr: {error}";
            if (cancellationToken.IsCancellationRequested && readers.IsCompleted && process.HasExited)
                throw new OperationCanceledException(detail, ex, cancellationToken);
            throw new InvalidOperationException(detail, ex);
        }
    }

    private static async Task<string> CleanupAsync(
        Process process, Task readers, CancellationTokenSource readCancellation)
    {
        var detail = "cleanup:";
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(5000);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            detail += $" direct child exited {process.ExitCode};";
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            detail += $" direct child not confirmed: {ex.Message};";
        }

        // An exited parent cannot supply descendant ownership. Cancel this call's reads separately from child exit.
        await readCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await readers.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            detail += $" readers={readers.Status}";
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or InvalidOperationException)
        {
            detail += $" readers={readers.Status}: {ex.GetType().Name}";
        }
        return detail;
    }

    private static string Excerpt(string text) => text.Length <= 1024 ? text : text[..1024] + " [truncated]";

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

    private static string Executable(PwshDialect dialect) => dialect switch
    {
        PwshDialect.PowerShell7 => "pwsh",
        PwshDialect.WindowsPowerShell51 => "powershell.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

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

internal sealed record NativeOracleResult(int ExitCode, string Stdout, string Stderr);
