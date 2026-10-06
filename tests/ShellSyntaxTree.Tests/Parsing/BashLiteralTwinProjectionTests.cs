// -----------------------------------------------------------------------
// <copyright file="BashLiteralTwinProjectionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
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

    // The launch facts prove HOME, so a tilde word has a proved value.
    private static readonly BashParser LaunchParser = new(new BashParserOptions
    {
        WorkingDirectory = WorkingDirectory,
        HomeDirectory = "/home/agent",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
        LaunchEnvironment = new ShellLaunchEnvironment(
            new[] { new KeyValuePair<string, string>("HOME", "/home/agent") },
            Array.Empty<string>()),
    });

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
            Assert.Equal($"gh api -X PATCH {path} -f milestone=157 >/dev/null", twin.Source);
            Assert.Equal(WorkingDirectory, twin.WorkingDirectory);
            Assert.Equal(path, Assert.Single(twin.Words).Value);
            AssertSameFacts(Assert.Single(Parser.Parse(twin.Source).Commands), twin.Occurrence);
            Assert.Equal(new[] { "gh", "api" },
                Assert.IsType<ShellCommandWords.Known>(twin.Occurrence.CommandWords).Words);
            var argument = twin.Occurrence.Arguments.Single(candidate => candidate.Argument.Raw == path);
            Assert.Equal(ArgKind.Literal, argument.Argument.Kind);
            Assert.Equal("/work/" + path, argument.Argument.Resolved);
        }

        var echo = Assert.Single(projection.Commands, command => command.SourceOccurrenceIndex == 1);
        Assert.Equal(issues.Select(issue => "moved " + issue),
            echo.Twins.Select(twin => Assert.Single(twin.Words).Value));
        Assert.Equal("echo 'moved 8250'", echo.Twins[0].Source);
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

    [Theory]
    [InlineData("for d in a b; do ~/bin/tool \"$d\"; done")]
    [InlineData("for d in a b; do ~/bin/tool sub \"$d\"; done")]
    public void A_program_word_that_the_shell_can_change_gives_no_twins(string source)
    {
        // The launch facts prove HOME, so only the program-word rule applies.
        Assert.False(LaunchParser.TryProjectLiteralTwins(source, out _));
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
    [InlineData("'@(a)'")]
    [InlineData("'+(a).txt'")]
    [InlineData("'!(a)'")]
    [InlineData("'a)b'")]
    [InlineData("'a(b'")]
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
    // A heredoc body is outside the words of the command, so the twin text
    // cannot hold it.
    [InlineData("for n in a b; do cat \"$n\" <<'EOF'\nx\nEOF\ndone")]
    public void A_redirect_that_depends_on_a_value_gives_no_twins(string source)
    {
        Assert.False(Parser.TryProjectLiteralTwins(source, out _));
    }

    [Theory]
    [InlineData("for n in a b; do cat \"$n\" 2>&1 >/dev/null <<< x; done")]
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
    [Theory]
    [InlineData("for n in build; do rm -rf \"$n\\\ndir/\"; done")]
    [InlineData("for n in a; do printf '<%s>' \"$\\\n(id -u)\" \"$n\"; done")]
    [InlineData("for n in a; do cat \"$\\\nHOME/.ssh/id_rsa\" \"$n\"; done")]
    [InlineData("x=/etc/hostname; cat $\\\nx")]
    [InlineData("for n in a; do printf %s \"$\\\n{n}\"; done")]
    [InlineData("for n in a; do printf %s x$\\\n'y' \"$n\"; done")]
    [InlineData("for n in a \\\nb; do cat \"$n\"; done")]
    public void A_source_with_a_line_continuation_gives_no_twins(string source)
    {
        // The lexer does not remove a backslash-newline inside an expansion,
        // but Bash does. The guard keeps a wrong value out of every twin.
        Assert.False(LaunchParser.TryProjectLiteralTwins(source, out _));
    }

    [Theory]
    [InlineData("x=/etc\r\nfor n in a; do cat \"$x\" \"$n\"; done")]
    [InlineData("for n in a; do printf '%s' \"$n\"\rid; done")]
    [InlineData("for n in a b; do cat \"$n\"; done\r\n")]
    public void A_source_with_a_carriage_return_gives_no_twins(string source)
    {
        // Bash reads a carriage return as a word character.
        Assert.False(LaunchParser.TryProjectLiteralTwins(source, out _));
    }

    [Theory]
    [InlineData("for n in out.txt; do dd if=~/.ssh/id_rsa of=\"$n\"; done")]
    [InlineData("for n in a b; do env PATH=x:~/bin \"$n\"; done")]
    [InlineData("for n in a b; do cat \"$n\" > of=~/x; done")]
    public void A_word_with_a_tilde_after_an_equals_sign_or_colon_gives_no_twins(string source)
    {
        // Bash expands such a tilde, but the parser keeps it as text.
        Assert.False(LaunchParser.TryProjectLiteralTwins(source, out _));
    }

    [Theory]
    [InlineData("for n in a b; do cat ~/x \"$n\"; done")]
    [InlineData("for n in a b; do cat \"$HOME/x\" \"$n\"; done")]
    public void A_home_value_needs_a_live_launch_home(string source)
    {
        Assert.False(Parser.TryProjectLiteralTwins(source, out _));

        Assert.True(LaunchParser.TryProjectLiteralTwins(source, out var projection));
        Assert.All(Assert.Single(projection!.Commands).Twins, twin =>
            Assert.Contains(twin.Occurrence.Arguments, argument =>
                argument.Value is ShellValueDomain.Exact { Value: var value } &&
                value.StartsWith("/home/agent/", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_charged_command_keeps_its_charge_when_a_twin_fails()
    {
        var values = string.Join(" ", Enumerable.Range(0, 31).Select(index => $"v{index}"));
        // The last value '' fails the twin match of `cat`, after 31 good parses.
        var source = $"for n in {values} ''; do " +
                     string.Concat(Enumerable.Repeat("cat \"$n\"; ", 6)) + "done";

        Assert.False(Project(source, out var projection, out var cost));

        Assert.Null(projection);
        Assert.Equal(BashLiteralTwinAnalyzer.MaximumTwinParses, cost.Parses);
    }

    [Fact]
    public void The_twin_parses_stop_at_the_parse_budget()
    {
        var values = string.Join(" ", Enumerable.Range(1, 32));
        var source = $"for n in {values}; do a \"x/$n\"; b \"x/$n\"; c \"x/$n\"; d \"x/$n\"; e \"x/$n\"; done";

        Assert.True(Project(source, out var projection, out var cost));

        Assert.Equal(BashLiteralTwinAnalyzer.MaximumTwinParses, cost.Parses);
        Assert.Equal(new[] { 0, 1, 2, 3 },
            projection!.Commands.Select(command => command.SourceOccurrenceIndex));
    }

    [Theory]
    [InlineData(2_000)]
    public void A_source_with_many_commands_builds_a_fixed_amount_of_twin_text(int commands)
    {
        var values = string.Join(" ", Enumerable.Range(0, 32).Select(index => $"v{index}"));
        var source = $"for n in {values}; do " +
                     string.Concat(Enumerable.Repeat("cat \"$n\"; ", commands)) + "done";

        Assert.True(Project(source, out var projection, out var cost));

        // Only the first four commands fit in the parse budget. Each builds 32
        // twin texts: `cat v0` to `cat v9` (6 characters) and `cat v10` to
        // `cat v31` (7 characters). No other command builds a twin text.
        Assert.Equal(4, projection!.Commands.Count);
        Assert.Equal(BashLiteralTwinAnalyzer.MaximumTwinParses, cost.Parses);
        Assert.Equal(4 * (10 * 6 + 22 * 7), cost.Characters);
    }

    [Fact]
    public void A_long_command_gets_no_twin_text()
    {
        var values = string.Join(" ", Enumerable.Range(0, 32).Select(index => $"v{index}"));
        var word = new string('a', 4096);

        // Each twin text has more than 4 KB, so 32 of them pass the character
        // budget of eight characters for each source character.
        Assert.False(Project($"for n in {values}; do cat \"$n\" {word}; done", out _, out var cost));
        Assert.Equal(0, cost.Parses);
        Assert.Equal(0, cost.Characters);

        Assert.True(Project($"for n in {values}; do cat \"$n\" {word.Substring(0, 64)}; done",
            out _, out cost));
        Assert.Equal(32, cost.Parses);
    }

    [Fact]
    public void A_nested_loop_builds_twins_of_single_commands()
    {
        var values = string.Join(" ", Enumerable.Range(0, 32));
        var source = $"for a in {values}; do x=$a; for b in {values}; do y=$x$b; z=$y$a; " +
                     "cat \"$a\"; cat \"$a\"; cat \"$a\"; cat \"$a\"; done; done";

        Assert.True(Project(source, out var projection, out var cost));

        // Each twin text is one command, `cat 0` to `cat 31`, not the loop.
        Assert.All(projection!.Commands.SelectMany(command => command.Twins),
            twin => Assert.StartsWith("cat ", twin.Source));
        Assert.Equal(BashLiteralTwinAnalyzer.MaximumTwinParses, cost.Parses);
        Assert.Equal(4 * (10 * 5 + 22 * 6), cost.Characters);
    }

    [Theory]
    [InlineData("for f in if=~/.ssh/id_rsa; do dd \"$f\" of=out.txt; done")]
    [InlineData("for n in x=~/y; do cat \"$n\"; done")]
    [InlineData("for n in x=~; do cat \"$n\"; done")]
    [InlineData("for n in a=b:~/y; do cat \"$n\"; done")]
    [InlineData("for n in a b=~/y c; do cat \"$n\"; done")]
    [InlineData("for n in x=~+; do cat \"$n\"; done")]
    [InlineData("for n in a; do for m in x=~/y; do cat \"$n\" \"$m\"; done; done")]
    [InlineData("for n in x=~/y; do cat $n; done")]
    public void A_loop_list_item_with_an_assignment_tilde_gives_no_twins(string source)
    {
        // Bash expands the tilde in the list item. The launch facts prove
        // HOME, so only the full-source tilde guard applies.
        Assert.False(LaunchParser.TryProjectLiteralTwins(source, out _));
    }

    [Fact]
    public void A_command_without_an_exact_directory_gives_no_twins()
    {
        // The launch facts prove HOME, so only the exact-directory gate applies.
        Assert.False(LaunchParser.TryProjectLiteralTwins(
            "cd \"$TARGET\"; for n in a b; do cat \"$n\"; done", out _));
    }

    [Fact]
    public void A_command_with_an_assignment_prefix_gives_no_twins()
    {
        Assert.False(Parser.TryProjectLiteralTwins(
            "for n in a b; do FOO=1 cat \"$n\"; done", out _));
    }

    [Theory]
    [InlineData("for n in a x; do tool '$n' -o\"$n\"; done")]
    [InlineData("for n in a x; do git -C\"$n\" status; done")]
    [InlineData("for n in a x; do tool -xo\"$n\"; done")]
    [InlineData("for n in a x; do tool -o\"${n}\"; done")]
    [InlineData("for n in a x; do tool -o\"$n\"x; done")]
    [InlineData("for f in a.txt b.txt; do cat \"$f\"; done")]
    public void The_unknown_initial_state_gives_no_twins(string source)
    {
        // With an unknown initial state, the parser can report `-o$n` as an
        // exact value, but Bash passes `-oa`.
        var unknown = new BashParser(new BashParserOptions
        {
            WorkingDirectory = WorkingDirectory,
            HomeDirectory = "/home/agent",
            PublishAuthoredSourceFacts = true,
        });

        Assert.False(unknown.TryProjectLiteralTwins(source, out var projection));
        Assert.Null(projection);
    }

    [Theory]
    [InlineData("cd && for n in a; do cmd \"$n\"; done")]
    [InlineData("cd -L && for n in a; do cmd \"$n\"; done")]
    [InlineData("cd -- && for n in a; do cmd \"$n\"; done")]
    [InlineData("builtin cd && for n in a; do cmd \"$n\"; done")]
    [InlineData("command cd && for n in a; do cmd \"$n\"; done")]
    [InlineData("cd >/dev/null && for n in a; do cmd \"$n\"; done")]
    [InlineData("if cd; then for n in a; do cmd \"$n\"; done; fi")]
    [InlineData("for d in x; do cd && cmd \"$d\"; done")]
    public void A_directory_from_an_unproved_home_gives_no_twins(string source)
    {
        // Without launch facts, `cd` goes to the HomeDirectory assumption in
        // the parse, but Bash goes to its real HOME.
        Assert.False(Parser.TryProjectLiteralTwins(source, out _));
        var isolated = CreateParser(BashInitialStateMode.IsolatedNonInteractive);
        Assert.False(isolated.TryProjectLiteralTwins(source, out _));

        // A live launch HOME proves the directory.
        Assert.True(LaunchParser.TryProjectLiteralTwins(source, out var projection));
        Assert.StartsWith("/home/agent",
            Assert.Single(Assert.Single(projection!.Commands).Twins).WorkingDirectory);
    }

    [Fact]
    public void A_directory_below_the_home_directory_keeps_its_twins_when_it_does_not_come_from_home()
    {
        // An absolute cd names the directory. The home assumption plays no
        // part, so the occurrence keeps its twins without launch facts.
        Assert.True(Parser.TryProjectLiteralTwins(
            "cd /home/agent/repo && for n in a b; do cmd \"$n\"; done", out var projection));
        Assert.All(Assert.Single(projection!.Commands).Twins,
            twin => Assert.Equal("/home/agent/repo", twin.WorkingDirectory));
    }

    private static bool Project(
        string source,
        out BashLiteralTwinProjection? projection,
        out BashLiteralTwinCost cost)
    {
        var options = new BashParserOptions
        {
            WorkingDirectory = WorkingDirectory,
            HomeDirectory = "/home/agent",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            PublishAuthoredSourceFacts = true,
        };
        return BashLiteralTwinAnalyzer.TryProject(
            new BashParser(options).Parse(source), options, out projection, out cost);
    }

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
