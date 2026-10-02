// -----------------------------------------------------------------------
// <copyright file="CommandWordsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using ShellSyntaxTree.Internal.Parsing;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins <see cref="CommandOccurrence.CommandWords"/> (#194). The words must
/// not depend on option order, must not use program-specific grammar, and
/// must never be shorter than the words the program really receives.
/// </summary>
public class CommandWordsTests
{
    // ------------------------------------------------------------ owner table

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
    [InlineData("du -sh ./*", "du")]
    [InlineData("ls -la ../x", "ls")]
    [InlineData("echo \"hello world\"", "echo")]
    [InlineData("make build", "make build")]
    [InlineData("git -p filter-branch", "git filter-branch")]
    [InlineData("git -p filter-branch --force HEAD", "git filter-branch HEAD")]
    [InlineData("git \"push\" --force", "git push")]
    public void Bash_owner_table(string source, string expected) =>
        AssertBashWords(source, expected);

    [Fact]
    public void Plain_word_after_an_option_is_kept_by_policy()
    {
        Assert.True(ShellCommandWordProjection.KeepsPlainWordAfterOption);
        AssertBashWords("pgrep -x name", "pgrep name");
    }

    // ------------------------------------------------------------ quoting

    [Theory]
    [InlineData("git \"push\"", "git push")]
    [InlineData("git 'log'", "git log")]
    [InlineData("gi\"t\" push", "git push")]
    [InlineData("git \"pu\"sh", "git push")]
    [InlineData("git commit -m \"fix the bug\"", "git commit")]
    [InlineData("git commit -m 'fix the bug'", "git commit")]
    [InlineData("git push \"\" origin", "git push origin")]
    public void Bash_quoted_single_word_is_a_plain_word(string source, string expected) =>
        AssertBashWords(source, expected);

    // ------------------------------------------------------------ hiding attempts

    [Theory]
    [InlineData("git \"filter-branch\"", "git filter-branch")]
    [InlineData("git 'filter-branch'", "git filter-branch")]
    [InlineData("git filter\"-branch\"", "git filter-branch")]
    [InlineData("git \\filter-branch", "git filter-branch")]
    [InlineData("git -c x=y push", "git x=y push")]
    [InlineData("git --git-dir=. push", "git push")]
    [InlineData("git -C repo push", "git push")]
    [InlineData("gh --debug auth logout", "gh auth logout")]
    // `env` and `command` are the program words. The real verb follows them.
    [InlineData("env git push", "env git push")]
    [InlineData("command git push", "command git push")]
    [InlineData("\\git push", "git push")]
    [InlineData("\"git\" push", "git push")]
    [InlineData("'git' push", "git push")]
    [InlineData("git -- push", "git push")]
    [InlineData("git --no-pager -- push origin", "git push origin")]
    public void Bash_hiding_attempts_keep_the_real_verb(string source, string expected) =>
        AssertBashWords(source, expected);

    [Theory]
    [InlineData("git $'push'")]
    [InlineData("$'git' push")]
    [InlineData("git $\"push\"")]
    public void Bash_ansi_c_and_locale_quotes_fail_closed(string source)
    {
        // The parser does not resolve these quote forms, so it rejects the
        // complete input. No command words can reach a grant.
        Assert.True(ParseBashRaw(source).IsUnparseable);
    }

    [Theory]
    [InlineData("git `push", "git push")]
    [InlineData("git \"push\"", "git push")]
    [InlineData("git 'log'", "git log")]
    [InlineData("git -- push", "git push")]
    [InlineData("git --no-pager log -1", "git log")]
    [InlineData("gh --debug auth logout", "gh auth logout")]
    public void PowerShell_hiding_attempts_keep_the_real_verb(string source, string expected) =>
        AssertPwshWords(source, expected);

    // ------------------------------------------------------------ expansions

