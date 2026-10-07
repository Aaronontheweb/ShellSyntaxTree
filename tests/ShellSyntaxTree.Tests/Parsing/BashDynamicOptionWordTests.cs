// -----------------------------------------------------------------------
// <copyright file="BashDynamicOptionWordTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// An option word with an expansion (<c>-o"$n"</c>, <c>--$n=x</c>) has no
/// exact value unless the parser proves it (#245). The parser gives every
/// option word the Literal kind. Before 0.4.0-beta.24 the projection then
/// used the raw spelling as the exact value: <c>-o$n</c>, but Bash passes
/// <c>-oa</c>. Each Bash claim also runs in real Bash when Bash is available.
/// </summary>
public class BashDynamicOptionWordTests
{
    private static readonly BashInitialStateMode[] Modes =
    {
        BashInitialStateMode.Unknown,
        BashInitialStateMode.IsolatedNonInteractive,
        BashInitialStateMode.FreshNonInteractiveNoStartup,
    };

    private static BashParser CreateParser(BashInitialStateMode mode) => new(new BashParserOptions
    {
        HomeDirectory = BashOracle.Home,
        WorkingDirectory = "/work",
        InitialStateMode = mode,
        PublishAuthoredSourceFacts = true,
    });

    [Theory]
    // The sources of #245.
    [InlineData("for n in a x; do tool -o\"$n\"; done", "-oa", "-ox")]
    [InlineData("for n in a; do git -C\"$n\" status; done", "-Ca")]
    [InlineData("for n in a x; do tool -xo\"$n\"; done", "-xoa", "-xox")]
    [InlineData("for n in a x; do tool -o\"${n}\"; done", "-oa", "-ox")]
    [InlineData("for n in a x; do tool -o\"$n\"x; done", "-oax", "-oxx")]
    // Other option shapes.
    [InlineData("for n in a x; do tool -o$n; done", "-oa", "-ox")]
    [InlineData("for n in a x; do tool -\"$n\"; done", "-a", "-x")]
    [InlineData("for n in a x; do tool -o'a'\"$n\"; done", "-oaa", "-oax")]
    public void Unknown_state_gives_no_exact_value_for_an_option_with_a_variable(
        string source,
        params string[] bashWords)
    {
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            Assert.Equal(bashWords, logged.Select(argv => argv[1]).ToArray());
        }

        var argument = CreateParser(BashInitialStateMode.Unknown)
            .Parse(source).Commands.Single().Arguments[0];

