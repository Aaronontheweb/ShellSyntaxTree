// -----------------------------------------------------------------------
// <copyright file="ResolverProvenanceTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class ResolverProvenanceTests
{
    private static readonly BashParser Bash = new(new BashParserOptions
    {
        HomeDirectory = "/home/test",
        WorkingDirectory = "/work",
    });

    private static readonly PwshParser Pwsh = new(new PwshParserOptions
    {
        HomeDirectory = "C:/Users/user",
        WorkingDirectory = "C:/work",
    });

    [Theory]
    [InlineData("cat \\$HOME", "\\$HOME", "/work/$HOME")]
    [InlineData("cat \"\\$HOME.txt\"", "\"\\$HOME.txt\"", "/work/$HOME.txt")]
    [InlineData("cat \"\"~", "\"\"~", "/work/~")]
    public void Bash_literal_boundaries_block_reconstructed_expansion(
        string source, string raw, string resolved)
    {
        var argument = Assert.Single(Assert.Single(Bash.Parse(source).Clauses).Args);

        Assert.Equal(raw, argument.Raw);
        Assert.Equal(ArgKind.Literal, argument.Kind);
        Assert.True(argument.IsPath);
        Assert.Equal(resolved, argument.Resolved);
    }

    [Fact]
    public void Bash_unquoted_home_with_field_splitting_risk_fails_closed()
    {
        var parser = new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test user",
            WorkingDirectory = "/work",
        });

        var unquoted = Assert.Single(Assert.Single(parser.Parse("cat $HOME").Clauses).Args);
        Assert.Equal(ArgKind.DynamicSkip, unquoted.Kind);
        Assert.False(unquoted.IsPath);

        var quoted = Assert.Single(Assert.Single(parser.Parse("cat \"$HOME\"").Clauses).Args);
        Assert.Equal(ArgKind.Tilde, quoted.Kind);
        Assert.True(quoted.IsPath);
        Assert.Equal("/home/test user", quoted.Resolved);
    }

    [Fact]
    public void Bash_redirect_aggregates_all_adjacent_fragments()
    {
        var redirect = Assert.Single(
            Assert.Single(Bash.Parse("echo ok > \\$HOME\".txt\"").Clauses).Redirects);

        Assert.False(redirect.IsDynamicSkip);
        Assert.Equal("/work/$HOME.txt", redirect.Target);
    }

    [Fact]
    public void Bash_command_substitution_inside_double_quotes_is_opaque()
    {
        var argument = Assert.Single(
            Assert.Single(Bash.Parse("cat \"`printf /etc/passwd`\"").Clauses).Args);

        Assert.Equal(ArgKind.DynamicSkip, argument.Kind);
        Assert.False(argument.IsPath);
        Assert.Null(argument.Resolved);
    }

    [Fact]
    public void Bash_unsupported_ansi_c_quote_is_unparseable()
    {
        var parsed = Bash.Parse("cat $'/etc/passwd'");

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("ANSI-C", parsed.UnparseableReason);
    }

    [Theory]
    [InlineData("echo ok > \"&1\"")]
    [InlineData("echo ok > \\&1")]
    public void Bash_literal_fd_shaped_redirect_target_is_a_file(string source)
    {
        var redirect = Assert.Single(Assert.Single(Bash.Parse(source).Clauses).Redirects);

        Assert.False(redirect.IsDynamicSkip);
        Assert.Equal("/work/&1", redirect.Target);
    }

    [Fact]
    public void Empty_path_value_fails_closed()
    {
        var bashArgument = Assert.Single(Assert.Single(Bash.Parse("cat \"\"").Clauses).Args);
        Assert.Equal(ArgKind.DynamicSkip, bashArgument.Kind);

        var pwshArgument = Assert.Single(
            Assert.Single(Pwsh.Parse("Get-Content \"\"").Clauses).Args);
        Assert.Equal(ArgKind.DynamicSkip, pwshArgument.Kind);
    }

    [Fact]
    public void Bash_empty_inline_native_value_preserves_argument_cardinality()
    {
        var clause = Assert.Single(
            Bash.Parse("curl --data=\"\" https://example.invalid/api").Clauses);

        Assert.Equal(3, clause.Args.Count);
        Assert.Equal("--data", clause.Args[0].Raw);
        Assert.Equal("\"\"", clause.Args[1].Raw);
        Assert.Equal(ArgKind.Literal, clause.Args[1].Kind);
    }

    [Theory]
    [InlineData("Get-Content `$HOME", "`$HOME", "C:/work/$HOME")]
    [InlineData("Get-Content \"`$HOME.txt\"", "\"`$HOME.txt\"", "C:/work/$HOME.txt")]
    public void PowerShell_backtick_escapes_remain_literal(
        string source, string raw, string resolved)
    {
        var argument = Assert.Single(Assert.Single(Pwsh.Parse(source).Clauses).Args);

        Assert.Equal(raw, argument.Raw);
        Assert.Equal(ArgKind.Literal, argument.Kind);
        Assert.True(argument.IsPath);
        Assert.Equal(resolved, argument.Resolved);
    }

    [Fact]
    public void PowerShell_literalpath_abbreviation_suppresses_wildcards()
    {
        var argument = Assert.Single(
            Assert.Single(Pwsh.Parse("Get-Content -LiteralP \"*.txt\"").Clauses).Args,
            candidate => !candidate.IsFlag);

        Assert.Equal(ArgKind.Literal, argument.Kind);
        Assert.True(argument.IsPath);
        Assert.Equal("C:/work/*.txt", argument.Resolved);
    }

    [Fact]
    public void PowerShell_comma_array_semantics_are_quote_sensitive()
    {
        var unquoted = Assert.Single(
            Assert.Single(Pwsh.Parse("Get-Content a,b").Clauses).Args);
        Assert.Equal(ArgKind.DynamicSkip, unquoted.Kind);
        Assert.False(unquoted.IsPath);

        var quoted = Assert.Single(
            Assert.Single(Pwsh.Parse("Get-Content \"a,b\"").Clauses).Args);
        Assert.Equal(ArgKind.Literal, quoted.Kind);
        Assert.True(quoted.IsPath);
        Assert.Equal("C:/work/a,b", quoted.Resolved);

        var nonPath = Assert.Single(
            Assert.Single(Pwsh.Parse("Write-Output a,b").Clauses).Args);
        Assert.Equal(ArgKind.DynamicSkip, nonPath.Kind);
        Assert.False(nonPath.IsPath);
    }

    [Fact]
    public void Merely_cmdlet_shaped_command_does_not_prove_path_semantics()
    {
        var argument = Assert.Single(
            Assert.Single(Pwsh.Parse("Get-Foo -Path FileSystem::C:/safe").Clauses).Args,
            candidate => !candidate.IsFlag);

        Assert.Equal(ArgKind.DynamicSkip, argument.Kind);
        Assert.False(argument.IsPath);
        Assert.Null(argument.Resolved);
    }

    [Theory]
    [InlineData("Get-Content -LiteralPath $HOME[0]")]
    [InlineData("Get-Content -Path:$HOME.Length")]
    public void PowerShell_cmdlet_member_and_index_expressions_fail_closed(string source)
    {
        var argument = Assert.Single(Pwsh.Parse(source).Clauses).Args[1];

        Assert.Equal(ArgKind.DynamicSkip, argument.Kind);
        Assert.False(argument.IsPath);
        Assert.Null(argument.Resolved);
    }

    [Fact]
    public void PowerShell_spaced_native_member_expression_fails_closed()
    {
        var argument = Assert.Single(
            Pwsh.Parse("curl --output $HOME.Length https://example.invalid").Clauses).Args[1];

        Assert.Equal(ArgKind.DynamicSkip, argument.Kind);
        Assert.False(argument.IsPath);
        Assert.Null(argument.Resolved);
    }

    [Fact]
    public void PowerShell_inline_native_member_spelling_is_literal_suffix_text()
    {
        var argument = Assert.Single(
            Pwsh.Parse("curl --output=$HOME.Length https://example.invalid").Clauses).Args[1];

        Assert.Equal(ArgKind.Tilde, argument.Kind);
        Assert.True(argument.IsPath);
        Assert.Equal("C:/Users/user.Length", argument.Resolved);
    }

    [Theory]
    [InlineData("Get-Content -P:\"safe.txt\"")]
    [InlineData("Get-Content -P safe.txt")]
    public void PowerShell_ambiguous_parameter_binding_fails_closed(string source)
    {
        var clause = Assert.Single(Pwsh.Parse(source).Clauses);

        Assert.Contains(clause.Args, argument => argument.Kind == ArgKind.DynamicSkip);
        Assert.DoesNotContain(clause.Args, argument => argument.Resolved == "C:/work/safe.txt");
    }

    [Theory]
    [InlineData("Get-Content Z:\\x")]
    [InlineData("Write-Output ok > Z:\\x")]
    [InlineData("Get-Content C:relative.txt")]
    [InlineData("Write-Output ok > C:relative.txt")]
    public void Unproved_single_letter_psdrive_fails_closed(string source)
    {
        var clause = Assert.Single(Pwsh.Parse(source).Clauses);
        if (clause.Redirects.Count > 0)
        {
            Assert.True(Assert.Single(clause.Redirects).IsDynamicSkip);
            return;
        }

        var argument = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.DynamicSkip, argument.Kind);
        Assert.False(argument.IsPath);
    }

    [Fact]
    public void Inline_quoted_literalpath_is_one_exact_value()
    {
        var clause = Assert.Single(Pwsh.Parse("Get-Content -LiteralPath:\"*.txt\"").Clauses);

        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("-LiteralPath", clause.Args[0].Raw);
        Assert.Equal("\"*.txt\"", clause.Args[1].Raw);
        Assert.Equal(ArgKind.Literal, clause.Args[1].Kind);
        Assert.Equal("C:/work/*.txt", clause.Args[1].Resolved);
    }

    [Fact]
    public void Quoted_suffix_after_variable_is_not_a_member_expression()
    {
        var argument = Assert.Single(
            Assert.Single(Pwsh.Parse("Get-Content -Path $HOME\".Length\"").Clauses).Args,
            candidate => !candidate.IsFlag);

        Assert.Equal(ArgKind.Tilde, argument.Kind);
        Assert.Equal("C:/Users/user.Length", argument.Resolved);
    }

    [Fact]
    public void Opaque_command_identity_does_not_become_literal()
    {
        var bash = Bash.Parse("r$(printf m) -rf /tmp/x");
        Assert.True(bash.IsUnparseable);
        Assert.Empty(bash.Clauses);

        var pwshClause = Assert.Single(Pwsh.Parse(
            "Get-$(Write-Output Content) /etc/passwd").Clauses);
        Assert.True(pwshClause.Verb.IsDynamic);
    }

    [Fact]
    public void PowerShell_redirect_distinguishes_null_sink_from_escaped_literal()
    {
        var sink = Assert.Single(
            Assert.Single(Pwsh.Parse("Write-Output ok > $null").Clauses).Redirects);
        Assert.True(sink.IsDynamicSkip);
        Assert.Equal("$null", sink.Target);

        var literal = Assert.Single(
            Assert.Single(Pwsh.Parse("Write-Output ok > `$null").Clauses).Redirects);
        Assert.False(literal.IsDynamicSkip);
        Assert.Equal("C:/work/$null", literal.Target);
    }
}
