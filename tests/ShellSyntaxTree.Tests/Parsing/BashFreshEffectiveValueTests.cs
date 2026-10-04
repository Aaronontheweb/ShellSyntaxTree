// -----------------------------------------------------------------------
// <copyright file="BashFreshEffectiveValueTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the effective values of #224. The fresh-process mode proves the
/// same variable state as the isolated mode, so it publishes effective
/// values with or without the authored-facts opt-in. A quoted expansion of
/// an exactly bound name gives a command word.
/// </summary>
public class BashFreshEffectiveValueTests
{
    private static BashParser Parser(
        BashInitialStateMode mode,
        bool publishAuthored) => new(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = mode,
            PublishAuthoredSourceFacts = publishAuthored,
        });

    private static ShellValueDomain LastValue(ParsedCommand parsed) =>
        parsed.Commands[^1].Arguments[^1].Value;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fresh_mode_publishes_the_effective_loop_value(bool publishAuthored)
    {
        var parsed = Parser(BashInitialStateMode.FreshNonInteractiveNoStartup, publishAuthored)
            .Parse("for f in a.txt b.txt; do cat \"/w/$f\"; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var value = Assert.IsType<ShellValueDomain.FiniteSet>(LastValue(parsed));
        Assert.Equal(new[] { "/w/a.txt", "/w/b.txt" }, value.Values);
    }

    [Fact]
    public void Fresh_mode_with_the_opt_in_publishes_the_effective_assignment_value()
    {
        var parsed = Parser(BashInitialStateMode.FreshNonInteractiveNoStartup, publishAuthored: true)
            .Parse("x=/etc/passwd; cat \"$x\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal("/etc/passwd", Assert.IsType<ShellValueDomain.Exact>(LastValue(parsed)).Value);
        Assert.Equal(
            "cat",
            string.Join(" ", Assert.IsType<ShellCommandWords.Known>(parsed.Commands[^1].CommandWords).Words));
    }

    [Fact]
    public void Fresh_mode_with_the_opt_in_resolves_a_later_path_after_a_loop_cd()
    {
        var parsed = Parser(BashInitialStateMode.FreshNonInteractiveNoStartup, publishAuthored: true)
            .Parse("for d in /a; do cd \"$d\" && cat file.txt; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cat = parsed.Commands[^1];
        Assert.Equal("/a", Assert.IsType<ShellValueDomain.Exact>(cat.WorkingDirectory).Value);
        Assert.Equal("/a/file.txt", cat.Clause.Args.Single(a => !a.IsCwdAttribution).Resolved);
    }

    [Fact]
    public void Unknown_mode_with_the_opt_in_stays_authored_only()
    {
        // Unknown mode does not prove variable attributes, so the effective
        // value and the binding word stay unknown.
        var parsed = Parser(BashInitialStateMode.Unknown, publishAuthored: true)
            .Parse("for s in push; do git \"$s\"; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var git = parsed.Commands.Single();
        Assert.IsType<ShellValueDomain.Unknown>(git.Arguments.Single().Value);
        Assert.Equal("push", Assert.IsType<ShellValueDomain.Exact>(git.Arguments.Single().AuthoredValue).Value);
        Assert.IsType<ShellCommandWords.Unknown>(git.CommandWords);
    }

    [Theory]
    [InlineData(BashInitialStateMode.IsolatedNonInteractive)]
    [InlineData(BashInitialStateMode.FreshNonInteractiveNoStartup)]
    public void Single_value_loop_binding_gives_a_command_word(BashInitialStateMode mode)
    {
        var parsed = Parser(mode, publishAuthored: false)
            .Parse("for s in push; do git \"$s\" origin; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(
            "git push origin",
            string.Join(" ", Assert.IsType<ShellCommandWords.Known>(parsed.Commands.Single().CommandWords).Words));
    }

    // ------------------------------------------------------------ cd arity

    [Theory]
    [InlineData("for f in 'a b'; do cd $f && pwd; done")]
    [InlineData("for f in *.txt; do cd $f && pwd; done")]
    public void Unquoted_loop_cd_with_the_opt_in_gives_an_unknown_directory(string source)
    {
        // The opt-in publishes an occurrence that fails only the field-
        // splitting proof. `cd` changes to an unknown directory or fails.
        foreach (var mode in new[]
                 {
                     BashInitialStateMode.IsolatedNonInteractive,
                     BashInitialStateMode.FreshNonInteractiveNoStartup,
                 })
        {
            var parsed = Parser(mode, publishAuthored: true).Parse(source);

            Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
            Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands[^1].WorkingDirectory);
        }
    }

    [Theory]
    [InlineData(BashInitialStateMode.IsolatedNonInteractive)]
    [InlineData(BashInitialStateMode.FreshNonInteractiveNoStartup)]
    public void Unquoted_loop_cd_without_the_opt_in_fails_atomically(BashInitialStateMode mode)
    {
        var parsed = Parser(mode, publishAuthored: false)
            .Parse("for f in 'a b'; do cd $f && pwd; done");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Theory]
    [InlineData("for f in a.txt b.txt; do cat \"/w/$f\"; done; git \"$f\"")]
    [InlineData("x=push; for f in 'a b'; do cd $f && git \"$x\"; done")]
    public void Every_prefix_parses_without_an_exception(string source)
    {
        var parser = Parser(BashInitialStateMode.FreshNonInteractiveNoStartup, publishAuthored: true);
        for (var length = 0; length <= source.Length; length++)
        {
            var prefix = source.Substring(0, length);
            var exception = Record.Exception(() =>
            {
                parser.Parse(prefix);
                parser.TryProjectFiniteScopes(prefix, out _);
            });
            Assert.True(exception is null, $"{prefix}: {exception}");
        }
    }
}
