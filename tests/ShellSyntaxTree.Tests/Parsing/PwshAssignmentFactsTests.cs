// -----------------------------------------------------------------------
// <copyright file="PwshAssignmentFactsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class PwshAssignmentFactsTests
{
    private static readonly PwshParser Parser = new(new PwshParserOptions
    {
        WorkingDirectory = "C:/work",
        InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
    });

    [Theory]
    [InlineData("$root='C:/work/tree'; Get-Item \"$root/file\"")]
    [InlineData("$root= 'C:/work/tree'; Get-Item \"$root/file\"")]
    [InlineData("$root ='C:/work/tree'; Get-Item \"$root/file\"")]
    [InlineData("$root = 'C:/work/tree'; Get-Item \"$root/file\"")]
    [InlineData("$root\t=\t'C:/work/tree'; Get-Item \"$root/file\"")]
    public void Assignment_state_resolves_a_later_argument_and_remains_visible(
        string source)
    {
        var result = Parser.Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        Assert.Equal("Get-Item", Assert.Single(command.Clause.Verb.Tokens));
        var assignment = Assert.Single(command.Assignments);
        Assert.Equal("root", assignment.Name);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, assignment.Scope);
        Assert.False(assignment.MayAffectProcessEnvironment);
        Assert.Equal("C:/work/tree", Assert.IsType<ShellValueDomain.Exact>(
            assignment.AuthoredValue).Value);
        Assert.Equal("C:/work/tree", Assert.IsType<ShellValueDomain.Exact>(
            assignment.EffectiveValue).Value);
        Assert.Equal(0, assignment.SourceStart);
        Assert.Equal(source.IndexOf(';'), assignment.SourceLength);
        Assert.Equal("C:/work/tree/file", Assert.IsType<ShellValueDomain.Exact>(
            Assert.Single(command.Arguments).Value).Value);

        var list = Assert.IsType<CommandListSyntax>(Assert.Single(result.Syntax.Statements));
        var syntax = Assert.IsType<ShellAssignmentSyntax>(list.Items[0].Command);
        Assert.Equal(0, syntax.SourceStart);
        Assert.Equal(source.IndexOf(';'), syntax.SourceLength);
        Assert.Same(assignment, syntax.Assignment);
    }

    [Fact]
    public void Assignment_state_remains_visible_without_a_parameter_reference()
    {
        var command = Assert.Single(
            Parser.Parse("$mode='fast'; Get-Item item").Commands);

        var assignment = Assert.Single(command.Assignments);
        Assert.Equal("mode", assignment.Name);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, assignment.Scope);
    }

    [Theory]
    [InlineData("$root='one'; $root='two'; Get-Item $root")]
    [InlineData("$root = 'one'; $root = 'two'; Get-Item $root")]
    [InlineData("$root='value'")]
    [InlineData("$root = 'value'")]
    [InlineData("$root='value' && Get-Item $root")]
    [InlineData("$root = 'value' && Get-Item $root")]
    [InlineData("$root='value' || Get-Item $root")]
    [InlineData("$root='value' | Get-Item item")]
    [InlineData("$root = 'value' | Get-Item item")]
    [InlineData("($root='value'; Get-Item item)")]
    [InlineData("$root='value' > marker; Get-Item item")]
    [InlineData("$root = 'value' > marker; Get-Item item")]
    [InlineData("$root=\"value\"; Get-Item $root")]
    [InlineData("$root = \"value\"; Get-Item $root")]
    [InlineData("$root=$(Get-Item value); Get-Item $root")]
    [InlineData("$root = $(Get-Item value); Get-Item $root")]
    [InlineData("$root = & 'Get-Item' value; Get-Item $root")]
    [InlineData("$root = { Get-Item value }; Get-Item $root")]
    [InlineData("$root=@('value'); Get-Item $root")]
    [InlineData("$root = @('value'); Get-Item $root")]
    [InlineData("$root=@{ key='value' }; Get-Item $root")]
    [InlineData("$root = @{ key='value' }; Get-Item $root")]
    [InlineData("$root+='value'; Get-Item $root")]
    [InlineData("$root += 'value'; Get-Item $root")]
    [InlineData("$root -= 'value'; Get-Item $root")]
    [InlineData("$root *= 'value'; Get-Item $root")]
    [InlineData("$root /= 'value'; Get-Item $root")]
    [InlineData("$root %= 'value'; Get-Item $root")]
    [InlineData("$root ??= 'value'; Get-Item $root")]
    [InlineData("$root == 'value'; Get-Item $root")]
    [InlineData("$root =+ 'value'; Get-Item $root")]
    [InlineData("$root =- 'value'; Get-Item $root")]
    [InlineData("$a=$b='value'; Get-Item $a")]
    [InlineData("$a = $b = 'value'; Get-Item $a")]
    [InlineData("$a, $b = 'value'; Get-Item $a")]
    [InlineData("$env:ROOT='value'; Get-Item item")]
    [InlineData("$env:ROOT = 'value'; Get-Item item")]
    [InlineData("$global:root='value'; Get-Item item")]
    [InlineData("$global:root = 'value'; Get-Item item")]
    [InlineData("[string]$root='value'; Get-Item $root")]
    [InlineData("[string]$root = 'value'; Get-Item $root")]
    [InlineData("$state.root='value'; Get-Item item")]
    [InlineData("$state.root = 'value'; Get-Item item")]
    [InlineData("$items[0]='value'; Get-Item item")]
    [InlineData("$items[0] = 'value'; Get-Item item")]
    [InlineData("$HOME='value'; Get-Item item")]
    [InlineData("$HOME = 'value'; Get-Item item")]
    [InlineData("$root='value'; & 'Get-Item' item")]
    [InlineData("$root='value'; . './script.ps1'")]
    [InlineData("$root='value'; ForEach-Object { Get-Item item }")]
    [InlineData("$root='value'; Invoke-CustomMutation; Get-Item $root")]
    [InlineData("Remove-Item $x='/tmp/target'")]
    [InlineData("Remove-Item $x = '/tmp/target'")]
    [InlineData("$root <# gap #> = 'value'; Get-Item $root")]
    [InlineData("$root=<# gap #>'value'; Get-Item $root")]
    [InlineData("$root = <# gap #> 'value'; Get-Item $root")]
    [InlineData("$root # gap\n= 'value'; Get-Item $root")]
    [InlineData("$root = # gap\n'value'; Get-Item $root")]
    [InlineData("$root`\n= 'value'; Get-Item $root")]
    [InlineData("$root =`\n'value'; Get-Item $root")]
    [InlineData("$root\u00A0= 'value'; Get-Item $root")]
    [InlineData("$root =\u00A0'value'; Get-Item $root")]
    [InlineData("$root = @'\nvalue\n'@; Get-Item $root")]
    [InlineData("$root = @\"\nvalue\n\"@; Get-Item $root")]
    public void Unsupported_assignment_state_fails_closed(string source)
    {
        var result = Parser.Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Assignment_after_prior_variable_mutation_fails_closed()
    {
        var result = Parser.Parse(
            "Set-Variable -Name mode -Value old; $root='value'; Get-Item $root");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Unknown_initial_state_keeps_assignment_syntax_unresolved()
    {
        var result = new PwshParser().Parse("$mode='fast'; Get-Item item");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }
}
