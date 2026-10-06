// -----------------------------------------------------------------------
// <copyright file="BashWordHashAndTildeTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the review findings of #243. A <c>#</c> starts a comment only when it
/// begins a word. A reserved word with a line continuation is still a
/// reserved word. A <c>~</c> after <c>=</c> or <c>:</c> of an
/// assignment-shaped word expands from HOME. Each Bash claim also runs in
/// real Bash when Bash is available.
/// </summary>
public class BashWordHashAndTildeTests
{
    private static readonly BashParser Parser = new(new BashParserOptions
    {
        HomeDirectory = BashOracle.Home,
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
        LaunchEnvironment = new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["HOME"] = BashOracle.Home },
            new[] { "CDPATH" }),
    });

    // ---------------------------------------------------------------- '#' inside a word

    public static TheoryData<string, string> WordHashSources => new()
    {
        { "printf '<%s>' \"a\"# ; printf HIDDEN", "<a#>HIDDEN" },
        { "printf '<%s>' 'a'#;printf HIDDEN", "<a#>HIDDEN" },
        { "printf '<%s>' $'a'#; printf HIDDEN", "<a#>HIDDEN" },
        { "printf '<%s>' $(true)#; printf HIDDEN", "<#>HIDDEN" },
        { "printf '<%s>' $((1))#; printf HIDDEN", "<1#>HIDDEN" },
        { "printf '<%s>' $(printf a\"b\"# ; printf HIDDEN)", "<ab#HIDDEN>" },
        { "printf '<%s>' $\\\n(printf a)#b; printf HIDDEN", "<a#b>HIDDEN" },
        { "printf '<%s>' \"a\"\\\n#b; printf HIDDEN", "<a#b>HIDDEN" },
    };

    [Theory]
    [MemberData(nameof(WordHashSources))]
    public void Hash_after_a_word_part_is_word_text(string source, string bashOutput)
    {
        // Bash starts a comment only when `#` begins a word. Before
        // 0.4.0-beta.22 the lexer started one after a quote or `)`, so the
        // command after `;` had no occurrence.
        BashOracle.AssertPrints(source, bashOutput);
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var hidden = Assert.Single(
            parsed.Commands,
            command => command.Clause.Elements.Any(element => element.Value == "HIDDEN"));
        Assert.True(hidden.IsComplete);
    }

    [Theory]
    [InlineData("printf '<%s>' \"a\"#", "a#")]
    [InlineData("printf '<%s>' 'a'#b", "a#b")]
    [InlineData("printf '<%s>' \"a\"\\\n#b", "a#b")]
    public void Hash_after_a_quote_is_part_of_the_value(string source, string value)
    {
        BashOracle.AssertPrints(source, "<" + value + ">");

        var argument = Parser.Parse(source).Commands.Single().Arguments.Last();
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value);
    }

    [Theory]
    [InlineData("printf '<%s>' \"a\" \\\n#b; printf HIDDEN", "<a>")]
    [InlineData("printf '<%s>' a;#b; printf HIDDEN", "<a>")]
    [InlineData("printf '<%s>' a|#b; printf HIDDEN\ncat", "<a>")]
    public void Hash_that_begins_a_word_starts_a_comment(string source, string bashOutput)
    {
        BashOracle.AssertPrints(source, bashOutput);
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.DoesNotContain(
            parsed.Commands,
            command => command.Clause.Elements.Any(element => element.Value == "HIDDEN"));
    }

    [Theory]
    // The assignment value gate rejects `#`.
    [InlineData("x=\"a\"#; printf HIDDEN", "HIDDEN")]
    // A CR after the `#` is a CR in code.
    [InlineData("printf '<%s>' \"a\"#\r; printf HIDDEN", "<a#\r>HIDDEN")]
    public void Hash_word_that_cannot_be_modeled_fails_closed(string source, string bashOutput)
    {
        BashOracle.AssertPrints(source, bashOutput);
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    public static TheoryData<string> HashSeedSources => new()
    {
        "echo \"$(touch /tmp/x)\" $(id -u)",
        "x=\"$(printf HIDDEN)\"; echo \"$x\"",
        "n=build; rm -rf \"$n/\" \"${n}/out\"",
        "x=/etc/hostname; cat \"$x\" $x",
        "true && touch /tmp/x || echo no",
        "echo a >> out 2>&1; cat <<< word",
        "git -C repo push origin main",
        "curl -o /tmp/out https://example.invalid",
        "echo \"$@\" \"${x}\" $y $1 \"$?\"",
        "echo $(echo $(id -u)) > /tmp/out",
        "if true; then touch /tmp/x; fi",
        "echo \"a\" 'b' $'c' $(id) $((1)) d; touch /tmp/x",
        "printf '%s' \"$(printf a)\"; rm -f ./x",
    };

    /// <summary>
    /// A generated check: a <c>#</c> at every point of each seed. Each command
    /// that Bash runs must have an occurrence, and each complete occurrence
    /// with exact values must match an argv that Bash logs for its program.
    /// The parser may also fail closed.
    /// </summary>
    [Theory]
    [MemberData(nameof(HashSeedSources))]
    public void Hash_at_any_point_matches_bash_or_fails_closed(string seed)
    {
        if (!BashOracle.IsAvailable())
        {
            return;
        }

        // The oracle must see the commands, or the check proves nothing.
        Assert.NotEmpty(BashOracle.LoggedCommands(seed)!);
        for (var index = 0; index <= seed.Length; index++)
        {
            var source = seed.Substring(0, index) + "#" + seed.Substring(index);
            var parsed = Parser.Parse(source);
            if (parsed.IsUnparseable)
            {
                continue;
            }

            var ran = BashOracle.LoggedCommands(source)!;
            var programs = parsed.Commands.Select(ProgramWord).ToList();
            foreach (var argv in ran)
            {
                Assert.True(
                    programs.Contains(argv[0]),
                    $"Bash ran `{string.Join(" ", argv)}` with no occurrence in {Quote(source)}");
            }

            foreach (var command in parsed.Commands)
            {
                var expected = ExactArgv(command);
                if (!command.IsComplete || expected is null ||
                    !ran.Any(argv => argv[0] == expected[0]))
                {
                    continue;
                }

                Assert.True(
                    ran.Any(argv => argv.SequenceEqual(expected)),
                    $"`{string.Join(" ", expected)}` does not match Bash for {Quote(source)}: " +
                    string.Join(" | ", ran.Select(argv => string.Join(" ", argv))));
            }
        }
    }

    // ---------------------------------------------------------------- reserved words

    [Theory]
    [InlineData("time\\\n printf HIDDEN")]
    [InlineData("t\\\nime printf HIDDEN")]
    [InlineData("!\\\n printf HIDDEN")]
    [InlineData("coproc\\\n printf HIDDEN")]
    [InlineData("{\\\n printf HIDDEN; }\\\n")]
    public void Reserved_word_with_a_continuation_fails_closed(string source)
    {
        // Bash removes the continuation, so the reserved word stays and Bash
        // runs `printf HIDDEN`. Before 0.4.0-beta.22 these parsed with the
        // reserved word as the program.
        if (BashOracle.IsAvailable())
        {
            Assert.Contains(
                BashOracle.LoggedCommands(source + "\nwait")!,
                argv => argv.SequenceEqual(new[] { "printf", "HIDDEN" }));
        }

        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Theory]
    [InlineData("i\\\nf true; then printf HIDDEN; f\\\ni", "HIDDEN")]
    [InlineData("for f in a; d\\\no printf HIDDEN; d\\\none", "HIDDEN")]
    public void Keyword_with_a_continuation_is_a_keyword(string source, string bashOutput)
    {
        BashOracle.AssertPrints(source, bashOutput);
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Contains(parsed.Commands, command => command.Clause.Elements.Any(e => e.Value == "HIDDEN"));
    }

    [Theory]
    // Bash reads `(\⏎(` as the arithmetic command `((`: `ls` is a variable
    // there, not a program. The parser fails closed on `((…))`.
    [InlineData("(\\\n(ls))")]
    [InlineData("((ls)\\\n)")]
    public void Arithmetic_command_with_a_continuation_fails_closed(string source)
    {
        if (BashOracle.IsAvailable())
        {
            Assert.Empty(BashOracle.LoggedCommands(source)!);
        }

        Assert.True(Parser.Parse(source).IsUnparseable);
    }

    [Fact]
    public void Case_terminator_with_a_continuation_ends_the_item()
    {
        const string source = "case a in a) printf HIDDEN;\\\n; esac";
        BashOracle.AssertPrints(source, "HIDDEN");
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Contains(parsed.Commands, command => command.Clause.Elements.Any(e => e.Value == "HIDDEN"));
    }

    [Theory]
    [InlineData("time printf HIDDEN")]
    [InlineData("! printf HIDDEN")]
    [InlineData("coproc printf HIDDEN")]
    [InlineData("{ printf HIDDEN; }")]
    public void Continuation_at_any_point_of_reserved_syntax_fails_closed(string seed)
    {
        Assert.True(Parser.Parse(seed).IsUnparseable);
        for (var index = 0; index <= seed.Length; index++)
        {
            var source = seed.Substring(0, index) + "\\\n" + seed.Substring(index);
            Assert.True(Parser.Parse(source).IsUnparseable, Quote(source));
        }
    }

    // ---------------------------------------------------------------- assignment-word tilde

    [Theory]
    [InlineData("PREFIX=~/x", "PREFIX=/home/test/x")]
    [InlineData("a=b:~/y", "a=b:/home/test/y")]
    [InlineData("a=~", "a=/home/test")]
    [InlineData("a=~:~/q", "a=/home/test:/home/test/q")]
    [InlineData("a=:~/y", "a=:/home/test/y")]
    [InlineData("a+=~/x", "a+=/home/test/x")]
    [InlineData("a=\\\n~/x", "a=/home/test/x")]
    // Not expanded by Bash: the text stays.
    [InlineData("a=x~", "a=x~")]
    [InlineData("a=b=~/x", "a=b=~/x")]
    [InlineData("9a=~/x", "9a=~/x")]
    [InlineData("a=~\"/x\"", "a=~/x")]
    [InlineData("a=\\~/x", "a=~/x")]
    [InlineData("a=~\\/x", "a=~/x")]
    [InlineData("x:~/y", "x:~/y")]
    public void Tilde_after_equals_or_colon_has_the_bash_value(string word, string value)
    {
        // Outside POSIX mode Bash expands `~` after the first `=` and after
        // `:` of a word whose text before `=` is a name (#243).
        BashOracle.AssertPrints("printf '<%s>' " + word, "<" + value + ">");

        var argument = Parser.Parse("printf '<%s>' " + word).Commands.Single().Arguments.Last();
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value);
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(argument.AuthoredValue).Value);
    }

    [Theory]
    // An escaped character or an expansion before the `=` makes the word not
    // assignment-shaped, and one before the `~` breaks "directly after".
    [InlineData("a\\b=~/x", "ab=~/x")]
    [InlineData("a=\\:~/x", "a=:~/x")]
    [InlineData("a$x=~/y", "ab=~/y")]
    [InlineData("a=$x~/y", "a=b~/y")]
    public void Tilde_after_an_escape_or_an_expansion_stays_text(string word, string value)
    {
        var source = "x=b; printf '<%s>' " + word;
        BashOracle.AssertPrints(source, "<" + value + ">");

        var argument = Parser.Parse(source).Commands.Last().Arguments.Last();
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(argument.AuthoredValue).Value);
    }

    [Theory]
    [InlineData("a=~+", "a=")]
    [InlineData("a=~user/x", "a=~user/x")]
    [InlineData("\"a\"=~/x", "a=~/x")]
    [InlineData("a=\"b\":~/x", "a=b:/home/test/x")]
    public void Tilde_that_the_parser_cannot_prove_is_unknown(string word, string bashPrefix)
    {
        // `~+` is PWD and `~user` a home folder. After a quoted part the
        // lexer cannot see whether the word is assignment-shaped.
        if (BashOracle.IsAvailable())
        {
            var logged = BashOracle.LoggedCommands("printf '<%s>' " + word)!;
            Assert.StartsWith(bashPrefix, Assert.Single(logged)[2]);
        }

        var argument = Parser.Parse("printf '<%s>' " + word).Commands.Single().Arguments.Last();
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
    }

    [Fact]
    public void Here_string_keeps_an_assignment_word_tilde()
    {
        const string source = "cat <<< a=~/x";
        BashOracle.AssertPrints(source, "a=~/x");

        var redirect = Assert.IsType<HereStringRedirectAnalysis>(
            Assert.Single(Parser.Parse(source).Commands.Single().Redirects));
        Assert.Equal("a=~/x\n", Assert.IsType<ShellValueDomain.Exact>(redirect.Data).Value);
    }

    [Fact]
    public void Redirect_target_expands_an_assignment_word_tilde()
    {
        var redirect = Assert.IsType<FileRedirectAnalysis>(
            Assert.Single(Parser.Parse("printf a > a=~/r").Commands.Single().Redirects));

        Assert.Equal("/work/a=/home/test/r", Assert.IsType<ShellValueDomain.Exact>(redirect.Target).Value);
    }

    [Theory]
    [InlineData("x=~/a; printf '<%s>' \"$x\"", "</home/test/a>")]
    [InlineData("export Y=~/b; printf '<%s>' \"$Y\"", "</home/test/b>")]
    public void Assignment_with_a_leading_tilde_still_parses(string source, string bashOutput)
    {
        BashOracle.AssertPrints(source, bashOutput);
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(
            bashOutput.Substring(1, bashOutput.Length - 2),
            Assert.IsType<ShellValueDomain.Exact>(parsed.Commands.Last().Arguments.Last().Value).Value);
    }

    [Theory]
    [InlineData("make install PREFIX=~/x a=~+")]
    [InlineData("make install a=\\\n~")]
    [InlineData("make install a=\\\n~+")]
    public void Assignment_word_tilde_is_not_a_static_command_word(string source)
    {
        var words = Parser.Parse(source).Commands.Single().CommandWords;

        var known = Assert.IsType<ShellCommandWords.Known>(words);
        Assert.DoesNotContain(known.Words, word => word.Contains('~'));
    }

    // ---------------------------------------------------------------- helpers

    private static string ProgramWord(CommandOccurrence command) =>
        command.Clause.Elements.FirstOrDefault()?.Value ?? string.Empty;

    /// <summary>
    /// The argv that the occurrence proves, or null when a word is not exact.
    /// An inline <c>--name=value</c> word has two analyzed arguments.
    /// </summary>
    private static string[]? ExactArgv(CommandOccurrence command)
    {
        var argv = new List<string>();
        foreach (var element in command.Clause.Elements)
        {
            if (element.Role == ClauseElementRole.Verb)
            {
                argv.Add(element.Value);
                continue;
            }

            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            var parts = command.Arguments.Where(argument => ReferenceEquals(argument.Element, element)).ToList();
            if (parts.Count == 0 || parts.Any(part => part.Value is not ShellValueDomain.Exact))
            {
                return null;
            }

            var values = parts.Select(part => ((ShellValueDomain.Exact)part.Value).Value).ToList();
            argv.Add(values.Count == 2 ? values[0] + "=" + values[1] : values[0]);
        }

        return argv.Count == 0 ? null : argv.ToArray();
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
}
