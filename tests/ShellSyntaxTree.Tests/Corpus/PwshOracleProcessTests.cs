// -----------------------------------------------------------------------
// <copyright file="PwshOracleProcessTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShellSyntaxTree.Tools.PwshCorpus;
using Xunit;

namespace ShellSyntaxTree.Tests.Corpus;

public class PwshOracleProcessTests
{
    [Fact]
    public async Task Only_native_absence_and_version_mismatch_mean_unavailable()
    {
        var absent = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), $"sst-absent-{Guid.NewGuid():N}"));
        Assert.False(await PwshOracle.IsAvailableAsync(absent, CancellationToken.None));
        Assert.False(await PwshOracle.IsAvailableAsync(PwshDialect.Unknown, CancellationToken.None));
        Assert.False(await PwshOracle.IsAvailableAsync(Emit("", "", 2), CancellationToken.None));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PwshOracle.IsAvailableAsync(Emit("", "probe detail", 17), CancellationToken.None));
        Assert.Contains("version probe exited with code 17", error.Message);
        Assert.Contains("probe detail", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PwshOracle.RunRequiredAsync(absent, 10000, CancellationToken.None));
    }

    [Fact]
    public async Task A_start_failure_does_not_mean_an_absent_executable()
    {
        var directory = Directory.CreateTempSubdirectory("sst-oracle-start-");
        try
        {
            var path = Path.Combine(directory.FullName, "not-an-executable");
            await File.WriteAllTextAsync(path, "native start control");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PwshOracle.IsAvailableAsync(new ProcessStartInfo(path), CancellationToken.None));
            Assert.Contains("start failed", error.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(17)]
    public async Task An_invocation_nonzero_exit_preserves_its_native_failure(int exitCode)
    {
        Assert.True(await PwshOracle.IsAvailableAsync(Emit("", "", 0), CancellationToken.None));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PwshOracle.RunRequiredAsync(Emit("output detail", "native detail", exitCode), 10000, CancellationToken.None));
        Assert.Contains($"invocation exited with code {exitCode}", error.Message);
        Assert.Contains("output detail", error.Message);
        Assert.Contains("native detail", error.Message);
    }

    [Theory]
    [InlineData("invalid json", "invalid JSON")]
    [InlineData("null", "JSON null")]
    public async Task A_native_JSON_failure_keeps_its_response_context(string response, string expected)
    {
        var stdout = await PwshOracle.RunRequiredAsync(Emit(response, "", 0), 10000, CancellationToken.None);
        var error = Assert.Throws<InvalidOperationException>(() =>
            PwshOracle.ReadJsonResponse<PwshAliasDefinition[]>("native-child", stdout));
        Assert.Contains(expected, error.Message);
        Assert.Contains(response, error.Message);
    }

    [Fact]
    public async Task Both_pipes_drain_without_unbounded_exception_text()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PwshOracle.RunRequiredAsync(Child("flood"),
                10000, CancellationToken.None));
        Assert.Contains("exited with code 17", error.Message);
        Assert.Contains("[truncated]", error.Message);
        Assert.True(error.Message.Length < 2500, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deadline_or_caller_cancellation_cleans_up_its_direct_child(bool cancel)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
        var operation = PwshOracle.RunPowerShellAsync(Child("signal", port), cancel ? 10000 : 3000, cancellation.Token);
        Process? native = null;
        try
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            var pid = int.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                ?? throw new InvalidOperationException("No native PID."));
            native = Process.GetProcessById(pid);
            if (cancel)
                cancellation.Cancel();

            Exception error = cancel
                ? await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation)
                : await Assert.ThrowsAsync<InvalidOperationException>(async () => await operation);
            Assert.Contains("cleanup: direct child exited", error.Message);
            Assert.Matches("readers=(RanToCompletion|Canceled|Faulted)", error.Message);
            Assert.Contains(cancel ? "caller cancellation" : "process deadline 3000 ms", error.Message);
            await native.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(native.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            // Observe the actual helper even if a native acknowledgement or assertion fails.
            await operation.ContinueWith(completed => completed.Exception,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (native is not null)
            {
                if (!native.HasExited)
                    native.Kill(entireProcessTree: true);
                await native.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                native.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("alias-exit", "invocation exited with code 17")]
    [InlineData("variable-exit", "invocation exited with code 17")]
    [InlineData("alias-json", "invalid JSON")]
    [InlineData("alias-null", "JSON null")]
    [InlineData("probe-exit", "version probe exited with code 17")]
    [InlineData("probe-version", "unavailable")]
    public async Task Public_oracle_operations_preserve_native_and_JSON_failures(string scenario, string expected)
    {
        var result = await PwshOracle.RunRequiredAsync(Child("public-proof", scenario), 15000, CancellationToken.None);
        using var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(expected, json.RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("worker-proof")]
    [InlineData("eof-proof")]
    public async Task Native_phase_controls_use_the_actual_async_oracle_in_a_private_process(string mode)
    {
        using var process = Process.Start(ForCapture(Child(mode)))
            ?? throw new InvalidOperationException("The private proof did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            var text = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.ExitCode == 0, $"{text}\n{await stderr.WaitAsync(TimeSpan.FromSeconds(5))}");
            using var result = JsonDocument.Parse(text);
            Assert.True(result.RootElement.GetProperty("success").GetBoolean());
            if (mode == "eof-proof")
            {
                Assert.True(result.RootElement.GetProperty("holderExited").GetBoolean());
                Assert.Contains("stream deadline 5000 ms", result.RootElement.GetProperty("failure").GetString());
            }
            else
            {
                Assert.True(result.RootElement.GetProperty("nativeExited").GetBoolean());
                Assert.True(result.RootElement.GetProperty("readersComplete").GetBoolean());
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static ProcessStartInfo Emit(string stdout, string stderr, int exitCode) =>
        Child("emit", Encode(stdout), Encode(stderr), exitCode.ToString());

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static ProcessStartInfo Child(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OracleTestChild", "ShellSyntaxTree.OracleTestChild.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    private static ProcessStartInfo ForCapture(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return start;
    }
}
