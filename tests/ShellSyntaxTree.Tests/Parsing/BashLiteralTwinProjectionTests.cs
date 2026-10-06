// -----------------------------------------------------------------------
// <copyright file="BashLiteralTwinProjectionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class BashLiteralTwinProjectionTests
{
    private const string WorkingDirectory = "/work";

    private static BashParser CreateParser(
        BashInitialStateMode mode = BashInitialStateMode.FreshNonInteractiveNoStartup) =>
        new(new BashParserOptions
        {
            WorkingDirectory = WorkingDirectory,
            HomeDirectory = "/home/agent",
            InitialStateMode = mode,
            PublishAuthoredSourceFacts = true,
        });

    private static readonly BashParser Parser = CreateParser();

    [Fact]
    public void The_real_gh_api_loop_gives_one_gh_api_twin_for_each_issue()
    {
        const string source =
            "for n in 8250 8244 8223 8208 8187; do gh api -X PATCH repos/akkadotnet/akka.net/issues/$n " +
            "-f milestone=157 >/dev/null && echo \"moved $n\"; done";

        Assert.True(Parser.TryProjectLiteralTwins(source, out var projection));

        var issues = new[] { "8250", "8244", "8223", "8208", "8187" };
        var api = Assert.Single(projection!.Commands, command => command.SourceOccurrenceIndex == 0);
        Assert.Same(projection.Parsed.Commands[0], api.SourceOccurrence);
        Assert.Equal(issues.Length, api.Twins.Count);
        for (var index = 0; index < issues.Length; index++)
        {
            var twin = api.Twins[index];
            var path = "repos/akkadotnet/akka.net/issues/" + issues[index];
            Assert.Equal(source.Replace("issues/$n", "issues/" + issues[index]), twin.Source);
            Assert.Equal(path, Assert.Single(twin.Words).Value);
            var literal = Parser.Parse(
                $"gh api -X PATCH {path} -f milestone=157 >/dev/null").Commands[0];
            AssertSameFacts(literal, twin.Occurrence);
            Assert.Equal(new[] { "gh", "api" },
                Assert.IsType<ShellCommandWords.Known>(twin.Occurrence.CommandWords).Words);
            var argument = twin.Occurrence.Arguments.Single(candidate => candidate.Argument.Raw == path);
            Assert.Equal(ArgKind.Literal, argument.Argument.Kind);
            Assert.Equal("/work/" + path, argument.Argument.Resolved);
        }

        var echo = Assert.Single(projection.Commands, command => command.SourceOccurrenceIndex == 1);
        Assert.Equal(issues.Select(issue => "moved " + issue),
            echo.Twins.Select(twin => Assert.Single(twin.Words).Value));
    }

    [Fact]
    public void A_quoted_file_loop_gives_the_facts_of_each_literal_command()
    {
        Assert.True(Parser.TryProjectLiteralTwins(
            "for f in a.txt b.txt; do cat \"$f\"; done", out var projection));

        var command = Assert.Single(projection!.Commands);
        Assert.IsType<ShellCommandWords.Unknown>(command.SourceOccurrence.CommandWords);
        Assert.Equal(new[] { "a.txt", "b.txt" },
            command.Twins.Select(twin => Assert.Single(twin.Words).Value));
        foreach (var twin in command.Twins)
        {
            var literal = Assert.Single(Parser.Parse("cat " + twin.Words[0].Value).Commands);
            AssertSameFacts(literal, twin.Occurrence);
            Assert.Equal(new[] { "cat" },
                Assert.IsType<ShellCommandWords.Known>(twin.Occurrence.CommandWords).Words);
        }
    }

    [Theory]
    [InlineData("for n in $(gh issue list); do gh api repos/x/$n; done")]
    [InlineData("for n in $(gh issue list); do gh api \"repos/x/$n\"; done")]
    [InlineData("for n in a b; do gh api \"repos/$BASE/$n\"; done")]
    [InlineData("for f in /work/*.txt; do cat \"$f\"; done")]
    public void A_word_without_a_complete_value_set_gives_no_twins(string source)
    {
        Assert.False(Parser.TryProjectLiteralTwins(source, out var projection));
        Assert.Null(projection);
    }

    [Fact]
    public void A_list_over_the_value_limit_gives_no_twins()
    {
        var thirtyTwo = string.Join(" ", Enumerable.Range(1, 32));
        Assert.True(Parser.TryProjectLiteralTwins(
            $"for n in {thirtyTwo}; do gh api \"repos/x/$n\"; done", out var projection));
        Assert.Equal(32, Assert.Single(projection!.Commands).Twins.Count);

        var thirtyThree = string.Join(" ", Enumerable.Range(1, 33));
        Assert.False(Parser.TryProjectLiteralTwins(
            $"for n in {thirtyThree}; do gh api \"repos/x/$n\"; done", out _));
    }

    [Fact]
    public void Combinations_over_the_limit_give_no_twins()
    {
        // Each word has six values. The combinations (36) pass the limit of 32.
        Assert.False(Parser.TryProjectLiteralTwins(
            "for a in 1 2 3 4 5 6; do for b in 1 2 3 4 5 6; do cp \"src/$a\" \"dst/$b\"; done; done",
            out _));
    }

    [Fact]
    public void The_twins_of_a_projection_stop_at_the_total_budget()
    {
        var values = string.Join(" ", Enumerable.Range(1, 32));
        var source = $"for n in {values}; do a \"x/$n\"; b \"x/$n\"; c \"x/$n\"; d \"x/$n\"; e \"x/$n\"; done";

        Assert.True(Parser.TryProjectLiteralTwins(source, out var projection));

        Assert.Equal(new[] { 0, 1, 2, 3 },
            projection!.Commands.Select(command => command.SourceOccurrenceIndex));
        Assert.Equal(128, projection.Commands.Sum(command => command.Twins.Count));
    }

    [Theory]
    [InlineData("for d in a b; do ~/bin/tool \"$d\"; done")]
    [InlineData("for d in a b; do ~/bin/tool sub \"$d\"; done")]
    public void A_program_word_that_the_shell_can_change_gives_no_twins(string source)
    {
        Assert.False(Parser.TryProjectLiteralTwins(source, out _));
    }

    [Theory]
    [InlineData("'a b'")]
    [InlineData("$'a\\tb'")]
    [InlineData("$'a\\nb'")]
    [InlineData("'*.txt'")]
    [InlineData("'a?'")]
    [InlineData("'[ab]'")]
    [InlineData("'a\\b'")]
    [InlineData("''")]
    public void An_unquoted_value_that_can_split_or_glob_gives_no_twins(string value)
    {
        Assert.False(Parser.TryProjectLiteralTwins(
            $"for n in {value} plain; do echo $n; done", out _));
    }

    [Theory]
    [InlineData("'a b.txt'", "a b.txt")]
    [InlineData("'*.txt'", "*.txt")]
    [InlineData("\"it's\"", "it's")]
    public void A_quoted_value_keeps_its_exact_text_in_the_twin(string authored, string value)
    {
        Assert.True(Parser.TryProjectLiteralTwins(
            $"for f in {authored} plain; do cat \"$f\"; done", out var projection));

        var twin = Assert.Single(projection!.Commands).Twins[0];
        Assert.Equal(value, Assert.Single(twin.Words).Value);
        var argument = Assert.Single(twin.Occurrence.Arguments);
        Assert.Equal(ArgKind.Literal, argument.Argument.Kind);
        Assert.False(argument.MayPathnameExpand);
        Assert.False(argument.MayFieldSplit);
        Assert.Equal(value, Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value);
    }

    [Fact]
    public void An_unquoted_value_needs_the_fresh_process_contract()
    {
        const string unquoted = "for n in 1 2; do gh api repos/x/$n; done";
        const string quoted = "for n in 1 2; do gh api \"repos/x/$n\"; done";
        var isolated = CreateParser(BashInitialStateMode.IsolatedNonInteractive);

        Assert.True(Parser.TryProjectLiteralTwins(unquoted, out _));
        Assert.False(isolated.TryProjectLiteralTwins(unquoted, out _));
        Assert.True(isolated.TryProjectLiteralTwins(quoted, out var projection));
        Assert.Equal(2, Assert.Single(projection!.Commands).Twins.Count);
    }

    [Fact]
    public void Nested_loops_give_each_combination_of_values()
    {
        Assert.True(Parser.TryProjectLiteralTwins(
            "for a in x y; do for b in 1 2; do cp \"src/$a\" \"dst/$b\"; done; done",
            out var projection));

        Assert.Equal(
            new[] { "src/x dst/1", "src/x dst/2", "src/y dst/1", "src/y dst/2" },
            Assert.Single(projection!.Commands).Twins.Select(twin =>
                string.Join(" ", twin.Words.Select(word => word.Value))));

        Assert.True(Parser.TryProjectLiteralTwins(
            "for a in x y; do for b in 1 2; do echo \"$a-$b\"; done; done",
            out projection));
        Assert.Equal(new[] { "x-1", "x-2", "y-1", "y-2" },
            Assert.Single(projection!.Commands).Twins.Select(twin => Assert.Single(twin.Words).Value));
    }

    [Fact]
    public void A_value_in_the_verb_slot_gives_the_command_words_of_each_literal()
    {
        Assert.True(Parser.TryProjectLiteralTwins(
            "for v in push fetch; do git $v origin; done", out var projection));

        var command = Assert.Single(projection!.Commands);
        Assert.IsType<ShellCommandWords.Unknown>(command.SourceOccurrence.CommandWords);
        Assert.Equal(
            new[] { "git push origin", "git fetch origin" },
            command.Twins.Select(twin => string.Join(" ",
                Assert.IsType<ShellCommandWords.Known>(twin.Occurrence.CommandWords).Words)));
    }

    [Fact]
    public void A_value_that_starts_with_a_dash_is_an_option_in_the_twin()
    {
        Assert.True(Parser.TryProjectLiteralTwins(
            "for n in -rf build; do rm \"$n\"; done", out var projection));

        var twins = Assert.Single(projection!.Commands).Twins;
        Assert.True(Assert.Single(twins[0].Occurrence.Arguments).Argument.IsFlag);
        Assert.False(Assert.Single(twins[1].Occurrence.Arguments).Argument.IsFlag);
    }

    [Theory]
    [InlineData("for n in a b; do cat \"$n\" <<< \"$n\"; done")]
    [InlineData("for n in a b; do cat \"$n\" <<EOF\n$n\nEOF\ndone")]
    [InlineData("for n in a b; do cat \"$n\" > \"out/$n\"; done")]
    public void A_redirect_that_depends_on_a_value_gives_no_twins(string source)
    {
        Assert.False(Parser.TryProjectLiteralTwins(source, out _));
    }

    [Theory]
    [InlineData("for n in a b; do cat \"$n\" 2>&1 >/dev/null <<< x; done")]
    [InlineData("for n in a b; do cat \"$n\" <<'EOF'\nx\nEOF\ndone")]
    [InlineData("for n in a b; do cat \"$n\" >> /work/log 2>&-; done")]
    public void A_fixed_redirect_keeps_the_twins(string source)
    {
        Assert.True(Parser.TryProjectLiteralTwins(source, out var projection));
        Assert.Equal(2, Assert.Single(projection!.Commands).Twins.Count);
    }

    [Fact]
    public void A_word_with_two_arguments_gives_no_twins()
    {
        Assert.False(Parser.TryProjectLiteralTwins(
            "for n in a b; do cmd --opt=$n; done", out _));
    }

    [Fact]
    public void A_word_without_a_source_span_gives_no_twins()
    {
        Assert.False(Parser.TryProjectLiteralTwins(
            "bash -c 'for n in a b; do cat \"$n\"; done'", out _));
    }

    [Fact]
    public void An_incomplete_command_gives_no_twins()
    {
        // The source occurrence is incomplete: "$p" can be an option of
        // bash. Each literal twin (`bash x ls`) is complete, so only the gate
        // on the source occurrence gives no twins.
        Assert.False(Parser.TryProjectLiteralTwins(
            "for p in x y; do bash \"$p\" ls; done", out _));
    }

    [Fact]
    public void A_value_that_is_not_one_literal_word_in_the_twin_gives_no_twins()
    {
        // The parser proves the values "" and "a". The twin `cat ''` has no
        // exact literal value for its operand, so the twin does not match.
        Assert.False(Parser.TryProjectLiteralTwins(
            "for f in '' a; do cat \"$f\"; done", out _));
    }

    [Fact]
    public void A_literal_command_gives_no_twins()
    {
        Assert.False(Parser.TryProjectLiteralTwins("gh api repos/x/1 >/dev/null", out _));
    }

    // The facts a consumer reads for a command: its words, directory, and the
    // value and path facts of each argument. The structural role can differ,
    // because the twin stays inside its loop.
    private static void AssertSameFacts(CommandOccurrence expected, CommandOccurrence actual)
    {
        Assert.Equal(expected.IsComplete, actual.IsComplete);
        Assert.Equal(expected.Clause.Verb.Tokens, actual.Clause.Verb.Tokens);
        Assert.Equal(Words(expected), Words(actual));
        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(expected.Arguments.Count, actual.Arguments.Count);
        for (var index = 0; index < expected.Arguments.Count; index++)
        {
            var left = expected.Arguments[index];
            var right = actual.Arguments[index];
            Assert.Equal(left.Argument, right.Argument);
            Assert.Equal(left.Value, right.Value);
            Assert.Equal(left.AuthoredFileSystemValue, right.AuthoredFileSystemValue);
            Assert.Equal(left.AuthoredNonFileSystemValue, right.AuthoredNonFileSystemValue);
            Assert.Equal(left.AuthoredPathShape, right.AuthoredPathShape);
            Assert.Equal(left.MayPathnameExpand, right.MayPathnameExpand);
            Assert.Equal(left.MayFieldSplit, right.MayFieldSplit);
        }
    }

    private static string Words(CommandOccurrence occurrence) =>
        occurrence.CommandWords is ShellCommandWords.Known known
            ? string.Join(" ", known.Words)
            : "<unknown>";
}
