// -----------------------------------------------------------------------
// <copyright file="BashQuotedEqualsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Only an unquoted <c>=</c> splits a Bash word into an option and its
/// inline value. A quoted or escaped <c>=</c> is plain text. Before
/// 0.4.0-beta.24, <c>awk -F'[= ]' '{print $2}' f</c> was unparseable,
/// because the parser split the word at the quoted <c>=</c>. Each Bash claim
/// also runs in real Bash when Bash is available.
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
    public void Quoted_equals_word_is_one_argument_with_the_bash_value(
        string source,
        BashInitialStateMode mode)
    {
        var parsed = CreateParser(mode).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = Assert.Single(parsed.Commands);
        Assert.True(command.IsComplete);

        // One argument for each word, with the exact text that Bash passes.
        var values = command.Arguments
            .Select(argument => Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value)
            .ToArray();
        Assert.Equal(
            command.Clause.Elements.Count(element => element.Role == ClauseElementRole.Argument),
            values.Length);
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            var argv = Assert.Single(logged);
            Assert.Equal(argv.Skip(1).ToArray(), values);
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
    [InlineData("curl --output\\=/tmp/x https://example.invalid")]
    [InlineData("curl --output\"=\"/tmp/x https://example.invalid")]
    [InlineData("curl --output'='/tmp/x https://example.invalid")]
    public void Quoted_equals_option_has_the_facts_of_a_fully_quoted_word(string source)
    {
        // A fully quoted `"--output=/tmp/x"` was already one argument. A
        // word with only the `=` quoted now gets the same value and path
        // facts. The program can still read `--output` and `/tmp/x`; a
        // consumer that needs that split reads the exact value.
        var parser = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup);
        var expected = parser.Parse("curl \"--output=/tmp/x\" https://example.invalid")
            .Commands.Single().Arguments[0];
        var actual = parser.Parse(source).Commands.Single().Arguments[0];

        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Argument.Kind, actual.Argument.Kind);
        Assert.Equal(expected.Argument.IsPath, actual.Argument.IsPath);
        Assert.Equal(expected.Argument.Resolved, actual.Argument.Resolved);
        Assert.Single(parser.Parse(source).Commands.Single().Arguments, a => a.Element == actual.Element);
    }

    [Theory]
    // Bash expands `~` only after an unquoted `=` of an assignment-shaped
    // word. A quoted or escaped `=` keeps the text. The tilde rule and the
    // inline value rule use the same test.
    [InlineData("PREFIX'='~/x", "PREFIX=~/x")]
    [InlineData("PREFIX\"=\"~/x", "PREFIX=~/x")]
    [InlineData("PREFIX\\=~/x", "PREFIX=~/x")]
    [InlineData("-F'x='~/y", "-Fx=~/y")]
    public void Tilde_after_a_quoted_equals_stays_text(string word, string value)
    {
        var source = "printf '<%s>' " + word;
        BashOracle.AssertPrints(source, "<" + value + ">");

        var argument = CreateParser(BashInitialStateMode.FreshNonInteractiveNoStartup)
            .Parse(source).Commands.Single().Arguments.Last();
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value);
    }
}