    [Theory]
    [InlineData("git {push,log}")]
    [InlineData("git {push,log} origin")]
    [InlineData("git push {a,b}")]
    [InlineData("git {a..c}")]
    [InlineData("git $(echo push)")]
    [InlineData("git push $(cmd)")]
    [InlineData("git \"$(cmd)\"")]
    [InlineData("r=push; git \"$r\"")]
    [InlineData("r=push; git push \"$r\"")]
    [InlineData("r=push; git $r")]
    // Unquoted expansion in an option can split into more words.
    [InlineData("r='x push'; git --c=$r log")]
    // Unquoted expansion in a path can split too.
    [InlineData("r=/tmp; ls $r/x")]
    // "$@" and "${a[@]}" give one word per element, even in an option.
    [InlineData("git --x=\"$@\" log")]
    [InlineData("git \"$@\"")]
    public void Bash_expansion_makes_words_unknown(string source)
    {
        var git = ParseBash(source).Commands.Last();

        Assert.IsType<ShellCommandWords.Unknown>(git.CommandWords);
    }

    [Theory]
    [InlineData("git $SUB")]
    [InlineData("git push $REMOTE")]
    [InlineData("git ${SUB}")]
    [InlineData("git \"$X\"")]
    [InlineData("git `cmd`")]
    [InlineData("git push `cmd`")]
    [InlineData("git $((1+1))")]
    [InlineData("git push $((1+1))")]
    [InlineData("git <(cmd)")]
    public void Bash_unproved_expansion_fails_closed_before_words(string source)
    {
        // These forms are rejected by the parser, which is stricter than
        // an Unknown word list.
        Assert.True(ParseBashRaw(source).IsUnparseable);
    }

    [Theory]
    [InlineData("r=push; git --repo=\"$r\" log", "git log")]
    [InlineData("r=/tmp; ls \"$r/x\"", "ls")]
    [InlineData("git \"$(cmd)\"/x", "git")]
    public void Bash_quoted_expansion_in_option_or_path_is_skipped(
        string source,
        string expected)
    {
        var command = ParseBash(source).Commands.Last();

        Assert.Equal(expected, Words(command));
    }

    [Theory]
    [InlineData("git $sub")]
    [InlineData("git push $remote")]
    [InlineData("git \"$sub\"")]
    [InlineData("git $(Get-Sub)")]
    [InlineData("git push,log")]
    [InlineData("git --format=%h,%s log")]
    [InlineData("Get-Process $var")]
    [InlineData("Get-Process -Name $var")]
    [InlineData("Get-Process @params")]
    [InlineData("du -sh *")]
    [InlineData("git p?sh")]
    [InlineData("Get-ChildItem *.cs")]
    public void PowerShell_expansion_makes_words_unknown(string source)
    {
        var command = ParsePwsh(source).Commands.Last();

        Assert.IsType<ShellCommandWords.Unknown>(command.CommandWords);
    }

    // ------------------------------------------------------------ paths and globs

    [Theory]
    [InlineData("tool .")]
    [InlineData("tool ..")]
    [InlineData("tool ./x")]
    [InlineData("tool ../x")]
    [InlineData("tool ~/x")]
    [InlineData("tool /abs")]
    [InlineData("tool ./*")]
    [InlineData("tool src/*")]
    [InlineData("tool src/*.cs")]
    [InlineData("tool **/x")]
    [InlineData("tool ../*.cs")]
    public void Bash_path_and_slash_glob_operands_are_skipped(string source)
    {
        var command = Assert.Single(ParseBash(source).Commands);
        var operand = Assert.Single(command.Clause.Elements, item => item.Role == ClauseElementRole.Argument);

        Assert.Equal("tool", Words(command));
        Assert.True(operand.IsPath);
    }

    [Theory]
    [InlineData("tool \"*\"")]
    [InlineData("tool '*'")]
    [InlineData("tool \\*")]
    public void Bash_quoted_or_escaped_glob_is_data(string source)
    {
        var command = Assert.Single(ParseBash(source).Commands);
        var operand = Assert.Single(command.Clause.Elements, item => item.Role == ClauseElementRole.Argument);

        Assert.False(operand.IsPath);
        Assert.Equal(ArgKind.Literal, operand.Kind);
        Assert.Equal("tool", Words(command));
    }

