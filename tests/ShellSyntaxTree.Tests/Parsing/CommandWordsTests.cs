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
    // #237: a plain word after a switch is skipped as its value.
    [InlineData("git --no-pager log -1", "git")]
    [InlineData("git show b42bf5a", "git show")]
    [InlineData("git push origin v0.4.0", "git push origin")]
    [InlineData("git push origin feature-x", "git push origin feature-x")]
    [InlineData("pgrep -x name", "pgrep")]
    [InlineData("df -h .", "df")]
    [InlineData("du -sh ./*", "du")]
    [InlineData("ls -la ../x", "ls")]
    [InlineData("echo \"hello world\"", "echo")]
    [InlineData("make build", "make build")]
    [InlineData("git -p filter-branch", "git")]
    [InlineData("git -p filter-branch --force HEAD", "git")]
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
    // #237: in the verb slot too, a plain word after an option is the
    // option's value. Before #237 these kept the word after the option.
    [InlineData("git -p filter-branch", "git")]
    [InlineData("git --no-pager log", "git")]
    [InlineData("git -c x=y push", "git push")]
    [InlineData("gh --debug auth logout", "gh logout")]
    [InlineData("git \"push\"", "git push")]
    [InlineData("git \\push", "git push")]
    [InlineData("pgrep -x name", "pgrep")]
    public void Bash_verb_slot_skips_a_plain_word_after_an_option(
        string source,
        string expected) =>
        AssertBashWords(source, expected);

    // ------------------------------------------------------------ option value in the verb slot (#237)

    [Theory]
    // Owner rule (#237): a plain word directly after an option is that
    // option's value, also in the verb slot. The next plain word is the verb.
    [InlineData("ilspycmd -t Mattermost.MattermostClient /p/x.dll", "ilspycmd")]
    // `x.dll` does not follow an option, so it stays. A consumer can drop a
    // file word.
    [InlineData("ilspycmd -t A.B -o out x.dll", "ilspycmd x.dll")]
    [InlineData("gh -R owner/repo pr view 12", "gh pr view")]
    [InlineData("gh --repo owner/repo pr view", "gh pr view")]
    [InlineData("git -C ~/repo log --oneline", "git log")]
    [InlineData("git -c user.name=x commit -m msg", "git commit")]
    [InlineData("dotnet --verbosity q build Foo.sln", "dotnet build Foo.sln")]
    [InlineData("kubectl -n prod get pods", "kubectl get pods")]
    [InlineData("git -C repo push", "git push")]
    // An attached value is part of the option word, so `sub` is the verb.
    [InlineData("cmd --flag=value sub", "cmd sub")]
    // Bare `--` ends the options and takes no value, so `sub` is the verb.
    [InlineData("cmd -- sub", "cmd sub")]
    [InlineData("git --no-pager -- push origin", "git push origin")]
    // A value or a path after an option does not use up the verb slot.
    [InlineData("cmd -x 5 sub", "cmd sub")]
    [InlineData("cmd -x ./* sub", "cmd sub")]
    // No option before the verb: no change.
    [InlineData("git push origin main", "git push origin main")]
    [InlineData("gh pr view", "gh pr view")]
    [InlineData("ilspycmd x.dll", "ilspycmd x.dll")]
    public void Bash_verb_slot_skips_an_option_value(string source, string expected) =>
        AssertBashWords(source, expected);

    [Theory]
    // Accepted trade-off (#237): the projection does not know which options
    // take a value. A switch without a value, followed by a subcommand,
    // hides that subcommand. The next plain word becomes the verb.
    [InlineData("docker --debug run", "docker")]
    [InlineData("docker --debug run img", "docker img")]
    [InlineData("git --no-pager log -1", "git")]
    [InlineData("git -p filter-branch --force HEAD", "git")]
    [InlineData("gh --debug auth logout", "gh logout")]
    public void Accepted_trade_off_a_switch_hides_the_subcommand_after_it(
        string source,
        string expected) =>
        AssertBashWords(source, expected);

    [Theory]
    [InlineData("ilspycmd -t A.B /p/x.dll", "ilspycmd")]
    [InlineData("gh --repo owner/repo pr view", "gh pr view")]
    [InlineData("kubectl -n prod get pods", "kubectl get pods")]
    [InlineData("Get-Process -Name foo", "Get-Process")]
    [InlineData("cmd -- sub", "cmd sub")]
    // Accepted trade-off, as in Bash.
    [InlineData("docker --debug run", "docker")]
    [InlineData("git --no-pager log -1", "git")]
    [InlineData("gh --debug auth logout", "gh logout")]
    public void PowerShell_verb_slot_skips_an_option_value(string source, string expected) =>
        AssertPwshWords(source, expected);

    [Fact]
    public void Bash_exact_option_binding_skips_its_value_in_the_verb_slot()
    {
        // `"$o"` has one exact value, `-x`, so it is a static option word.
        Assert.Equal("cmd sub", Words(ParseBash("o=-x; cmd \"$o\" value sub").Commands.Last()));
    }

    [Theory]
    // Fail closed: a dynamic word or a bare glob in the verb slot gives
    // Unknown, also when it follows an option.
    [InlineData("cmd -x $v sub")]
    [InlineData("cmd -x \"$v\" sub")]
    [InlineData("cmd -x * sub")]
    [InlineData("cmd $opt value sub")]
    [InlineData("cmd \"$opt\" value sub")]
    [InlineData("o='-x y'; cmd $o sub")]
    public void Bash_dynamic_word_in_the_verb_slot_stays_unknown_after_an_option(string source)
    {
        Assert.IsType<ShellCommandWords.Unknown>(ParseBash(source).Commands.Last().CommandWords);
    }

    [Fact]
    public void Bash_bare_double_dash_after_the_verb_slot_keeps_its_rule()
    {
        // Out of scope for #237: after the verb slot, the word after `--`
        // is still skipped as an option value.
        AssertBashWords("git checkout -- main", "git checkout");
    }

    [Theory]
    [InlineData("git *")]
    [InlineData("git p?sh")]
    [InlineData("git {push,log}")]
    [InlineData("git {push,a/b}")]
    [InlineData("git -p {push,log}")]
    [InlineData("git -p *")]
    [InlineData("r=push; git $r")]
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
    [InlineData("r=$(cmd); git push \"$r\"", "git push")]
    // `main` follows an option, so it is skipped as that option's value.
    [InlineData("git log --oneline main", "git log")]
    [InlineData("git filter-branch --force HEAD", "git filter-branch")]
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
    [InlineData("Get-Process -Name foo", "Get-Process")]
    [InlineData("gh --debug auth logout", "gh logout")]
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
    [InlineData("git -c x=y push", "git push")]
    [InlineData("git --git-dir=. push", "git push")]
    [InlineData("git -C repo push", "git push")]
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
    [InlineData("git $'push'", "git push")]
    [InlineData("$'git' push", "git push")]
    [InlineData("git $'p'ush", "git push")]
    [InlineData("git $'\\x70ush'", "git push")]
    public void Bash_decoded_ansi_c_quote_parses_but_gives_no_words(string source, string decoded)
    {
        // The parser decodes the string (#232). The word projection reads the
        // authored text and treats `$` as an expansion, so the words stay
        // Unknown and no grant can match them.
        var parsed = ParseBashRaw(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = Assert.Single(parsed.Commands);
        Assert.Equal(decoded, string.Join(" ", command.Clause.Elements.Select(e => e.Value)));
        Assert.IsType<ShellCommandWords.Unknown>(command.CommandWords);
    }

    [Theory]
    [InlineData("git $\"push\"")]
    [InlineData("git p$\"ush\"")]
    [InlineData("git $'\\u0070ush'")]
    [InlineData("git $'\\zpush'")]
    public void Bash_locale_quotes_and_undecoded_ansi_c_escapes_fail_closed(string source)
    {
        // The parser cannot prove these values, so it rejects the complete
        // input. No command words can reach a grant.
        Assert.True(ParseBashRaw(source).IsUnparseable);
    }

    [Theory]
    [InlineData("git `push", "git push")]
    [InlineData("git \"push\"", "git push")]
    [InlineData("git 'log'", "git log")]
    [InlineData("git -- push", "git push")]
    [InlineData("git -c x=y push", "git push")]
    public void PowerShell_hiding_attempts_keep_the_real_verb(string source, string expected) =>
        AssertPwshWords(source, expected);

    // ------------------------------------------------------------ expansions

    [Theory]
    [InlineData("git {push,log}")]
    [InlineData("git {push,log} origin")]
    [InlineData("git {a..c}")]
    [InlineData("git $(echo push)")]
    [InlineData("git \"$(cmd)\"")]
    [InlineData("r=push; git $r")]
    [InlineData("r=$(cmd); git \"$r\"")]
    [InlineData("r=push; git \"$r\"*")]
    // An empty value gives no word, as for an empty launch value.
    [InlineData("r=''; git \"$r\"")]
    [InlineData("for s in push pull; do git \"$s\"; done")]
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
    [InlineData("r=push; git \"$r\"", "git push")]
    // #237: the binding word is static, so it is the value of `-p`.
    [InlineData("r=push; git -p \"$r\"", "git")]
    [InlineData("r=push; git -C . \"$r\"", "git push")]
    [InlineData("r=push; git push \"$r\"", "git push push")]
    [InlineData("r=push; git \"$r\"-x origin", "git push-x origin")]
    // The binding word `-p` is an option, so `log` is its value (#237).
    [InlineData("x=-p; git \"$x\" log", "git")]
    [InlineData("x=-p; git \"$x\" -- log", "git log")]
    [InlineData("for s in push pull; do :; done; git \"$s\"", "git pull")]
    [InlineData("r=/tmp; ls \"$r/x\"", "ls")]
    public void Bash_quoted_exact_binding_gives_its_value_as_a_word(string source, string expected)
    {
        // A quoted expansion of a name with one exact value is one word that
        // the program receives (#224). It gets the static-word rules.
        Assert.Equal(expected, Words(ParseBash(source).Commands.Last()));
    }

    [Theory]
    [InlineData("git `cmd`")]
    [InlineData("git push `cmd`")]
    [InlineData("git $((x=1))")]
    [InlineData("git push $((a[1]))")]
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
    [InlineData("Get-Process -Name foo", "Get-Process")]
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
        // stands in for one. Before the verb slot, every option has a value
        // (#237): a switch without a value would take `pr` as its value.
        // After the verb chain, switches, plain option values, digits, and
        // bare globs may appear in any order.
        var prefix = new[]
        {
            new[] { "-R", "o/r" },
            new[] { "--json=title" },
            new[] { "-L", "5" },
            new[] { "--repo=o/r" },
            new[] { "-X", "value" },
        };
        var suffix = prefix.Concat(new[]
        {
            new[] { "--web" },
            new[] { "-q" },
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
