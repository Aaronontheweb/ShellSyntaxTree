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
    [InlineData("git -p filter-branch --force HEAD", "git filter-branch")]
    [InlineData("git \"push\" --force", "git push")]
    public void Bash_owner_table(string source, string expected) =>
        AssertBashWords(source, expected);

    // ------------------------------------------------------------ position rule (#197)

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Verb_slot_is_filled_by_the_first_word_after_the_program(
        int commandWordCount,
        bool filled)
    {
        Assert.Equal(filled, ShellCommandWordProjection.IsVerbSlotFilled(commandWordCount));
    }

    [Theory]
    // Strict until the verb slot is filled: these must not hide the verb.
    [InlineData("git -p filter-branch", "git filter-branch")]
    [InlineData("git --no-pager log", "git log")]
    [InlineData("git -c x=y push", "git x=y push")]
    [InlineData("gh --debug auth logout", "gh auth logout")]
    [InlineData("git \"push\"", "git push")]
    [InlineData("git \\push", "git push")]
    [InlineData("pgrep -x name", "pgrep name")]
    public void Bash_verb_slot_keeps_a_plain_word_after_an_option(
        string source,
        string expected) =>
        AssertBashWords(source, expected);

    [Theory]
    [InlineData("git *")]
    [InlineData("git p?sh")]
    [InlineData("git {push,log}")]
    [InlineData("git {push,a/b}")]
    [InlineData("git -p {push,log}")]
    [InlineData("git -p *")]
    [InlineData("r=push; git $r")]
    [InlineData("r=push; git -p \"$r\"")]
    [InlineData("du -sh *")]
    [InlineData("rm -f {a,b}.txt")]
    public void Bash_verb_slot_dynamic_word_makes_words_unknown(string source)
    {
        Assert.IsType<ShellCommandWords.Unknown>(
            ParseBash(source).Commands.Last().CommandWords);
    }

    [Theory]
    [InlineData("git $SUB", null)]
    [InlineData("git ${SUB}", null)]
    [InlineData("git \"$X\"", null)]
    [InlineData("git push $REMOTE", "git push")]
    public void Bash_unknown_variable_read_follows_the_dynamic_word_rule(string source, string? expected)
    {
        // An unassigned variable is an unknown value in fresh mode (#221).
        // In the verb slot it makes the words unknown. After the slot it is
        // skipped as an argument.
        var parsed = ParseBashRaw(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var words = parsed.Commands.Single().CommandWords;
        if (expected is null)
        {
            Assert.IsType<ShellCommandWords.Unknown>(words);
        }
        else
        {
            Assert.Equal(expected, string.Join(" ", Assert.IsType<ShellCommandWords.Known>(words).Words));
        }
    }

    [Theory]
    [InlineData("git $SUB")]
    [InlineData("git push $REMOTE")]
    public void Bash_unproved_expansion_fails_closed_in_unknown_mode(string source)
    {
        Assert.True(new BashParser().Parse(source).IsUnparseable);
    }

    [Theory]
    // After the verb slot: a plain word directly after an option is that
    // option's value, and dynamic words are arguments.
    [InlineData("dotnet build -c Release", "dotnet build")]
    [InlineData("dotnet test -c Release --filter X", "dotnet test")]
    [InlineData("gh pr view 1 --json state,url --jq .x", "gh pr view")]
    [InlineData("git commit -m fix", "git commit")]
    [InlineData("git commit -m \"fix it\"", "git commit")]
    [InlineData("gh pr list --state open", "gh pr list")]
    [InlineData("n=5; gh pr update-branch $n", "gh pr update-branch")]
    [InlineData("git add *", "git add")]
    [InlineData("git add ./*", "git add")]
    [InlineData("git add p?sh", "git add")]
    [InlineData("git push {a,b}", "git push")]
    [InlineData("git push $(cmd)", "git push")]
    [InlineData("r=push; git push \"$r\"", "git push")]
    // `main` follows an option, so it is skipped as that option's value.
    [InlineData("git log --oneline main", "git log")]
    [InlineData("git -p filter-branch --force HEAD", "git filter-branch")]
    // Plain words that do not follow an option are kept.
    [InlineData("git push origin feature-x", "git push origin feature-x")]
    [InlineData("make build", "make build")]
    [InlineData("git worktree add dev", "git worktree add dev")]
    [InlineData("git push -f origin main", "git push main")]
    public void Bash_after_verb_slot_skips_option_values_and_arguments(
        string source,
        string expected)
    {
        Assert.Equal(expected, Words(ParseBash(source).Commands.Last()));
    }

    [Fact]
    public void Bash_loop_body_occurrence_skips_the_loop_value_after_the_slot()
    {
        var result = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        }).Parse("for n in 1 2; do gh pr view $n; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.NotEmpty(result.Commands);
        Assert.All(result.Commands, command => Assert.Equal("gh pr view", Words(command)));
    }

    [Fact]
    public void Documented_limit_sub_subcommand_after_an_option_is_skipped()
    {
        // `add` follows `-v` after the verb slot, so it is skipped as an
        // option value. `evil` and `url` do not follow an option, so they
        // stay. The top-level verb `remote` stays protected.
        AssertBashWords("git remote -v add evil url", "git remote evil url");
    }

    [Theory]
    [InlineData("Get-Process -Name foo", "Get-Process foo")]
    [InlineData("gh --debug auth logout", "gh auth logout")]
    [InlineData("dotnet build -c Release", "dotnet build")]
    [InlineData("git commit -m fix", "git commit")]
    [InlineData("gh pr list --state open", "gh pr list")]
    [InlineData("gh pr view 1 -R o/r", "gh pr view")]
    [InlineData("git add *", "git add")]
    [InlineData("git push $remote", "git push")]
    [InlineData("git push origin feature-x", "git push origin feature-x")]
    [InlineData("Remove-Item x.txt -Force", "Remove-Item")]
    public void PowerShell_position_rule(string source, string expected) =>
        AssertPwshWords(source, expected);

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
    [InlineData("git {a..c}")]
    [InlineData("git $(echo push)")]
    [InlineData("git \"$(cmd)\"")]
    [InlineData("r=push; git \"$r\"")]
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
        // stands in for one. Before the verb slot, an option value must not
        // be a plain word, because a plain word there is kept. After the
        // verb chain, plain option values, digits, and bare globs may appear
        // in any order.
        var prefix = new[]
        {
            new[] { "-R", "o/r" },
            new[] { "--web" },
            new[] { "--json=title" },
            new[] { "-L", "5" },
            new[] { "--repo=o/r" },
            new[] { "-q" },
        };
        var suffix = prefix.Concat(new[]
        {
            new[] { "--state", "open" },
            new[] { "-c", "Release" },
            new[] { "--json", "state,url" },
            new[] { "123" },
            new[] { "*" },
        }).ToArray();
        var random = new Random(197);
        for (var sample = 0; sample < 40; sample++)
        {
            var words = new List<string>();
            foreach (var option in prefix.OrderBy(_ => random.Next()).Take(random.Next(0, 3)))
            {
                words.AddRange(option);
            }

            words.Add("pr");
            words.Add("view");
            foreach (var option in suffix.OrderBy(_ => random.Next()).Take(random.Next(1, suffix.Length + 1)))
            {
                words.AddRange(option);
            }

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
