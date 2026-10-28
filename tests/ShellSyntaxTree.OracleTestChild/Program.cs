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
            case "holder":
                return Console.In.ReadLine() == "GO" ? int.Parse(args[1]) : 3;
            case "holder-owner":
                return HolderOwner(args[1], args[2]);
            case "signal":
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, int.Parse(args[1]));
                    client.ReceiveTimeout = 20000;
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
                    writer.WriteLine(Environment.ProcessId);
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
                var holderStart = Child("holder-owner", args[1], args[2]);
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
                return EofProof(0);
            case "eof-nonzero-proof":
                return EofProof(17);
            default:
                return 2;
        }
    }

    private static int HolderOwner(string port, string exitCode)
    {
        var start = Child("holder", exitCode);
        start.RedirectStandardInput = true;
        using var holder = Process.Start(start)
            ?? throw new InvalidOperationException("The pipe-holder did not start.");
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, int.Parse(port));
            client.ReceiveTimeout = 20000;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine($"{Environment.ProcessId} {holder.Id}");
            if (reader.ReadLine() != "OWNED")
                return 3;
            Console.Out.WriteLine("OWNED");
            if (reader.ReadLine() != "GO")
                return 3;
            holder.StandardInput.WriteLine("GO");
            if (!holder.WaitForExit(5000))
                throw new InvalidOperationException("The pipe-holder did not exit after release.");
            // This process starts the holder, so its exit code comes from the real parent on every host.
            writer.WriteLine($"EXIT {holder.Id} {holder.ExitCode}");
            return 0;
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill(entireProcessTree: true);
            if (!holder.WaitForExit(5000))
                throw new InvalidOperationException("The holder owner did not complete cleanup.");
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

    private static int EofProof(int requestedExitCode)
    {
        // Linux reaps the orphan owner here. The real owner reports the holder's native exit status on every host.
        if (OperatingSystem.IsLinux() && Prctl(36, 1, 0, 0, 0) != 0)
            throw new InvalidOperationException("The private subreaper did not start.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
        var operation = PwshOracle.RunPowerShellAsync(Child("parent", port, requestedExitCode.ToString()), 10000, CancellationToken.None);
        using var client = listener.AcceptTcpClient();
        client.ReceiveTimeout = 15000;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        var identities = (reader.ReadLine() ?? throw new InvalidOperationException("No owner and holder PIDs.")).Split(' ');
        var ownerPid = int.Parse(identities[0]);
        var holderPid = int.Parse(identities[1]);
        using var owner = OperatingSystem.IsLinux() ? null : Process.GetProcessById(ownerPid);
        // Windows ExitCode requires a retained handle. Acquire it before the owner permits its parent to exit.
        if (OperatingSystem.IsWindows() && owner!.SafeHandle.IsInvalid)
            throw new InvalidOperationException("The owner's Windows process handle is invalid.");
        writer.WriteLine("OWNED");
        string failure;
        bool ownerExited;
        bool holderExited = false;
        int holderExitCode = -1;
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
            try
            {
                writer.WriteLine("GO");
                var report = (reader.ReadLine() ?? throw new InvalidOperationException("No holder exit report.")).Split(' ');
                if (report.Length != 3 || report[0] != "EXIT" || int.Parse(report[1]) != holderPid)
                    throw new InvalidOperationException("The exit report does not identify the owned holder.");
                holderExitCode = int.Parse(report[2]);
                holderExited = true;
            }
            finally
            {
                if (OperatingSystem.IsLinux())
                    ownerExited = WaitPid(ownerPid, out var status, 0) == ownerPid && status == 0;
                else
                    ownerExited = owner!.WaitForExit(5000)
                        && (!OperatingSystem.IsWindows() || owner.ExitCode == 0);
            }
        }

        if (holderExitCode != 0)
            throw new InvalidOperationException(
                $"The pipe-holder exited with code {holderExitCode}; holderExited={holderExited}; ownerExited={ownerExited}.");
        if (!ownerExited || !holderExited || !failure.Contains("stream deadline 5000 ms", StringComparison.Ordinal)
            || !failure.Contains("readers=Canceled", StringComparison.Ordinal))
            throw new InvalidOperationException($"The EOF control did not complete cleanup: {failure}");
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, holderExited, holderExitCode, ownerExited, holderPid, ownerPid, failure }));
        return 0;
    }

    private static ProcessStartInfo Child(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    private static string Decode(string text) => Encoding.UTF8.GetString(Convert.FromBase64String(text));

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, int arg2, int arg3, int arg4, int arg5);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int pid, out int status, int options);
}
