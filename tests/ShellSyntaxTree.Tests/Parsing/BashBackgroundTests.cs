// -----------------------------------------------------------------------
// <copyright file="BashBackgroundTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins Bash background lists (#215). A single <c>&amp;</c> ends an and-or
/// list that runs in an asynchronous subshell. Every command stays a normal
/// occurrence, and the list's state changes do not reach the next command.
/// </summary>
public class BashBackgroundTests
{
    private static readonly BashParser Parser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
    });

    [Fact]
    public void Trailing_ampersand_wraps_the_command_in_a_background_group()
    {
        var parsed = Parser.Parse("sleep 5 &");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var group = Assert.IsType<GroupSyntax>(Assert.Single(parsed.Syntax.Statements));
        Assert.Equal(ShellGroupKind.Background, group.GroupKind);
        Assert.Equal(0, group.SourceStart);
        Assert.Equal(9, group.SourceLength);
        var sleep = Assert.Single(parsed.Commands);
        Assert.Equal(CommandOccurrenceRole.Ordinary, sleep.ImmediateRole);
        Assert.Contains(sleep.Ancestry, frame =>
            ReferenceEquals(frame.Ancestor, group) && frame.Region == CommandAncestryRegion.GroupBody);
    }

    [Fact]
    public void Background_server_and_later_commands_are_all_visible()
    {
        var parsed = Parser.Parse(
            "nohup python3 -m http.server 8899 > /tmp/s.log 2>&1 & sleep 1; ss -ltn");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(new[] { "nohup", "sleep", "ss" }, parsed.Commands.Select(c => c.Clause.Verb.Tokens[0]));
        var server = parsed.Commands[0];
        Assert.Contains(server.Ancestry, frame =>
            frame.Ancestor is GroupSyntax { GroupKind: ShellGroupKind.Background });
        var output = Assert.IsType<FileRedirectAnalysis>(server.Redirects[0]);
        Assert.Equal("/tmp/s.log", Assert.IsType<ShellValueDomain.Exact>(output.Target).Value);
        Assert.All(parsed.Commands.Skip(1), command => Assert.DoesNotContain(command.Ancestry, frame =>
            frame.Ancestor is GroupSyntax { GroupKind: ShellGroupKind.Background }));
    }

    [Fact]
    public void Ampersand_applies_to_the_whole_and_or_list()
    {
        var parsed = Parser.Parse("a && b & c");

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(parsed.Syntax.Statements));
        Assert.Equal(2, list.Items.Count);
        var group = Assert.IsType<GroupSyntax>(list.Items[0].Command);
        Assert.Equal(ShellGroupKind.Background, group.GroupKind);
        var inner = Assert.IsType<CommandListSyntax>(Assert.Single(group.Body.Statements));
        Assert.Equal(
            new[] { CompoundOperator.None, CompoundOperator.AndIf },
            inner.Items.Select(item => item.Operator));
        Assert.Equal(CompoundOperator.Sequence, list.Items[1].Operator);
        Assert.Equal(CompoundOperator.Sequence, parsed.Clauses[2].Operator);
    }

    [Fact]
    public void Ampersand_does_not_reach_back_past_a_sequence()
    {
        var parsed = Parser.Parse("a; b && c & d");

        var a = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "a");
        Assert.DoesNotContain(a.Ancestry, frame =>
            frame.Ancestor is GroupSyntax { GroupKind: ShellGroupKind.Background });
        Assert.All(
            parsed.Commands.Where(c => c.Clause.Verb.Tokens[0] is "b" or "c"),
            command => Assert.Contains(command.Ancestry, frame =>
                frame.Ancestor is GroupSyntax { GroupKind: ShellGroupKind.Background }));
    }

    [Fact]
    public void Ampersand_applies_to_the_whole_pipeline()
    {
        var parsed = Parser.Parse("a | b &");

        var group = Assert.IsType<GroupSyntax>(Assert.Single(parsed.Syntax.Statements));
        Assert.IsType<PipelineSyntax>(Assert.Single(group.Body.Statements));
        Assert.All(parsed.Commands, c => Assert.Equal(CommandOccurrenceRole.PipelineStage, c.ImmediateRole));
    }

    [Fact]
    public void Directory_change_in_a_background_list_does_not_reach_the_next_command()
    {
        var parsed = Parser.Parse("cd /tmp && make & ls; cd /a & cat f");

        var make = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "make");
        Assert.Equal("/tmp", Assert.IsType<ShellValueDomain.Exact>(make.WorkingDirectory).Value);
        var ls = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "ls");
        Assert.Equal("/work", Assert.IsType<ShellValueDomain.Exact>(ls.WorkingDirectory).Value);
        Assert.DoesNotContain(ls.Clause.Args, arg => arg.IsCwdAttribution);
        var cat = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "cat");
        Assert.Equal("/work", Assert.IsType<ShellValueDomain.Exact>(cat.WorkingDirectory).Value);
        Assert.Equal("/work/f", cat.Clause.Args.Single(a => !a.IsCwdAttribution).Resolved);
    }

    [Fact]
    public void Assignment_in_a_background_list_does_not_reach_the_next_command()
    {
        var parsed = Parser.Parse("x=1 & cat \"$x\"");

        var cat = Assert.Single(parsed.Commands);
        Assert.Empty(cat.Assignments);
        Assert.IsType<ShellValueDomain.Unknown>(cat.Arguments.Single().Value);
    }

    [Theory]
    [InlineData("python3 -m http.server 8899 & PID=$!; sleep 1; kill \"$PID\"", "PID")]
    [InlineData("make; rc=$?; echo \"$rc\"", "rc")]
    public void Job_and_status_parameters_give_an_unknown_binding(string source, string name)
    {
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var last = parsed.Commands.Last();
        var assignment = Assert.Single(last.Assignments, a => a.Name == name);
        Assert.IsType<ShellValueDomain.Unknown>(assignment.EffectiveValue);
        Assert.IsType<ShellValueDomain.Unknown>(last.Arguments.Single().Value);
    }

    [Theory]
    [InlineData("for i in a b; do sleep 1 & done; wait")]
    [InlineData("(sleep 1 &)")]
    [InlineData("echo \"$(sleep 1 &)\"")]
    [InlineData("while true; do sleep 1 & done")]
    [InlineData("if true; then sleep 1 & fi")]
    public void Background_list_can_end_a_nested_list(string source)
    {
        // A for-in loop in fresh mode needs authored-fact publication.
        var parsed = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            PublishAuthoredSourceFacts = true,
        }).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var sleep = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "sleep");
        Assert.Contains(sleep.Ancestry, frame =>
            frame.Ancestor is GroupSyntax { GroupKind: ShellGroupKind.Background });
    }

    [Theory]
    [InlineData("x & ; y")]
    [InlineData("x & && y")]
    [InlineData("x & || y")]
    [InlineData("& x")]
    [InlineData("x | & y")]
    public void Ampersand_in_a_wrong_position_fails_closed(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Fact]
    public void Long_background_list_parses_without_recursion()
    {
        var source = string.Join(" & ", Enumerable.Repeat("sleep 1", 2000)) + " &";

        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(2000, parsed.Commands.Count);
    }

    [Theory]
    [InlineData("nohup python3 -m http.server 8899 > /tmp/s.log 2>&1 & sleep 1; ss -ltn")]
    [InlineData("a && b & c | d & e; f &")]
    [InlineData("python3 -m http.server & PID=$!; kill \"$PID\"")]
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
