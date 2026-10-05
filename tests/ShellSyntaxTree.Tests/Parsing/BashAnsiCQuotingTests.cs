// -----------------------------------------------------------------------
// <copyright file="BashAnsiCQuotingTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the ANSI-C quote decoding of #232. Bash decodes the escapes of a
/// <c>$'…'</c> string before it uses the word. The parser decodes only
/// escapes with one exact ASCII result and fails closed on every other
/// escape and on <c>$"…"</c>.
/// </summary>
public class BashAnsiCQuotingTests
{
    private static readonly ShellLaunchEnvironment Launch = new(
        new Dictionary<string, string> { ["HOME"] = "/home/test" },
        new[] { "CDPATH" });

    private static readonly BashParser Parser = new(new BashParserOptions
    {
        HomeDirectory = "/home/test",
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
        LaunchEnvironment = Launch,
    });

    private static string Decoded(string source)
    {
        var parsed = Parser.Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        return Assert.Single(parsed.Commands).Clause.Elements.Last(e => e.Role == ClauseElementRole.Argument).Value;
    }

    [Theory]
    [InlineData("cat $'plain'", "plain")]
    [InlineData("cat $'\\a'", "\a")]
    [InlineData("cat $'\\b'", "\b")]
    [InlineData("cat $'\\e'", "\u001b")]
    [InlineData("cat $'\\E'", "\u001b")]
    [InlineData("cat $'\\f'", "\f")]
    [InlineData("cat $'\\n'", "\n")]
    [InlineData("cat $'\\r'", "\r")]
    [InlineData("cat $'\\t'", "\t")]
    [InlineData("cat $'\\v'", "\v")]
    [InlineData("cat $'\\\\'", "\\")]
    [InlineData("cat $'it\\'s'", "it's")]
    [InlineData("cat $'\\\"'", "\"")]
    [InlineData("cat $'\\?'", "?")]
    [InlineData("cat $'\\101'", "A")]
    [InlineData("cat $'\\0101'", "\b1")]
    [InlineData("cat $'\\7'", "\a")]
    [InlineData("cat $'\\x41'", "A")]
    [InlineData("cat $'\\x4g'", "\u0004g")]
    [InlineData("cat $'\\x7e'", "~")]
    [InlineData("cat $'a\"b$x`c'", "a\"b$x`c")]
    [InlineData("cat $'*'", "*")]
    public void Escape_decodes_like_bash(string source, string expected)
    {
        Assert.Equal(expected, Decoded(source));
    }

    [Theory]
    [InlineData("cat ~/.netclaw/$'\\x6beys'/key-1.xml", "/home/test/.netclaw/keys/key-1.xml")]
    [InlineData("cat $'webhooks'/route.json", "/work/webhooks/route.json")]
    [InlineData("cat a$'b'c", "/work/abc")]
    [InlineData("cat \"a\"$'\\x62'c", "/work/abc")]
    public void Decoded_word_is_the_exact_path(string source, string path)
    {
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var argument = Assert.Single(Assert.Single(parsed.Commands).Arguments);
        Assert.Equal(path, argument.Argument.Resolved);
        Assert.Equal(path, Assert.IsType<ShellValueDomain.Exact>(argument.AuthoredFileSystemValue).Value);
        Assert.False(argument.MayPathnameExpand);
    }

    [Fact]
    public void Quoted_glob_in_an_ansi_c_string_is_literal()
    {
        var parsed = Parser.Parse("cat $'*.txt'");

        var argument = Assert.Single(Assert.Single(parsed.Commands).Arguments);
        Assert.Equal("/work/*.txt", argument.Argument.Resolved);
        Assert.False(argument.MayPathnameExpand);
    }

    [Theory]
    [InlineData("cat $'\\u0041'")]
    [InlineData("cat $'\\U00000041'")]
    [InlineData("cat $'\\cA'")]
    [InlineData("cat $'\\0'")]
    [InlineData("cat $'\\x00'")]
    [InlineData("cat $'\\xff'")]
    [InlineData("cat $'\\200'")]
    [InlineData("cat $'\\x'")]
    [InlineData("cat $'\\z'")]
    [InlineData("cat $'\\8'")]
    [InlineData("cat $'unterminated")]
    [InlineData("cat $'a\\")]
    [InlineData("cat $\"text\"")]
    [InlineData("cat a$\"text\"")]
    [InlineData("cat ~/.netclaw/$'\\u006beys'/key-1.xml")]
    public void Undecodable_string_fails_closed(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Theory]
    [InlineData("cat \"$'x'\"", "$'x'")]
    [InlineData("cat '$'x''", "$x")]
    [InlineData("cat \\$'x'", "$x")]
    public void Quoted_or_escaped_dollar_stays_literal(string source, string expected)
    {
        Assert.Equal(expected, Decoded(source));
    }

    [Fact]
    public void Escaped_quote_does_not_end_a_string_in_a_substitution()
    {
        var parsed = Parser.Parse("cat $(echo $'a)b\\'c')");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Contains(parsed.Commands, c => c.ImmediateRole == CommandOccurrenceRole.Substitution &&
            c.Clause.Elements.Any(e => e.Value == "a)b'c"));
    }

    [Theory]
    [InlineData("cat ~/.netclaw/$'\\x6beys'/key-1.xml; echo $'a\\tb' \"$'x'\" $'\\u0041'")]
    [InlineData("cat $(echo $'a)b\\'c') a$\"b\"")]
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
}
