// -----------------------------------------------------------------------
// <copyright file="ShellValueOracleTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class ShellValueOracleTests
{
    [Theory]
    [InlineData("cat <<EOF\n$(printf executed)\nEOF", "executed")]
    [InlineData("cat <<E\"OF\"\n$(printf executed)\nEOF", "$(printf executed)")]
    [InlineData("cat <<E\\OF\n$(printf executed)\nEOF", "$(printf executed)")]
    [InlineData("cat <<EOF\n'$(printf executed)'\nEOF", "'executed'")]
    [InlineData("cat <<EOF\n\\$(printf executed)\nEOF", "$(printf executed)")]
    [InlineData("cat <<EOF\n\\\\$(printf executed)\nEOF", "\\executed")]
    [InlineData("cat <<-EOF\n\t$(printf executed)\n\tEOF", "executed")]
    public void Bash_heredoc_expansion_matches_delimiter_and_body_rules(
        string source,
        string expected)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        Assert.Equal(expected, Run("bash", "-c", source));
    }

    [Theory]
    [InlineData("cat <<EOF >out\nbody\nEOF")]
    [InlineData("cat <<EOF; evil\nbody\nEOF")]
    [InlineData("cat <<EOF | sh\nbody\nEOF")]
    [InlineData("cat <<A <<B\na\nA\nb\nB")]
    public void Bash_accepts_heredoc_header_forms_the_bounded_parser_declines(
        string source)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var result = RunUnchecked("bash", "-n", "-c", source);

        Assert.Equal(0, result.ExitCode);
        Assert.True(
            string.IsNullOrEmpty(result.StandardError),
            $"bash syntax oracle wrote to stderr: {result.StandardError}");
    }

    [Fact]
    public void Standalone_escapes_are_literal_in_both_shells()
    {
        if (IsNativeBashAvailable())
        {
            var bash = Run("bash", "-c", "printf '<%s>\\n' \\$HOME");
            Assert.Equal("<$HOME>", bash);
        }

        if (IsAvailable("pwsh") && File.Exists("/usr/bin/printf"))
        {
            var pwsh = Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "& /usr/bin/printf '<%s>\\n' `$HOME");
            Assert.Equal("<$HOME>", pwsh);
        }
    }

    [Fact]
    public void Bash_escape_and_adjacent_quote_produce_one_literal_argument()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "-c",
            "printf '<%s>\\n' --data=@\\$HOME\".json\"");

        Assert.Equal("<--data=@$HOME.json>", output);
    }

    [Fact]
    public void Static_mixed_quoting_and_within_token_escapes_are_literal()
    {
        if (IsNativeBashAvailable())
        {
            var bash = Run(
                "bash",
                "-c",
                "printf '<%s>\\n' pre\"mid\"'post'\\ value pre\\$HOMEpost");
            Assert.Equal(
                new[] { "<premidpost value>", "<pre$HOMEpost>" },
                Lines(bash));
        }

        if (IsAvailable("pwsh") && File.Exists("/usr/bin/printf"))
        {
            var pwsh = Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "& /usr/bin/printf '<%s>\\n' pre\"mid\"'post' pre`$HOMEpost");
            Assert.Equal(
                new[] { "<premidpost>", "<pre$HOMEpost>" },
                Lines(pwsh));
        }
    }

    [Fact]
    public void Literal_and_expandable_fragments_compose_in_both_shells()
    {
        if (IsNativeBashAvailable())
        {
            var bash = Run(
                "bash",
                "-c",
                "HOME=/oracle/home; printf '<%s>\\n' \"prefix-$HOME-suffix\"");
            Assert.Equal("<prefix-/oracle/home-suffix>", bash);
        }

        if (IsAvailable("pwsh") && File.Exists("/usr/bin/printf"))
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            Assert.False(string.IsNullOrEmpty(home));
            var pwsh = Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "& /usr/bin/printf '<%s>\\n' \"prefix-$HOME-suffix\"");
            Assert.Equal($"<prefix-{home}-suffix>", pwsh);
        }
    }

    [Fact]
    public void Runtime_parameter_forms_execute_in_both_shells()
    {
        if (IsNativeBashAvailable())
        {
            var bash = Run(
                "bash",
                "-c",
                "printf '<%s>\\n' \"$?\" \"$#\" \"$1\" \"${10}\" \"$*\" \"$@\"",
                "oracle",
                "one",
                "two",
                "three",
                "four",
                "five",
                "six",
                "seven",
                "eight",
                "nine",
                "ten");
            Assert.Equal(
                new[]
                {
                    "<0>",
                    "<10>",
                    "<one>",
                    "<ten>",
                    "<one two three four five six seven eight nine ten>",
                    "<one>",
                    "<two>",
                    "<three>",
                    "<four>",
                    "<five>",
                    "<six>",
                    "<seven>",
                    "<eight>",
                    "<nine>",
                    "<ten>",
                },
                Lines(bash));
        }

        if (IsAvailable("pwsh") && File.Exists("/usr/bin/printf"))
        {
            var pwsh = Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "$1='one'; $é='unicode'; $global:scoped='scoped'; ${braced-name}='braced'; "
                + "& /usr/bin/printf '<%s>\\n' $? $1 $é $global:scoped ${braced-name}");
            Assert.Equal(
                new[] { "<True>", "<one>", "<unicode>", "<scoped>", "<braced>" },
                Lines(pwsh));
        }
    }

    [Fact]
    public void Incomplete_and_escaped_braced_interpolation_differ_in_both_shells()
    {
        if (IsNativeBashAvailable())
        {
            var incomplete = RunUnchecked(
                "bash",
                "-n",
                "-c",
                "printf '<%s>\\n' \"${HOME\"");
            Assert.NotEqual(0, incomplete.ExitCode);

            var escaped = Run(
                "bash",
                "-c",
                "printf '<%s>\\n' \"\\${HOME\"");
            Assert.Equal("<${HOME>", escaped);
        }

        if (IsAvailable("pwsh") && File.Exists("/usr/bin/printf"))
        {
            var incomplete = RunUnchecked(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "Write-Output \"${HOME\"");
            Assert.NotEqual(0, incomplete.ExitCode);

            var escaped = Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "& /usr/bin/printf '<%s>\\n' \"`${HOME\"");
            Assert.Equal("<${HOME>", escaped);
        }
    }

    [Fact]
    public void Provider_looking_values_are_shell_and_consumer_specific()
    {
        if (IsNativeBashAvailable())
        {
            var bash = Run(
                "bash",
                "-c",
                "printf '<%s>\\n' filesystem::/safe \"FileSystem::/tmp\"");
            Assert.Equal(
                new[] { "<filesystem::/safe>", "<FileSystem::/tmp>" },
                Lines(bash));
        }

        if (IsAvailable("pwsh")
            && File.Exists("/usr/bin/printf")
            && Directory.Exists("/tmp"))
        {
            var native = Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "& /usr/bin/printf '<%s>\\n' FileSystem::/tmp \"FileSystem::/tmp\"");
            var cmdlet = Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "(Get-Item 'FileSystem::/tmp').FullName");

            Assert.Equal(
                new[] { "<FileSystem::/tmp>", "<FileSystem::/tmp>" },
                Lines(native));
            Assert.Equal("/tmp", cmdlet);
        }
    }

    [Fact]
    public void Adjacent_redirect_fragments_form_one_literal_target_in_both_shells()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "shellsyntaxtree-oracle-" + Guid.NewGuid().ToString("N"));
        var bashDirectory = Path.Combine(root, "bash");
        var pwshDirectory = Path.Combine(root, "pwsh");
        Directory.CreateDirectory(bashDirectory);
        Directory.CreateDirectory(pwshDirectory);
        try
        {
            if (IsNativeBashAvailable())
            {
                RunInWorkingDirectory(
                    "bash",
                    bashDirectory,
                    "-c",
                    "printf bash > \\$HOME\".txt\"");
                Assert.Equal(
                    "bash",
                    File.ReadAllText(Path.Combine(bashDirectory, "$HOME.txt")));
            }

            if (IsAvailable("pwsh"))
            {
                RunInWorkingDirectory(
                    "pwsh",
                    pwshDirectory,
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    "Write-Output pwsh > `$HOME\".txt\"");
                Assert.Equal(
                    "pwsh",
                    File.ReadAllText(Path.Combine(pwshDirectory, "$HOME.txt")).Trim());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Wildcard_argument_and_redirect_contexts_diverge()
    {
        if (Path.DirectorySeparatorChar != '/')
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "shellsyntaxtree-oracle-" + Guid.NewGuid().ToString("N"));
        var bashDirectory = Path.Combine(root, "bash");
        var pwshDirectory = Path.Combine(root, "pwsh");
        Directory.CreateDirectory(bashDirectory);
        Directory.CreateDirectory(pwshDirectory);
        try
        {
            if (IsNativeBashAvailable())
            {
                File.WriteAllText(Path.Combine(bashDirectory, "a.txt"), "a");
                File.WriteAllText(Path.Combine(bashDirectory, "b.txt"), "b");
                var ambiguous = RunUncheckedInWorkingDirectory(
                    "bash",
                    bashDirectory,
                    "-c",
                    "printf ambiguous > *.txt");
                Assert.NotEqual(0, ambiguous.ExitCode);

                RunInWorkingDirectory(
                    "bash",
                    bashDirectory,
                    "-c",
                    "printf literal > \"*.txt\"");
                Assert.Equal(
                    "literal",
                    File.ReadAllText(Path.Combine(bashDirectory, "*.txt")));
            }

            if (IsAvailable("pwsh") && File.Exists("/usr/bin/printf"))
            {
                var matchedPath = Path.Combine(pwshDirectory, "a.txt");
                File.WriteAllText(matchedPath, "a");
                var nativeUnquoted = RunInWorkingDirectory(
                    "pwsh",
                    pwshDirectory,
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    "& /usr/bin/printf '<%s>\\n' *.txt");
                var nativeQuoted = RunInWorkingDirectory(
                    "pwsh",
                    pwshDirectory,
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    "& /usr/bin/printf '<%s>\\n' \"*.txt\"");
                var cmdlet = RunInWorkingDirectory(
                    "pwsh",
                    pwshDirectory,
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    "(Resolve-Path -Path '*.txt').Path; Test-Path -LiteralPath '*.txt'");

                Assert.Equal("<a.txt>", nativeUnquoted);
                Assert.Equal("<*.txt>", nativeQuoted);
                Assert.Equal(
                    new[] { matchedPath, "False" },
                    Lines(cmdlet));

                RunInWorkingDirectory(
                    "pwsh",
                    pwshDirectory,
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    "Write-Output redirected > \"*.txt\"");
                Assert.Equal("redirected", File.ReadAllText(matchedPath).Trim());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PowerShell_redirects_apply_tilde_provider_and_psdrive_semantics()
    {
        if (!IsAvailable("pwsh") || Path.DirectorySeparatorChar != '/')
        {
            return;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        Assert.False(string.IsNullOrEmpty(home));
        var root = Path.Combine(
            Path.GetTempPath(),
            "shellsyntaxtree-oracle-" + Guid.NewGuid().ToString("N"));
        var tildeFileName = ".shellsyntaxtree-oracle-" + Guid.NewGuid().ToString("N") + ".txt";
        var tildePath = Path.Combine(home!, tildeFileName);
        Directory.CreateDirectory(root);
        try
        {
            var escapedRoot = root.Replace("'", "''", StringComparison.Ordinal);
            Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                $"Write-Output provider > 'FileSystem::{escapedRoot}/provider.txt'");
            Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                $"New-PSDrive -Name SST -PSProvider FileSystem -Root '{escapedRoot}' | Out-Null; "
                + "Write-Output drive > 'SST:/drive.txt'");
            Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                $"Write-Output tilde > '~/{tildeFileName}'");

            Assert.Equal("provider", File.ReadAllText(Path.Combine(root, "provider.txt")).Trim());
            Assert.Equal("drive", File.ReadAllText(Path.Combine(root, "drive.txt")).Trim());
            Assert.Equal("tilde", File.ReadAllText(tildePath).Trim());
        }
        finally
        {
            if (File.Exists(tildePath))
            {
                File.Delete(tildePath);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PowerShell_escape_and_adjacent_quote_produce_one_literal_argument()
    {
        if (!IsAvailable("pwsh") || !File.Exists("/usr/bin/printf"))
        {
            return;
        }

        var output = Run(
            "pwsh",
            "-NoLogo",
            "-NoProfile",
            "-Command",
            "& /usr/bin/printf '<%s>\\n' --data=@`$HOME\".json\"");

        Assert.Equal("<--data=@$HOME.json>", output);
    }

    [Fact]
    public void Bash_ansi_c_quoting_transforms_the_authored_value()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "-c",
            "printf '<%s>\\n' $'/etc/passwd'");

        Assert.Equal("</etc/passwd>", output);
    }

    [Fact]
    public void Bash_quoted_and_escaped_fd_spelling_redirects_to_literal_files()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "shellsyntaxtree-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            RunInWorkingDirectory(
                "bash",
                workingDirectory,
                "-c",
                "printf quoted > \"&1\"; printf escaped > \\&2");

            Assert.Equal("quoted", File.ReadAllText(Path.Combine(workingDirectory, "&1")));
            Assert.Equal("escaped", File.ReadAllText(Path.Combine(workingDirectory, "&2")));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void PowerShell_native_and_cmdlet_member_spelling_have_different_meanings()
    {
        if (!IsAvailable("pwsh") || !File.Exists("/usr/bin/printf"))
        {
            return;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        Assert.False(string.IsNullOrEmpty(home));

        var nativeExpression = Run(
            "pwsh",
            "-NoLogo",
            "-NoProfile",
            "-Command",
            "& /usr/bin/printf '<%s>\\n' $HOME.Length");
        var nativeInline = Run(
            "pwsh",
            "-NoLogo",
            "-NoProfile",
            "-Command",
            "& /usr/bin/printf '<%s>\\n' --output=$HOME.Length");
        var cmdlet = Run(
            "pwsh",
            "-NoLogo",
            "-NoProfile",
            "-Command",
            "Write-Output $HOME.Length");
        var quotedSuffix = Run(
            "pwsh",
            "-NoLogo",
            "-NoProfile",
            "-Command",
            "Write-Output $HOME\".Length\"");

        Assert.Equal(
            $"<{home!.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}>",
            nativeExpression);
        Assert.Equal($"<--output={home}.Length>", nativeInline);
        Assert.Equal(home!.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), cmdlet);
        Assert.Equal(home + ".Length", quotedSuffix);
    }

    [Fact]
    public void Dynamic_command_fragments_are_executable_identity()
    {
        if (IsNativeBashAvailable())
        {
            var bash = Run(
                "bash",
                "-c",
                "p$(printf rintf) '<%s>\\n' safe");
            Assert.Equal("<safe>", bash);
        }

        if (IsAvailable("pwsh"))
        {
            var pwsh = Run(
                "pwsh",
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "& \"Write-$(Write-Output Output)\" safe");
            Assert.Equal("safe", pwsh);
        }
    }

    [Fact]
    public void Bash_continuation_and_comment_boundary_samples_are_valid()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var sources = new[]
        {
            "echo \"$(printf x # )\nid)\"",
            "echo \"$(printf x \\\n# )\nid)\"",
            "echo \"$(printf x \\\r\n# )\r\nid)\"",
            "r\\\nm target",
            "echo \"$(r\\\n#suffix)\"",
            "echo \"$(X\\\n=1 rm -rf /tmp/x)\"",
        };
        foreach (var source in sources)
        {
            var result = RunUnchecked("bash", "-n", "-c", source);

            Assert.Equal(0, result.ExitCode);
            Assert.True(
                string.IsNullOrEmpty(result.StandardError),
                $"bash syntax oracle wrote to stderr: {result.StandardError}");
        }
    }

    private static bool IsAvailable(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return process is not null && process.WaitForExit(10_000);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
            or FileNotFoundException)
        {
            return false;
        }
    }

    private static bool IsNativeBashAvailable() =>
        !OperatingSystem.IsWindows() && IsAvailable("bash");

    private static string Run(string executable, params string[] arguments)
        => RunCore(executable, workingDirectory: null, arguments);

    private static string RunInWorkingDirectory(
        string executable, string workingDirectory, params string[] arguments)
        => RunCore(executable, workingDirectory, arguments);

    private static string RunCore(
        string executable, string? workingDirectory, params string[] arguments)
    {
        var result = RunUncheckedCore(executable, workingDirectory, arguments);
        Assert.Equal(0, result.ExitCode);
        Assert.True(
            string.IsNullOrEmpty(result.StandardError),
            $"{executable} oracle wrote to stderr: {result.StandardError}");
        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    private static ProcessResult RunUnchecked(
        string executable,
        params string[] arguments) =>
        RunUncheckedCore(executable, workingDirectory: null, arguments);

    private static ProcessResult RunUncheckedInWorkingDirectory(
        string executable,
        string workingDirectory,
        params string[] arguments) =>
        RunUncheckedCore(executable, workingDirectory, arguments);

    private static ProcessResult RunUncheckedCore(
        string executable, string? workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), $"{executable} oracle timed out");
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string[] Lines(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

    private readonly record struct ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
