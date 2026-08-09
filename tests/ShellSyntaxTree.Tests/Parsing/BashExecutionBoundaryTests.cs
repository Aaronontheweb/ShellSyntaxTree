// -----------------------------------------------------------------------
// <copyright file="BashExecutionBoundaryTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class BashExecutionBoundaryTests
{
    [Theory]
    [InlineData("eval 'printf unsafe'")]
    [InlineData("source script.sh")]
    [InlineData(". script.sh")]
    [InlineData("trap 'printf unsafe' DEBUG")]
    [InlineData("let 'value=1'")]
    [InlineData("declare value=1")]
    [InlineData("typeset value=1")]
    [InlineData("local value=1")]
    [InlineData("readonly value=1")]
    [InlineData("export value=1")]
    [InlineData("unset value")]
    [InlineData("read value")]
    [InlineData("readarray value")]
    [InlineData("mapfile value")]
    [InlineData("getopts ab value")]
    [InlineData("set -u")]
    [InlineData("printf -v value '%s' data")]
    [InlineData("printf -vvalue '%s' data")]
    public void Execution_bearing_builtin_fails_atomically(string source)
    {
        var result = Parse(source, BashInitialStateMode.IsolatedNonInteractive);

        AssertAtomicFailure(result, "execution-bearing builtin");
    }

    [Theory]
    [InlineData("command -- eval 'printf unsafe'")]
    [InlineData("builtin -- declare value=1")]
    [InlineData("command -p -- builtin -- unset value")]
    [InlineData("builtin command -- printf -v value data")]
    public void Static_dispatch_wrapper_cannot_hide_execution_bearing_builtin(string source)
    {
        var result = Parse(source, BashInitialStateMode.IsolatedNonInteractive);

        AssertAtomicFailure(result, "execution-bearing builtin");
    }

    [Theory]
    [InlineData("printf \"$option\" data")]
    [InlineData("printf -v\"$name\" data")]
    public void Dynamic_printf_option_position_fails_atomically(string source)
    {
        var result = Parse(source, BashInitialStateMode.IsolatedNonInteractive);

        AssertAtomicFailure(result, "execution-bearing builtin");
    }

    [Theory]
    [InlineData("command -z echo ok")]
    [InlineData("builtin -p echo ok")]
    [InlineData("command \"$dispatch\" ok")]
    public void Unsupported_dispatch_grammar_fails_atomically(string source)
    {
        var result = Parse(source, BashInitialStateMode.IsolatedNonInteractive);

        AssertAtomicFailure(result, "dispatch grammar");
    }

    [Theory]
    [InlineData("printf '%s' \"$value\"")]
    [InlineData("printf '%s' \"${value}\"")]
    [InlineData("cat >&$descriptor")]
    [InlineData("cat <<EOF\n${value}\nEOF")]
    public void Unknown_initial_state_rejects_named_parameter_expansion(string source)
    {
        var result = Parse(source, BashInitialStateMode.Unknown);

        AssertAtomicFailure(result, "variable-attribute state");
    }

    [Theory]
    [InlineData("(printf '%s' \"$value\")")]
    [InlineData("printf '%s' \"$(printf '%s' \"$value\")\"")]
    public void Unknown_parameter_state_propagates_into_isolated_execution_regions(
        string source)
    {
        var result = Parse(source, BashInitialStateMode.Unknown);

        AssertAtomicFailure(result, "variable-attribute state");
    }

    [Theory]
    [InlineData("printf '%s' \"$value\"")]
    [InlineData("cat <<EOF\n${value}\nEOF")]
    [InlineData("printf -- \"$format\" data")]
    public void Isolated_initial_state_accepts_fresh_named_parameter_expansion(string source)
    {
        var result = Parse(source, BashInitialStateMode.IsolatedNonInteractive);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.NotEmpty(result.Commands);
    }

    [Theory]
    [InlineData("(printf '%s' \"$value\")")]
    [InlineData("printf '%s' \"$(printf '%s' \"$value\")\"")]
    public void Proved_isolated_parameter_state_is_inherited_by_isolated_execution_regions(
        string source)
    {
        var result = Parse(source, BashInitialStateMode.IsolatedNonInteractive);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.NotEmpty(result.Commands);
    }

    [Theory]
    [InlineData("printf '%s' '$value'")]
    [InlineData("printf '%s' \\$value")]
    [InlineData("printf '%s' \"$1\"")]
    [InlineData("printf '%s' \"$?\"")]
    [InlineData("command -v eval")]
    [InlineData("command -V unset")]
    [InlineData("command")]
    [InlineData("builtin")]
    [InlineData("printf '%s' data")]
    [InlineData("printf -- '%s' data")]
    public void Non_named_or_non_executing_forms_remain_supported_in_unknown_state(string source)
    {
        var result = Parse(source, BashInitialStateMode.Unknown);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
    }

    [Fact]
    public void Nameref_declaration_before_heredoc_expansion_fails_atomically()
    {
        const string source = "declare -a a; declare -n x='a[$(printf NREF >&2)0]'; " +
            "cat <<EOF\n${x}\nEOF";

        var result = Parse(source, BashInitialStateMode.IsolatedNonInteractive);

        AssertAtomicFailure(result, "execution-bearing builtin");
    }

    private static ParsedCommand Parse(string source, BashInitialStateMode initialStateMode) =>
        new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
            InitialStateMode = initialStateMode,
        }).Parse(source);

    private static void AssertAtomicFailure(ParsedCommand result, string reason)
    {
        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains(reason, result.UnparseableReason!);
    }
}
