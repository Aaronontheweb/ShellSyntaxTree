// -----------------------------------------------------------------------
// <copyright file="LaunchVariableGlobTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Bash.Parsing;
using ShellSyntaxTree.Internal.Resolving;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class LaunchVariableGlobTests
{
    [Theory]
    [InlineData("ls -d \"$HOME\"/repositories/*/", "ls -d /home/agent/repositories/*/")]
    [InlineData("ls -d $HOME/repositories/*/*/", "ls -d /home/agent/repositories/*/*/")]
    [InlineData("ls -d ${HOME}/repositories/*/", "ls -d /home/agent/repositories/*/")]
    [InlineData("ls \"$SST_ROOT\"/*.txt", "ls /srv/data/*.txt")]
    [InlineData("ls $SST_ROOT/*.txt", "ls /srv/data/*.txt")]
    [InlineData("ls /srv/\"${SST_PART}\"/*", "ls /srv/data/*")]
    [InlineData("ls \"$SST_REL\"/*", "ls src/*")]
    [InlineData("ls \"$SST_EMPTY\"/*", "ls /*")]
    public void Live_variable_glob_has_the_facts_of_its_literal_form(string source, string literal)
    {
        var parser = Parser();
        var parsed = parser.Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var argument = parsed.Commands.Single().Arguments.Last();
        var actual = Assert.IsType<ShellValueDomain.PathPattern>(argument.Value);
        var expected = Assert.IsType<ShellValueDomain.PathPattern>(
            parser.Parse(literal).Commands.Single().Arguments.Last().Value);

        Assert.Equal(expected.Pattern, actual.Pattern);
        Assert.Equal(expected.CoveringDirectory, actual.CoveringDirectory);
        Assert.Equal(expected.Glob!.SegmentDepth, actual.Glob!.SegmentDepth);
        Assert.Equal(expected.Glob.MayStartWithDash, actual.Glob.MayStartWithDash);
        Assert.Equal(expected.Glob.Segments, actual.Glob.Segments);
        Assert.Equal(ArgKind.Glob, argument.Argument.Kind);
        Assert.True(argument.Argument.IsPath);
        Assert.Null(argument.Argument.Resolved);
    }

    [Theory]
    [InlineData("ls \"$UNKNOWN\"/*")]
    [InlineData("ls \"$(printf /srv/data)\"/*")]
    [InlineData("ls \"${HOME:-/outside}\"/*")]
    [InlineData("ls \"${HOME}/a*\"/*")]
    [InlineData("ls \"$HOME\"/../*")]
    [InlineData("ls \"$HOME\"/*/../x")]
    [InlineData("HOME=/outside; ls \"$HOME\"/*")]
    [InlineData("wait -p HOME; ls \"$HOME\"/*")]
    [InlineData("for HOME in /outside; do ls \"$HOME\"/*; done")]
    [InlineData("shopt -s dotglob; ls \"$HOME\"/*")]
    [InlineData("bash -c 'ls \"$HOME\"/*'")]
    public void Unproved_variable_glob_has_no_pattern_fact(string source)
    {
        var parsed = Parser().Parse(source);
        if (!parsed.IsUnparseable)
        {
            Assert.All(parsed.Commands.SelectMany(c => c.Arguments),
                a => Assert.IsNotType<ShellValueDomain.PathPattern>(a.Value));
        }
    }

    [Theory]
    [InlineData("/home/two words", false, false)]
    [InlineData("/home/two words", true, true)]
    [InlineData("/home/a\tb", false, false)]
    [InlineData("/home/a\nb", false, false)]
    [InlineData("/home/a*", false, false)]
    [InlineData("/home/a*", true, false)]
    [InlineData("*", true, false)]
    [InlineData("?", true, false)]
    [InlineData("[a]", true, false)]
    [InlineData("\\x", true, false)]
    [InlineData("{a,b}", true, false)]
    [InlineData("}a", true, false)]
    [InlineData("/home/a?", true, false)]
    [InlineData("/home/a[b]", true, false)]
    [InlineData("/home/a\\b", true, false)]
    [InlineData("/home/{a,b}", true, false)]
    [InlineData("../outside", true, false)]
    [InlineData("-option", false, false)]
    public void Variable_value_must_preserve_one_representable_path_word(
        string value, bool quoted, bool expectedPattern)
    {
        var parser = Parser(value);
        var parsed = parser.Parse(quoted ? "ls \"$HOME\"/*" : "ls $HOME/*");
        if (parsed.IsUnparseable)
        {
            Assert.False(expectedPattern, parsed.UnparseableReason);
        }
        else
        {
            Assert.Equal(expectedPattern,
                parsed.Commands.Single().Arguments.Single().Value is ShellValueDomain.PathPattern);
        }
    }

    [Fact]
    public void Empty_launch_home_is_rejected_before_parse()
        => Assert.Throws<ArgumentException>(() => Parser(string.Empty));

    [Fact]
    public void Empty_unquoted_variable_has_no_pattern_fact()
    {
        var parsed = Parser().Parse("ls $SST_EMPTY/*");
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsNotType<ShellValueDomain.PathPattern>(parsed.Commands.Single().Arguments.Single().Value);
    }

    [Theory]
    [InlineData(BashInitialStateMode.Unknown, true)]
    [InlineData(BashInitialStateMode.IsolatedNonInteractive, true)]
    [InlineData(BashInitialStateMode.FreshNonInteractiveNoStartup, false)]
    public void Variable_glob_needs_fresh_mode_and_live_launch_values(
        BashInitialStateMode mode, bool launch)
    {
        var parsed = Parser(mode: mode, launch: launch).Parse("ls \"$HOME\"/*");
        if (!parsed.IsUnparseable)
        {
            Assert.IsNotType<ShellValueDomain.PathPattern>(parsed.Commands.Single().Arguments.Single().Value);
        }
    }

    [Fact]
    public void Glob_analysis_refuses_a_revoked_name_while_other_launch_facts_remain_live()
    {
        var launch = new ShellLaunchEnvironment(new Dictionary<string, string>
        {
            ["HOME"] = "/home/agent",
            ["SST_ROOT"] = "/before",
        }, Array.Empty<string>()).Revoke("SST_ROOT");
        var options = new BashParserOptions
        {
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            LaunchEnvironment = launch,
        };
        var value = ShellValue.Concat(BashLexer.Tokenize("\"$SST_ROOT\"/*")
            .Where(token => token.ResolverValue is not null).Select(token => token.ResolverValue!));

        Assert.False(BashGlobPatternAnalysis.TryAnalyze(value, options, "/work", out _));
    }

    [Theory]
    [InlineData("HOME=/outside; printf ok > \"$HOME\"/*.log")]
    [InlineData("SST_ROOT=/outside; printf ok > \"$SST_ROOT\"/*.log")]
    public void A_redirect_never_uses_a_revoked_launch_value(string source)
    {
        var parsed = Parser().Parse(source);
        if (!parsed.IsUnparseable)
        {
            var redirect = Assert.IsType<FileRedirectAnalysis>(parsed.Commands.Last().Redirects.Single());
            Assert.IsType<ShellValueDomain.Unknown>(redirect.Target);
        }
    }

    [Fact]
    public void Variable_glob_redirect_keeps_the_literal_target_facts()
    {
        var parsed = Parser().Parse("printf ok > \"$HOME\"/logs/*.txt");
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var redirect = Assert.IsType<FileRedirectAnalysis>(parsed.Commands.Single().Redirects.Single());
        Assert.True(redirect.IsComplete);
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(redirect.Target);
        Assert.Equal("/home/agent/logs", pattern.CoveringDirectory);
        Assert.Equal(1, pattern.Glob!.SegmentDepth);
    }

    [BashTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Variable_glob_matches_native_bash(bool quoted, bool spaceInHome)
    {
        var root = Path.Combine(Path.GetTempPath(), "sst-variable-glob-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, spaceInHome ? "two words" : "home");
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "repositories", "a", "one"));
            Directory.CreateDirectory(Path.Combine(home, "repositories", "b", "two"));
            var source = quoted
                ? "printf '<%s>\\n' \"$HOME\"/repositories/*/*/"
                : "printf '<%s>\\n' $HOME/repositories/*/*/";
            var parsed = Parser(home).Parse(source);
            var value = parsed.Commands.Single().Arguments.Last().Value;
            string expected;
            if (!quoted && spaceInHome)
            {
                Assert.IsNotType<ShellValueDomain.PathPattern>(value);
                expected = "<" + root + "/two>\n<words/repositories/*/*/>";
            }
            else
            {
                var pattern = Assert.IsType<ShellValueDomain.PathPattern>(value);
                Assert.Equal(home + "/repositories", pattern.CoveringDirectory);
                Assert.Equal(2, pattern.Glob!.SegmentDepth);
                expected = "<" + home + "/repositories/a/one/>\n<" + home + "/repositories/b/two/>";
            }

            var nativeSource = "cd '" + root.Replace("'", "'\\''") + "'; " + source;
            BashOracle.AssertShellPrints(new[] { "bash", "--noprofile", "--norc" }, nativeSource, expected,
                new Dictionary<string, string> { ["HOME"] = home });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static BashParser Parser(
        string home = "/home/agent",
        BashInitialStateMode mode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        bool launch = true)
        => new(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = mode,
            LaunchEnvironment = launch ? new ShellLaunchEnvironment(
                new Dictionary<string, string>
                {
                    ["HOME"] = home,
                    ["SST_ROOT"] = "/srv/data",
                    ["SST_PART"] = "data",
                    ["SST_REL"] = "src",
                    ["SST_EMPTY"] = string.Empty,
                }, Array.Empty<string>()) : null,
        });
}
