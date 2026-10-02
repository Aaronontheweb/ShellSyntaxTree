// -----------------------------------------------------------------------
// <copyright file="CommandWordsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using ShellSyntaxTree.Internal.Parsing;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins <see cref="CommandOccurrence.CommandWords"/>. The words must not
/// depend on option order, and they must not use program-specific grammar.
/// </summary>
public class CommandWordsTests
{
    [Theory]
    [InlineData("gh pr view 123", "gh pr view")]
    [InlineData("gh pr view 123 -R o/r", "gh pr view")]
    [InlineData("gh -R o/r pr view 123", "gh pr view")]
    [InlineData("gh --repo=o/r pr view --web 123", "gh pr view")]
    [InlineData("git --no-pager log -1", "git log")]
    [InlineData("git show b42bf5a", "git show")]
    [InlineData("git push origin v0.4.0", "git push origin")]
    [InlineData("git push origin feature-x", "git push origin feature-x")]
    [InlineData("pgrep -x name", "pgrep name")]
    [InlineData("df -h .", "df")]
    [InlineData("du -sh *", "du")]
    [InlineData("ls -la ../x", "ls")]
    [InlineData("echo \"hello world\"", "echo")]
    [InlineData("make build", "make build")]
    // `HEAD` is a plain word, so it stays. Only program grammar could
    // prove that it is a revision value of filter-branch.
    [InlineData("git -p filter-branch --force HEAD", "git filter-branch HEAD")]
    public void Bash_command_words_follow_general_shell_conventions(
        string source,
        string expected)
    {
        Assert.Equal(expected, Words(Assert.Single(ParseBash(source).Commands)));
    }

    [Theory]
    [InlineData("Start-Sleep -Seconds 300", "Start-Sleep")]
    [InlineData("Get-Process -Name foo", "Get-Process foo")]
    [InlineData("gh -R o/r pr view 123", "gh pr view")]
    [InlineData("gh pr view 123 -R o/r", "gh pr view")]
    [InlineData("Get-Item -Path:foo bar", "Get-Item")]
    [InlineData("Get-ChildItem .", "Get-ChildItem")]
    [InlineData("du -sh *", "du")]
    [InlineData("& 'gh' pr view", "gh pr view")]
    public void PowerShell_parameter_tokens_are_options(string source, string expected)
    {
        Assert.Equal(expected, Words(Assert.Single(ParsePwsh(source).Commands)));
    }

    [Theory]
    [InlineData("gh pr view 123 -R o/r", "gh -R o/r pr view 123")]
    [InlineData("gh pr view --web 123", "gh --web pr view 123")]
    [InlineData("git log --oneline -n 5", "git --no-pager log -n 5")]
    public void Option_order_does_not_change_the_words(string first, string second)
    {
        Assert.Equal(
            Words(Assert.Single(ParseBash(first).Commands)),
            Words(Assert.Single(ParseBash(second).Commands)));
    }

    [Fact]
    public void Redirect_targets_and_assignment_prefixes_are_not_words()
    {
        var command = Assert.Single(ParseBash("MODE=fast make build > out.log").Commands);

        Assert.Equal("make build", Words(command));
    }

    [Fact]
    public void Each_occurrence_gets_its_own_words()
    {
        var result = ParseBash("cd /work && gh pr view 7 | head -n 3");

        Assert.Equal(
            new[] { "cd", "gh pr view", "head" },
            result.Commands.Select(Words).ToArray());
    }

    [Theory]
    [InlineData("git \"push\"")]
    [InlineData("git 'push'")]
    [InlineData("git \\push")]
    [InlineData("git {push,log}")]
    [InlineData("git ~/push")]
    public void Quoted_escaped_and_expanded_words_are_skipped(string source)
    {
        Assert.Equal("git", Words(Assert.Single(ParseBash(source).Commands)));
    }

    [Fact]
    public void Substitution_words_belong_to_the_substitution_occurrence()
    {
        var result = ParseBash("gh pr view $(id -u)");

        Assert.Equal(
            new[] { "id", "gh pr view" },
            result.Commands.Select(Words).ToArray());
    }

    [Fact]
    public void Plain_word_after_an_option_is_kept_by_policy()
    {
        Assert.True(ShellCommandWordProjection.KeepsPlainWordAfterOption);
        Assert.Equal(
            "git filter-branch",
            Words(Assert.Single(ParseBash("git -p filter-branch").Commands)));
    }

    [Fact]
    public void Dynamic_command_name_has_unknown_words()
    {
        var command = Assert.Single(ParsePwsh("& $exe pr view").Commands);

        Assert.True(command.Clause.Verb.IsDynamic);
        Assert.IsType<ShellCommandWords.Unknown>(command.CommandWords);
    }

    [Fact]
    public void Incomplete_occurrence_has_unknown_words()
    {
        var loop = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        }).Parse("for f in a b; do printf '%s' \"$f\" > \"$f.out\"; done");
        var bash = Assert.Single(loop.Commands);
        var pwsh = Assert.Single(ParsePwsh(
            "pwsh -Command \"foreach ($x in $y) { $x }\"").Commands);

        Assert.False(bash.IsComplete);
        Assert.IsType<ShellCommandWords.Unknown>(bash.CommandWords);
        Assert.False(pwsh.IsComplete);
        Assert.IsType<ShellCommandWords.Unknown>(pwsh.CommandWords);
    }

    [Fact]
    public void Default_occurrence_has_unknown_words()
    {
        Assert.IsType<ShellCommandWords.Unknown>(new CommandOccurrence().CommandWords);
    }

    private static string Words(CommandOccurrence command) =>
        string.Join(" ", Assert.IsType<ShellCommandWords.Known>(command.CommandWords).Words);

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
