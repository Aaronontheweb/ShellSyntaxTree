// -----------------------------------------------------------------------
// <copyright file="BashBraceExpansionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the brace-expansion rule of #227. Bash expands an unquoted
/// <c>{a,b}</c> or <c>{x..y}</c> to several words before every other
/// expansion. The parser does not compute the words. A word with a brace
/// expansion has no exact value, no resolved path, and no command word.
/// </summary>
public class BashBraceExpansionTests
{
    private static readonly ShellLaunchEnvironment Launch = new(
        new Dictionary<string, string>
        {
            ["TMPDIR"] = "/tmp/nc",
            ["HOME"] = "/home/test",
        },
        new[] { "CDPATH" });

    private static readonly BashParser Parser = new(new BashParserOptions
    {
        HomeDirectory = "/home/test",
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
        LaunchEnvironment = Launch,
    });

    private static AnalyzedArgument Argument(string source, string raw)
    {
        var parsed = Parser.Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        return parsed.Commands.SelectMany(c => c.Arguments).Single(a => a.Element.Raw == raw);
    }

    private static void AssertUnresolved(AnalyzedArgument argument)
    {
        Assert.Equal(ArgKind.DynamicSkip, argument.Argument.Kind);
        Assert.False(argument.Argument.IsPath);
        Assert.Null(argument.Argument.Resolved);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredValue);
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredFileSystemValue);
    }

    [Theory]
    [InlineData("cat ~/.netclaw/config/{netclaw,secrets}.json", "~/.netclaw/config/{netclaw,secrets}.json")]
    [InlineData("cat secrets.js{on,}", "secrets.js{on,}")]
    [InlineData("cat ~/.netclaw/{keys,config}/key-1.xml", "~/.netclaw/{keys,config}/key-1.xml")]
    [InlineData("cat {a,b}", "{a,b}")]
    [InlineData("cat {,}", "{,}")]
    [InlineData("cat {1..3}.txt", "{1..3}.txt")]
    [InlineData("cat {-2..2..2}", "{-2..2..2}")]
    [InlineData("cat {a..c}", "{a..c}")]
    [InlineData("cat a{b{c,d}e}f", "a{b{c,d}e}f")]
    [InlineData("cat {a,{b,c}}", "{a,{b,c}}")]
    [InlineData("cat {a,b}{1,2}", "{a,b}{1,2}")]
    [InlineData("cat {a,b}*", "{a,b}*")]
    [InlineData("cat src/{a,b}/*.cs", "src/{a,b}/*.cs")]
    [InlineData("cat {\"x y\",z}", "{\"x y\",z}")]
    [InlineData("cat \"a\"{b,c}", "\"a\"{b,c}")]
    [InlineData("cat $HOME/{a,b}", "$HOME/{a,b}")]
    [InlineData("cat \"$TMPDIR\"/{a,b}", "\"$TMPDIR\"/{a,b}")]
    [InlineData("cat -- {-n,a}", "{-n,a}")]
    [InlineData("grep -e {foo,/etc/shadow}", "{foo,/etc/shadow}")]
    [InlineData("cp a {b,c}", "{b,c}")]
    [InlineData("echo {\"a\":1,\"b\":2}", "{\"a\":1,\"b\":2}")]
    public void Brace_word_has_no_exact_value_or_path(string source, string raw)
    {
        AssertUnresolved(Argument(source, raw));
    }

    [Fact]
    public void Brace_word_across_a_line_continuation_has_no_exact_value()
    {
        AssertUnresolved(Argument("cat {a,\\\nb}", "{a,\\\nb}"));
    }

    [Fact]
    public void Brace_word_split_across_quotes_and_a_continuation_has_no_exact_value()
    {
        // Bash removes the continuation first, so the word is {a,"b",c}.
        var parsed = Parser.Parse("cat {a,\"b\"\\\n,c}");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.All(
            parsed.Commands.Single().Arguments,
            a => Assert.IsType<ShellValueDomain.Unknown>(a.Value));
    }

    [Fact]
    public void Brace_word_in_an_option_value_has_no_exact_value()
    {
        var parsed = Parser.Parse("cat --file={/etc/shadow,x}");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Contains(
            parsed.Commands.Single().Arguments,
            a => a.Argument.Kind == ArgKind.DynamicSkip && a.Value is ShellValueDomain.Unknown);
        Assert.DoesNotContain(
            parsed.Commands.Single().Arguments,
            a => a.Value is ShellValueDomain.Exact exact && exact.Value.Contains('{'));
    }

    [Theory]
    [InlineData("cat {a}", "{a}", "/work/{a}")]
    [InlineData("cat x{}y", "x{}y", "/work/x{}y")]
    [InlineData("cat \"{a,b}\"", "\"{a,b}\"", "/work/{a,b}")]
    [InlineData("cat '{a,b}'", "'{a,b}'", "/work/{a,b}")]
    [InlineData("cat \"{\"a,b}", "\"{\"a,b}", "/work/{a,b}")]
    [InlineData("cat \\{a,b}", "\\{a,b}", "/work/{a,b}")]
    [InlineData("cat {a\\,b}", "{a\\,b}", "/work/{a,b}")]
    [InlineData("cat {a,b\\}", "{a,b\\}", "/work/{a,b}")]
    [InlineData("cat {!..#}", "{!..#}", "/work/{!..#}")]
    [InlineData("cat {1..3..x}", "{1..3..x}", "/work/{1..3..x}")]
    [InlineData("cat {a..3}", "{a..3}", "/work/{a..3}")]
    [InlineData("cat {../x}", "{../x}", "/work/{../x}")]
    public void Literal_brace_word_keeps_its_exact_path(string source, string raw, string path)
    {
        var argument = Argument(source, raw);

        Assert.Equal(path, argument.Argument.Resolved);
    }

    [Theory]
    [InlineData("find . -name x -exec rm {} \\;", "{}")]
    [InlineData("xargs -I{} cp {} dst", "{}")]
    [InlineData("awk '{print $1,$2}' f", "'{print $1,$2}'")]
    [InlineData("echo ${HOME}", "${HOME}")]
    public void Non_expanding_brace_text_keeps_its_value(string source, string raw)
    {
        var argument = Argument(source, raw);

        Assert.IsNotType<ShellValueDomain.Unknown>(argument.AuthoredValue);
    }

    [Theory]
    [InlineData("{ls,-la}")]
    [InlineData("{a,b}")]
    [InlineData("x{a,b} c")]
    public void Brace_word_in_the_command_name_fails_closed(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("command-name brace expansion", parsed.UnparseableReason!);
    }

    [Theory]
    [InlineData("git {push,log}")]
    [InlineData("git {push,a/b}")]
    [InlineData("git -p {push,log}")]
    public void Brace_word_makes_the_command_words_unknown(string source)
    {
        Assert.IsType<ShellCommandWords.Unknown>(Parser.Parse(source).Commands.Single().CommandWords);
    }

    [Fact]
    public void Brace_redirect_target_is_unresolved()
    {
        var parsed = Parser.Parse("cat ~/{a,b} > out{1,2}");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var redirect = Assert.IsType<FileRedirectAnalysis>(parsed.Commands.Single().Redirects.Single());
        Assert.False(redirect.IsComplete);
        Assert.IsType<ShellValueDomain.Unknown>(redirect.Target);
    }

    [Fact]
    public void Brace_case_word_fails_closed()
    {
        // Bash does not brace-expand a case word, but the parser fails
        // closed rather than publish a pattern it did not prove.
        var parsed = Parser.Parse("case b in {a,b}) echo m;; esac");

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("brace list", parsed.UnparseableReason!);
    }

    [Fact]
    public void Brace_loop_list_gives_no_exact_binding()
    {
        var parsed = Parser.Parse("for f in {a,b}; do cat \"$f\"; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands.Single().Arguments.Single().Value);
    }

    [Theory]
    [InlineData("cat ~/.netclaw/config/{netclaw,secrets}.json > out{1,2}; cat \"a\"{b,\"c d\"}")]
    [InlineData("echo {a,{b,c}}{1..3} ${HOME} {} x{}y \\{p,q}")]
    public void Every_prefix_parses_without_an_exception(string source)
    {
        for (var length = 0; length <= source.Length; length++)
        {
            var prefix = source.Substring(0, length);
            var exception = Record.Exception(() =>
            {
                Parser.Parse(prefix);
                Parser.TryProjectFiniteScopes(prefix, out _);
            });
            Assert.True(exception is null, $"{prefix}: {exception}");
        }
    }

    [Fact]
    public void Hostile_brace_nesting_is_linear()
    {
        var source = "cat " + new string('{', 50_000) + "a" + new string('}', 50_000);

        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
    }
}