    [Fact]
    public void Directory_reference_follows_cd_in_a_list()
    {
        var result = ParseBash("cd /x && df -h .");
        var df = result.Commands.Last();
        var dot = Assert.Single(df.Arguments, item => item.Argument.Raw == ".");

        Assert.Equal(new[] { "cd", "df" }, result.Commands.Select(Words).ToArray());
        Assert.True(dot.Argument.IsPath);
    }

    [Theory]
    // Option A (#194): a bare glob can expand to any file name, such as
    // `push`, so the words are unknown. The glob is still a path fact.
    [InlineData("git *")]
    [InlineData("git p?sh")]
    [InlineData("git [ab]*")]
    [InlineData("git **")]
    [InlineData("git push *")]
    [InlineData("du -sh *")]
    [InlineData("tool *.cs")]
    [InlineData("tool ?")]
    [InlineData("tool [ab]")]
    public void Bash_bare_glob_makes_words_unknown(string source)
    {
        var command = Assert.Single(ParseBash(source).Commands);
        var glob = command.Clause.Elements.Last();

        Assert.IsType<ShellCommandWords.Unknown>(command.CommandWords);
        Assert.Equal(ArgKind.Glob, glob.Kind);
        Assert.True(glob.IsPath);
    }

    [Fact]
    public void Bare_glob_is_unknown_even_when_the_lexer_kind_is_not_glob()
    {
        // tar -F takes command text, so the parser marks the value
        // DynamicSkip. The shell still expands the unquoted glob first.
        var command = Assert.Single(ParseBash("tar -F *.json").Commands);

        Assert.Equal(ArgKind.DynamicSkip, command.Clause.Elements.Last().Kind);
        Assert.IsType<ShellCommandWords.Unknown>(command.CommandWords);
    }

    [Theory]
    [InlineData("du -sh ./*", "du")]
    [InlineData("git ./*", "git")]
    [InlineData("ls src/*.cs", "ls")]
    [InlineData("git ../*.cs", "git")]
    [InlineData("tool --include=*.cs", "tool")]
    [InlineData("du -sh \"*\"", "du")]
    [InlineData("du -sh \\*", "du")]
    [InlineData("git 'p?sh'", "git")]
    public void Bash_slash_glob_option_glob_and_quoted_glob_are_skipped(
        string source,
        string expected) =>
        AssertBashWords(source, expected);

    // ------------------------------------------------------------ digits

    [Theory]
    [InlineData("git checkout abc123", "git checkout")]
    [InlineData("git checkout v1", "git checkout")]
    [InlineData("git checkout 1.0", "git checkout")]
    // Cost: a branch name with a digit is skipped, so grants for different
    // release branches share one key.
    [InlineData("git checkout release-2.0", "git checkout")]
    [InlineData("tool x86_64", "tool")]
    [InlineData("git show 3f2a9c1", "git show")]
    // A hash with no digit is a plain word and stays.
    [InlineData("git show deadbeef", "git show deadbeef")]
    public void Bash_words_with_digits_are_skipped(string source, string expected) =>
        AssertBashWords(source, expected);

    // ------------------------------------------------------------ lists

    [Fact]
    public void Each_occurrence_in_a_pipeline_and_list_gets_its_own_words()
    {
        var result = ParseBash("git log | head -1; gh pr view 1 && make build");

        Assert.Equal(
            new[] { "git log", "head", "gh pr view", "make build" },
            result.Commands.Select(Words).ToArray());
    }

    [Fact]
    public void Redirect_targets_and_assignment_prefixes_are_not_words()
    {
        AssertBashWords("MODE=fast make build > out.log", "make build");
        AssertBashWords("make build 2> err.log < in.txt", "make build");
    }

    [Fact]
    public void Substitution_words_belong_to_the_substitution_occurrence()
    {
        var result = ParseBash("echo \"$(id -u)\"");

        Assert.Equal("id", Words(result.Commands[0]));
        Assert.IsType<ShellCommandWords.Unknown>(result.Commands[1].CommandWords);
    }

