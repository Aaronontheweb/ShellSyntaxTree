// -----------------------------------------------------------------------
// <copyright file="BashWordExpansionFactsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins <see cref="AnalyzedArgument.MayPathnameExpand"/> and
/// <see cref="AnalyzedArgument.MayFieldSplit"/> (#232). Each fact comes from
/// the authored word, so it holds also when the value is <c>Unknown</c>.
/// </summary>
public class BashWordExpansionFactsTests
{
    private static readonly BashParser Parser = new(new BashParserOptions
    {
        HomeDirectory = "/home/test",
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
    });

    private static AnalyzedArgument Argument(string word)
    {
        var parsed = Parser.Parse("ls " + word);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        return Assert.Single(parsed.Commands.Last().Arguments);
    }

    [Theory]
    [InlineData("plain", false, false)]
    [InlineData("~/x", false, false)]
    [InlineData("*", true, false)]
    [InlineData("a?b", true, false)]
    [InlineData("a[1]", true, false)]
    [InlineData("\\*", false, false)]
    [InlineData("a\\*b", false, false)]
    [InlineData("'*'", false, false)]
    [InlineData("\"*\"", false, false)]
    [InlineData("$'*'", false, false)]
    [InlineData("$x", true, true)]
    [InlineData("${x}", true, true)]
    [InlineData("\"$x\"", false, false)]
    [InlineData("\"${x}\"", false, false)]
    [InlineData("\"${d}ret\"/*", true, false)]
    [InlineData("${d}*", true, true)]
    [InlineData("\"$d\"$e", true, true)]
    [InlineData("$(cmd)", true, true)]
    [InlineData("\"$(cmd)\"", false, false)]
    [InlineData("\"$(echo \"*\")\"", false, false)]
    [InlineData("$((1+2))", false, true)]
    [InlineData("a$((1+2))*", true, true)]
    [InlineData("\"$((1+2))\"", false, false)]
    [InlineData("\"$@\"", false, true)]
    [InlineData("\"${@}\"", false, true)]
    [InlineData("\"$*\"", false, false)]
    [InlineData("$@", true, true)]
    [InlineData("{a,b}", true, true)]
    [InlineData("x{1..3}", true, true)]
    [InlineData("\"{a,b}\"", false, false)]
    [InlineData("{a}", false, false)]
    public void Fact_follows_the_authored_quoting(string word, bool glob, bool split)
    {
        var argument = Argument(word);

        Assert.Equal(glob, argument.MayPathnameExpand);
        Assert.Equal(split, argument.MayFieldSplit);
    }

    [Fact]
    public void Inline_option_value_gets_the_fact_of_the_whole_word()
    {
        var parsed = Parser.Parse("ls --opt=$x");

        Assert.All(Assert.Single(parsed.Commands).Arguments, argument =>
        {
            Assert.True(argument.MayPathnameExpand);
            Assert.True(argument.MayFieldSplit);
        });
    }

    [Fact]
    public void PowerShell_argument_is_conservative()
    {
        var parser = new PwshParser(new PwshParserOptions { WorkingDirectory = "C:/work" });
        var parsed = parser.Parse("Get-Content 'a.txt'");

        Assert.All(parsed.Commands.SelectMany(c => c.Arguments), argument =>
        {
            Assert.True(argument.MayPathnameExpand);
            Assert.True(argument.MayFieldSplit);
        });
    }
}
