// -----------------------------------------------------------------------
// <copyright file="BashLineContinuationTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins #243. Bash removes a line continuation (a backslash and a newline)
/// before it splits the input into tokens, except in single quotes,
/// <c>$'…'</c>, a comment, and a heredoc body. Before 0.4.0-beta.22 the
/// lexer read <c>"$\⏎(cmd)"</c> as literal text, so <c>cmd</c> ran with no
/// occurrence. Each source here is also run in real Bash when Bash is
/// available, so the expected Bash behavior is proved, not assumed.
/// </summary>
public class BashLineContinuationTests
{
    private const string Home = "/home/test";

    private static readonly BashParser Parser = new(new BashParserOptions
    {
        HomeDirectory = Home,
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
        LaunchEnvironment = new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["HOME"] = Home },
            new[] { "CDPATH" }),
    });

    // ---------------------------------------------------------------- hidden commands

    public static TheoryData<string, string> HiddenCommandSources => new()
    {
        // The reported bug: an expansion in double quotes.
        { "printf '<%s>' \"$\\\n(printf HIDDEN)\"", "<HIDDEN>" },
        { "printf '<%s>' $\\\n(printf HIDDEN)", "<HIDDEN>" },
        { "printf '<%s>' \"$\\\n\\\n(printf HIDDEN)\"", "<HIDDEN>" },
        { "x=\"$\\\n(printf HIDDEN)\"; printf '<%s>' \"$x\"", "<HIDDEN>" },
        { "printf '<%s>' \"$(printf '%s' \"$\\\n(printf HIDDEN)\")\"", "<HIDDEN>" },
        { "printf '<%s>' $(printf '%s' $\\\n(printf HIDDEN))", "<HIDDEN>" },

        // An operator can continue across a line.
        { "true &\\\n& printf '<%s>' HIDDEN", "<HIDDEN>" },
        { "false |\\\n| printf '<%s>' HIDDEN", "<HIDDEN>" },

        // A comment does not continue: the next line is a command.
        { "printf '<%s>' a # $\\\n(printf HIDDEN)", "<a>HIDDEN" },

        // An escaped backslash before a newline is not a continuation.
        { "printf '<%s>' a\\\\\nprintf '<%s>' HIDDEN", "<a\\><HIDDEN>" },
    };

    [Theory]
    [MemberData(nameof(HiddenCommandSources))]
    public void Command_after_a_continuation_is_an_occurrence(string source, string bashOutput)
    {
        AssertBashPrints(source, bashOutput);
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var hidden = Assert.Single(
            parsed.Commands,
            command => command.Clause.Verb.Tokens.SequenceEqual(new[] { "printf" }) &&
                command.Clause.Elements.Any(element => element.Value == "HIDDEN"));
        Assert.True(hidden.IsComplete);
        Assert.Equal(
            "HIDDEN",
            Assert.IsType<ShellValueDomain.Exact>(hidden.Arguments.Last().Value).Value);
    }

    [Fact]
    public void Hidden_substitution_has_the_substitution_role_and_source_span()
    {
        const string source = "echo \"$\\\n(touch /tmp/x)\"";
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(2, parsed.Commands.Count);
        var touch = parsed.Commands[0];
        Assert.Equal(CommandOccurrenceRole.Substitution, touch.ImmediateRole);
        Assert.Equal(new[] { "touch" }, touch.Clause.Verb.Tokens);
        Assert.Equal("/tmp/x", Assert.Single(touch.Arguments).Argument.Resolved);
        var echo = parsed.Commands[1];
        Assert.Equal(new[] { "echo" }, echo.Clause.Verb.Tokens);
        Assert.IsType<ShellValueDomain.Unknown>(Assert.Single(echo.Arguments).Value);
    }

    // ---------------------------------------------------------------- exact values

    [Theory]
    [InlineData("n=build; printf '<%s>' \"$\\\nn/\"", "build/")]
    [InlineData("ndir=/tmp/q; n=build; printf '<%s>' \"$n\\\ndir\"", "/tmp/q")]
    [InlineData("printf '<%s>' \"$\\\nHOME/.ssh/id_rsa\"", "/home/test/.ssh/id_rsa")]
    [InlineData("for n in a; do printf '<%s>' \"$\\\n{n}\"; done", "a")]
    [InlineData("x=VAL; printf '<%s>' \"${\\\nx}\"", "VAL")]
    [InlineData("x=VAL; printf '<%s>' \"${x\\\n}\"", "VAL")]
    [InlineData("printf '<%s>' x$\\\n'y'", "xy")]
    [InlineData("printf '<%s>' a\\\nb", "ab")]
    [InlineData("printf '<%s>' -\\\no", "-o")]
    public void Expansion_across_a_continuation_has_the_bash_value(string source, string value)
    {
        AssertBashPrints(source, "<" + value + ">");

        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(LastArgument(source).Value).Value);
    }

    [Fact]
    public void Name_after_a_continuation_is_part_of_the_variable_name()
    {
        // Bash reads `$ndir`, which is unset, so `rm -rf "$n\⏎dir/"` runs
        // `rm -rf /`. The old value was the wrong exact path `builddir/`.
        const string source = "n=build; printf '<%s>' \"$n\\\ndir/\"";
        AssertBashPrints(source, "</>");

        var value = LastArgument(source).Value;
        Assert.True(
            value is ShellValueDomain.Unknown ||
            value is ShellValueDomain.Exact { Value: "/" },
            $"unexpected value {value}");
    }

    [Fact]
    public void Unquoted_name_after_a_continuation_reads_the_bound_value()
    {
        const string source = "x=/etc/hostname; printf '<%s>' $\\\nx";
        AssertBashPrints(source, "</etc/hostname>");

        Assert.Equal(
            "/etc/hostname",
            Assert.IsType<ShellValueDomain.Exact>(LastArgument(source).AuthoredValue).Value);
    }

    [Theory]
    [InlineData("set -- p q; printf '<%s>' \"$\\\n@\"", "<p><q>")]
    [InlineData("set -- p q; printf '<%s>' \"${\\\n@}\"", "<p><q>")]
    public void Quoted_all_positional_across_a_continuation_can_split(string source, string bashOutput)
    {
        AssertBashPrints(source, bashOutput);

        Assert.True(LastArgument(source).MayFieldSplit);
    }

    [Theory]
    [InlineData("printf '<%s>' {a.\\\n.c}", "<a><b><c>")]
    [InlineData("printf '<%s>' {\\\n-1..1}", "<-1><0><1>")]
    public void Brace_sequence_across_a_continuation_expands(string source, string bashOutput)
    {
        AssertBashPrints(source, bashOutput);

        var argument = LastArgument(source);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        Assert.True(argument.MayPathnameExpand);
        Assert.True(argument.MayFieldSplit);
    }

    [Fact]
    public void Brace_sequence_across_a_continuation_can_name_a_subcommand()
    {
        var parsed = Parser.Parse("git {a.\\\n.c}");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsType<ShellCommandWords.Unknown>(Assert.Single(parsed.Commands).CommandWords);
    }

    // ---------------------------------------------------------------- options and redirects

    [Fact]
    public void Option_after_a_continuation_binds_its_path_value()
    {
        var parsed = Parser.Parse("curl -\\\no /tmp/out https://example.invalid");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var output = Assert.Single(parsed.Commands).Clause.Args[1];
        Assert.True(output.IsPath);
        Assert.Equal("/tmp/out", output.Resolved);
    }

    [Fact]
    public void Option_after_a_continuation_keeps_its_value_out_of_the_command_words()
    {
        var parsed = Parser.Parse("git -\\\nC repo push");

        Assert.Equal(
            new[] { "git", "push" },
            Assert.IsType<ShellCommandWords.Known>(Assert.Single(parsed.Commands).CommandWords).Words);
    }

    [Theory]
    [InlineData("printf a >\\\n> out", null)]
    [InlineData("printf a 2>\\\n> out", 2)]
    public void Append_operator_continues_across_a_line(string source, int? descriptor)
    {
        var redirect = Assert.IsType<FileRedirectAnalysis>(
            Assert.Single(Assert.Single(Parser.Parse(source).Commands).Redirects));

        Assert.Equal(FileRedirectMode.Append, redirect.Mode);
        Assert.Equal("/work/out", Assert.IsType<ShellValueDomain.Exact>(redirect.Target).Value);
        if (descriptor is int value)
        {
            Assert.Equal(value, Assert.IsType<RedirectSource.Descriptor>(redirect.Source).Value);
        }
        else
        {
            Assert.IsType<RedirectSource.Default>(redirect.Source);
        }
    }

    [Theory]
    [InlineData("printf a >\\\n&2")]
    [InlineData("printf a >&\\\n2")]
    public void Descriptor_target_continues_across_a_line(string source)
    {
        AssertBashWritesToStandardError(source, "a");
        var command = Assert.Single(Parser.Parse(source).Commands);

        var redirect = Assert.IsType<DescriptorDuplicateRedirectAnalysis>(Assert.Single(command.Redirects));
        Assert.Equal(2, redirect.TargetDescriptor);
        var authored = Assert.Single(command.Clause.Redirects);
        Assert.Equal("&2", authored.Target);
        Assert.True(authored.IsDynamicSkip);
    }

    [Fact]
    public void Here_string_operator_continues_across_a_line()
    {
        const string source = "cat <\\\n<<word";
        AssertBashPrints(source, "word");

        var redirect = Assert.IsType<HereStringRedirectAnalysis>(
            Assert.Single(Assert.Single(Parser.Parse(source).Commands).Redirects));
        Assert.Equal("word\n", Assert.IsType<ShellValueDomain.Exact>(redirect.Data).Value);
    }

    [Theory]
    [InlineData("cat <\\\n<EOF\nbody\nEOF", false)]
    [InlineData("cat <<\\\n-EOF\n\tbody\n\tEOF", true)]
    public void Heredoc_operator_continues_across_a_line(string source, bool stripTabs)
    {
        AssertBashPrints(source, "body");

        var redirect = Assert.IsType<HereDocumentRedirectAnalysis>(
            Assert.Single(Assert.Single(Parser.Parse(source).Commands).Redirects));
        Assert.Equal(stripTabs, redirect.Document.StripLeadingTabs);
        Assert.Equal("EOF", redirect.Document.Delimiter.Raw);
    }

    // ---------------------------------------------------------------- literal contexts

    [Fact]
    public void Single_quotes_keep_the_continuation()
    {
        const string source = "printf '<%s>' '$\\\n(printf HIDDEN)'";
        AssertBashPrints(source, "<$\\\n(printf HIDDEN)>");

        var parsed = Parser.Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = Assert.Single(parsed.Commands);
        Assert.Equal(
            "$\\\n(printf HIDDEN)",
            Assert.IsType<ShellValueDomain.Exact>(command.Arguments.Last().Value).Value);
    }

    [Fact]
    public void Quoted_heredoc_delimiter_keeps_the_continuation()
    {
        const string source = "cat <<'EOF'\n$\\\n(printf HIDDEN)\nEOF";
        AssertBashPrints(source, "$\\\n(printf HIDDEN)");

        var parsed = Parser.Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var redirect = Assert.IsType<HereDocumentRedirectAnalysis>(
            Assert.Single(Assert.Single(parsed.Commands).Redirects));
        Assert.Equal(HereDocumentExpansionMode.Literal, redirect.Document.ExpansionMode);
        Assert.Equal("$\\\n(printf HIDDEN)\n", redirect.Document.Body.Raw);
    }

    [Theory]
    [InlineData("printf '<%s>' \"a\\\\\nb\"", "<a\\\nb>")]
    [InlineData("printf '<%s>' \"$\\\r\n(printf HIDDEN)\"", "<$\\\r\n(printf HIDDEN)>")]
    public void Double_quotes_keep_an_escaped_backslash_and_a_carriage_return(
        string source,
        string bashOutput)
    {
        // In double quotes, only backslash + LF is a continuation. `\\` is
        // an escaped backslash, and `\` + CR stays text, as in Bash.
        AssertBashPrints(source, bashOutput);

        var parsed = Parser.Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = Assert.Single(parsed.Commands);
        Assert.Equal(
            bashOutput.Substring(1, bashOutput.Length - 2),
            Assert.IsType<ShellValueDomain.Exact>(command.Arguments.Last().Value).Value);
    }

    // ---------------------------------------------------------------- fail closed

    [Theory]
    // Bash reads `$(\⏎(` as arithmetic. The arithmetic grammar reads only an
    // exact `$((` marker.
    [InlineData("printf '<%s>' \"$(\\\n(1+2))\"")]
    [InlineData("printf '<%s>' $\\\n((1+2))")]
    [InlineData("printf '<%s>' $(printf '%s' $(\\\n(1+2)))")]
    // Bash joins the lines of an expanding heredoc body before it looks for
    // the delimiter. The parser does not model that join.
    [InlineData("cat <<EOF\n$\\\n(printf HIDDEN)\nEOF")]
    [InlineData("cat <<EOF\nEO\\\nF\nprintf HIDDEN")]
    [InlineData("cat <<EO\\\nF\nbody\nEOF")]
    // Bash keeps `\⏎` in `$'…'`. The decoder rejects that escape.
    [InlineData("printf '<%s>' $'a\\\nb'")]
    // A locale string, legacy backticks, and a complex parameter expansion.
    [InlineData("printf '<%s>' $\\\n\"text\"")]
    [InlineData("printf '<%s>' `printf '%s' $\\\n(printf HIDDEN)`")]
    [InlineData("printf '<%s>' \"${x:-$\\\n(printf HIDDEN)}\"")]
    public void Unmodeled_context_fails_closed(string source)
    {
        AssertBashSucceeds(source);
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    // ---------------------------------------------------------------- general rule

    public static TheoryData<string> InvarianceSources => new()
    {
        "echo \"$(touch /tmp/x)\" $(id -u)",
        "x=\"$(printf HIDDEN)\"; echo \"$x\"",
        "n=build; rm -rf \"$n/\" \"${n}/out\"",
        "x=/etc/hostname; cat \"$x\" $x",
        "for n in a b; do printf \"%s\" \"${n}\" \"$n\"; done",
        "true && touch /tmp/x || echo no",
        "echo a >> out 2>&1; cat <<< word",
        "cd /tmp && rm -rf ./build",
        "git -C repo push origin main",
        "curl -o /tmp/out https://example.invalid",
        "echo \"$@\" \"${x}\" $y $1 \"$?\"",
        "cat {a,b}.txt {1..3}",
        "( cd /tmp; ls ) | wc -l",
        "echo $(echo $(id -u)) > /tmp/out",
        "if true; then touch /tmp/x; fi",
    };

    /// <summary>
    /// The general rule: a continuation where Bash removes it never changes
    /// a fact. Each source has no single quote, comment, or heredoc, so a
    /// continuation at any point is removed. The parser must give the same
    /// facts or fail closed. It must never give a different fact.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvarianceSources))]
    public void Continuation_at_any_point_keeps_the_facts_or_fails_closed(string source)
    {
        var expected = Facts(Parser.Parse(source));
        Assert.NotNull(expected);
        var parsedVariants = 0;
        for (var index = 0; index <= source.Length; index++)
        {
            if (IsEscaped(source, index))
            {
                continue;
            }

            var variant = source.Substring(0, index) + "\\\n" + source.Substring(index);
            var parsed = Parser.Parse(variant);
            if (parsed.IsUnparseable)
            {
                continue;
            }

            parsedVariants++;
            Assert.True(
                expected == Facts(parsed),
                $"facts changed for {Quote(variant)}:\n{expected}\n---\n{Facts(parsed)}");
        }

        // Most points are inside words or between them, so most variants
        // parse. A rule that failed closed everywhere would prove nothing.
        Assert.True(parsedVariants * 2 > source.Length, $"only {parsedVariants} variants parsed");
    }

    // ---------------------------------------------------------------- helpers

    private static AnalyzedArgument LastArgument(string source)
    {
        var parsed = Parser.Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        return parsed.Commands.Last().Arguments.Last();
    }

    private static bool IsEscaped(string source, int index)
    {
        var backslashes = 0;
        for (var position = index - 1; position >= 0 && source[position] == '\\'; position--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    private static string? Facts(ParsedCommand parsed)
    {
        if (parsed.IsUnparseable)
        {
            return null;
        }

        var facts = new StringBuilder();
        foreach (var command in parsed.Commands)
        {
            facts.Append(command.ImmediateRole).Append(' ')
                .Append(string.Join(">", command.Ancestry.Select(a => a.Region))).Append(' ')
                .Append(command.IsComplete).Append(' ')
                .Append(Domain(command.WorkingDirectory)).Append(' ')
                .Append(command.CommandWords is ShellCommandWords.Known known
                    ? "[" + string.Join(",", known.Words) + "]"
                    : "?")
                .Append('\n');
            foreach (var argument in command.Arguments)
            {
                facts.Append("  ")
                    .Append(Domain(argument.Value)).Append(' ')
                    .Append(Domain(argument.AuthoredValue)).Append(' ')
                    .Append(Domain(argument.AuthoredFileSystemValue)).Append(' ')
                    .Append(argument.MayPathnameExpand).Append(' ')
                    .Append(argument.MayFieldSplit).Append(' ')
                    .Append(argument.Argument.Kind).Append(' ')
                    .Append(argument.Argument.IsPath).Append(' ')
                    .Append(argument.Argument.Resolved)
                    .Append('\n');
            }

            foreach (var redirect in command.Redirects)
            {
                facts.Append("  ").Append(redirect.GetType().Name).Append(' ')
                    .Append(redirect.Source).Append(' ')
                    .Append(redirect switch
                    {
                        FileRedirectAnalysis file => file.Mode + " " + Domain(file.Target),
                        DescriptorDuplicateRedirectAnalysis duplicate => duplicate.TargetDescriptor.ToString(),
                        HereStringRedirectAnalysis hereString => Domain(hereString.Data),
                        _ => string.Empty,
                    })
                    .Append('\n');
            }
        }

        return facts.ToString();
    }

    private static string Domain(ShellValueDomain domain) => domain switch
    {
        ShellValueDomain.Exact exact => "Exact(" + Quote(exact.Value) + ")",
        ShellValueDomain.FiniteSet set => "Set(" + string.Join(",", set.Values.Select(Quote)) + ")",
        ShellValueDomain.Concatenation concatenation =>
            "Cat(" + string.Join("+", concatenation.Parts.Select(Domain)) + ")",
        _ => domain.GetType().Name,
    };

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    // ---------------------------------------------------------------- bash oracle

    private static bool IsNativeBashAvailable() =>
        !OperatingSystem.IsWindows() && BashResult("true") is { ExitCode: 0 };

    private static void AssertBashPrints(string source, string expected)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var result = BashResult(source)!.Value;
        Assert.True(result.ExitCode == 0, $"bash failed: {result.StandardError}");
        Assert.True(string.IsNullOrEmpty(result.StandardError), result.StandardError);
        Assert.Equal(expected, result.StandardOutput.TrimEnd('\n'));
    }

    private static void AssertBashWritesToStandardError(string source, string expected)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var result = BashResult(source)!.Value;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(expected, result.StandardError);
    }

    private static void AssertBashSucceeds(string source)
    {
        if (!IsNativeBashAvailable())
        {
            return;
        }

        var result = BashResult(source)!.Value;
        Assert.True(result.ExitCode == 0, $"bash failed: {result.StandardError}");
    }

    private static ProcessResult? BashResult(string source)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--noprofile");
        startInfo.ArgumentList.Add("--norc");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(source);
        startInfo.Environment["HOME"] = Home;
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

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

    private readonly record struct ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
