// -----------------------------------------------------------------------
// <copyright file="GlobCommandWordsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Bash.Parsing;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Resolving;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class GlobCommandWordsTests
{
    [Theory]
    [InlineData("cmd $HOME/x/*", "cmd")]
    [InlineData("cmd \"$HOME\"/x/*", "cmd")]
    [InlineData("cmd ${ROOT}/*", "cmd")]
    [InlineData("cmd $ROOT*", "cmd")]
    [InlineData("cmd \"$ROOT\"*", "cmd")]
    [InlineData("cmd \"$SPACE\"/*", "cmd")]
    [InlineData("cmd $REL/*", "cmd")]
    [InlineData("cmd $ROOT/* sub", "cmd sub")]
    [InlineData("cmd -x $ROOT/* sub", "cmd sub")]
    [InlineData("cmd action $ROOT/* --flag value", "cmd action")]
    public void A_proved_path_glob_keeps_the_static_command_words(string source, string expected)
    {
        var command = Command(source);
        var words = Assert.IsType<ShellCommandWords.Known>(command.CommandWords);
        Assert.Equal(expected.Split(' '), words.Words);
    }

    [Theory]
    [InlineData("cmd $PREFIX*")]
    [InlineData("cmd \"$PREFIX\"*")]
    [InlineData("cmd *$TAIL/x")]
    [InlineData("cmd $SPACE/*")]
    [InlineData("cmd --x=$SPACE/* sub")]
    [InlineData("cmd --x=$ROOT/* sub")]
    [InlineData("cmd $UNKNOWN/*")]
    [InlineData("cmd $HOME/../*")]
    [InlineData("cmd ${ROOT:-/outside}/*")]
    public void A_glob_without_whole_word_proof_keeps_unknown_command_words(string source)
    {
        var parsed = Parser().Parse(source);
        if (!parsed.IsUnparseable)
        {
            Assert.IsType<ShellCommandWords.Unknown>(parsed.Commands.Single().CommandWords);
        }
    }

    [Fact]
    public void A_bare_glob_can_supply_a_subcommand_without_a_leading_dash()
    {
        var command = Command("cmd $PREFIX*");
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(command.Arguments.Single().Value);
        Assert.Equal("/work/pu*", pattern.Pattern);
        Assert.False(pattern.Glob!.MayStartWithDash);
        Assert.IsType<ShellCommandWords.Unknown>(command.CommandWords);
    }

    [Fact]
    public void An_unproved_directory_on_a_later_visit_removes_the_word_proof()
    {
        var control = Parser().Parse("for item in a b; do cmd $REL/*; done");
        Assert.False(control.IsUnparseable, control.UnparseableReason);
        Assert.IsType<ShellCommandWords.Known>(control.Commands.Single().CommandWords);

        var parsed = Parser().Parse("for item in a b; do cmd $REL/*; cd \"$UNKNOWN\"; done");
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var command = parsed.Commands.Single(c => c.Clause.Verb.Tokens.First() == "cmd");
        Assert.IsType<ShellValueDomain.Unknown>(command.Arguments.Single().Value);
        Assert.IsType<ShellCommandWords.Unknown>(command.CommandWords);
    }

    [Theory]
    [InlineData("path", false, true)]
    [InlineData("bare", false, false)]
    [InlineData("bare", true, false)]
    [InlineData("unknown", false, false)]
    [InlineData("unknown", true, false)]
    public void Every_joined_visit_must_prove_the_original_path_word(
        string otherKind, bool reverse, bool expectedProof)
    {
        var path = Pattern("$ROOT*");
        var other = otherKind switch
        {
            "path" => path with { },
            "bare" => Pattern("$PREFIX*"),
            _ => ShellValueDomainFacts.Unknown,
        };
        var joined = reverse
            ? BashLoopBindingContext.JoinDomains(other, path)
            : BashLoopBindingContext.JoinDomains(path, other);

        Assert.Equal(expectedProof, joined.IsPathGlobWord);
        var words = ShellCommandWordProjection.Project(
            Command("cmd $ROOT*").Clause, true, ShellProjectionLanguage.Bash,
            CommandOccurrenceFacts.EmptyLaunchWordValues,
            new[] { new EffectiveArgumentFacts { ClauseElementIndex = 1, Value = joined } });
        if (expectedProof)
        {
            Assert.Equal(new[] { "cmd" }, Assert.IsType<ShellCommandWords.Known>(words).Words);
        }
        else
        {
            Assert.IsType<ShellCommandWords.Unknown>(words);
        }
    }

    [Fact]
    public void Equal_absolute_patterns_do_not_imply_equal_original_word_shapes()
    {
        var path = Pattern("$ROOT*");
        var bare = Pattern("$PREFIX*");
        Assert.Equal(path.Pattern, bare.Pattern);
        Assert.Equal(path.CoveringDirectory, bare.CoveringDirectory);
        Assert.Equal(path.Glob!.Segments, bare.Glob!.Segments);
        Assert.False(path.Glob.MayStartWithDash);
        Assert.False(bare.Glob.MayStartWithDash);
        Assert.True(path.IsPathGlobWord);
        Assert.False(bare.IsPathGlobWord);
        Assert.False(ShellValueDomainFacts.AreEqual(path, bare));
        Assert.False(ShellValueDomainFacts.AreEqual(bare, path));
    }

    [Fact]
    public void An_option_value_cannot_supply_proof_for_the_whole_option_word()
    {
        var words = ShellCommandWordProjection.Project(
            Command("cmd --x=$ROOT/* sub").Clause, true, ShellProjectionLanguage.Bash,
            CommandOccurrenceFacts.EmptyLaunchWordValues,
            new[] { new EffectiveArgumentFacts { ClauseElementIndex = 1, Value = Pattern("$ROOT*") } });
        Assert.IsType<ShellCommandWords.Unknown>(words);
    }

    [BashTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_path_glob_results_keep_the_separator(bool hasMatches)
    {
        var root = Path.Combine(Path.GetTempPath(), "sst-glob-words-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            if (hasMatches)
            {
                File.WriteAllText(Path.Combine(root, "-flag"), string.Empty);
                File.WriteAllText(Path.Combine(root, "push word"), string.Empty);
            }

            var options = Options() with
            {
                LaunchEnvironment = new ShellLaunchEnvironment(
                    new Dictionary<string, string> { ["ROOT"] = root }, Array.Empty<string>()),
            };
            var parsed = new BashParser(options).Parse("cmd $ROOT/*");
            Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
            Assert.Equal(new[] { "cmd" },
                Assert.IsType<ShellCommandWords.Known>(parsed.Commands.Single().CommandWords).Words);
            var expected = hasMatches
                ? "<" + root + "/-flag>\n<" + root + "/push word>"
                : "<" + root + "/*>";
            BashOracle.AssertShellPrints(new[] { "bash", "--noprofile", "--norc" },
                "printf '<%s>\\n' $ROOT/*", expected,
                new Dictionary<string, string> { ["ROOT"] = root });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [BashTheory]
    [InlineData("pu")]
    public void Native_bare_glob_can_supply_a_subcommand(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), "sst-bare-glob-word-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "push"), string.Empty);
            var parsed = new BashParser(Options() with { WorkingDirectory = root }).Parse("cmd $PREFIX*");
            Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
            Assert.IsType<ShellCommandWords.Unknown>(parsed.Commands.Single().CommandWords);
            var source = "cd '" + root.Replace("'", "'\\''") + "'; printf '<%s>\\n' $PREFIX*";
            BashOracle.AssertShellPrints(new[] { "bash", "--noprofile", "--norc" }, source, "<push>",
                new Dictionary<string, string> { ["PREFIX"] = prefix });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ShellValueDomainFacts Pattern(string source)
    {
        var value = ShellValue.Concat(BashLexer.Tokenize(source)
            .Where(token => token.ResolverValue is not null).Select(token => token.ResolverValue!));
        Assert.True(BashGlobPatternAnalysis.TryAnalyze(value, Options(), "/work", out var pattern));
        return pattern;
    }

    private static CommandOccurrence Command(string source)
    {
        var parsed = Parser().Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        return Assert.Single(parsed.Commands);
    }

    private static BashParser Parser() => new(Options());

    private static BashParserOptions Options() => new()
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        LaunchEnvironment = new ShellLaunchEnvironment(new Dictionary<string, string>
        {
            ["HOME"] = "/home/agent",
            ["ROOT"] = "/work/pu",
            ["REL"] = "src",
            ["PREFIX"] = "pu",
            ["TAIL"] = "a",
            ["SPACE"] = "/work/two words",
        }, Array.Empty<string>()),
    };
}
