// -----------------------------------------------------------------------
// <copyright file="BashControlFlowTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins Bash <c>while</c>, <c>until</c>, <c>if</c>, and <c>case</c> (#ISSUE),
/// and the bounded <c>read</c> builtin. Each inner command is a normal
/// occurrence with its own role, ancestry, and facts.
/// </summary>
public class BashControlFlowTests
{
    private static readonly BashParser Parser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
    });

    private static readonly BashParser UnknownModeParser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
    });

    // ------------------------------------------------------------ while and until

    [Fact]
    public void While_loop_exposes_condition_and_body_commands()
    {
        var parsed = Parser.Parse("while curl https://example.invalid/ready; do rm /tmp/marker; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var loop = Assert.IsType<ConditionLoopSyntax>(Assert.Single(parsed.Syntax.Statements));
        Assert.Equal(ConditionLoopKind.While, loop.LoopKind);
        Assert.Equal(new[] { "curl", "rm" }, parsed.Commands.Select(c => c.Clause.Verb.Tokens[0]));

        var curl = parsed.Commands[0];
        Assert.Equal(CommandOccurrenceRole.Condition, curl.ImmediateRole);
        Assert.Contains(curl.Ancestry, frame =>
            ReferenceEquals(frame.Ancestor, loop) && frame.Region == CommandAncestryRegion.Condition);
        var rm = parsed.Commands[1];
        Assert.Equal(CommandOccurrenceRole.LoopBody, rm.ImmediateRole);
        Assert.Contains(rm.Ancestry, frame =>
            ReferenceEquals(frame.Ancestor, loop) && frame.Region == CommandAncestryRegion.LoopBody);
        Assert.Equal("/tmp/marker", rm.Clause.Args.Single().Resolved);
        Assert.All(parsed.Commands, command => Assert.True(command.IsComplete));
    }

    [Fact]
    public void Until_loop_has_the_until_kind()
    {
        var parsed = Parser.Parse("until git pull; do sleep 1; done; echo done");

        var loop = Assert.IsType<ConditionLoopSyntax>(
            Assert.IsType<CommandListSyntax>(Assert.Single(parsed.Syntax.Statements)).Items[0].Command);
        Assert.Equal(ConditionLoopKind.Until, loop.LoopKind);
        Assert.Equal(
            new[] { CommandOccurrenceRole.Condition, CommandOccurrenceRole.LoopBody, CommandOccurrenceRole.Ordinary },
            parsed.Commands.Select(c => c.ImmediateRole));
    }

    [Fact]
    public void Directory_after_a_loop_body_cd_is_not_proved()
    {
        var parsed = Parser.Parse("while probe; do cd /tmp; done; cat file.txt");

        var cat = parsed.Commands.Last();
        Assert.IsType<ShellValueDomain.Unknown>(cat.WorkingDirectory);
    }

    [Fact]
    public void Loop_condition_sees_the_body_state_of_the_last_iteration()
    {
        var parsed = Parser.Parse("cd /a; while ls; do cd /b; done");

        var ls = parsed.Commands.Single(c => c.ImmediateRole == CommandOccurrenceRole.Condition);
        Assert.IsType<ShellValueDomain.Unknown>(ls.WorkingDirectory);
    }

    [Fact]
    public void Until_body_runs_after_the_condition_fails()
    {
        var parsed = Parser.Parse("until cd /a; do ls; done");

        var ls = parsed.Commands.Single(c => c.ImmediateRole == CommandOccurrenceRole.LoopBody);
        Assert.Equal("/work", Assert.IsType<ShellValueDomain.Exact>(ls.WorkingDirectory).Value);
    }

    [Fact]
    public void While_body_runs_after_the_condition_succeeds()
    {
        var parsed = Parser.Parse("while cd /a; do ls; done");

        var ls = parsed.Commands.Single(c => c.ImmediateRole == CommandOccurrenceRole.LoopBody);
        Assert.Equal("/a", Assert.IsType<ShellValueDomain.Exact>(ls.WorkingDirectory).Value);
    }

    [Fact]
    public void Launch_variable_assignment_in_a_while_body_fails_closed()
    {
        var parser = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            LaunchEnvironment = new ShellLaunchEnvironment(
                new Dictionary<string, string> { ["SUB"] = "push" },
                Array.Empty<string>()),
        });

        Assert.False(parser.Parse("while probe; do git \"$SUB\"; done").IsUnparseable);
        Assert.True(parser.Parse("while probe; do git \"$SUB\"; SUB=pull; done").IsUnparseable);
    }

    // ------------------------------------------------------------ if

    [Fact]
    public void If_statement_exposes_each_condition_and_branch()
    {
        var parsed = Parser.Parse(
            "if git diff --quiet; then echo clean; elif test -f x; then cat x; else echo other; fi");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var conditional = Assert.IsType<ConditionalSyntax>(Assert.Single(parsed.Syntax.Statements));
        Assert.Equal(2, conditional.Branches.Count);
        Assert.NotNull(conditional.Else);
        Assert.Equal(
            new[] { "git", "echo", "test", "cat", "echo" },
            parsed.Commands.Select(c => c.Clause.Verb.Tokens[0]));
        Assert.Equal(
            new[]
            {
                CommandOccurrenceRole.Condition,
                CommandOccurrenceRole.Branch,
                CommandOccurrenceRole.Condition,
                CommandOccurrenceRole.Branch,
                CommandOccurrenceRole.Branch,
            },
            parsed.Commands.Select(c => c.ImmediateRole));
        Assert.Equal(
            new int?[] { 0, 0, 1, 1, 2 },
            parsed.Commands.Select(c => c.Ancestry.Single(f => ReferenceEquals(f.Ancestor, conditional)).ChildIndex));
        Assert.Equal(
            new[]
            {
                CommandAncestryRegion.Condition,
                CommandAncestryRegion.Branch,
                CommandAncestryRegion.Condition,
                CommandAncestryRegion.Branch,
                CommandAncestryRegion.Branch,
            },
            parsed.Commands.Select(c => c.Ancestry.Single(f => ReferenceEquals(f.Ancestor, conditional)).Region));
    }

    [Fact]
    public void Divergent_branch_directories_join_to_unknown()
    {
        var parsed = Parser.Parse("if test -f marker; then cd /a; else cd /b; fi; cat file.txt");

        var cat = parsed.Commands.Last();
        Assert.Equal(CommandOccurrenceRole.Ordinary, cat.ImmediateRole);
        Assert.IsType<ShellValueDomain.Unknown>(cat.WorkingDirectory);
        Assert.Null(cat.Clause.Args.Single(a => !a.IsCwdAttribution).Resolved);

        // The compatibility attribution after the statement is dynamic.
        Assert.Equal(ArgKind.DynamicSkip, cat.Clause.Args.Single(a => a.IsCwdAttribution).Kind);
    }

    [Fact]
    public void If_without_else_can_skip_the_branch()
    {
        var parsed = Parser.Parse("if probe; then cd /a; fi && ls");

        var ls = parsed.Commands.Last();
        Assert.IsType<ShellValueDomain.Unknown>(ls.WorkingDirectory);
    }

    [Fact]
    public void Case_can_match_no_item()
    {
        var parsed = Parser.Parse("case x in a) cd /a;; esac && ls");

        var ls = parsed.Commands.Last();
        Assert.IsType<ShellValueDomain.Unknown>(ls.WorkingDirectory);
    }

    [Fact]
    public void Branch_body_sees_the_directory_before_the_statement()
    {
        var parsed = Parser.Parse("cd /a && if true; then cat f; fi");

        var cat = parsed.Commands.Last();
        Assert.Equal("/a", Assert.IsType<ShellValueDomain.Exact>(cat.WorkingDirectory).Value);
        Assert.Equal("/a/f", cat.Clause.Args.Single(a => !a.IsCwdAttribution).Resolved);
    }

    [Fact]
    public void Elif_condition_runs_only_after_the_earlier_condition_fails()
    {
        var parsed = Parser.Parse("if cd /a; then true; elif ls; then true; fi");

        var ls = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "ls");
        Assert.Equal("/work", Assert.IsType<ShellValueDomain.Exact>(ls.WorkingDirectory).Value);
    }

    [Fact]
    public void Test_bracket_is_a_static_program_word()
    {
        var parsed = Parser.Parse("if [ -d /x ]; then ls /x; fi");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var test = parsed.Commands[0];
        Assert.Equal("[", test.Clause.Verb.Tokens[0]);
        Assert.Equal(
            new[] { "[", "]" },
            Assert.IsType<ShellCommandWords.Known>(test.CommandWords).Words);
    }

    // ------------------------------------------------------------ case

    [Fact]
    public void Case_statement_exposes_each_item_body()
    {
        var parsed = Parser.Parse("case x in a) echo a;; (b|c) echo bc;; *) rm -f /tmp/x;; esac");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var caseSyntax = Assert.IsType<CaseSyntax>(Assert.Single(parsed.Syntax.Statements));
        Assert.Equal("x", caseSyntax.Subject.Raw);
        Assert.Equal(
            new[] { "a", "b|c", "*" },
            caseSyntax.Items.Select(item => string.Join("|", item.Patterns.Select(p => p.Raw))));
        Assert.Equal(
            new[] { "echo", "echo", "rm" },
            parsed.Commands.Select(c => c.Clause.Verb.Tokens[0]));
        Assert.All(parsed.Commands, c => Assert.Equal(CommandOccurrenceRole.Branch, c.ImmediateRole));
        Assert.Equal(
            new int?[] { 0, 1, 2 },
            parsed.Commands.Select(c => c.Ancestry.Single(f => ReferenceEquals(f.Ancestor, caseSyntax)).ChildIndex));
    }

    [Fact]
    public void Case_item_can_be_empty_and_the_last_item_can_omit_the_terminator()
    {
        var parsed = Parser.Parse("case x in a) ;; b) echo b\nesac");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal("echo", Assert.Single(parsed.Commands).Clause.Verb.Tokens[0]);
    }

    [Fact]
    public void Case_subject_reads_a_bound_variable()
    {
        var parsed = Parser.Parse("x=a; case \"$x\" in a) echo a;; esac");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
    }

    // ------------------------------------------------------------ read

    [Fact]
    public void While_read_loop_binds_the_name_to_an_unknown_value()
    {
        var parsed = Parser.Parse("grep -rln x src | while read -r f; do echo \"== $f\"; done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(new[] { "grep", "read", "echo" }, parsed.Commands.Select(c => c.Clause.Verb.Tokens[0]));
        var read = parsed.Commands[1];
        Assert.Equal(CommandOccurrenceRole.Condition, read.ImmediateRole);
        var echo = parsed.Commands[2];
        var assignment = Assert.Single(echo.Assignments);
        Assert.Equal("f", assignment.Name);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, assignment.Scope);
        Assert.IsType<ShellValueDomain.Unknown>(assignment.EffectiveValue);
        Assert.IsType<ShellValueDomain.Unknown>(echo.Arguments.Single().Value);
    }

    [Fact]
    public void Read_replaces_an_earlier_assignment()
    {
        var parsed = Parser.Parse("x=/a; read x; cat \"$x\"");

        var cat = parsed.Commands.Last();
        Assert.IsType<ShellValueDomain.Unknown>(Assert.Single(cat.Assignments).EffectiveValue);
        Assert.IsType<ShellValueDomain.Unknown>(cat.Arguments.Single().Value);
    }

    [Theory]
    [InlineData("read -a arr; cat \"$arr\"")]
    [InlineData("read -p prompt x; cat \"$x\"")]
    [InlineData("read -e x; cat \"$x\"")]
    [InlineData("read -i text x; cat \"$x\"")]
    [InlineData("read -d \"$delim\" x")]
    [InlineData("read PATH")]
    [InlineData("read IFS")]
    [InlineData("read \"$name\"")]
    [InlineData("IFS= read -r line")]
    [InlineData("command read x")]
    public void Unbounded_read_fails_closed(string source)
    {
        Assert.True(Parser.Parse(source).IsUnparseable);
    }

    [Fact]
    public void Read_needs_fresh_mode()
    {
        var parser = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        });

        Assert.True(parser.Parse("read x").IsUnparseable);
    }

    [Fact]
    public void Read_cannot_reassign_a_loop_binding()
    {
        var parser = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            PublishAuthoredSourceFacts = true,
        });

        Assert.False(parser.Parse("for f in a b; do echo \"$f\"; done").IsUnparseable);
        Assert.True(parser.Parse("for f in a b; do read f; done").IsUnparseable);
    }

    // ------------------------------------------------------------ modes and failures

    [Theory]
    [InlineData("while true; do echo hi; done")]
    [InlineData("if true; then echo hi; fi")]
    [InlineData("case x in a) echo a;; esac")]
    public void Control_flow_without_bindings_parses_in_unknown_mode(string source)
    {
        Assert.False(UnknownModeParser.Parse(source).IsUnparseable);
    }

    [Theory]
    [InlineData("while true; do echo hi")]
    [InlineData("while true; echo hi; done")]
    [InlineData("while; do echo hi; done")]
    [InlineData("while true; do done")]
    [InlineData("if true; then echo hi")]
    [InlineData("if true; echo hi; fi")]
    [InlineData("if true; then fi")]
    [InlineData("if true; then echo a; else fi")]
    [InlineData("case x in a) echo a;;")]
    [InlineData("case x a) echo a;; esac")]
    [InlineData("case x in y esac")]
    [InlineData("case $(id) in a) echo a;; esac")]
    [InlineData("case x in $(id)) echo a;; esac")]
    [InlineData("case a$(id) in a) echo a;; esac")]
    [InlineData("case \"$(id)\" in a) echo a;; esac")]
    [InlineData("case x in a$(id)) echo a;; esac")]
    [InlineData("case \"$unset_name\" in a) echo a;; esac")]
    [InlineData("while read -r l; do echo hi; done < file.txt")]
    [InlineData("if true; then echo hi; fi > out.txt")]
    [InlineData("then echo hi")]
    [InlineData("echo hi; fi")]
    [InlineData("esac")]
    [InlineData("select x in a b; do echo hi; done")]
    [InlineData("if [[ -f x ]]; then echo hi; fi")]
    public void Malformed_or_unsupported_control_flow_fails_closed(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Theory]
    [InlineData("while true; do done")]
    [InlineData("while do ls; done")]
    [InlineData("if true; then fi")]
    [InlineData("if true; then ls; else fi")]
    public void Empty_condition_or_body_is_rejected_by_the_grammar(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("cannot be empty", parsed.UnparseableReason);
    }

    [Theory]
    [InlineData("if true; then ", "ls", "; fi")]
    [InlineData("case x in a) ", "ls", ";; esac")]
    [InlineData("while true; do ", "ls", "; done")]
    [InlineData("until true; do ", "ls", "; done")]
    public void Deep_nesting_fails_closed_without_an_exception(
        string open,
        string inner,
        string close)
    {
        var deep = string.Concat(Enumerable.Repeat(open, 3000)) + inner +
                   string.Concat(Enumerable.Repeat(close, 3000));
        var limit = string.Concat(Enumerable.Repeat(open, 17)) + inner +
                    string.Concat(Enumerable.Repeat(close, 17));
        var allowed = string.Concat(Enumerable.Repeat(open, 8)) + inner +
                      string.Concat(Enumerable.Repeat(close, 8));

        var deepResult = Parser.Parse(deep);
        Assert.True(deepResult.IsUnparseable);
        Assert.Contains("nesting depth", deepResult.UnparseableReason);
        var limitResult = Parser.Parse(limit);
        Assert.True(limitResult.IsUnparseable);
        Assert.Contains("nesting depth", limitResult.UnparseableReason);
        Assert.False(Parser.Parse(allowed).IsUnparseable);
    }

    [Theory]
    [InlineData("while read -r f; do if [ -d \"$f\" ]; then cd \"$f\"; fi; done")]
    [InlineData("if a; then while b; do case x in y) c;; esac; done; elif d; then e; else f; fi")]
    [InlineData("for d in a b; do if test -d $d; then echo $d; fi; done")]
    [InlineData("x=$(if true; then echo a; fi)")]
    [InlineData("case \"$1\" in --help) usage;; esac")]
    public void Every_prefix_of_a_control_flow_source_parses_without_an_exception(string source)
    {
        var parsers = new[]
        {
            Parser,
            UnknownModeParser,
            new BashParser(new BashParserOptions
            {
                WorkingDirectory = "/work",
                InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
                PublishAuthoredSourceFacts = true,
                LaunchEnvironment = new ShellLaunchEnvironment(
                    new Dictionary<string, string> { ["HOME"] = "/home/a" },
                    new[] { "CDPATH" }),
            }),
        };
        foreach (var parser in parsers)
        {
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
}
