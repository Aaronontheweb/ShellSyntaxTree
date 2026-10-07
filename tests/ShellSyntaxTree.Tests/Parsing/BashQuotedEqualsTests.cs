// -----------------------------------------------------------------------
// <copyright file="BashQuotedEqualsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// A program splits an option word at the first <c>=</c> of the word that it
/// receives, whatever the shell quotes were. Before 0.4.0-beta.24,
/// <c>awk -F'[= ]' '{print $2}' f</c> was unparseable: the parser split the
/// word at the quoted <c>=</c>, and the projection did not pair the parts.
/// Each Bash claim also runs in real Bash when Bash is available.
/// </summary>
public class BashQuotedEqualsTests
{
    private static BashParser CreateParser(BashInitialStateMode mode) => new(new BashParserOptions
    {
        HomeDirectory = BashOracle.Home,
        WorkingDirectory = "/work",
        InitialStateMode = mode,
        PublishAuthoredSourceFacts = true,
        LaunchEnvironment = new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["HOME"] = BashOracle.Home },
            new[] { "CDPATH" }),
    });

    public static TheoryData<string, BashInitialStateMode> QuotedEqualsSources()
    {
        var sources = new[]
        {
            // Real Netclaw traffic.
            "awk -F'[= ]' '{print $2}' f",
            "awk -F'x=y' 1 /tmp/x",
            "cat -F'= ' f",
            "echo -F'[= ]'",
            "cat -F\"x=y\" f",
            // Other quoted or escaped forms.
            "curl -o'x=y' https://example.invalid",
            "cat -F$'x=y' f",
            "echo --foo\"=\"/x",
            "echo --foo\\=/x",
            "echo -F'x=y'=z",
            "echo -F'x='~/y",
            "git -C'a=b' status",
            "printf '<%s>' -F\"[=]\" -G'=' -H\\=",
        };
        var data = new TheoryData<string, BashInitialStateMode>();
        foreach (var source in sources)
        {
            foreach (var mode in new[]
                     {
                         BashInitialStateMode.Unknown,
                         BashInitialStateMode.IsolatedNonInteractive,
                         BashInitialStateMode.FreshNonInteractiveNoStartup,
                     })
            {
                data.Add(source, mode);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(QuotedEqualsSources))]
    public void Quoted_equals_word_has_the_split_that_the_program_reads(
        string source,
        BashInitialStateMode mode)
    {
        var parsed = CreateParser(mode).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = Assert.Single(parsed.Commands);
        Assert.True(command.IsComplete);

        // A program splits an option word at the first `=` of the word that
        // it receives. Each part has the exact text of that split.
        var logged = BashOracle.LoggedCommands(source);
        if (logged is null)
        {
            return;
        }

        var argv = Assert.Single(logged);
        var elements = command.Clause.Elements
            .Where(element => element.Role == ClauseElementRole.Argument)
            .ToArray();
        Assert.Equal(argv.Length - 1, elements.Length);
        for (var index = 0; index < elements.Length; index++)
        {
            var word = argv[index + 1];
            var parts = command.Arguments
                .Where(argument => argument.Element == elements[index])
                .Select(argument => Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value)
                .ToArray();
            var equals = word.IndexOf('=');
            Assert.Equal(
                parts.Length == 2
                    ? new[] { word.Substring(0, equals), word.Substring(equals + 1) }
                    : new[] { word },
                parts);
            Assert.Equal(word.StartsWith('-') && equals > 1 && equals < word.Length - 1, parts.Length == 2);
        }
    }

    [Theory]
    // An `=` in an expansion gives no known split point.
    [InlineData("cat --$(echo a=b) f")]
    [InlineData("cat --$((1==1))=b f")]
    [InlineData("cat \"--$(echo a=b)\" f")]
    [InlineData("cat \"--x$(echo a=b)=c\" f")]
    public void Equals_in_an_expansion_does_not_split(string source)
    {
        var parsed = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = parsed.Commands.Single(c => c.Clause.Verb.Tokens.FirstOrDefault() == "cat");
        var first = command.Arguments[0];
        Assert.IsType<ShellValueDomain.Unknown>(first.Value);
        Assert.Single(command.Arguments, argument => argument.Element == first.Element);
    }

    public static TheoryData<string> PathOptionSpellings => new()
    {
        "tar --file\\=/home/u/.bashrc -c x",
        "tar --file'='/home/u/.bashrc -c x",
        "tar --file\"=\"/home/u/.bashrc -c x",
        "tar --file$'='/home/u/.bashrc -c x",
        "tar \"--file=/home/u/.bashrc\" -c x",
        "tar '--file=/home/u/.bashrc' -c x",
        "tar $'--file=/home/u/.bashrc' -c x",
        "tar --fi'le=/home/u/'.bashrc -c x",
        "tar --fi'le'=/home/u/.bashrc -c x",
        "tar \"--file\"'='\"/home/u/.bashrc\" -c x",
        "tar --file\\='/home/u/.bashrc' -c x",
        "tar --'file'='/home/u/.bashrc' -c x",
    };

    [Theory]
    [MemberData(nameof(PathOptionSpellings))]
    public void Quoted_option_spelling_has_the_facts_of_the_unquoted_form(string source)
    {
        // A program never sees shell quotes. Each spelling passes
        // `--file=/home/u/.bashrc`, and tar writes the archive there.
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            Assert.Equal(
                new[] { "tar", "--file=/home/u/.bashrc", "-c", "x" },
                Assert.Single(logged));
        }

        var parser = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup);
        var expected = parser.Parse("tar --file=/home/u/.bashrc -c x").Commands.Single();
        var actual = parser.Parse(source).Commands.Single();

        Assert.True(actual.IsComplete);
        // The command words read the authored text. They are the same, or
        // Unknown for an ANSI-C quote, which the word scan does not decode.
        if (actual.CommandWords is ShellCommandWords.Known known)
        {
            Assert.Equal(
                Assert.IsType<ShellCommandWords.Known>(expected.CommandWords).Words,
                known.Words);
        }
        else
        {
            Assert.Contains("$'", source, StringComparison.Ordinal);
        }
        Assert.Equal(expected.Arguments.Count, actual.Arguments.Count);
        for (var index = 0; index < expected.Arguments.Count; index++)
        {
            var e = expected.Arguments[index];
            var a = actual.Arguments[index];
            Assert.Equal(e.Value, a.Value);
            Assert.Equal(e.Argument.Kind, a.Argument.Kind);
            Assert.Equal(e.Argument.IsPath, a.Argument.IsPath);
            Assert.Equal(e.Argument.Resolved, a.Argument.Resolved);
            Assert.Equal(e.Element.IsFlag, a.Element.IsFlag);
            Assert.Equal(e.AuthoredFileSystemValue, a.AuthoredFileSystemValue);
        }

        Assert.True(actual.Arguments[1].Argument.IsPath);
        Assert.Equal("/home/u/.bashrc", actual.Arguments[1].Argument.Resolved);
    }

    public static TheoryData<string, string> TildeValueSpellings()
    {
        var data = new TheoryData<string, string>();
        foreach (var value in new[] { "~/x", "~root/../../etc/cron.d/x", "~+/x", "~nosuchuser/x" })
        {
            foreach (var separator in new[] { "'='", "\"=\"", "$'='", "\\=" })
            {
                data.Add("tar --file" + separator + value + " -c x", value);
            }

            data.Add("tar --fil\"e=\"" + value + " -c x", value);
            data.Add("tar '--file='" + value + " -c x", value);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TildeValueSpellings))]
    public void Tilde_after_a_quoted_equals_is_text_in_the_value(string source, string value)
    {
        // Review round 2 of #246: Bash expands `~` only at the start of a
        // word or after an unquoted `=` of an assignment-shaped word. After
        // a quoted `=`, the program gets `--file=~/x`, and tar opens the
        // value relative to its working directory, as for the unquoted
        // `--file=~/x`.
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            Assert.Equal(new[] { "tar", "--file=" + value, "-c", "x" }, Assert.Single(logged));
        }

        var parser = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup);
        var expected = parser.Parse("tar --file=" + value + " -c x").Commands.Single().Arguments[1];
        var actual = parser.Parse(source).Commands.Single().Arguments[1];
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(actual.Value).Value);
        Assert.Equal(expected.Argument.IsPath, actual.Argument.IsPath);
        Assert.Equal(expected.Argument.Resolved, actual.Argument.Resolved);
        Assert.True(actual.Argument.IsPath);
    }

    [Theory]
    [InlineData("--file'='~root/../out.tar", "~root/../out.tar")]
    [InlineData("--file\"=\"~/x", "~/x")]
    [InlineData("--file=~/x", "~/x")]
    public void Tilde_value_names_the_file_relative_to_the_working_directory(string word, string value)
    {
        // A harmless stand-in for tar: Bash prints the full path of the
        // file that a program opens for the value, in a scratch folder.
        if (!BashOracle.IsAvailable())
        {
            return;
        }

        var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sst-tilde-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scratch, "~root"));
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("bash")
            {
                WorkingDirectory = scratch,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--noprofile");
            startInfo.ArgumentList.Add("--norc");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("set -- " + word + "; realpath -m -- \"${1#--file=}\"");
            startInfo.Environment["HOME"] = BashOracle.Home;
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            var opened = process.StandardOutput.ReadToEnd().Trim();
            Assert.True(process.WaitForExit(10_000));

            var parser = new BashParser(new BashParserOptions
            {
                HomeDirectory = BashOracle.Home,
                WorkingDirectory = scratch,
                InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            });
            var argument = parser.Parse("tar " + word + " -c x").Commands.Single().Arguments[1];
            Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value);
            Assert.Equal(opened, argument.Argument.Resolved);
        }
        finally
        {
            System.IO.Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData("git --git-dir\\=/tmp/x push")]
    [InlineData("git --git-dir'='/tmp/x push")]
    [InlineData("git \"--git-dir=/tmp/x\" push")]
    public void Quoted_git_dir_keeps_the_path(string source)
    {
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            Assert.Equal(new[] { "git", "--git-dir=/tmp/x", "push" }, Assert.Single(logged));
        }

        var arguments = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup)
            .Parse(source).Commands.Single().Arguments;
        Assert.Equal("--git-dir", Assert.IsType<ShellValueDomain.Exact>(arguments[0].Value).Value);
        Assert.True(arguments[1].Argument.IsPath);
        Assert.Equal("/tmp/x", arguments[1].Argument.Resolved);
    }

    [Theory]
    [InlineData("for v in q; do tool --a\\=$v end; done")]
    [InlineData("for v in q; do tool --a'='\"$v\" end; done")]
    [InlineData("for v in q; do tool --a\"=\"$v end; done")]
    public void Quoted_equals_with_a_variable_has_no_raw_exact_value(string source)
    {
        // Review of #246: in the Unknown mode, one argument with the exact
        // raw spelling `--a=$v` leaked. Bash passes `--a=q`.
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            Assert.Equal(new[] { "tool", "--a=q", "end" }, Assert.Single(logged));
        }

        foreach (var mode in new[]
                 {
                     BashInitialStateMode.Unknown,
                     BashInitialStateMode.IsolatedNonInteractive,
                     BashInitialStateMode.FreshNonInteractiveNoStartup,
                 })
        {
            var arguments = CreateParser(mode).Parse(source).Commands.Single().Arguments;
            Assert.Equal("--a", Assert.IsType<ShellValueDomain.Exact>(arguments[0].Value).Value);
            Assert.DoesNotContain(
                arguments,
                argument => argument.Value is ShellValueDomain.Exact exact && exact.Value.Contains('$'));
        }
    }

    [Theory]
    [InlineData("cat --foo='x=y' f", "--foo", "x=y")]
    [InlineData("cat --foo=\"a b\" f", "--foo", "a b")]
    [InlineData("cat --foo=x f", "--foo", "x")]
    // The `=` after the quoted part is unquoted, so it splits.
    [InlineData("cat -D'x'=y f", "-Dx", "y")]
    [InlineData("cat -D\"x\"=y=z f", "-Dx", "y=z")]
    public void Unquoted_equals_still_splits_an_inline_value(string source, string option, string value)
    {
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            Assert.Equal(option + "=" + value, Assert.Single(logged)[1]);
        }

        var parsed = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var arguments = Assert.Single(parsed.Commands).Arguments;
        Assert.Equal(3, arguments.Count);
        Assert.Same(arguments[0].Element, arguments[1].Element);
        Assert.Equal(option, Assert.IsType<ShellValueDomain.Exact>(arguments[0].Value).Value);
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(arguments[1].Value).Value);
    }

    [Theory]
    [InlineData("cat --'x'='y' f", "--x", "y")]
    [InlineData("cat --'x'='a=b' f", "--x", "a=b")]
    [InlineData("cat --\"x\"=\"a b\" f", "--x", "a b")]
    [InlineData("cat -\"o\"='' f", "-o", "")]
    [InlineData("cat --x\"y\"='z' f", "--xy", "z")]
    public void Quoted_option_name_pairs_with_a_quoted_value(string source, string option, string value)
    {
        // A quoted part on both sides of an unquoted `=` made the source
        // unparseable before 0.4.0-beta.24: the option argument has the
        // decoded name, and the value argument has the authored spelling.
        Unquoted_equals_still_splits_an_inline_value(source, option, value);
    }

    [Theory]
    // Only the word that holds the pair is a pair. The raw text of the
    // first word ends with `=--c`, but its value does not start with `--c=`.
    [InlineData("cat x=--c --c=d", "x=--c", "--c", "d")]
    [InlineData("cat x='=--c' --c='d'", "x==--c", "--c", "d")]
    public void Pair_rule_does_not_take_a_word_before_the_pair(
        string source,
        string first,
        string option,
        string value)
    {
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            Assert.Equal(new[] { "cat", first, option + "=" + value }, Assert.Single(logged));
        }

        var arguments = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup)
            .Parse(source).Commands.Single().Arguments;
        Assert.Equal(
            new[] { first, option, value },
            arguments.Select(a => Assert.IsType<ShellValueDomain.Exact>(a.Value).Value).ToArray());
    }

    [Fact]
    public void Quoted_option_name_with_a_path_value_keeps_the_path()
    {
        var arguments = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup)
            .Parse("curl --'output'='/tmp/x' https://example.invalid")
            .Commands.Single().Arguments;

        Assert.Equal("--output", Assert.IsType<ShellValueDomain.Exact>(arguments[0].Value).Value);
        Assert.Equal("/tmp/x", Assert.IsType<ShellValueDomain.Exact>(arguments[1].Value).Value);
        Assert.True(arguments[1].Argument.IsPath);
        Assert.Equal("/tmp/x", arguments[1].Argument.Resolved);
    }

    [Fact]
    public void Brace_word_with_an_unquoted_equals_still_splits()
    {
        // The lexer keeps the answer before the brace expansion replaces
        // the value of the word.
        var arguments = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup)
            .Parse("cat --file={/etc/shadow,x}")
            .Commands.Single().Arguments;

        Assert.Equal(2, arguments.Count);
        Assert.Equal("--file", Assert.IsType<ShellValueDomain.Exact>(arguments[0].Value).Value);
        Assert.IsType<ShellValueDomain.Unknown>(arguments[1].Value);
    }

    [Theory]
    // Bash expands `~` only after an unquoted `=` of an assignment-shaped
    // word. A quoted or escaped `=` keeps the text. The tilde rule and the
    // inline value rule use the same test.
    [InlineData("PREFIX'='~/x", "PREFIX=~/x")]
    [InlineData("PREFIX\"=\"~/x", "PREFIX=~/x")]
    [InlineData("PREFIX\\=~/x", "PREFIX=~/x")]
    // An option word splits at its first `=`: the value part is `~/y`.
    [InlineData("-F'x='~/y", "-Fx=~/y", "~/y")]
    public void Tilde_after_a_quoted_equals_stays_text(string word, string bashWord, string? lastValue = null)
    {
        var source = "printf '<%s>' " + word;
        BashOracle.AssertPrints(source, "<" + bashWord + ">");
        var value = lastValue ?? bashWord;

        var argument = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup)
            .Parse(source).Commands.Single().Arguments.Last();
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value);
    }
}