        // The state is not proved, so the value is not known. The authored
        // value is the set that the loop gives.
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        Assert.False(argument.HasEffectiveValue);
        var authored = argument.AuthoredValue switch
        {
            ShellValueDomain.Exact exact => new[] { exact.Value },
            ShellValueDomain.FiniteSet set => set.Values.ToArray(),
            _ => throw new Xunit.Sdk.XunitException("authored value is not proved"),
        };
        Assert.Equal(bashWords.OrderBy(word => word, System.StringComparer.Ordinal), authored);
    }

    [Theory]
    [InlineData("for n in a x; do tool -o\"$n\"; done", "-oa", "-ox")]
    [InlineData("for n in a; do git -C\"$n\" status; done", "-Ca")]
    [InlineData("for n in a x; do tool -\"$n\"; done", "-a", "-x")]
    public void Proved_state_keeps_the_option_values(string source, params string[] bashWords)
    {
        foreach (var mode in new[]
                 {
                     BashInitialStateMode.IsolatedNonInteractive,
                     BashInitialStateMode.FreshNonInteractiveNoStartup,
                 })
        {
            var value = CreateParser(mode).Parse(source).Commands.Single().Arguments[0].Value;
            var values = value switch
            {
                ShellValueDomain.Exact exact => new[] { exact.Value },
                ShellValueDomain.FiniteSet set => set.Values.ToArray(),
                _ => throw new Xunit.Sdk.XunitException($"{mode}: value is not proved"),
            };
            Assert.Equal(bashWords.OrderBy(word => word, System.StringComparer.Ordinal), values);
        }
    }

    [Theory]
    [InlineData("for n in a x; do tool --$n=x; done")]
    [InlineData("for n in a x; do tool --\"$n\"=x; done")]
    [InlineData("for n in a x; do tool --x\"$n\"=y; done")]
    [InlineData("n=a; tool --\"$n\"=x")]
    [InlineData("tool --\"$1\"=x")]
    [InlineData("tool --$(id)=x")]
    [InlineData("tool --*=x")]
    [InlineData("for n in a x; do tool --$n={a,b}; done")]
    [InlineData("for n in a x; do tool -\"$n\"x={a,b}; done")]
    // A brace expansion in the option name.
    [InlineData("tool --{a,b}=x")]
    [InlineData("tool --x{,=}y")]
    [InlineData("tool --x{1..2}=a")]
    [InlineData("tool -F{a,b=c}")]
    [InlineData("tool \"--$1\"={a,b}")]
    [InlineData("tool \"--$1=x\"")]
    [InlineData("for v in q; do tool \"--$v=x\"; done")]
    public void Option_part_with_an_expansion_gives_no_exact_pair(string source)
    {
        // The option part was the exact raw text `--$n` in every mode. The
        // value part is not proved either: with n='a=b', Bash passes
        // `--a=b=x`, and the program reads the value `b=x`.
        BashOracle.AssertPrints(
            "n='a=b'; printf '<%s>' --\"$n\"=x",
            "<--a=b=x>");

        foreach (var mode in Modes)
        {
            var parsed = CreateParser(mode).Parse(source);
            if (parsed.IsUnparseable)
            {
                // `n=a; …` needs the fresh-process mode.
                Assert.NotEqual(BashInitialStateMode.FreshNonInteractiveNoStartup, mode);
                continue;
            }

            var arguments = parsed.Commands.Last().Arguments;
            Assert.Equal(2, arguments.Count);
            Assert.All(arguments, argument => Assert.IsType<ShellValueDomain.Unknown>(argument.Value));
        }
    }

    [Theory]
    [InlineData("cat --file={/etc/shadow,x}", "--file")]
    [InlineData("cat --file=\"a\"{b,c}", "--file")]
    [InlineData("cat --\"x\"=y", "--x")]
    [InlineData("cat --x='$n'", "--x")]
    [InlineData("for n in a; do cat --file=\"$n\"; done", "--file")]
    // Review of #247: a quoted or escaped name before a brace value.
    [InlineData("cat --'a'={p,q}", "--a")]
    [InlineData("cat \"--a\"={p,q}", "--a")]
    [InlineData("cat '--a'={p,q}", "--a")]
    [InlineData("cat $'--a'={p,q}", "--a")]
    [InlineData("cat -'F'x={p,q}", "-Fx")]
    [InlineData("cat --a\\b={p,q}", "--ab")]
    public void Static_option_part_keeps_its_exact_value(string source, string option)
    {
        var logged = BashOracle.LoggedCommands(source);
        if (logged is not null)
        {
            Assert.All(
                Assert.Single(logged).Skip(1),
                word => Assert.StartsWith(option + "=", word, System.StringComparison.Ordinal));
        }

        foreach (var mode in Modes)
        {
            var arguments = CreateParser(mode).Parse(source).Commands.Single().Arguments;
            Assert.Equal(2, arguments.Count);
            Assert.Equal(option, Assert.IsType<ShellValueDomain.Exact>(arguments[0].Value).Value);
        }
    }

    public static IEnumerable<object[]> SweepSources()
    {
        var words = new[]
        {
            "-o\"$n\"", "-o$n", "--$n=x", "--o=\"$n\"", "-\"$n\"", "x\"$n\"", "\"$n\"",
            "-o'$n'", "-o\\$n", "--o=$n", "-o\"$n\"-\"$n\"", "--$n", "--\"$n\"",
            "-o\"${n}\"", "-o\"${n}\"x", "--\"$n\"=\"$n\"", "-o\"$n\"=x", "--o\"=\"$n",
            "--o\\=$n", "--o'='\"$n\"", "--o$'='$n", "\"--o=$n\"", "\"--$n=x\"", "--'o'\\=\"$n\"",
        };
        foreach (var word in words)
        {
            foreach (var mode in Modes)
            {
                yield return new object[] { "for n in a x=y; do tool " + word + "; done", mode };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SweepSources))]
    public void Every_proved_option_value_matches_bash(string source, BashInitialStateMode mode)
    {
        // A sweep of word shapes. Each exact or finite value must hold the
        // word that Bash passes. For an option pair, the option part is the
        // text before the first `=` of that word and the value is the rest.
        var parsed = CreateParser(mode).Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = parsed.Commands.Single();
        var logged = BashOracle.LoggedCommands(source);
        if (logged is null)
        {
            return;
        }

        var element = command.Clause.Elements.Single(e => e.Role == ClauseElementRole.Argument);
        var arguments = command.Arguments.Where(argument => argument.Element == element).ToArray();
        foreach (var argv in logged)
        {
            var word = Assert.Single(argv.Skip(1));
            if (arguments.Length == 1)
            {
                AssertHolds(arguments[0].Value, word, source);
                continue;
            }

            var equals = word.IndexOf('=');
            Assert.True(equals > 0, source);
            AssertHolds(arguments[0].Value, word.Substring(0, equals), source);
            AssertHolds(arguments[1].Value, word.Substring(equals + 1), source);
        }
    }

    private static void AssertHolds(ShellValueDomain value, string word, string source)
    {
        switch (value)
        {
            case ShellValueDomain.Exact exact:
                Assert.True(exact.Value == word, $"{source}: exact {exact.Value}, Bash {word}");
                break;
            case ShellValueDomain.FiniteSet set:
                Assert.True(set.Values.Contains(word), $"{source}: set lacks Bash {word}");
                break;
        }
    }
}
