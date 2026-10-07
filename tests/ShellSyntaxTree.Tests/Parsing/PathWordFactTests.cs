// -----------------------------------------------------------------------
// <copyright file="PathWordFactTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the 0.4.0-beta.11 path-word facts (#206): a tilde program word, the
/// pathname-expansion facts of a glob word, and the script operand of a
/// shell invoker.
/// </summary>
public class PathWordFactTests
{
    private const string Start = "/work";
    private const string Home = "/home/agent";

    private static readonly ShellLaunchEnvironment Launch = new(
        new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["TMPDIR"] = "/tmp/nc",
        },
        new[] { "CDPATH" });

    private static readonly ShellLaunchEnvironment LaunchWithoutHome = new(
        new Dictionary<string, string> { ["TMPDIR"] = "/tmp/nc" },
        new[] { "CDPATH" });

    // ------------------------------------------------------------ tilde program word

    [Theory]
    [InlineData("~/.dotnet/tools/ilspycmd -h", Home + "/.dotnet/tools/ilspycmd")]
    [InlineData("~/bin/tool sub", Home + "/bin/tool sub")]
    [InlineData("~ a", Home + " a")]
    [InlineData("cd /tmp && ~/bin/tool", Home + "/bin/tool")]
    public void Tilde_program_word_expands_from_the_live_launch_home(string source, string expected)
    {
        var parsed = Bash(Launch).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(expected, Words(parsed.Commands.Last()));
    }

    [Fact]
    public void Tilde_program_word_expands_under_isolated_mode()
    {
        var parsed = Bash(Launch, BashInitialStateMode.IsolatedNonInteractive)
            .Parse("~/bin/tool sub");

        Assert.Equal(Home + "/bin/tool sub", Words(parsed.Commands.Single()));
    }

    [Theory]
    [InlineData(null, BashInitialStateMode.FreshNonInteractiveNoStartup)]
    [InlineData("launch", BashInitialStateMode.Unknown)]
    [InlineData("no-home", BashInitialStateMode.FreshNonInteractiveNoStartup)]
    public void Tilde_program_word_stays_unknown_without_a_live_home(
        string? launch,
        BashInitialStateMode mode)
    {
        var environment = launch switch
        {
            "launch" => Launch,
            "no-home" => LaunchWithoutHome,
            _ => null,
        };
        var parsed = Bash(environment, mode).Parse("~/bin/tool sub");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.IsType<ShellCommandWords.Unknown>(parsed.Commands.Single().CommandWords);
    }

    [Theory]
    [InlineData("~user/bin/tool")]
    [InlineData("~+/tool")]
    [InlineData("~user/$TMPDIR/tool")]
    [InlineData("wait -p x; ~/bin/tool")]
    public void Tilde_program_word_with_an_unproved_expansion_does_not_resolve(string source)
    {
        var parsed = Bash(Launch).Parse(source);

        Assert.True(
            parsed.IsUnparseable ||
            parsed.Commands.Last().CommandWords is ShellCommandWords.Unknown);
    }

    [Fact]
    public void Quoted_tilde_program_word_stays_literal()
    {
        var parsed = Bash(Launch).Parse("\"~\"/bin/tool");

        Assert.Equal("~/bin/tool", Words(parsed.Commands.Single()));
    }

    // ------------------------------------------------------------ glob pattern facts

    [Fact]
    public void Tilde_glob_with_a_wildcard_directory_gives_the_covering_directory_and_depth()
    {
        var parsed = Bash(Launch).Parse("ls -d ~/repositories/*/akka*");

        var argument = parsed.Commands.Single().Arguments.Single(a => a.Argument.Kind == ArgKind.Glob);
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(argument.Value);
        Assert.Equal(Home + "/repositories/*/akka*", pattern.Pattern);
        Assert.Equal(Home + "/repositories", pattern.CoveringDirectory);
        var glob = Assert.IsType<ShellGlobExpansion>(pattern.Glob);
        Assert.Equal(2, glob.SegmentDepth);
        Assert.Equal(new[] { "*", "akka*" }, glob.Segments.Select(s => s.Text));
        Assert.All(glob.Segments, s => Assert.True(s.IsPattern));
        Assert.All(glob.Segments, s => Assert.False(s.MayMatchDotEntry));
        Assert.False(glob.MayStartWithDash);

        // The compatibility argument does not change.
        Assert.Null(argument.Argument.Resolved);
        Assert.True(argument.Argument.IsPath);
    }

    [Theory]
    [InlineData("ls src/*/akka*", Start + "/src", 2, false)]
    [InlineData("grep -rn x src/**/*.csproj", Start + "/src", 2, false)]
    [InlineData("ls /usr/lib/llvm-*", "/usr/lib", 1, false)]
    [InlineData("rm /tmp/x/*/*.tmp", "/tmp/x", 2, false)]
    [InlineData("ls ./*", Start, 1, false)]
    [InlineData("ls *.cs", Start, 1, true)]
    [InlineData("ls -d */", Start, 1, true)]
    [InlineData("ls x//y/*", Start + "/x/y", 1, false)]
    [InlineData("cat ~/.netclaw/*/tool-approvals.json", Home + "/.netclaw", 2, false)]
    [InlineData("cd /tmp && ls *.log", "/tmp", 1, true)]
    public void Glob_argument_gives_a_pattern_fact(
        string source,
        string covering,
        int depth,
        bool mayStartWithDash)
    {
        var parsed = Bash(Launch).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var argument = parsed.Commands.Last().Arguments.Single(a => a.Argument.Kind == ArgKind.Glob);
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(argument.Value);
        Assert.Equal(covering, pattern.CoveringDirectory);
        Assert.Equal(depth, pattern.Glob!.SegmentDepth);
        Assert.Equal(mayStartWithDash, pattern.Glob.MayStartWithDash);
    }

    [Fact]
    public void Literal_segment_after_a_pattern_segment_is_part_of_the_depth()
    {
        var pattern = GlobValue("cat ~/.netclaw/*/tool-approvals.json");

        Assert.Equal(new[] { true, false }, pattern.Glob!.Segments.Select(s => s.IsPattern));
        Assert.Equal("tool-approvals.json", pattern.Glob.Segments[1].Text);
    }

    [Theory]
    [InlineData("ls ~/x/.*", true)]
    [InlineData("du -sh ~/repositories/akka.net/.*", true)]
    [InlineData("ls ~/x/*", false)]
    [InlineData("ls ~/x/?", false)]
    [InlineData("ls ~/x/[ab]*", true)]
    public void Dot_fact_follows_the_leading_character_of_the_segment(string source, bool mayMatchDot)
    {
        var pattern = GlobValue(source);

        Assert.Equal(mayMatchDot, pattern.Glob!.Segments.Single().MayMatchDotEntry);
    }

    [Fact]
    public void Trailing_slash_is_kept_in_the_pattern_but_is_not_a_segment()
    {
        var pattern = GlobValue("ls -d src/*/");

        Assert.Equal(Start + "/src/*/", pattern.Pattern);
        Assert.Equal(1, pattern.Glob!.SegmentDepth);
    }

    [Theory]
    [InlineData("ls src/../*")]
    [InlineData("ls ../*")]
    [InlineData("ls */../x")]
    [InlineData("ls */./x")]
    [InlineData("ls *//x")]
    [InlineData("ls \"a*\"/*")]
    [InlineData("ls a\\*/*")]
    [InlineData("ls src/{a,b}/*")]
    [InlineData("ls \"$TMPDIR\"/*")]
    [InlineData("ls $HOME/*")]
    [InlineData("ls ~*")]
    [InlineData("ls ~user/*")]
    [InlineData("ls -- -*")]
    public void Glob_word_with_an_unsupported_part_stays_unresolved(string source)
    {
        var parsed = Bash(Launch).Parse(source);

        if (parsed.IsUnparseable)
        {
            return;
        }

        Assert.All(
            parsed.Commands.SelectMany(c => c.Arguments),
            a => Assert.IsNotType<ShellValueDomain.PathPattern>(a.Value));
    }

    [Fact]
    public void Tilde_after_a_quote_is_a_literal_folder_in_a_glob()
    {
        // Bash keeps a `~` that follows a quote as text (0.4.0-beta.24):
        // `ls ''~/*` lists the folder named `~` in the working directory.
        // Before, the parser left the word unresolved.
        var value = Bash(Launch).Parse("ls ''~/*").Commands.Single().Arguments.Single().Value;

        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(value);
        Assert.EndsWith("/~/*", pattern.Pattern, System.StringComparison.Ordinal);
        Assert.DoesNotContain(BashOracle.Home, pattern.Pattern, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, BashInitialStateMode.FreshNonInteractiveNoStartup, Start)]
    [InlineData("launch", BashInitialStateMode.IsolatedNonInteractive, Start)]
    [InlineData("launch", BashInitialStateMode.Unknown, Start)]
    [InlineData("empty", BashInitialStateMode.FreshNonInteractiveNoStartup, Start)]
    [InlineData("launch", BashInitialStateMode.FreshNonInteractiveNoStartup, null)]
    public void Glob_fact_needs_fresh_mode_live_launch_facts_and_a_start_directory(
        string? launch,
        BashInitialStateMode mode,
        string? workingDirectory)
    {
        var environment = launch switch
        {
            "launch" => Launch,
            "empty" => new ShellLaunchEnvironment(
                new Dictionary<string, string>(),
                Array.Empty<string>()),
            _ => null,
        };
        var parsed = new BashParser(new BashParserOptions
        {
            HomeDirectory = Home,
            WorkingDirectory = workingDirectory,
            InitialStateMode = mode,
            LaunchEnvironment = environment,
        }).Parse("ls src/*.cs");

        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands.Single().Arguments.Single().Value);
    }

    [Fact]
    public void Absolute_glob_does_not_need_a_start_directory()
    {
        var parsed = new BashParser(new BashParserOptions
        {
            HomeDirectory = Home,
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            LaunchEnvironment = Launch,
        }).Parse("ls /usr/lib/llvm-*");

        Assert.IsType<ShellValueDomain.PathPattern>(parsed.Commands.Single().Arguments.Single().Value);
    }

    [Fact]
    public void Tilde_glob_needs_the_launch_home()
    {
        var parsed = Bash(LaunchWithoutHome).Parse("ls ~/x/*");

        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands.Single().Arguments.Single().Value);
    }

    [Fact]
    public void Decoded_bash_c_child_gets_no_glob_fact()
    {
        var parsed = Bash(Launch).Parse("bash -c 'ls ~/x/*'");

        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands.Single().Arguments.Single().Value);
    }

    [Fact]
    public void Glob_after_a_launch_fact_revocation_gets_no_fact()
    {
        var parsed = Bash(Launch).Parse("wait -p x; ls /tmp/*");

        Assert.IsType<ShellValueDomain.Unknown>(parsed.Commands.Last().Arguments.Single().Value);
    }

    [Theory]
    [InlineData("shopt -s dotglob; ls *")]
    [InlineData("shopt -s globstar; ls **")]
    [InlineData("set -f; ls *")]
    [InlineData("set -o noglob; ls *")]
    [InlineData("GLOBIGNORE=x; ls *")]
    [InlineData("source x.sh; ls *")]
    [InlineData("eval x; ls *")]
    [InlineData("BASHOPTS=dotglob ls *")]
    public void Shell_option_change_fails_the_parse(string source)
    {
        Assert.True(Bash(Launch).Parse(source).IsUnparseable);
    }

    [Fact]
    public void Glob_in_a_substitution_gets_a_fact()
    {
        var parsed = Bash(Launch).Parse("echo $(ls ~/*)");

        var inner = parsed.Commands.Single(c => c.ImmediateRole == CommandOccurrenceRole.Substitution);
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(inner.Arguments.Single().Value);
        Assert.Equal(Home, pattern.CoveringDirectory);
    }

    [Fact]
    public void Glob_in_a_loop_body_with_one_directory_keeps_the_fact()
    {
        var parsed = Bash(Launch).Parse("for f in a b; do ls /tmp/*.log; done");

        var body = parsed.Commands.Single(c => c.ImmediateRole == CommandOccurrenceRole.LoopBody);
        Assert.IsType<ShellValueDomain.PathPattern>(body.Arguments.Single().Value);
    }

    [Theory]
    [InlineData("echo hi > /tmp/x/*.log", "/tmp/x", FileRedirectMode.Output)]
    [InlineData("cat < in/*.txt", Start + "/in", FileRedirectMode.Input)]
    [InlineData("ls 2> ~/e*.log", Home, FileRedirectMode.Output)]
    [InlineData("ls >> \"a\"*.log", Start, FileRedirectMode.Append)]
    [InlineData("ls &> *.log", Start, FileRedirectMode.CombinedOutput)]
    public void Glob_redirect_target_gives_a_complete_pattern_target(
        string source,
        string covering,
        FileRedirectMode mode)
    {
        var parsed = Bash(Launch).Parse(source);

        var occurrence = parsed.Commands.Single();
        var redirect = Assert.IsType<FileRedirectAnalysis>(occurrence.Redirects.Single());
        Assert.True(redirect.IsComplete);
        Assert.Equal(mode, redirect.Mode);
        var target = Assert.IsType<ShellValueDomain.PathPattern>(redirect.Target);
        Assert.Equal(covering, target.CoveringDirectory);
        Assert.Equal(1, target.Glob!.SegmentDepth);
        Assert.True(occurrence.IsComplete);
        Assert.IsType<ShellCommandWords.Known>(occurrence.CommandWords);
    }

    [Theory]
    [InlineData("echo hi > /tmp/x/*.log", null)]
    [InlineData("echo hi > ../*.log", "launch")]
    [InlineData("echo hi > \"$TMPDIR\"/*.log", "launch")]
    public void Unproved_glob_redirect_target_keeps_the_occurrence_incomplete(
        string source,
        string? launch)
    {
        var parsed = Bash(launch is null ? null : Launch).Parse(source);

        if (parsed.IsUnparseable)
        {
            return;
        }

        var occurrence = parsed.Commands.Single();
        Assert.False(occurrence.IsComplete);
        Assert.IsType<ShellValueDomain.Unknown>(
            Assert.IsType<FileRedirectAnalysis>(occurrence.Redirects.Single()).Target);
    }

    [Fact]
    public void Finite_projection_slice_keeps_the_glob_fact_in_its_directory()
    {
        const string source = "cd /tmp && ls *.log";
        var parser = Bash(Launch);

        Assert.True(parser.TryProjectFiniteScopes(source, out var projection));
        var ls = projection!.Commands.Single(c => c.Source.StartsWith("ls", StringComparison.Ordinal));
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(
            ls.ScopedOccurrence.Arguments.Single().Value);
        Assert.Equal("/tmp", pattern.CoveringDirectory);
    }

    // ------------------------------------------------------------ script operand

    [Theory]
    [InlineData("bash scripts/audit.sh --repo-root ~/repositories")]
    [InlineData("bash ~/.netclaw/skills/disk-cleanup/scripts/prune.sh --dry-run")]
    [InlineData("bash x.sh -c y")]
    [InlineData("sh x.sh -c y")]
    [InlineData("bash -o pipefail x.sh -c y")]
    [InlineData("bash -eo pipefail x.sh -c y")]
    [InlineData("bash +O extglob x.sh -c y")]
    [InlineData("bash -x ./run.sh --flag ~/out")]
    [InlineData("bash x.sh $(date)")]
    public void Script_operand_ends_the_shell_option_scan(string source)
    {
        var parsed = Bash(Launch).Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var occurrence = parsed.Commands.Last();
        Assert.True(occurrence.IsComplete);
        Assert.Equal(source.Split(' ')[0], occurrence.Clause.Verb.Tokens[0]);
    }

    [Fact]
    public void Script_operand_gives_the_invoker_command_word()
    {
        var parsed = Bash(Launch).Parse("bash scripts/audit.sh --repo-root ~/repositories");

        Assert.Equal("bash", Words(parsed.Commands.Single()));
    }

    [Theory]
    [InlineData("bash -O extglob -c \"$(date)\"")]
    [InlineData("bash --rcfile r -c y")]
    [InlineData("bash --init-file r -c y")]
    [InlineData("bash -O -c y")]
    [InlineData("bash -o x -c y")]
    [InlineData("bash - -c y")]
    [InlineData("bash ~user/x.sh -c y")]
    [InlineData("bash $(printf -- -c) 'touch x'")]
    [InlineData("bash *.sh -c y")]
    [InlineData("bash -eo x -c y")]
    public void Option_value_or_unproved_word_is_not_the_script_operand(string source)
    {
        var parsed = Bash(Launch).Parse(source);

        if (parsed.IsUnparseable)
        {
            return;
        }

        var occurrence = parsed.Commands.Last();
        Assert.False(occurrence.IsComplete);
        Assert.IsType<ShellCommandWords.Unknown>(occurrence.CommandWords);
    }

    [Fact]
    public void Script_operand_rule_does_not_change_bash_c_decoding()
    {
        var parsed = Bash(Launch).Parse("bash -c 'git status'");

        Assert.Equal("git status", Words(parsed.Commands.Single()));
    }

    [Fact]
    public void Script_operand_rule_applies_without_launch_facts()
    {
        var parsed = new BashParser(new BashParserOptions { WorkingDirectory = Start })
            .Parse("bash x.sh -c y");

        Assert.True(parsed.Commands.Single().IsComplete);
    }

    // ------------------------------------------------------------ helpers

    private static BashParser Bash(
        ShellLaunchEnvironment? launch,
        BashInitialStateMode mode = BashInitialStateMode.FreshNonInteractiveNoStartup) =>
        new(new BashParserOptions
        {
            HomeDirectory = Home,
            WorkingDirectory = Start,
            InitialStateMode = mode,
            PublishAuthoredSourceFacts = true,
            LaunchEnvironment = launch,
        });

    private static ShellValueDomain.PathPattern GlobValue(string source)
    {
        var parsed = Bash(Launch).Parse(source);
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var argument = parsed.Commands.Last().Arguments.Single(a => a.Argument.Kind == ArgKind.Glob);
        return Assert.IsType<ShellValueDomain.PathPattern>(argument.Value);
    }

    private static string Words(CommandOccurrence occurrence) =>
        string.Join(" ", Assert.IsType<ShellCommandWords.Known>(occurrence.CommandWords).Words);
}
