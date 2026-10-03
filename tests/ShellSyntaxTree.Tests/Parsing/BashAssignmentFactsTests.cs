// -----------------------------------------------------------------------
// <copyright file="BashAssignmentFactsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests;

public class BashAssignmentFactsTests
{
    private static readonly BashParser Parser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
    });

    [Fact]
    public void Assignment_state_resolves_a_later_argument_and_remains_visible()
    {
        const string source = "root='/work/tree'; inspect \"$root/file\"";
        var result = Parser.Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.Equal("inspect", Assert.Single(command.Clause.Verb.Tokens));
        var assignment = Assert.Single(command.Assignments);
        Assert.Equal("root", assignment.Name);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, assignment.Scope);
        Assert.True(assignment.MayAffectProcessEnvironment);
        Assert.Equal("/work/tree", Assert.IsType<ShellValueDomain.Exact>(
            assignment.AuthoredValue).Value);
        Assert.Equal("/work/tree", Assert.IsType<ShellValueDomain.Exact>(
            assignment.EffectiveValue).Value);
        Assert.Equal(0, assignment.SourceStart);
        Assert.Equal(source.IndexOf(';'), assignment.SourceLength);
        Assert.Equal("/work/tree/file", Assert.IsType<ShellValueDomain.Exact>(
            Assert.Single(command.Arguments).Value).Value);
    }

    [Fact]
    public void Assignment_state_remains_visible_without_a_parameter_reference()
    {
        var command = Assert.Single(Parser.Parse("mode='fast'; inspect item").Commands);

        var assignment = Assert.Single(command.Assignments);
        Assert.Equal("mode", assignment.Name);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, assignment.Scope);
        Assert.True(assignment.MayAffectProcessEnvironment);
    }

    [Fact]
    public void Static_command_environment_prefix_is_visible_on_its_command()
    {
        const string source = "TOOL_MODE=static inspect --catalog";
        var result = Parser.Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.Equal("inspect", Assert.Single(command.Clause.Verb.Tokens));
        var assignment = Assert.Single(command.Assignments);
        Assert.Equal("TOOL_MODE", assignment.Name);
        Assert.Equal(
            ShellVariableAssignmentScope.CommandEnvironment,
            assignment.Scope);
        Assert.Equal("static", Assert.IsType<ShellValueDomain.Exact>(
            assignment.EffectiveValue).Value);
        Assert.Equal(0, assignment.SourceStart);
        Assert.Equal(source.IndexOf(' '), assignment.SourceLength);
    }

    [Theory]
    [InlineData("X=1 Y=2 ls -la", "ls -la")]
    [InlineData("A=1 B='two words' C='x' ls", "ls")]
    [InlineData("FOO=a BAR=b BAZ=c make test", "make test")]
    [InlineData("X=1 Y=2 netclaw daemon stop", "netclaw daemon stop")]
    [InlineData("A=1 B=2 C=3 D=4 E=5 inspect --catalog item", "inspect --catalog item")]
    public void Multiple_command_environment_prefixes_match_the_single_prefix_command(
        string source,
        string command)
    {
        var multiple = Parser.Parse(source);
        var single = Parser.Parse("X=1 " + command);

        Assert.False(multiple.IsUnparseable, multiple.UnparseableReason);
        Assert.False(single.IsUnparseable, single.UnparseableReason);
        var actual = Assert.Single(multiple.Commands);
        var expected = Assert.Single(single.Commands);
        Assert.Equal(expected.Clause.Verb.Tokens, actual.Clause.Verb.Tokens);
        Assert.Equal(
            expected.Clause.Args.Select(arg => (arg.Raw, arg.Resolved, arg.Kind)),
            actual.Clause.Args.Select(arg => (arg.Raw, arg.Resolved, arg.Kind)));
        Assert.Equal(
            expected.Arguments.Select(argument => argument.Value),
            actual.Arguments.Select(argument => argument.Value));
        Assert.Equal(
            expected.Clause.Verb.Tokens,
            Assert.Single(multiple.Clauses).Verb.Tokens);
    }

    [Fact]
    public void Multiple_command_environment_prefixes_publish_each_fact_in_order()
    {
        const string source = "A=1 B='two words' C=x inspect --catalog";
        var result = Parser.Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.Equal("inspect", Assert.Single(command.Clause.Verb.Tokens));
        Assert.Equal(
            new[]
            {
                ("A", "1", 0, 3),
                ("B", "two words", 4, 13),
                ("C", "x", 18, 3),
            },
            command.Assignments.Select(assignment => (
                assignment.Name,
                Assert.IsType<ShellValueDomain.Exact>(assignment.EffectiveValue).Value,
                assignment.SourceStart,
                assignment.SourceLength)));
        foreach (var assignment in command.Assignments)
        {
            Assert.Equal(
                ShellVariableAssignmentScope.CommandEnvironment,
                assignment.Scope);
            Assert.True(assignment.MayAffectProcessEnvironment);
            Assert.Equal(assignment.EffectiveValue, assignment.AuthoredValue);
        }
    }

    [Fact]
    public void Shell_state_precedes_multiple_command_environment_prefixes()
    {
        var result = Parser.Parse(
            "root='/work'; A=1 B=2 inspect \"$root/file\"; inspect next");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.Equal(
            new[]
            {
                ("root", ShellVariableAssignmentScope.ShellState),
                ("A", ShellVariableAssignmentScope.CommandEnvironment),
                ("B", ShellVariableAssignmentScope.CommandEnvironment),
            },
            result.Commands[0].Assignments.Select(assignment =>
                (assignment.Name, assignment.Scope)));
        Assert.Equal("/work/file", Assert.IsType<ShellValueDomain.Exact>(
            Assert.Single(result.Commands[0].Arguments).Value).Value);
        Assert.Equal("root", Assert.Single(result.Commands[1].Assignments).Name);
    }

    [Theory]
    [InlineData("X=1 Y=`id` ls")]
    [InlineData("X=1 Y=$other ls")]
    [InlineData("X=1 Y=\"$other\" ls")]
    [InlineData("X=1 Y=$'a' ls")]
    [InlineData("X=1 Y=* ls")]
    [InlineData("X=1 Y+=2 ls")]
    [InlineData("X=1 Y[0]=2 ls")]
    [InlineData("X=1 PATH=/other ls")]
    [InlineData("X=1 Y=2 LD_PRELOAD=/other/lib.so ls")]
    [InlineData("X=1 BASH_ENV=/other ls")]
    [InlineData("X=1 X=2 ls")]
    [InlineData("X=1 Y=2 Z=3 X=4 ls")]
    [InlineData("X=1 Y=2")]
    [InlineData("X=1 Y=2 Z=3")]
    [InlineData("X=1 Y=2; inspect item")]
    [InlineData("X=1 Y=2 > marker")]
    [InlineData("X=1 > marker Y=2 inspect item")]
    [InlineData("X=1 Y=2 > marker inspect item")]
    [InlineData("X=1 Y=2 printf '%s' item")]
    [InlineData("X=1 Y=2 cd /work")]
    [InlineData("X=1 Y=2 bash -c 'inspect item'")]
    [InlineData("X=1 Y=2 inspect \"$X\"")]
    public void Unsafe_multiple_prefix_forms_fail_closed(string source)
    {
        var result = Parser.Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Theory]
    [InlineData(BashInitialStateMode.Unknown)]
    [InlineData(BashInitialStateMode.IsolatedNonInteractive)]
    public void Multiple_prefixes_require_fresh_initial_state(BashInitialStateMode mode)
    {
        var parser = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = mode,
        });

        var result = parser.Parse("X=1 Y=2 ls -la");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Prefix_expansion_uses_prior_state_and_does_not_persist()
    {
        var result = Parser.Parse(
            "root='/work'; MODE=fast inspect \"$root/file\"; inspect next");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        Assert.Equal(
            new[]
            {
                ShellVariableAssignmentScope.ShellState,
                ShellVariableAssignmentScope.CommandEnvironment,
            },
            result.Commands[0].Assignments.Select(assignment => assignment.Scope));
        Assert.Equal("/work/file", Assert.IsType<ShellValueDomain.Exact>(
            Assert.Single(result.Commands[0].Arguments).Value).Value);
        var later = Assert.Single(result.Commands[1].Assignments);
        Assert.Equal("root", later.Name);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, later.Scope);
    }

    [Theory]
    [InlineData("root='/work' | inspect item")]
    [InlineData("(root='/work'; inspect item)")]
    [InlineData("root='/work' > marker; inspect item")]
    [InlineData("root=$other; inspect \"$root/file\"")]
    [InlineData("root[0]=value; inspect item")]
    [InlineData("root+=value; inspect item")]
    [InlineData("root=foo:~; inspect item")]
    [InlineData("root=~root; inspect item")]
    [InlineData("root=$'a\\n'; inspect item")]
    [InlineData("root=$\"hello\"; inspect item")]
    [InlineData("root=foo$'bar'; inspect item")]
    [InlineData("root='/work'; inspect \"$other/file\"")]
    [InlineData("root=''; inspect \"${root:-fallback}\"")]
    [InlineData("root='$(printf hidden)'; inspect \"${root@P}\"")]
    public void Unsupported_assignment_state_fails_closed(string source)
    {
        var result = Parser.Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Theory]
    [InlineData("PATH=/other inspect item")]
    [InlineData("EXECIGNORE=inspect inspect item")]
    [InlineData("GLOBSORT=name inspect item")]
    [InlineData("LIBPATH=/other inspect item")]
    [InlineData("SHLIB_PATH=/other inspect item")]
    [InlineData("LD_PRELOAD=/other/lib.so inspect item")]
    [InlineData("DYLD_LIBRARY_PATH=/other inspect item")]
    [InlineData("_=chosen inspect item")]
    [InlineData("BASH_FUTURE=value inspect item")]
    [InlineData("COMP_FUTURE=value inspect item")]
    [InlineData("COMPFUTURE=value inspect item")]
    [InlineData("HISTFUTURE=value inspect item")]
    [InlineData("READLINE_FUTURE=value inspect item")]
    [InlineData("LC_FUTURE=value inspect item")]
    [InlineData("PROMPT_FUTURE=value inspect item")]
    [InlineData("PROMPTFUTURE=value inspect item")]
    [InlineData("PS9=value inspect item")]
    [InlineData("MODE=foo:~ inspect item")]
    [InlineData("MODE=~root inspect item")]
    [InlineData("MODE=$'a\\n' inspect item")]
    [InlineData("MODE=$\"hello\" inspect item")]
    [InlineData("MODE=foo$'bar' inspect item")]
    [InlineData("PS4='$(printf hidden)' /usr/bin/true")]
    [InlineData("set -x; PS4='$(printf hidden)' /usr/bin/true")]
    [InlineData("MODE=fast printf '%s' item")]
    [InlineData("MODE=fast cd /work")]
    [InlineData("MODE=fast bash -c 'inspect item'")]
    public void Unsafe_command_environment_prefix_fails_closed(string source)
    {
        var result = Parser.Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Unknown_initial_state_keeps_assignment_syntax_unresolved()
    {
        var result = new BashParser().Parse("mode='fast' inspect item");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Theory]
    [InlineData("inspect \"$ambient/file\"")]
    [InlineData("MODE=fast inspect \"$MODE\"")]
    public void Fresh_mode_does_not_prove_unassigned_parameter_state(string source)
    {
        var result = Parser.Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }
}
