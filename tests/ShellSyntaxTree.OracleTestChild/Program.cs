// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShellSyntaxTree.Tools.PwshCorpus;

namespace ShellSyntaxTree.OracleTestChild;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args[0] == "-NoProfile")
            return MockPowerShell(args);
        switch (args[0])
        {
            case "emit":
                Console.Out.Write(Decode(args[1]));
                Console.Error.Write(Decode(args[2]));
                return int.Parse(args[3]);
            case "flood":
                Console.Out.Write(new string('o', 65536));
                Console.Error.Write(new string('e', 65536));
                return 17;
            case "signal":
            case "holder":
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, int.Parse(args[1]));
                    client.ReceiveTimeout = 20000;
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
                    writer.WriteLine(Environment.ProcessId);
                    if (args[0] == "holder")
                    {
                        if (reader.ReadLine() != "OWNED")
                            return 3;
                        Console.Out.WriteLine("OWNED");
                    }
                    if (reader.ReadLine() != "GO")
                        return 3;
                    if (args[0] == "signal")
                    {
                        Console.Out.Write("[]");
                        Console.Error.Write("native stderr");
                    }
                    return 0;
                }
            case "parent":
                var holderStart = Child("holder", args[1]);
                holderStart.RedirectStandardOutput = true;
                using (var holder = Process.Start(holderStart)
                       ?? throw new InvalidOperationException("The holder did not start."))
                {
                    if (holder.StandardOutput.ReadLine() != "OWNED")
                    {
                        if (!holder.HasExited)
                            holder.Kill(entireProcessTree: true);
                        if (!holder.WaitForExit(5000))
                            throw new InvalidOperationException("The holder did not exit before ownership transfer.");
                        return 4;
                    }
                    Console.Out.Write("[]");
                }
                return 0;
            case "public-proof":
                return PublicProof(args[1]);
            case "worker-proof":
                return WorkerProof();
            case "eof-proof":
                return EofProof();
            default:
                return 2;
        }
    }

    private static int MockPowerShell(string[] args)
    {
        var scenario = Environment.GetEnvironmentVariable("SST_ORACLE_SCENARIO");
        var command = string.Join(" ", args);
        if (command.Contains("PSVersionTable", StringComparison.Ordinal))
        {
            if (scenario == "probe-version")
                return 2;
            if (scenario != "probe-exit")
                return 0;
        }
        else if (scenario == "alias-json")
        {
            Console.Out.Write("malformed native JSON");
            return 0;
        }
        else if (scenario == "alias-null")
        {
            Console.Out.Write("null");
            return 0;
        }
        Console.Error.Write("public native failure");
        return 17;
    }

    private static int PublicProof(string scenario)
    {
        var directory = Directory.CreateTempSubdirectory("sst-oracle-public-");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
                File.Copy(source, Path.Combine(directory.FullName, Path.GetFileName(source)));
            var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
            var apphost = Path.Combine(AppContext.BaseDirectory, "ShellSyntaxTree.OracleTestChild" + suffix);
            var mock = Path.Combine(directory.FullName, "pwsh" + suffix);
            File.Copy(apphost, mock);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(mock, File.GetUnixFileMode(apphost));
            // The xUnit host never changes PATH. This one private child owns the mock host and its directory.
            Environment.SetEnvironmentVariable("PATH", directory.FullName + Path.PathSeparator + originalPath);
            Environment.SetEnvironmentVariable("SST_ORACLE_SCENARIO", scenario);
            string outcome;
            try
            {
                bool unavailable = scenario == "variable-exit"
                    ? PwshOracle.GetVariableNamesAsync(PwshDialect.PowerShell7, CancellationToken.None).GetAwaiter().GetResult() is null
                    : PwshOracle.GetAliasDefinitionsAsync(PwshDialect.PowerShell7, CancellationToken.None).GetAwaiter().GetResult() is null;
                outcome = unavailable ? "unavailable" : "unexpected successful response";
            }
            catch (InvalidOperationException ex)
            {
                outcome = ex.Message;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, outcome }));
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("SST_ORACLE_SCENARIO", null);
            directory.Delete(recursive: true);
        }
    }

    private static int WorkerProof()
    {
        // This private process owns the constraint. The parallel xUnit host keeps its original worker settings.
        ThreadPool.GetMaxThreads(out _, out var ioMax);
        if (!ThreadPool.SetMinThreads(1, 1) || !ThreadPool.SetMaxThreads(1, ioMax))
            throw new InvalidOperationException("The private worker constraint failed.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
        using var readsStarted = new ManualResetEventSlim();
        var operation = Task.Run(async () =>
        {
            var pending = PwshOracle.RunPowerShellAsync(Child("signal", port), 10000, CancellationToken.None);
            // The child waits for GO, so the first incomplete await follows creation of both pipe readers.
            readsStarted.Set();
            return await pending.ConfigureAwait(false);
        });
        using var client = listener.AcceptTcpClient();
        client.ReceiveTimeout = 15000;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        var pid = int.Parse(reader.ReadLine() ?? throw new InvalidOperationException("No native PID."));
        using var native = Process.GetProcessById(pid);
        try
        {
            if (!readsStarted.Wait(15000))
                throw new InvalidOperationException("The reader phase did not start.");
            if (native.HasExited)
            {
                _ = operation.GetAwaiter().GetResult();
                throw new InvalidOperationException("The native child exited before its GO phase.");
            }
            writer.WriteLine("GO");
            // Only the private entry thread blocks. The sole worker remains available for the actual async oracle.
            var result = operation.GetAwaiter().GetResult();
            if (result is not { ExitCode: 0, Stdout: "[]", Stderr: "native stderr" } || !native.WaitForExit(5000))
                throw new InvalidOperationException("The sole-worker native proof failed.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, nativeExited = native.HasExited, readersComplete = true }));
            return 0;
        }
        finally
        {
            if (!native.HasExited)
                native.Kill(entireProcessTree: true);
            if (!native.WaitForExit(5000))
                throw new InvalidOperationException("The worker control did not clean up its direct child.");
        }
    }

    private static int EofProof()
    {
        // Linux delegates orphan reaping to this private process. Other hosts retain a native process handle.
        if (OperatingSystem.IsLinux() && Prctl(36, 1, 0, 0, 0) != 0)
            throw new InvalidOperationException("The private subreaper did not start.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
        var operation = PwshOracle.RunPowerShellAsync(Child("parent", port), 10000, CancellationToken.None);
        using var client = listener.AcceptTcpClient();
        client.ReceiveTimeout = 15000;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        var pid = int.Parse(reader.ReadLine() ?? throw new InvalidOperationException("No holder PID."));
        using var holder = OperatingSystem.IsLinux() ? null : Process.GetProcessById(pid);
        writer.WriteLine("OWNED");
        string failure;
        bool holderExited;
        try
        {
            _ = operation.GetAwaiter().GetResult();
            failure = "The inherited pipe did not trigger its stream deadline.";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            writer.WriteLine("GO");
            if (OperatingSystem.IsLinux())
                holderExited = WaitPid(pid, out var status, 0) == pid && status == 0;
            else
                holderExited = holder!.WaitForExit(5000) && holder.ExitCode == 0;
        }

        if (!holderExited || !failure.Contains("stream deadline 5000 ms", StringComparison.Ordinal)
            || !failure.Contains("readers=Canceled", StringComparison.Ordinal))
            throw new InvalidOperationException($"The EOF control did not complete cleanup: {failure}");
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, holderExited, failure }));
        return 0;
    }

    private static ProcessStartInfo Child(string mode, string argument)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(argument);
        return start;
    }

    private static string Decode(string text) => Encoding.UTF8.GetString(Convert.FromBase64String(text));

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, int arg2, int arg3, int arg4, int arg5);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int pid, out int status, int options);
}
