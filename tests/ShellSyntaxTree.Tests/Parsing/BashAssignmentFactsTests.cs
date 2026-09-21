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
    [InlineData("root='/one'; root='/two'; inspect \"$root/file\"")]
    [InlineData("root='/work'")]
    [InlineData("root='/work' && inspect \"$root/file\"")]
    [InlineData("root='/work' | inspect item")]
    [InlineData("(root='/work'; inspect item)")]
    [InlineData("root='/work' > marker; inspect item")]
    [InlineData("root=$(discover); inspect \"$root/file\"")]
    [InlineData("root=$other; inspect \"$root/file\"")]
    [InlineData("root[0]=value; inspect item")]
    [InlineData("root+=value; inspect item")]
    [InlineData("root='/work'; printf '%s' item")]
    [InlineData("root='/work'; inspect one | inspect two")]
    [InlineData("root='/work'; inspect one && inspect two")]
    [InlineData("root='/work'; inspect $(discover)")]
    [InlineData("root='/work'; bash -c 'inspect item'")]
    [InlineData("root=~; inspect item")]
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
    [InlineData("MODE=~ inspect item")]
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
    [InlineData("MODE=fast inspect item | inspect next")]
    [InlineData("MODE=fast inspect item && inspect next")]
    [InlineData("MODE=fast inspect item || inspect next")]
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
