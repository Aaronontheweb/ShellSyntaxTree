// -----------------------------------------------------------------------
// <copyright file="BashLoopControlTransferTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the bounded control-transfer builtins of #227. A <c>break</c>,
/// <c>continue</c>, <c>exit</c>, or <c>return</c> with no operand or one
/// static decimal operand changes no variable and no directory. The state
/// pass joins the state at a <c>break</c> into the end of its loop and the
/// state at a <c>continue</c> into the head of its loop.
/// </summary>
public class BashLoopControlTransferTests
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

    private static IReadOnlyList<string> Values(ShellValueDomain value) => value switch
    {
        ShellValueDomain.Exact exact => new[] { exact.Value },
        ShellValueDomain.FiniteSet set => set.Values.OrderBy(v => v, System.StringComparer.Ordinal).ToArray(),
        _ => throw new Xunit.Sdk.XunitException($"expected a bounded value, got {value}"),
    };

    [Theory]
    [InlineData("for d in a b; do continue; done")]
    [InlineData("for d in a b; do break; done")]
    [InlineData("for d in a b; do break 2; done")]
    [InlineData("for d in a b; do continue 1; done")]
    [InlineData("for d in a b; do if [ -d \"$d\" ]; then continue; fi; echo \"$d\"; done")]
    [InlineData("for d in a b; do [ -d \"$d\" ] || continue; cat \"$d/f\"; done")]
    [InlineData("for d in /a /b; do case \"$d\" in /a) continue;; esac; cat \"$d/f\"; done")]
    [InlineData("for f in *.txt; do [ -s \"$f\" ] || continue; wc -l \"$f\"; done")]
    [InlineData("while true; do break; done")]
    [InlineData("until false; do break; done")]
    [InlineData("for d in a b; do exit 1; done")]
    [InlineData("for d in a b; do exit; done")]
    [InlineData("for d in a b; do return 1; done")]
    [InlineData("[ -f a ] || exit 1; for f in a b; do echo \"$f\"; done")]
    [InlineData("[ -f a ] || exit 1; if true; then echo; fi")]
    [InlineData("[ -f a ] || exit 1; x=2; echo \"$x\"")]
    [InlineData("cat \"$HOME/a\" || exit 1; cat \"$TMPDIR/b\"")]
    public void Bounded_transfer_parses(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
    }

    [Theory]
    [InlineData("for d in a b; do break x; done")]
    [InlineData("for d in a b; do break 0; done")]
    [InlineData("for d in a b; do break -1; done")]
    [InlineData("for d in a b; do break 1 2; done")]
    [InlineData("for d in a b; do break \"$n\"; done")]
    [InlineData("for d in a b; do break '1'; done")]
    [InlineData("for d in a b; do exit \"$x\"; done")]
    [InlineData("for d in a b; do exit $?; done")]
    [InlineData("for d in a b; do command break; done")]
    public void Unbounded_transfer_in_a_loop_fails_closed(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Fact]
    public void Launch_value_survives_an_exit()
    {
        var parsed = Parser.Parse("cat \"$HOME/a\" || exit 1; cat \"$TMPDIR/b\"");

        var cat = parsed.Commands[^1];
        Assert.Equal("/tmp/nc/b", Assert.IsType<ShellValueDomain.Exact>(cat.Arguments.Single().Value).Value);
    }

    [Fact]
    public void Break_state_reaches_the_end_of_the_loop()
    {
        var parsed = Parser.Parse(
            "x=/a; for d in 1 2; do x=/b; [ -n \"$d\" ] && break; x=/c; done; cat \"$x\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(new[] { "/b", "/c" }, Values(parsed.Commands[^1].Arguments.Single().Value));
    }

    [Fact]
    public void Continue_state_reaches_the_next_iteration()
    {
        var parsed = Parser.Parse(
            "x=/a; for d in 1 2; do cat \"$x\"; x=/b; [ -n \"$d\" ] && continue; x=/c; done; cat \"$x\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var inLoop = parsed.Commands.First(c => c.ImmediateRole == CommandOccurrenceRole.LoopBody);
        Assert.Equal(new[] { "/a", "/b", "/c" }, Values(inLoop.Arguments.Single().Value));
        Assert.Equal(new[] { "/b", "/c" }, Values(parsed.Commands[^1].Arguments.Single().Value));
    }

    [Fact]
    public void Continue_in_the_last_iteration_reaches_the_end_of_the_loop()
    {
        var parsed = Parser.Parse(
            "x=/a; for d in 1; do x=/b; [ -n \"$d\" ] && continue; x=/c; done; cat \"$x\"");

        Assert.Equal(new[] { "/b", "/c" }, Values(parsed.Commands[^1].Arguments.Single().Value));
    }

    [Theory]
    [InlineData("break 2")]
    [InlineData("break 9")]
    [InlineData("continue 2")]
    public void Level_targets_the_outer_loop(string transfer)
    {
        var parsed = Parser.Parse(
            $"for d in 1 2; do for e in 3 4; do x=/e; {transfer}; done; x=/z; done; cat \"$x\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(new[] { "/e", "/z" }, Values(parsed.Commands[^1].Arguments.Single().Value));
    }

    [Fact]
    public void Break_state_reaches_the_end_of_a_fixed_point_loop()
    {
        var parsed = Parser.Parse(
            "x=/a; for d in *; do x=/b; [ -n \"$d\" ] && break; x=/c; done; cat \"$x\"");

        Assert.Equal(new[] { "/a", "/b", "/c" }, Values(parsed.Commands[^1].Arguments.Single().Value));
    }

    [Fact]
    public void Continue_state_reaches_the_head_of_a_fixed_point_loop()
    {
        var parsed = Parser.Parse(
            "x=/a; for d in *; do cat \"$x\"; x=/b; [ -n \"$d\" ] && continue; x=/c; done");

        Assert.Equal(new[] { "/a", "/b", "/c" }, Values(parsed.Commands[0].Arguments.Single().Value));
    }

    [Fact]
    public void Break_state_reaches_the_end_of_a_while_loop()
    {
        var parsed = Parser.Parse(
            "x=/a; while [ -f g ]; do x=/b; break; x=/c; done; cat \"$x\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(new[] { "/a", "/b", "/c" }, Values(parsed.Commands[^1].Arguments.Single().Value));
    }

    [Fact]
    public void Continue_state_reaches_the_condition_of_a_while_loop()
    {
        var parsed = Parser.Parse(
            "x=/a; while [ -f \"$x\" ]; do x=/b; continue; x=/c; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(
            new[] { "/a", "/b", "/c" },
            Values(parsed.Commands[0].Arguments.First(a => a.Element.Raw == "\"$x\"").Value));
    }

    [Theory]
    [InlineData("x=/a; for d in 1 2; do x=/b; [ -n \"$d\" ] && break 2; x=/c; done; cat \"$x\"")]
    [InlineData("for d in a b; do (break); x=$(continue); echo x | break; echo \"$d\"; done")]
    [InlineData("for d in /a /b; do cd \"$d\" || continue; cat f; done; cat g")]
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
