// -----------------------------------------------------------------------
// <copyright file="BashExportAndReadTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Pins the fresh-mode variable rules of #221: a read of an unassigned
/// name is an unknown value, a bounded <c>export</c> assigns and marks names,
/// <c>set --</c> sets the positional parameters, and a decoded
/// <c>bash -c</c> child lists only the exported assignments of its parent.
/// </summary>
public class BashExportAndReadTests
{
    private static readonly BashParser Parser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
    });

    private static string Exact(ShellValueDomain value) =>
        Assert.IsType<ShellValueDomain.Exact>(value).Value;

    // ------------------------------------------------------------ unknown reads

    [Theory]
    [InlineData("rm -rf \"$BUILD_DIR/out\"")]
    [InlineData("case \"$unset_name\" in a) echo a;; esac")]
    [InlineData("rm \"$(cat <<EOF\n$unset_name\nEOF\n)\"")]
    [InlineData("x=1; inspect \"$root\"")]
    [InlineData("root=$(cat \"$root\"); inspect \"$root\"")]
    public void Unassigned_name_reads_an_unknown_value(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.All(
            parsed.Commands.SelectMany(c => c.Arguments).Where(a => a.Argument.Raw.Contains('$')),
            argument => Assert.IsType<ShellValueDomain.Unknown>(argument.Value));
    }

    [Theory]
    [InlineData(BashInitialStateMode.Unknown)]
    public void Unknown_read_needs_a_proved_fresh_process(BashInitialStateMode mode)
    {
        var parser = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = mode,
        });

        Assert.True(parser.Parse("rm -rf \"$BUILD_DIR/out\"").IsUnparseable);
    }

    [Theory]
    [InlineData("declare -n ref=other; cat \"$x\"")]
    [InlineData("typeset -a x; cat \"$x\"")]
    [InlineData("local x; cat \"$x\"")]
    [InlineData("unset x; cat \"$x\"")]
    [InlineData("export -n x; cat \"$x\"")]
    [InlineData("export -f x; cat \"$x\"")]
    [InlineData("readonly x; cat \"$x\"")]
    [InlineData("set -e; cat \"$x\"")]
    [InlineData("set -o nounset; cat \"$x\"")]
    [InlineData("cat \"${x:-/fallback}\"")]
    [InlineData("cat \"${!x}\"")]
    [InlineData("cat \"${x[0]}\"")]
    [InlineData("$x arg")]
    [InlineData("\"$x/tool\" arg")]
    public void Variable_attribute_change_or_complex_read_fails_closed(string source)
    {
        // A later read could run code through a nameref or an array
        // subscript, or the read is not a plain `$NAME`.
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    // ------------------------------------------------------------ export

    [Fact]
    public void Export_assignment_binds_the_value_for_later_commands()
    {
        var parsed = Parser.Parse("export REPO_ROOT=/r; bash scripts/build.sh \"$REPO_ROOT/out\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var export = parsed.Commands[0];
        Assert.Empty(export.Assignments);
        var build = parsed.Commands[1];
        var assignment = Assert.Single(build.Assignments);
        Assert.Equal("REPO_ROOT", assignment.Name);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, assignment.Scope);
        Assert.Equal("/r", Exact(assignment.EffectiveValue));
        Assert.Equal("/r/out", Exact(build.Arguments[^1].AuthoredValue));
    }

    [Fact]
    public void Export_operands_assign_from_left_to_right()
    {
        var parsed = Parser.Parse("export A=1 B=\"$A/x\"; inspect item");

        var inspect = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "inspect");
        Assert.Equal("1/x", Exact(inspect.Assignments.Single(a => a.Name == "B").EffectiveValue));
    }

    [Theory]
    [InlineData("export PATH=/other; inspect item")]
    [InlineData("export LD_PRELOAD=/x.so; inspect item")]
    [InlineData("export BASH_ENV=/x; inspect item")]
    [InlineData("export LC_ALL=C; inspect item")]
    [InlineData("export PATH; inspect item")]
    [InlineData("export A=`id`; inspect item")]
    [InlineData("export A[0]=1; inspect item")]
    [InlineData("export A+=1; inspect item")]
    [InlineData("export \"$name\"=1; inspect item")]
    [InlineData("export -n A; inspect item")]
    [InlineData("export -- A=1; inspect item")]
    [InlineData("export A=1 > marker; inspect item")]
    [InlineData("(export A=1); inspect item")]
    [InlineData("bash -c 'export A=1'")]
    [InlineData("for i in a b; do export i=1; done")]
    public void Unbounded_export_fails_closed(string source)
    {
        var parsed = Parser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Theory]
    [InlineData("export A=1 | cat; inspect \"$A\"")]
    [InlineData("cat | export A=1; inspect \"$A\"")]
    [InlineData("export A=1 & inspect \"$A\"")]
    [InlineData("true || export A=1; inspect \"$A\"")]
    public void Export_in_a_subshell_or_on_one_path_does_not_prove_a_later_value(string source)
    {
        // A pipeline stage and a background list run in a subshell. A join
        // keeps a value only when every path assigns it.
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var inspect = parsed.Commands[^1];
        Assert.Empty(inspect.Assignments);
        Assert.IsType<ShellValueDomain.Unknown>(inspect.Arguments.Single().AuthoredValue);
        Assert.IsType<ShellValueDomain.Unknown>(inspect.Arguments.Single().Value);
    }

    [Fact]
    public void Export_query_does_not_change_state()
    {
        var parsed = Parser.Parse("x=1; export -p; inspect item");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal("1", Exact(parsed.Commands[^1].Assignments.Single().EffectiveValue));
    }

    [Fact]
    public void Export_needs_fresh_mode()
    {
        var parser = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        });

        Assert.True(parser.Parse("export A=1; inspect item").IsUnparseable);
    }

    // ------------------------------------------------------------ launch facts

    private static readonly BashParser LaunchParser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        HomeDirectory = "/home/agent",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
        PublishAuthoredSourceFacts = true,
        LaunchEnvironment = new ShellLaunchEnvironment(
            new Dictionary<string, string>
            {
                ["HOME"] = "/home/agent",
                ["TMPDIR"] = "/launch/tmp",
                ["TMP"] = "/launch/tmp",
                ["TEMP"] = "/launch/tmp",
                ["SUB"] = "push",
            },
            new[] { "CDPATH" }),
    });

    [Theory]
    [InlineData("export TMPDIR=/etc && cat \"$TMPDIR/notes.txt\"")]
    [InlineData("TMPDIR=/etc; export TMPDIR; cat \"$TMPDIR/notes.txt\"")]
    [InlineData("export TMPDIR; cat \"$TMPDIR/notes.txt\"")]
    [InlineData("export HOME=/x; cat ~/a \"$HOME/a\"")]
    [InlineData("export CDPATH=/x; cd a && ls")]
    [InlineData("export PATH=/x; ls")]
    [InlineData("export PATH; ls")]
    [InlineData("export LD_PRELOAD=/x.so; ls")]
    [InlineData("export LD_LIBRARY_PATH=/x; ls")]
    [InlineData("export BASH_ENV=/x; ls")]
    [InlineData("export ENV=/x; ls")]
    [InlineData("export SHELLOPTS; ls")]
    [InlineData("export BASHOPTS; ls")]
    [InlineData("export GLOBIGNORE=x; ls")]
    [InlineData("export IFS=:; ls")]
    public void Export_of_a_shell_owned_or_loader_name_fails_closed(string source)
    {
        // These names change how Bash, the loader, or a child shell behaves.
        // They follow the same rule as a PATH= prefix.
        var parsed = LaunchParser.Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
    }

    [Theory]
    [InlineData("export SUB=pull; git \"$SUB\" origin", "pull")]
    [InlineData("export SUB=pull && git \"$SUB\" origin", "pull")]
    [InlineData("SUB=pull; export SUB; git \"$SUB\" origin", "pull")]
    [InlineData("export SUB=pull | cat; git \"$SUB\" origin", null)]
    [InlineData("true || export SUB=pull; git \"$SUB\" origin", null)]
    public void Export_of_a_launch_name_revokes_the_launch_value(string source, string? authored)
    {
        // The launch value never survives an assignment to its name. The
        // new value is the authored value. On a path that may skip the
        // export, the value is unknown.
        var parsed = LaunchParser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var git = parsed.Commands[^1];
        var argument = git.Arguments.Single(a => a.Argument.Raw == "\"$SUB\"");
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        if (authored is null)
        {
            Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredValue);
        }
        else
        {
            Assert.Equal(authored, Exact(argument.AuthoredValue));
        }

        Assert.IsType<ShellCommandWords.Unknown>(git.CommandWords);
    }

    [Theory]
    [InlineData("export TMP=/etc && cat \"$TMP/notes.txt\"")]
    [InlineData("export TEMP=/etc && cat \"$TEMP/notes.txt\"")]
    public void Export_of_a_temporary_directory_name_never_keeps_the_launch_value(string source)
    {
        var parsed = LaunchParser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var argument = parsed.Commands[^1].Arguments.Single();
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        Assert.Equal("/etc/notes.txt", Exact(argument.AuthoredValue));
        Assert.DoesNotContain("/launch/tmp", argument.Argument.Resolved ?? string.Empty);
    }

    [Fact]
    public void Export_of_a_launch_name_without_a_value_keeps_the_launch_value()
    {
        // `export NAME` does not change the value of NAME.
        var parsed = LaunchParser.Parse("export SUB; git \"$SUB\" origin");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(
            "git push origin",
            string.Join(" ", Assert.IsType<ShellCommandWords.Known>(parsed.Commands[^1].CommandWords).Words));
    }

    // ------------------------------------------------------------ child process

    [Theory]
    [InlineData("x='a'; bash -c 'cat foo'", new string[0])]
    [InlineData("x=a; export x; bash -c 'cat foo'", new[] { "x" })]
    [InlineData("export x=a y; y=2; z=3; bash -c 'cat foo'", new[] { "x", "y" })]
    [InlineData("if c; then export x=1; fi; bash -c 'cat foo'", new string[0])]
    [InlineData("x=1; if c; then export x; fi; bash -c 'cat foo'", new string[0])]
    [InlineData("x=1; (cat foo)", new[] { "x" })]
    public void Child_process_lists_only_exported_assignments(string source, string[] expected)
    {
        // A decoded `bash -c` child is a new process. It gets an assignment
        // only when every path to it exports the name. A subshell keeps all
        // of them (#221).
        var parsed = Parser.Parse(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cat = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "cat");
        Assert.Equal(expected, cat.Assignments.Select(a => a.Name));
    }

    [Fact]
    public void Child_process_read_of_a_parent_assignment_is_unknown()
    {
        var parsed = Parser.Parse("x='a'; bash -c 'cat $x'");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var cat = parsed.Commands.Single(c => c.Clause.Verb.Tokens[0] == "cat");
        Assert.IsType<ShellValueDomain.Unknown>(cat.Arguments.Single().Value);
        Assert.IsType<ShellValueDomain.Unknown>(cat.Arguments.Single().AuthoredValue);
    }

    // ------------------------------------------------------------ positional parameters

    [Fact]
    public void Set_positional_then_read_gives_unknown_values()
    {
        var parsed = Parser.Parse("set -- $line; pid=$1; kill \"$pid\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var kill = parsed.Commands[^1];
        Assert.IsType<ShellValueDomain.Unknown>(kill.Assignments.Single(a => a.Name == "pid").EffectiveValue);
        Assert.IsType<ShellValueDomain.Unknown>(kill.Arguments.Single().Value);
    }

    [Theory]
    [InlineData("set -e; inspect item")]
    [InlineData("set -- a > marker; inspect item")]
    [InlineData("set +o posix; inspect item")]
    [InlineData("pid=${1:-x}; inspect item")]
    [InlineData("pid=${1}; inspect item")]
    public void Unbounded_set_or_positional_value_fails_closed(string source)
    {
        Assert.True(Parser.Parse(source).IsUnparseable);
    }

    [Theory]
    [InlineData("export REPO_ROOT=/r; bash scripts/build.sh \"$REPO_ROOT/out\"")]
    [InlineData("set -- $line; pid=$1; kill \"$pid\"")]
    [InlineData("x=a; export x y=2; bash -c 'cat $x'")]
    public void Every_prefix_parses_without_an_exception(string source)
    {
        for (var length = 0; length <= source.Length; length++)
        {
            var prefix = source.Substring(0, length);
            var exception = Record.Exception(() =>
            {
                Parser.Parse(prefix);
                Parser.TryProjectFiniteScopes(prefix, out _);
            });
            Assert.True(exception is null, $"{prefix}: {exception}");
        }
    }
}
