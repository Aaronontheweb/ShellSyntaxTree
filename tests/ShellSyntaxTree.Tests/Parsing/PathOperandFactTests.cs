// -----------------------------------------------------------------------
// <copyright file="PathOperandFactTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins issue #193: <c>.</c>, <c>..</c>, and a shell glob are path facts for
/// every program that has no per-verb rule. A glob stays an unresolved path
/// pattern.
/// </summary>
public class PathOperandFactTests
{
    [Theory]
    [InlineData("df -h .", ".", "/work")]
    [InlineData("du -sh ..", "..", "/")]
    [InlineData("totally-unknown-tool . ..", ".", "/work")]
    public void Bash_directory_reference_is_a_resolved_path(
        string source,
        string word,
        string resolved)
    {
        var argument = Argument(ParseBash(source), word);

        Assert.True(argument.Argument.IsPath);
        Assert.True(argument.Element.IsPath);
        Assert.Equal(ArgKind.Literal, argument.Argument.Kind);
        Assert.Equal(resolved, argument.Argument.Resolved);
    }

    [Theory]
    [InlineData("du -sh *", "*")]
    [InlineData("du -sh *.cs", "*.cs")]
    [InlineData("totally-unknown-tool src/*", "src/*")]
    [InlineData("echo *", "*")]
    public void Bash_glob_is_an_unresolved_path_pattern(string source, string word)
    {
        var argument = Argument(ParseBash(source), word);

        Assert.True(argument.Argument.IsPath);
        Assert.True(argument.Element.IsPath);
        Assert.Equal(ArgKind.Glob, argument.Argument.Kind);
        Assert.Null(argument.Argument.Resolved);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredFileSystemValue);
    }

    [Theory]
    [InlineData("du -sh '*'")]
    [InlineData("du -sh \"*\"")]
    [InlineData("du -sh \\*")]
    public void Bash_quoted_or_escaped_star_is_not_a_glob(string source)
    {
        var argument = Assert.Single(
            Assert.Single(ParseBash(source).Commands).Arguments,
            argument => !argument.Argument.IsFlag);

        Assert.NotEqual(ArgKind.Glob, argument.Argument.Kind);
        Assert.False(argument.Argument.IsPath);
    }

    [Theory]
    [InlineData("grep . notes", ".")]
    [InlineData("curl *", "*")]
    public void Per_verb_rule_still_wins(string source, string word)
    {
        Assert.False(Argument(ParseBash(source), word).Argument.IsPath);
    }

    [Fact]
    public void Directory_reference_after_unknown_cd_fails_closed_like_a_relative_path()
    {
        var result = ParseBash("cd \"$(pwd)\"; df -h . ./x");
        var df = result.Commands.Last();

        foreach (var word in new[] { ".", "./x" })
        {
            var argument = Assert.Single(df.Arguments, item => item.Argument.Raw == word);
            Assert.Equal(ArgKind.DynamicSkip, argument.Argument.Kind);
            Assert.False(argument.Argument.IsPath);
            Assert.Null(argument.Argument.Resolved);
        }
    }

    [Fact]
    public void Bash_glob_path_fact_adds_no_tree_access()
    {
        var command = Assert.Single(ParseBash("du -sh *").Commands);

        Assert.Empty(command.FileSystemTreeAccesses);
    }

    [Theory]
    [InlineData("du -sh *", "*", ArgKind.Glob)]
    [InlineData("df -h .", ".", ArgKind.Literal)]
    [InlineData("du -sh ..", "..", ArgKind.Literal)]
    [InlineData("Write-Output .", ".", ArgKind.Literal)]
    public void PowerShell_reports_directory_references_and_native_globs(
        string source,
        string word,
        ArgKind kind)
    {
        var argument = Argument(ParsePwsh(source), word);

        Assert.True(argument.Argument.IsPath);
        Assert.Equal(kind, argument.Argument.Kind);
    }

    [Fact]
    public void PowerShell_cmdlet_wildcard_is_not_a_shell_glob_path()
    {
        // PowerShell passes a wildcard to a cmdlet unexpanded. Only the
        // cmdlet decides whether it names files.
        var argument = Argument(ParsePwsh("Write-Output *"), "*");

        Assert.False(argument.Argument.IsPath);
    }

    private static AnalyzedArgument Argument(ParsedCommand result, string word) =>
        Assert.Single(
            Assert.Single(result.Commands).Arguments,
            argument => argument.Argument.Raw == word);

    private static ParsedCommand ParseBash(string source)
    {
        var result = new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/agent",
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        }).Parse(source);
        Assert.False(result.IsUnparseable, result.UnparseableReason);
        return result;
    }

    private static ParsedCommand ParsePwsh(string source)
    {
        var result = new PwshParser(new PwshParserOptions
        {
            HomeDirectory = "/home/agent",
            WorkingDirectory = "/work",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
        }).Parse(source);
        Assert.False(result.IsUnparseable, result.UnparseableReason);
        return result;
    }
}
