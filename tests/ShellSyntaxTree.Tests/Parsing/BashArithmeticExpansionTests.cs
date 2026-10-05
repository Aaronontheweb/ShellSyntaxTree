// -----------------------------------------------------------------------
// <copyright file="BashArithmeticExpansionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the bounded arithmetic expansion of #227. A <c>$((…))</c> is a
/// value with an unknown value domain. It is never a path or a command word.
/// The grammar rejects every token that can run code or change state, and
/// the state pass proves that each variable read holds an integer, because
/// Bash evaluates the value of a variable as an arithmetic expression too.
/// </summary>
public class BashArithmeticExpansionTests
{
    private static readonly ShellLaunchEnvironment Launch = new(
        new Dictionary<string, string>
        {
            ["TMPDIR"] = "/tmp/nc",
            ["HOME"] = "/home/test",
        },
        new[] { "CDPATH" });

    private static BashParser Parser(
        BashInitialStateMode mode = BashInitialStateMode.FreshNonInteractiveNoStartup) =>
        new(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
            InitialStateMode = mode,
            PublishAuthoredSourceFacts = true,
            LaunchEnvironment = Launch,
        });

    private static ParsedCommand Parse(string source) => Parser().Parse(source);

    // ------------------------------------------------------------ accepted forms

    [Theory]
    [InlineData("echo $((1+2))")]
    [InlineData("echo \"$((1+2))\"")]
    [InlineData("echo \"a$((1))b\"")]
    [InlineData("echo $((\t1\t)) $(( 2))")]
    [InlineData("echo $(( 1 ? 2 : 3 )) $(( 2**10 )) $(( 7 % 3 )) $(( ~0 )) $(( !0 )) $(( -1 - -1 ))")]
    [InlineData("echo $(( 1 << 4 )) $(( 16 >> 2 )) $(( 1 & 3 )) $(( 1 | 2 )) $(( 1 ^ 3 )) $(( 8 / 2 ))")]
    [InlineData("echo $(( 1 < 2 )) $(( 1 > 2 )) $(( 1 <= 2 )) $(( 1 >= 2 )) $(( 1 == 2 )) $(( 1 != 2 ))")]
    [InlineData("echo $(( 1 && 0 )) $(( 1 || 0 )) $(( (1 + 2) * (3 - 4) ))")]
    [InlineData("echo $(( 10#08 + 0x1f + 2#101 + 64#@_ ))")]
    [InlineData("echo $(( $((1+2)) * 3 ))")]
    [InlineData("echo $(( $? + $# + $$ ))")]
    [InlineData("n=5; echo $((n*2)) \"$(( $n * 2 ))\" \"$(( ${n} * 2 ))\"")]
    [InlineData("i=-3; echo $((i*2))")]
    [InlineData("x=$((1+2)); y=$((x*2)); echo \"$y\"")]
    [InlineData("for i in 1 2 3; do echo $((i*10)); done")]
    [InlineData("for i in 1 2; do j=$((i+1)); echo \"$j\"; done")]
    [InlineData("cat file$((1+2)).txt")]
    [InlineData("echo hi > out$((1)).txt")]
    [InlineData("cd $((1)) && ls")]
    [InlineData("export N=$((1+2))")]
    [InlineData("N=$((1+2)) env")]
    [InlineData("printf '%d\\n' $((3*4))")]
    [InlineData("echo $(echo $((1+2)))")]
    [InlineData("echo \"$(echo $((1<<2)))\"")]
    [InlineData("cat <<EOF\n$((1+2))\nEOF")]
    [InlineData("echo '$((x = 1))'")]
    public void Bounded_expansion_parses(string source)
    {
        var parsed = Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
    }

    [Fact]
    public void Expansion_argument_is_data_with_an_unknown_value()
    {
        var echo = Parse("echo $((1+2))").Commands.Single();

        var argument = echo.Arguments.Single();
        Assert.Equal("$((1+2))", argument.Element.Raw);
        Assert.Equal(ArgKind.DynamicSkip, argument.Argument.Kind);
        Assert.False(argument.Argument.IsPath);
        Assert.Null(argument.Argument.Resolved);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
    }

    [Fact]
    public void Expansion_inside_a_path_word_gives_no_path()
    {
        var cat = Parse("cat /work/file$((1+2)).txt").Commands.Single();

        var argument = cat.Arguments.Single();
        Assert.Equal(ArgKind.DynamicSkip, argument.Argument.Kind);
        Assert.Null(argument.Argument.Resolved);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredFileSystemValue);
    }

    [Fact]
    public void Expansion_in_an_option_value_is_not_a_command_word()
    {
        var command = Parse("git log -n \"$((1+2))\"").Commands.Single();

        Assert.Equal(
            "git log",
            string.Join(" ", Assert.IsType<ShellCommandWords.Known>(command.CommandWords).Words));
    }

    [Theory]
    [InlineData("git $((1+2))")]
    [InlineData("git \"$((1+2))\"")]
    [InlineData("echo \"$((1+2))\"")]
    public void Positional_expansion_makes_the_command_words_unknown(string source)
    {
        // The parser does not compute the value, so it cannot give a word,
        // as for a substitution.
        var command = Parse(source).Commands.Single();

        Assert.IsType<ShellCommandWords.Unknown>(command.CommandWords);
    }

    [Fact]
    public void Assignment_from_an_expansion_publishes_an_unknown_value()
    {
        var parsed = Parse("x=$((1+2)); echo \"$x\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var echo = parsed.Commands.Single();
        var assignment = echo.Assignments.Single(a => a.Name == "x");
        Assert.IsType<ShellValueDomain.Unknown>(assignment.AuthoredValue);
        Assert.IsType<ShellValueDomain.Unknown>(assignment.EffectiveValue);
        Assert.IsType<ShellValueDomain.Unknown>(echo.Arguments.Single().Value);
    }

    [Fact]
    public void Substitution_body_with_an_expansion_is_a_substitution_occurrence()
    {
        var parsed = Parse("echo $(echo $((1<<2)))");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(2, parsed.Commands.Count);
        Assert.Contains(parsed.Commands, c => c.ImmediateRole == CommandOccurrenceRole.Substitution);
    }

    [Theory]
    [InlineData(BashInitialStateMode.IsolatedNonInteractive)]
    [InlineData(BashInitialStateMode.FreshNonInteractiveNoStartup)]
    public void Loop_binding_of_integers_is_a_proved_read(BashInitialStateMode mode)
    {
        var parsed = Parser(mode).Parse("for i in 1 2; do echo $((i*10)); done");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
    }

    // ------------------------------------------------------------ rejected forms

    [Theory]
    [InlineData("echo $(( a[1] ))", "subscript")]
    [InlineData("echo $(( a[$(id)] ))", "subscript")]
    [InlineData("echo $(( x = 1 ))", "assignment")]
    [InlineData("echo $(( x += 1 ))", "assignment")]
    [InlineData("echo $(( x -= 1 ))", "assignment")]
    [InlineData("echo $(( x *= 2 ))", "assignment")]
    [InlineData("echo $(( x /= 2 ))", "assignment")]
    [InlineData("echo $(( x %= 2 ))", "assignment")]
    [InlineData("echo $(( x **= 2 ))", "assignment")]
    [InlineData("echo $(( x <<= 2 ))", "assignment")]
    [InlineData("echo $(( x >>= 2 ))", "assignment")]
    [InlineData("echo $(( x &= 2 ))", "assignment")]
    [InlineData("echo $(( x ^= 2 ))", "assignment")]
    [InlineData("echo $(( x |= 2 ))", "assignment")]
    [InlineData("echo $(( x++ ))", "assignment")]
    [InlineData("echo $(( --x ))", "assignment")]
    [InlineData("echo $(( 1 ? y = 2 : 3 ))", "assignment")]
    [InlineData("echo $(( 1, 2 ))", "comma")]
    [InlineData("echo $(( $(date +%s) / 60 ))", "command substitution")]
    [InlineData("age=$(( ($(date +%s) - $(stat -c %Y \"$d\")) / 86400 )); echo \"$age\"", "command substitution")]
    [InlineData("echo $(( `date` ))", "bounded grammar")]
    [InlineData("echo $(( \"1\" + 2 ))", "bounded grammar")]
    [InlineData("echo $(( 1 \\\n + 2 ))", "bounded grammar")]
    [InlineData("echo $(( 1e5 ))", "bounded grammar")]
    [InlineData("echo $(( ${x:-1} ))", "can read only")]
    [InlineData("echo $(( ${#x} ))", "can read only")]
    [InlineData("echo $(( $1 + 1 ))", "can read only")]
    [InlineData("echo $(( $@ ))", "can read only")]
    [InlineData("echo $((1+2)", "unterminated")]
    [InlineData("echo $(( 1 +", "unterminated")]
    [InlineData("echo $((x))", "proved integer")]
    [InlineData("echo $((TMPDIR))", "proved integer")]
    [InlineData("echo $(( RANDOM % 5 ))", "proved integer")]
    [InlineData("i=abc; echo $((i*2))", "proved integer")]
    [InlineData("i=; echo $((i))", "proved integer")]
    [InlineData("read i; echo $((i))", "proved integer")]
    [InlineData("x=$(date +%s); echo $((x))", "proved integer")]
    [InlineData("x=a$((1)); echo $((x))", "proved integer")]
    [InlineData("for i in a b; do echo $((i)); done", "proved integer")]
    [InlineData("for i in *; do echo $((i)); done", "proved integer")]
    [InlineData("for i in 1 2; do echo $((j)); j=$((i+1)); done", "proved integer")]
    [InlineData("x=$((1)); for i in *; do echo $((x)); x=abc; done", "proved integer")]
    [InlineData("for f in $((x)); do echo; done", "this position")]
    [InlineData("$((1+2)) foo", "command-name")]
    [InlineData("a$((1+2)) foo", "command-name")]
    [InlineData("bash -c 'echo $((1+2))'", "decoded command string")]
    [InlineData("((i++))", "arithmetic command")]
    [InlineData("(( 1 + 2 ))", "arithmetic command")]
    [InlineData("((ls))", "arithmetic command")]
    [InlineData("p=/safe; (( p = 0 )); cat \"$p\"", "arithmetic command")]
    [InlineData("echo ok && ((x = 1))", "arithmetic command")]
    [InlineData("let i=1", "execution-bearing builtin")]
    [InlineData("let x", "execution-bearing builtin")]
    [InlineData("echo $[1+2]", "obsolete arithmetic")]
    public void Unbounded_expansion_fails_closed(string source, string reason)
    {
        var parsed = Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Contains(reason, parsed.UnparseableReason!);
    }

    [Fact]
    public void Unknown_mode_rejects_every_variable_read()
    {
        // Without a proved initial state, a name can carry an ambient
        // attribute or value.
        var parsed = Parser(BashInitialStateMode.Unknown)
            .Parse("for i in 1 2; do echo $((i)); done");

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("proved integer", parsed.UnparseableReason!);
    }

    [Fact]
    public void Unknown_mode_accepts_constants()
    {
        var parsed = Parser(BashInitialStateMode.Unknown).Parse("echo $(( 6 * 7 + $? ))");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
    }

    [Fact]
    public void Nesting_beyond_the_limit_fails_closed()
    {
        var source = "echo " + string.Concat(Enumerable.Repeat("$((", 17)) + "1" +
            string.Concat(Enumerable.Repeat("))", 17));

        var parsed = Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("nesting depth", parsed.UnparseableReason!);
    }

    [Theory]
    [InlineData("( (ls) )")]
    [InlineData("((echo hi) )")]
    [InlineData("( (echo a); (echo b) )")]
    [InlineData("( (ls))")]
    [InlineData("( (echo a); (echo b))")]
    public void Nested_subshells_with_a_space_stay_subshells(string source)
    {
        var parsed = Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.All(parsed.Commands, c => Assert.True(c.Clause.IsSubshell));
    }

    [Theory]
    [InlineData("age=$(( (60 * 60) / 2 )); [ \"$age\" -gt 2 ] && echo \"$age\"")]
    [InlineData("for i in 1 2; do j=$(( i * 2 )); echo \"$(( j + $? ))\" > \"out$((i)).txt\"; done")]
    [InlineData("echo $(( a[$(id)] )); echo $(( $(date) )) ((x = 1))")]
    public void Every_prefix_parses_without_an_exception(string source)
    {
        var parser = Parser();
        for (var length = 0; length <= source.Length; length++)
        {
            var prefix = source.Substring(0, length);
            var exception = Record.Exception(() =>
            {
                parser.Parse(prefix);
                parser.TryProjectFiniteScopes(prefix, out _);
            });
            Assert.True(exception is null, $"{prefix}: {exception}");
        }
    }
}
