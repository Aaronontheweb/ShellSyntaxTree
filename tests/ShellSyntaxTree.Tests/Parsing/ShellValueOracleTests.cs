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

    [Fact]
    public void Bash_for_in_runtime_oracle_matches_word_formation_and_empty_values()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "-c",
            "for f in a\"b\" \"\" a a; do printf '<%s>\\n' \"$f\"; done");

        Assert.Equal(new[] { "<ab>", "<>", "<a>", "<a>" }, Lines(output));
    }

    [Fact]
    public void Bash_for_in_runtime_oracle_matches_multiline_control_and_nested_data_flow()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "--noprofile",
            "--norc",
            "-c",
            "root=$(mktemp -d); trap 'rm -rf \"$root\"' EXIT; " +
            "count=0; for f in; do count=1; done; printf 'empty=<%s>\\n' \"$count\"; " +
            "for f in a b\ndo\nprintf '%s\\n' \"$f\" > \"$root/$f.out\"\n" +
            "printf 'pipe=<%s>\\n' \"$f\" | tr '[:lower:]' '[:upper:]'\ndone; " +
            "cat \"$root/a.out\" \"$root/b.out\"; " +
            "for d in x y; do for f in 1 2; do printf 'nested=<%s%s>\\n' \"$d\" \"$f\"; done; done");

        Assert.Equal(
            new[]
            {
                "empty=<0>",
                "PIPE=<A>",
                "PIPE=<B>",
                "a",
                "b",
                "nested=<x1>",
                "nested=<x2>",
                "nested=<y1>",
                "nested=<y2>",
            },
            Lines(output));
    }

    [Fact]
    public void Bash_for_in_runtime_oracle_matches_scope_expansion_and_option_values()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "--noprofile",
            "--norc",
            "-c",
            "unset f; secret=value; " +
            "for f in 'a b'; do printf 'split=<%s>\\n' $f; done; " +
            "for f in -rf safe; do printf 'argv=<%s>\\n' \"$f\"; done; " +
            "for f in secret; do printf 'indirect=<%s>\\n' \"${!f}\"; " +
            "printf 'substitution=<%s>\\n' \"$(printf '%s' \"$f\")\"; " +
            "bash --noprofile --norc -c 'printf \"child=<%s>\\n\" \"$f\"'; " +
            "printf 'parent=<%s>\\n' \"$f\"; done");

        Assert.Equal(
            new[]
            {
                "split=<a>",
                "split=<b>",
                "argv=<-rf>",
                "argv=<safe>",
                "indirect=<value>",
                "substitution=<secret>",
                "child=<>",
                "parent=<secret>",
            },
            Lines(output));
    }

    [Theory]
    [InlineData("for")]
    [InlineData("in")]
    [InlineData("do")]
    [InlineData("done")]
    public void Bash_for_in_runtime_oracle_accepts_reserved_word_binding(string binding)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        Assert.Equal(
            "a",
            Run(
                "bash",
                "-c",
                $"for {binding} in a; do printf %s \"${binding}\"; done"));
    }

    [Fact]
    public void Bash_debug_trap_can_mutate_a_loop_binding_before_body_commands()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "-c",
            "for f in a b; do trap 'f=x' DEBUG; printf '<%s>\\n' \"$f\"; done");

        Assert.Equal(new[] { "<x>", "<x>" }, Lines(output));
    }

    [Fact]
    public void Bash_debug_trap_installed_before_a_loop_can_mutate_each_binding()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "-c",
            "trap 'f=x' DEBUG; for f in a b; do printf '<%s>\\n' \"$f\"; done");

        Assert.Equal(new[] { "<x>", "<x>" }, Lines(output));
    }

    [Fact]
    public void Bash_nested_loop_binding_reuse_does_not_restore_the_outer_value()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "-c",
            "for f in a b; do for f in x y; do :; done; printf 'after=<%s>\\n' \"$f\"; done");

        Assert.Equal(new[] { "after=<y>", "after=<y>" }, Lines(output));
    }

    [Fact]
    public void Bash_home_loop_binding_changes_expansion_semantics()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "--noprofile",
            "--norc",
            "-c",
            "HOME=/home/original; for HOME in /tmp; do printf '<%s>\\n' \"$HOME/x\"; done");

        Assert.Equal("</tmp/x>", output);
    }

    [Fact]
    public void Bash_magic_loop_bindings_do_not_read_back_the_authored_value()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var random = Run(
            "bash",
            "--noprofile",
            "--norc",
            "-c",
            "for RANDOM in not-a-number; do printf '%s' \"$RANDOM\"; done");
        var lineNumber = Run(
            "bash",
            "--noprofile",
            "--norc",
            "-c",
            "for LINENO in 7; do printf '%s' \"$LINENO\"; done");

        Assert.NotEqual("not-a-number", random);
        Assert.All(random, character => Assert.InRange(character, '0', '9'));
        Assert.NotEqual("7", lineNumber);
        Assert.True(int.TryParse(lineNumber, out var parsedLineNumber));
        Assert.True(parsedLineNumber > 0);
    }

    [Fact]
    public void Bash_identity_and_resolution_bindings_change_body_semantics()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "--noprofile",
            "--norc",
            "-c",
            "saved_path=$PATH; for PATH in /definitely/missing; do " +
            "if command -v git >/dev/null; then printf found; else printf missing; fi; done; " +
            "PATH=$saved_path; " +
            "for IFS in :; do value=a:b; printf '<%s>\\n' $value; done; " +
            "root=$(mktemp -d); mkdir -p \"$root/search/child\"; cd \"$root\"; " +
            "for CDPATH in \"$root/search\"; do cd child >/dev/null && " +
            "test \"$PWD\" = \"$root/search/child\" && printf cdpath; done");

        Assert.Equal(new[] { "missing<a>", "<b>", "cdpath" }, Lines(output));
    }

    [Fact]
    public void Bash_globskipdots_can_expand_dot_prefixed_globs_to_parent_traversal()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "-c",
            "shopt -u globskipdots; printf '<%s>\\n' /tmp/.[.] /tmp/.? /tmp/..*");

        Assert.Equal(new[] { "</tmp/..>", "</tmp/..>", "</tmp/..>" }, Lines(output));
    }

    [Theory]
    [InlineData("for 1f in a; do :; done")]
    [InlineData("for f-x in a; do :; done")]
    [InlineData("for \\f in a; do :; done")]
    public void Bash_runtime_rejects_identifiers_that_parse_only_can_misclassify(string source)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var result = RunUnchecked("bash", "-c", source);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("\\for f in a; do :; done")]
    [InlineData("for f \\in a; do :; done")]
    [InlineData("for f i\\n a; do :; done")]
    [InlineData("for f in a; \\do :; done")]
    [InlineData("for f in a; d\\o :; done")]
    [InlineData("for f in a; do :; \\done")]
    public void Bash_runtime_rejects_escaped_contextual_loop_keywords(string source)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var result = RunUnchecked("bash", "-n", "-c", source);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("command -- cd /tmp")]
    [InlineData("command -p cd /tmp")]
    [InlineData("builtin -- cd /tmp")]
    [InlineData("command -p -- builtin -- cd /tmp")]
    public void Bash_runtime_dispatch_wrappers_preserve_cd_in_process(string dispatch)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run("bash", "--noprofile", "--norc", "-c", $"cd /; {dispatch}; pwd");

        Assert.Equal("/tmp", output);
    }

    [Fact]
    public void Bash_runtime_cd_operand_count_and_option_status_match_transfer_grammar()
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var output = Run(
            "bash",
            "--noprofile",
            "--norc",
            "-c",
            "HOME=/tmp; cd /; cd --; pwd; " +
            "cd -Z >/dev/null 2>&1 || printf 'invalid\\n'; " +
            "cd / /tmp >/dev/null 2>&1 || printf 'multiple\\n'; " +
            "cd /; cd /tmp -- >/dev/null 2>&1 || printf 'terminator-after-operand\\n'; " +
            "cd /; cd /tmp -P >/dev/null 2>&1 || printf 'option-after-operand\\n'");

        Assert.Equal(
            new[]
            {
                "/tmp",
                "invalid",
                "multiple",
                "terminator-after-operand",
                "option-after-operand",
            },
            Lines(output));
    }

    [Fact]
    public void PowerShell_variable_writer_parameters_overwrite_existing_bindings()
    {
        if (!IsAvailable("pwsh"))
        {
            return;
        }

        var output = Run(
            "pwsh",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$f='safe'; Write-Output sensitive -OutV f | Out-Null; \"out=<$f>\"; " +
            "$f='safe'; Write-Output pipeline -Pi f | ForEach-Object { \"pipeline=<$f>\" }; " +
            "$f='safe'; Tee-Object -V f -InputObject tee | Out-Null; \"tee=<$f>\"; " +
            "$f='safe'; Write-Output inline -ov:f | Out-Null; \"inline=<$f>\"; " +
            "$f='safe'; Write-Output en \u2013OutVariable f | Out-Null; \"en=<$f>\"; " +
            "$f='safe'; Write-Output em \u2014OutVariable f | Out-Null; \"em=<$f>\"; " +
            "$f='safe'; Write-Output bar \u2015OutVariable f | Out-Null; \"bar=<$f>\"");

        Assert.Equal(
            new[]
            {
                "out=<sensitive>",
                "pipeline=<pipeline>",
                "tee=<tee>",
                "inline=<inline>",
                "en=<en>",
                "em=<em>",
                "bar=<bar>",
            },
            Lines(output));
    }

    [Fact]
    public void PowerShell_current_scope_receivers_and_data_blocks_have_distinct_state()
    {
        if (!IsAvailable("pwsh"))
        {
            return;
        }

        var output = Run(
            "pwsh",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$x='outer'; Measure-Command { $x='measure' } | Out-Null; " +
            "\"measure=<$x>\"; " +
            "$x='outer'; Trace-Command -Name ParameterBinding " +
            "-Expression { $x='trace' } -PSHost *> $null; \"trace=<$x>\"; " +
            "$x='outer'; Write-Output { $x='data' } | Out-Null; \"data=<$x>\"; " +
            "$x='outer'; Set-Alias Measure-Command Write-Output; " +
            "Measure-Command { $x='shadowed' } | Out-Null; \"shadowed=<$x>\"; " +
            "Microsoft.PowerShell.Utility\\Measure-Command { $x='qualified' } | " +
            "Out-Null; \"qualified=<$x>\"; " +
            "$ErrorActionPreference='SilentlyContinue'; Set-Location /; " +
            "Measure-Command { Set-Location /definitely-missing-sst } | Out-Null; " +
            "\"innerFailureHostStatus=<$?>\"");

        Assert.Equal(
            new[]
            {
                "measure=<measure>",
                "trace=<trace>",
                "data=<outer>",
                "shadowed=<outer>",
                "qualified=<qualified>",
                "innerFailureHostStatus=<True>",
            },
            Lines(output));
    }

    [Fact]
    public void PowerShell_pipeline_callbacks_share_state_and_use_semantic_phases()
    {
        if (!IsAvailable("pwsh"))
        {
            return;
        }

        var output = Run(
            "pwsh",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$x='outer'; 1,2 | ForEach-Object " +
            "-End { \"end-before=<$x>\"; $x='end' } " +
            "-Begin { \"begin-before=<$x>\"; $x='begin' } " +
            "-Process { \"process-$_-before=<$x>\"; $x=\"p$_\" }; " +
            "\"foreach-after=<$x>\"; " +
            "$x='outer'; @() | ForEach-Object -Begin { $x='empty-begin' } " +
            "-Process { $x='empty-process' } " +
            "-End { \"empty-end-before=<$x>\"; $x='empty-end' }; " +
            "\"empty-after=<$x>\"; " +
            "$x='outer'; 1,2 | Where-Object { $x=\"w$_\"; $true } | Out-Null; " +
            "\"where-after=<$x>\"; " +
            "$x='outer'; ForEach-Object { $x='standalone-process' }; " +
            "\"standalone-foreach=<$x>\"; " +
            "$x='outer'; Where-Object { $x='standalone-filter'; $true }; " +
            "\"standalone-where=<$x>\"; " +
            "$x='outer'; Where-Object -InputObject value " +
            "-FilterScript { $x='explicit-filter'; $true } | Out-Null; " +
            "\"explicit-where=<$x>\"; " +
            "$x='start'; 1,2 | ForEach-Object { \"interleave=<$x>\" } | " +
            "ForEach-Object { $x='down'; $_ }; " +
            "$x='start'; . { \"dot=<$x>\"; \"dot=<$x>\" } | " +
            "ForEach-Object { $x='down'; $_ }; " +
            "$x='start'; & { \"call=<$x>\"; \"call=<$x>\" } | " +
            "ForEach-Object { $x='down'; $_ }");

        Assert.Equal(
            new[]
            {
                "begin-before=<outer>",
                "process-1-before=<begin>",
                "process-2-before=<p1>",
                "end-before=<p2>",
                "foreach-after=<end>",
                "empty-end-before=<empty-begin>",
                "empty-after=<empty-end>",
                "where-after=<w2>",
                "standalone-foreach=<standalone-process>",
                "standalone-where=<outer>",
                "explicit-where=<explicit-filter>",
                "interleave=<start>",
                "interleave=<down>",
                "dot=<start>",
                "dot=<down>",
                "call=<start>",
                "call=<down>",
            },
            Lines(output));
    }

    [Fact]
    public void PowerShell_synchronous_regions_observe_scope_and_pipeline_stage_effects()
    {
        if (!IsAvailable("pwsh"))
        {
            return;
        }

        var output = Run(
            "pwsh",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$start=(Get-Location).Path; " +
            "$temp=[IO.Path]::TrimEndingDirectorySeparator(" +
            "(Resolve-Path ([IO.Path]::GetTempPath())).Path); " +
            "$root=[IO.Path]::TrimEndingDirectorySeparator(" +
            "[IO.Path]::GetPathRoot($start)); " +
            "$target=if($temp -ne $start){$temp}else{$root}; " +
            "if(!$target -or $target -eq $start -or " +
            "!(Test-Path -LiteralPath $target -PathType Container)){throw 'target'}; " +
            "\"target-distinct=<$($target -ne $start)>\"; " +
            "$x='outer'; Invoke-Command { " +
            "$x='inner'; Set-Location $target; Set-Alias zz Get-Date }; " +
            "\"default-x=<$x>\"; " +
            "\"default-cwd-target=<$((Get-Location).Path -eq $target)>\"; " +
            "\"default-alias=<$([bool](Get-Alias zz -ErrorAction Ignore))>\"; " +
            "$x='outer'; Invoke-Command -NoNewScope:$false { $x='false-inner' }; " +
            "\"false-x=<$x>\"; Set-Location $start; " +
            "Invoke-Command -NoNewScope:$true { " +
            "$x='shared'; Set-Location $target; Set-Alias zz Get-Date }; " +
            "\"shared-x=<$x>\"; " +
            "\"shared-cwd-target=<$((Get-Location).Path -eq $target)>\"; " +
            "\"shared-alias=<$([bool](Get-Alias zz -ErrorAction Ignore))>\"; " +
            "Set-Location $start; " +
            "$missing=Join-Path $start ('missing-'+[guid]::NewGuid().ToString('N')); " +
            "if(Test-Path -LiteralPath $missing){throw 'missing'}; " +
            "Invoke-Command { Set-Location $missing " +
            "-ErrorAction SilentlyContinue }; " +
            "\"failure-status=<$?>\"; " +
            "\"failure-cwd-start=<$((Get-Location).Path -eq $start)>\"; " +
            "$x='start'; Invoke-Command -NoNewScope { " +
            "\"invoke-interleave=<$x>\"; \"invoke-interleave=<$x>\" } | " +
            "Write-Output -OutVariable x | Out-Null; $x; " +
            "$x='start'; Measure-Command { " +
            "Write-Host \"measure-stage=<$x>\" } | " +
            "Write-Output -OutVariable x | Out-Null; " +
            "$x='start'; Trace-Command -Name ParameterBinding -Expression { " +
            "\"trace-stage=<$x>\" } -PSHost 5>$null | " +
            "Write-Output -OutVariable x | Out-Null; $x");

        Assert.Equal(
            new[]
            {
                "target-distinct=<True>",
                "default-x=<outer>",
                "default-cwd-target=<True>",
                "default-alias=<False>",
                "false-x=<outer>",
                "shared-x=<shared>",
                "shared-cwd-target=<True>",
                "shared-alias=<True>",
                "failure-status=<True>",
                "failure-cwd-start=<True>",
                "invoke-interleave=<>",
                "invoke-interleave=<invoke-interleave=<>>",
                "measure-stage=<>",
                "trace-stage=<>",
            },
            Lines(output));
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
