// -----------------------------------------------------------------------
// <copyright file="BashAssignmentShapeTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the wider Bash assignment shapes (#209): upper-case names, more than
/// one assignment, assignments in lists, expanded values, and prefixes in
/// pipelines and lists. Each command stays a normal occurrence. A value that
/// the parser cannot prove is <c>Unknown</c>.
/// </summary>
public class BashAssignmentShapeTests
{
    private const string Home = "/home/agent";

    private static readonly BashParser Parser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
    });

    // A for-in loop in fresh mode publishes authored values only.
    private static readonly BashParser LoopParser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
    });

    private static readonly BashParser LaunchParser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        HomeDirectory = Home,
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        LaunchEnvironment = new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["HOME"] = Home },
            new[] { "CDPATH" }),
    });

    // ------------------------------------------------------------ names

    [Theory]
    [InlineData("JOBID=105906864793; gh api \"repos/x/jobs/$JOBID/logs\"", "JOBID", "105906864793")]
    [InlineData("CH=main; git log \"$CH\"", "CH", "main")]
    [InlineData("LAST_TAG=1.5.71; git log \"$LAST_TAG\"", "LAST_TAG", "1.5.71")]
    public void Upper_case_shell_state_name_is_a_bounded_assignment(
        string source,
        string name,
        string value)
    {
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = Assert.Single(parsed.Commands);
        var assignment = Assert.Single(command.Assignments);
        Assert.Equal(name, assignment.Name);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, assignment.Scope);
        Assert.Equal(value, Exact(assignment.EffectiveValue));
        Assert.Contains(value, Exact(command.Arguments.Last().Value));
    }

    [Theory]
    [InlineData("PATH=/other; inspect item")]
    [InlineData("IFS=x; inspect item")]
    [InlineData("HOME=/other; inspect item")]
    [InlineData("BASH_ENV=/other; inspect item")]
    [InlineData("LD_PRELOAD=/x.so; inspect item")]
    [InlineData("GLOBIGNORE=x; inspect item")]
    [InlineData("SHELLOPTS=x; inspect item")]
    [InlineData("PS4=x; inspect item")]
    public void Shell_owned_shell_state_name_fails_closed(string source)
    {
        Assert.True(Parser.Parse(source).IsUnparseable);
    }

    // ------------------------------------------------------------ statements

    [Fact]
    public void Each_live_assignment_is_visible_on_a_later_command()
    {
        var parsed = Parser.Parse("SRC=/a; DST=/b; cp \"$SRC/f\" \"$DST/f\"");

        var command = Assert.Single(parsed.Commands);
        Assert.Equal(new[] { "SRC", "DST" }, command.Assignments.Select(a => a.Name));
        Assert.Equal(new[] { "/a/f", "/b/f" }, command.Arguments.Select(a => Exact(a.Value)));
    }

    [Fact]
    public void Reassignment_replaces_the_earlier_value()
    {
        var parsed = Parser.Parse("root='/one'; root='/two'; inspect \"$root/file\"");

        var command = Assert.Single(parsed.Commands);
        Assert.Equal("/two", Exact(Assert.Single(command.Assignments).EffectiveValue));
        Assert.Equal("/two/file", Exact(command.Arguments.Single().Value));
    }

    [Fact]
    public void Assignment_only_source_has_no_command()
    {
        var parsed = Parser.Parse("root='/work'");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Empty(parsed.Commands);
    }

    [Theory]
    [InlineData("root='/work' && inspect \"$root/file\"")]
    [InlineData("root='/work'; inspect \"$root/file\" | inspect two")]
    [InlineData("root='/work'; printf '%s' \"$root/file\"")]
    [InlineData("root='/work'; inspect $(discover) \"$root/file\"")]
    public void Assignment_reaches_a_command_in_a_list_or_pipeline(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var reader = parsed.Commands.Single(c => c.Arguments.Any(a => a.Argument.Raw.Contains("$root")));
        Assert.Equal("/work", Exact(Assert.Single(reader.Assignments).EffectiveValue));
        Assert.Equal("/work/file", Exact(reader.Arguments.Last().Value));
    }

    [Fact]
    public void Assignment_on_one_branch_is_not_proved_after_the_branch()
    {
        var parsed = Parser.Parse("probe || root='/work'; inspect \"$root/file\"");

        var inspect = parsed.Commands.Last();
        Assert.Empty(inspect.Assignments);
        Assert.IsType<ShellValueDomain.Unknown>(inspect.Arguments.Single().Value);
    }

    // ------------------------------------------------------------ substitution values

    [Fact]
    public void Substitution_value_gives_an_occurrence_and_an_unknown_binding()
    {
        var parsed = Parser.Parse("root=$(discover --root); inspect \"$root/file\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(2, parsed.Commands.Count);
        var discover = parsed.Commands[0];
        Assert.Equal(CommandOccurrenceRole.Substitution, discover.ImmediateRole);
        Assert.Equal("discover", Words(discover));
        Assert.True(discover.IsComplete);
        Assert.Equal("/work", Exact(discover.WorkingDirectory));
        var inspect = parsed.Commands[1];
        var assignment = Assert.Single(inspect.Assignments);
        Assert.IsType<ShellValueDomain.Unknown>(assignment.AuthoredValue);
        Assert.IsType<ShellValueDomain.Unknown>(assignment.EffectiveValue);
        Assert.IsType<ShellValueDomain.Unknown>(inspect.Arguments.Single().Value);
        Assert.Equal("inspect", Words(inspect));
    }

    [Fact]
    public void Substitution_assignment_can_fail_and_reach_an_or_branch()
    {
        var parsed = Parser.Parse("root=$(discover) || inspect fallback");

        var inspect = parsed.Commands.Last();
        Assert.Equal("inspect fallback", Words(inspect));
        Assert.Equal("/work", Exact(inspect.WorkingDirectory));
    }

    [Fact]
    public void Prefix_in_a_decoded_child_fails_closed()
    {
        Assert.True(Parser.Parse("bash -c 'A=1 inspect item'").IsUnparseable);
    }

    [Fact]
    public void Substitution_in_an_and_list_keeps_every_command()
    {
        var parsed = Parser.Parse(
            "git fetch && UP=$(git rev-parse upstream/dev) && git log \"$UP\"");

        Assert.Equal(
            new[] { "git fetch", "git rev-parse", "git log" },
            parsed.Commands.Select(Words));
    }

    [Fact]
    public void Substitution_reads_the_value_before_the_assignment()
    {
        var parsed = Parser.Parse("root=/a; root=$(ls \"$root\"); cat \"$root\"");

        var ls = parsed.Commands.Single(c => c.ImmediateRole == CommandOccurrenceRole.Substitution);
        Assert.Equal("/a", Exact(ls.Arguments.Single().Value));
        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands.Last().Arguments.Single().Value);
    }

    // ------------------------------------------------------------ expanded values

    [Fact]
    public void Tilde_value_expands_from_the_live_launch_home()
    {
        var parsed = LaunchParser.Parse("WT=~/r/wt; git -C \"$WT\" status");

        var command = Assert.Single(parsed.Commands);
        var assignment = Assert.Single(command.Assignments);
        Assert.IsType<ShellValueDomain.Unknown>(assignment.AuthoredValue);
        Assert.Equal(Home + "/r/wt", Exact(assignment.EffectiveValue));
    }

    [Fact]
    public void Tilde_value_without_a_launch_home_is_unknown()
    {
        var parsed = Parser.Parse("WT=~/r/wt; inspect item");

        Assert.IsType<ShellValueDomain.Unknown>(
            Assert.Single(parsed.Commands.Single().Assignments).EffectiveValue);
    }

    [Fact]
    public void Value_with_a_launch_variable_gets_the_launch_value()
    {
        var parsed = LaunchParser.Parse("R=\"$HOME/repositories\"; ls \"$R\"");

        Assert.Equal(
            Home + "/repositories",
            Exact(Assert.Single(parsed.Commands.Single().Assignments).EffectiveValue));
    }

    [Fact]
    public void Value_with_a_bound_variable_gets_the_bound_value()
    {
        var parsed = Parser.Parse("A=/x; B=\"$A/y\"; inspect \"$B\"");

        var command = Assert.Single(parsed.Commands);
        Assert.Equal("/x/y", Exact(command.Assignments.Single(a => a.Name == "B").EffectiveValue));
        Assert.Equal("/x/y", Exact(command.Arguments.Single().Value));
    }

    [Theory]
    [InlineData("root=$other; inspect item")]
    [InlineData("root=\"$other\"; inspect item")]
    [InlineData("root=foo:~; inspect item")]
    [InlineData("root=~root; inspect item")]
    [InlineData("root=`id`; inspect item")]
    [InlineData("root=$((1+2)); inspect item")]
    [InlineData("root=${other:-x}; inspect item")]
    [InlineData("root=a\\ b; inspect item")]
    [InlineData("root=*; inspect item")]
    [InlineData("root={a,b}; inspect item")]
    [InlineData("root=$'a'; inspect item")]
    [InlineData("root=$(cat \"$root\"); inspect item")]
    [InlineData("root=\"a\\b\"; inspect item")]
    [InlineData("x=1; inspect \"$root\"")]
    public void Unproved_value_fails_closed(string source)
    {
        Assert.True(Parser.Parse(source).IsUnparseable);
    }

    // ------------------------------------------------------------ prefixes

    [Theory]
    [InlineData("FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f x | tail", "git filter-branch")]
    [InlineData("cd /work && MODE=fast make", "make")]
    [InlineData("probe || MODE=fast make", "make")]
    [InlineData("GIT_EDITOR=true git rebase --continue 2>&1 | tail -15", "git rebase")]
    public void Prefix_in_a_pipeline_or_list_is_visible_on_its_command(string source, string words)
    {
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = parsed.Commands.Single(c => c.Assignments.Count > 0);
        Assert.Equal(words, Words(command));
        var assignment = Assert.Single(command.Assignments);
        Assert.Equal(ShellVariableAssignmentScope.CommandEnvironment, assignment.Scope);
        Assert.All(
            parsed.Commands.Where(c => !ReferenceEquals(c, command)),
            other => Assert.Empty(other.Assignments));
    }

    [Fact]
    public void Prefix_with_a_bound_variable_gets_the_bound_value()
    {
        var parsed = Parser.Parse("ROOT=/x; REPO_ROOT=\"$ROOT\" bash /x/audit.sh");

        var command = Assert.Single(parsed.Commands);
        var prefix = command.Assignments.Single(a => a.Name == "REPO_ROOT");
        Assert.IsType<ShellValueDomain.Unknown>(prefix.AuthoredValue);
        Assert.Equal("/x", Exact(prefix.EffectiveValue));
    }

    [Fact]
    public void Prefix_with_double_quotes_is_exact()
    {
        var command = Assert.Single(Parser.Parse("FOO=\"hello world\" perl -e x").Commands);

        Assert.Equal("hello world", Exact(Assert.Single(command.Assignments).EffectiveValue));
    }

    [Fact]
    public void Prefix_substitution_runs_as_an_occurrence()
    {
        var parsed = Parser.Parse("X=$(id -u) Y=2 ls");

        Assert.Equal(new[] { "id", "ls" }, parsed.Commands.Select(Words));
        var ls = parsed.Commands[1];
        Assert.IsType<ShellValueDomain.Unknown>(ls.Assignments.Single(a => a.Name == "X").EffectiveValue);
        Assert.Equal("2", Exact(ls.Assignments.Single(a => a.Name == "Y").EffectiveValue));
    }

    [Theory]
    [InlineData("PATH=/other inspect item | tail")]
    [InlineData("cd /work && LD_PRELOAD=/x.so inspect item")]
    [InlineData("MODE=fast printf '%s' item | tail")]
    [InlineData("cd /work && MODE=fast bash -c 'inspect item'")]
    public void Unsafe_prefix_in_a_list_still_fails_closed(string source)
    {
        Assert.True(Parser.Parse(source).IsUnparseable);
    }

    // ------------------------------------------------------------ state boundaries

    [Theory]
    [InlineData("x=1; wait -p y; cat \"$x\"")]
    [InlineData("x=1; read -a x; cat \"$x\"")]
    [InlineData("x=1; export x; cat \"$x\"")]
    [InlineData("x=1; unset x; cat \"$x\"")]
    [InlineData("x=1; for x in a b; do cat \"$x\"; done")]
    [InlineData("for i in a b; do i=2; done")]
    [InlineData("for i in a b; do (x=1); done")]
    [InlineData("(x=1; cat \"$x\")")]
    [InlineData("echo \"$(x=1; cat \"$x\")\"")]
    [InlineData("x=1 | cat")]
    [InlineData("inspect item | x=1")]
    [InlineData("x='a'; bash -c 'cat $x'")]
    public void Unmodeled_state_change_or_scope_fails_closed(string source)
    {
        Assert.True(LoopParser.Parse(source).IsUnparseable);
    }

    [Fact]
    public void Assignment_needs_fresh_mode()
    {
        var parser = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        });

        Assert.True(parser.Parse("JOBID=1; gh run view \"$JOBID\"").IsUnparseable);
    }

    // ------------------------------------------------------------ loop bodies

    [Fact]
    public void Loop_body_assignment_joins_the_values_of_each_iteration()
    {
        var parsed = LoopParser.Parse("x=1; for i in a b; do cat \"$x\"; x=2; done; cat \"$x\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var inBody = parsed.Commands[0];
        Assert.Equal(CommandOccurrenceRole.LoopBody, inBody.ImmediateRole);
        Assert.Equal(
            new[] { "1", "2" },
            Assert.IsType<ShellValueDomain.FiniteSet>(inBody.Arguments.Single().AuthoredValue).Values);
        Assert.Empty(inBody.Assignments);
        var after = parsed.Commands[1];
        Assert.Equal("2", Exact(after.Arguments.Single().AuthoredValue));
        Assert.Equal("2", Exact(Assert.Single(after.Assignments).EffectiveValue));
    }

    [Fact]
    public void Loop_that_may_not_run_does_not_prove_its_assignment()
    {
        var parsed = LoopParser.Parse("for i in $(ls); do x=1; done; cat \"$x\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cat = parsed.Commands.Last();
        Assert.Empty(cat.Assignments);
        Assert.IsType<ShellValueDomain.Unknown>(cat.Arguments.Single().AuthoredValue);
    }

    [Fact]
    public void Loop_body_substitution_assignment_keeps_each_command()
    {
        var parsed = LoopParser.Parse(
            "for d in /a/ /b/; do s=$(du -sh \"$d\" | cut -f1); echo \"$s $d\"; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(3, parsed.Commands.Count);
        Assert.Equal("cut", Words(parsed.Commands[1]));
        var echo = parsed.Commands[2];
        Assert.Equal(CommandOccurrenceRole.LoopBody, echo.ImmediateRole);
        Assert.IsType<ShellValueDomain.Unknown>(Assert.Single(echo.Assignments).EffectiveValue);
    }

    [Theory]
    [InlineData("for i in a; do A=1 inspect item; done")]
    [InlineData("(A=1 inspect item)")]
    [InlineData("echo $(A=1 inspect item)")]
    public void Prefix_in_a_nested_scope_is_visible_on_its_command(string source)
    {
        var parsed = LoopParser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var inspect = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "inspect");
        Assert.Equal("1", Exact(Assert.Single(inspect.Assignments).EffectiveValue));
    }

    [Fact]
    public void Launch_variable_assignment_inside_a_loop_fails_closed()
    {
        var parser = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            PublishAuthoredSourceFacts = true,
            LaunchEnvironment = new ShellLaunchEnvironment(
                new Dictionary<string, string> { ["SUB"] = "push" },
                System.Array.Empty<string>()),
        });

        // The loop itself is supported: the same source without the
        // assignment parses.
        Assert.False(parser.Parse("for i in a b; do git \"$SUB\"; done").IsUnparseable);
        Assert.True(parser.Parse("for i in a b; do git \"$SUB\"; SUB=pull; done").IsUnparseable);
    }

    // ------------------------------------------------------------ helpers

    private static string Exact(ShellValueDomain value) =>
        Assert.IsType<ShellValueDomain.Exact>(value).Value;

    private static string Words(CommandOccurrence occurrence) =>
        string.Join(" ", Assert.IsType<ShellCommandWords.Known>(occurrence.CommandWords).Words);
}