    // ------------------------------------------------------------ PowerShell

    [Theory]
    [InlineData("Start-Sleep -Seconds 300", "Start-Sleep")]
    [InlineData("Get-Process -Name foo", "Get-Process foo")]
    [InlineData("Get-Process foo", "Get-Process foo")]
    [InlineData("Remove-Item x.txt -Force", "Remove-Item")]
    [InlineData("Get-Item -Path:foo bar", "Get-Item")]
    [InlineData("Get-ChildItem .", "Get-ChildItem")]
    [InlineData("Get-ChildItem ./*.cs", "Get-ChildItem")]
    [InlineData("du -sh ./*", "du")]
    [InlineData("du -sh '*'", "du")]
    [InlineData("Write-Output \"a b\"", "Write-Output")]
    [InlineData("Write-Output 'it''s'", "Write-Output it's")]
    [InlineData("& 'C:\\x\\tool.exe' arg", "C:\\x\\tool.exe arg")]
    [InlineData("& 'gh' pr view", "gh pr view")]
    [InlineData("gh -R o/r pr view 1", "gh pr view")]
    [InlineData("gh pr view 1 -R o/r", "gh pr view")]
    [InlineData("ssh user@host", "ssh user@host")]
    public void PowerShell_words(string source, string expected) =>
        AssertPwshWords(source, expected);

    [Fact]
    public void PowerShell_wildcard_alias_is_a_static_command_name()
    {
        var result = ParsePwsh("Get-ChildItem | ? Name");

        Assert.Equal(new[] { "Get-ChildItem", "? Name" }, result.Commands.Select(Words).ToArray());
    }

    [Fact]
    public void Bash_glob_command_name_fails_closed()
    {
        // Bash expands a glob in the command name. The parser rejects it.
        Assert.True(ParseBashRaw("g?t push").IsUnparseable);
    }

    // ------------------------------------------------------------ unknown

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

    // ------------------------------------------------------------ order invariance

    public static IEnumerable<object[]> SeededOptionOrders()
    {
        // The repository has no property-test library, so a seeded table
        // stands in for one. Each option is valueless, carries an inline
        // value, or has a value that is a path or has a digit, so no option
        // value is a plain word.
        var options = new[]
        {
            new[] { "-R", "o/r" },
            new[] { "--web" },
            new[] { "--json=title" },
            new[] { "-L", "5" },
            new[] { "--repo=o/r" },
            new[] { "-q" },
        };
        var verb = new[] { "pr", "view" };
        var random = new Random(194);
        for (var sample = 0; sample < 40; sample++)
        {
            var chosen = options.OrderBy(_ => random.Next()).Take(random.Next(1, options.Length + 1));
            var words = new List<string>(verb);
            foreach (var option in chosen)
            {
                words.InsertRange(random.Next(0, words.Count + 1), option);
            }

            words.Insert(random.Next(0, words.Count + 1), "123");
            yield return new object[] { "gh " + string.Join(" ", words) };
        }
    }

    [Theory]
    [MemberData(nameof(SeededOptionOrders))]
    public void Option_order_does_not_change_the_words(string source)
    {
        AssertBashWords(source, "gh pr view");
        AssertPwshWords(source, "gh pr view");
    }

    // ------------------------------------------------------------ helpers

    private static void AssertBashWords(string source, string expected) =>
        Assert.Equal(expected, Words(Assert.Single(ParseBash(source).Commands)));

    private static void AssertPwshWords(string source, string expected) =>
        Assert.Equal(expected, Words(Assert.Single(ParsePwsh(source).Commands)));

    private static string Words(CommandOccurrence command) =>
        string.Join(" ", Assert.IsType<ShellCommandWords.Known>(command.CommandWords).Words);

    private static ParsedCommand ParseBashRaw(string source) =>
        new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/agent",
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        }).Parse(source);

    private static ParsedCommand ParseBash(string source)
    {
        var result = ParseBashRaw(source);
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
