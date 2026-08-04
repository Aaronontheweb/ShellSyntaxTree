// -----------------------------------------------------------------------
// <copyright file="PwshLexerTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using Xunit;

namespace ShellSyntaxTree.Tests.Lexing;

/// <summary>
/// Unit tests for <see cref="PwshLexer"/> — SPEC.POWERSHELL.md §5
/// tokenization: quoting, backtick escape, parameters, stream redirects,
/// statement separators, comments, opaque regions, <c>--%</c>.
/// </summary>
public class PwshLexerTests
{
    private static PwshToken[] Lex(string input) => PwshLexer.Tokenize(input).ToArray();

    private static PwshToken[] Significant(string input) =>
        PwshLexer.Tokenize(input)
            .Where(t => t.Kind != PwshTokenKind.Whitespace
                && t.Kind != PwshTokenKind.Continuation
                && t.Kind != PwshTokenKind.Comment)
            .ToArray();

    [Fact]
    public void Empty_input_yields_no_tokens()
    {
        Assert.Empty(Lex(""));
    }

    [Fact]
    public void Simple_cmdlet_lexes_as_one_word()
    {
        var tokens = Significant("Get-ChildItem");
        var t = Assert.Single(tokens);
        Assert.Equal(PwshTokenKind.Word, t.Kind);
        Assert.Equal("Get-ChildItem", t.Value);
    }

    [Fact]
    public void Single_quoted_string_preserves_bytes_literally()
    {
        var t = Assert.Single(Significant("'literal $HOME'"));
        Assert.Equal(PwshTokenKind.QuotedString, t.Kind);
        Assert.Equal("literal $HOME", t.Value);
        Assert.True(t.IsSingleQuoted);
    }

    [Fact]
    public void Doubled_single_quote_is_an_escaped_quote()
    {
        var t = Assert.Single(Significant("'it''s'"));
        Assert.Equal("it's", t.Value);
    }

    [Fact]
    public void Double_quoted_string_keeps_var_literal()
    {
        var t = Assert.Single(Significant("\"path $env:TEMP\""));
        Assert.Equal(PwshTokenKind.QuotedString, t.Kind);
        Assert.Equal("path $env:TEMP", t.Value);
        Assert.False(t.IsSingleQuoted);
        Assert.True(t.HasInterpolation);
    }

    [Theory]
    [InlineData("\"Get-$noun\"")]
    [InlineData("\"Get-$(Get-Variable noun)\"")]
    [InlineData("\"Get-$é\"")]
    public void Expandable_string_records_interpolation(string input)
    {
        var t = Assert.Single(Significant(input));
        Assert.True(t.HasInterpolation);
    }

    [Theory]
    [InlineData("\"Get-Date\"")]
    [InlineData("\"Write-Host `$name\"")]
    [InlineData("\"Write-Output $.\"")]
    public void Static_expandable_string_has_no_interpolation(string input)
    {
        var t = Assert.Single(Significant(input));
        Assert.False(t.HasInterpolation);
    }

    [Fact]
    public void Expandable_here_string_records_interpolation()
    {
        var t = Assert.Single(Significant("@\"\nGet-$noun\n\"@"));
        Assert.True(t.IsHereString);
        Assert.True(t.HasInterpolation);
    }

    [Fact]
    public void Expandable_here_string_decodes_backtick_newline_escape()
    {
        var t = Assert.Single(Significant("@\"\nWrite-Output ok`nGet-Date\n\"@"));
        Assert.Equal("Write-Output ok\nGet-Date", t.Value);
    }

    [Fact]
    public void Expandable_here_string_records_unicode_interpolation()
    {
        var t = Assert.Single(Significant("@\"\nGet-$é\n\"@"));
        Assert.True(t.HasInterpolation);
    }

    [Fact]
    public void Expandable_here_string_ignores_literal_dollar_punctuation()
    {
        var t = Assert.Single(Significant("@\"\nWrite-Output $.\n\"@"));
        Assert.False(t.HasInterpolation);
    }

    [Fact]
    public void Double_quote_recognizes_backtick_escapes()
    {
        var t = Assert.Single(Significant("\"a`tb\""));
        Assert.Equal("a\tb", t.Value);
    }

    [Fact]
    public void Double_quote_decodes_unicode_escape()
    {
        var t = Assert.Single(Significant("\"i`u{65}x\""));
        Assert.Equal("iex", t.Value);
    }

    [Fact]
    public void Bare_word_decodes_backtick_whitespace_and_newline_escape()
    {
        var t = Assert.Single(Significant("Write-Output` harmless`nGet-Date"));
        Assert.Equal("Write-Output harmless\nGet-Date", t.Value);
    }

    [Fact]
    public void Bare_word_decodes_unicode_escape()
    {
        var t = Assert.Single(Significant("i`u{65}x"));
        Assert.Equal("iex", t.Value);
    }

    [Theory]
    [InlineData("Remove-Item\vC:\\x")]
    [InlineData("Remove-Item\fC:\\x")]
    [InlineData("Remove-Item\u2003C:\\x")]
    public void PowerShell_inline_whitespace_separates_words(string input)
    {
        var tokens = Significant(input);
        Assert.Equal(2, tokens.Length);
        Assert.Equal("Remove-Item", tokens[0].Value);
        Assert.Equal("C:\\x", tokens[1].Value);
    }

    [Fact]
    public void Nul_character_emits_unparseable_sentinel()
    {
        var token = Assert.Single(Significant("\0"));
        Assert.Equal(PwshTokenKind.UnparseableSentinel, token.Kind);
    }

    [Fact]
    public void Unbalanced_single_quote_emits_sentinel()
    {
        var t = Assert.Single(Significant("'open"));
        Assert.Equal(PwshTokenKind.UnparseableSentinel, t.Kind);
    }

    [Fact]
    public void Parameter_token_is_classified()
    {
        var tokens = Significant("Get-Item -Path foo");
        Assert.Equal(PwshTokenKind.Word, tokens[0].Kind);
        Assert.Equal(PwshTokenKind.Parameter, tokens[1].Kind);
        Assert.Equal("-Path", tokens[1].Value);
        Assert.Equal(PwshTokenKind.Word, tokens[2].Kind);
    }

    [Fact]
    public void Colon_form_parameter_keeps_its_value()
    {
        var tokens = Significant("Get-Item -Path:C:\\logs");
        Assert.Equal(PwshTokenKind.Parameter, tokens[1].Kind);
        Assert.Equal("-Path:C:\\logs", tokens[1].Value);
    }

    [Fact]
    public void Comment_after_empty_colon_value_starts_a_comment()
    {
        var tokens = Significant("iex -Command:#comment");
        Assert.Equal(2, tokens.Length);
        Assert.Equal("-Command:", tokens[1].Value);
    }

    [Fact]
    public void Colon_value_scanner_consumes_complete_unicode_escape()
    {
        var tokens = Significant("iex -Command:Get-`u{44}ate");
        Assert.Equal(2, tokens.Length);
        Assert.Equal("-Command:Get-`u{44}ate", tokens[1].Value);
    }

    [Theory]
    [InlineData("git --work-tree repo", "--work-tree")]
    [InlineData("Get-Thing -Name-Part:value", "-Name-Part:value")]
    [InlineData("git --work-tree=../test", "--work-tree=../test")]
    [InlineData("Get-Help -?", "-?")]
    [InlineData("Get-Foo -Ba?r x", "-Ba?r")]
    public void Hyphenated_parameter_or_native_option_stays_one_token(
        string input, string expected)
    {
        var tokens = Significant(input);
        Assert.Equal(PwshTokenKind.Parameter, tokens[1].Kind);
        Assert.Equal(expected, tokens[1].Value);
    }

    [Fact]
    public void Negative_number_is_a_word_not_a_parameter()
    {
        var tokens = Significant("Start-Sleep -5");
        Assert.Equal(PwshTokenKind.Word, tokens[1].Kind);
        Assert.Equal("-5", tokens[1].Value);
    }

    [Fact]
    public void Bare_dash_is_a_word()
    {
        var tokens = Significant("Set-Location -");
        Assert.Equal(PwshTokenKind.Word, tokens[1].Kind);
        Assert.Equal("-", tokens[1].Value);
    }

    [Theory]
    [InlineData("a && b", "&&")]
    [InlineData("a || b", "||")]
    [InlineData("a | b", "|")]
    [InlineData("a ; b", ";")]
    public void Pipeline_operators_lex(string input, string op)
    {
        var tokens = Significant(input);
        Assert.Contains(tokens, t => t.Kind == PwshTokenKind.Operator && t.OperatorText == op);
    }

    [Theory]
    [InlineData("cmd > out.txt", ">")]
    [InlineData("cmd >> out.txt", ">>")]
    [InlineData("cmd 2> err.txt", "2>")]
    [InlineData("cmd 2>> err.txt", "2>>")]
    [InlineData("cmd 3> v.txt", "3>")]
    [InlineData("cmd *> all.txt", "*>")]
    [InlineData("cmd 2>&1", "2>&1")]
    [InlineData("cmd < in.txt", "<")]
    public void Redirect_operators_lex(string input, string op)
    {
        var tokens = Significant(input);
        Assert.Contains(tokens, t => t.Kind == PwshTokenKind.Operator && t.OperatorText == op);
    }

    [Fact]
    public void Star_glob_is_not_a_redirect()
    {
        var t = Assert.Single(Significant("*.txt"));
        Assert.Equal(PwshTokenKind.Word, t.Kind);
        Assert.Equal("*.txt", t.Value);
    }

    [Fact]
    public void Call_operator_lexes_as_operator()
    {
        var tokens = Significant("& git status");
        Assert.Equal(PwshTokenKind.Operator, tokens[0].Kind);
        Assert.Equal("&", tokens[0].OperatorText);
    }

    [Fact]
    public void Newline_run_is_a_statement_separator()
    {
        var tokens = Lex("a\nb");
        Assert.Contains(tokens, t => t.Kind == PwshTokenKind.Whitespace && t.IsStatementSeparator);
    }

    [Fact]
    public void Backtick_newline_is_a_continuation()
    {
        var tokens = Lex("a `\nb");
        Assert.Contains(tokens, t => t.Kind == PwshTokenKind.Continuation);
    }

    [Fact]
    public void Line_comment_is_dropped_by_significant_filter()
    {
        Assert.Empty(Significant("# just a comment"));
    }

    [Fact]
    public void Block_comment_lexes_as_comment()
    {
        var tokens = Lex("<# block #>");
        Assert.Contains(tokens, t => t.Kind == PwshTokenKind.Comment);
        Assert.DoesNotContain(tokens, t => t.Kind == PwshTokenKind.UnparseableSentinel);
    }

    [Fact]
    public void Unterminated_block_comment_emits_sentinel()
    {
        var tokens = Lex("<# never closed");
        Assert.Contains(tokens, t => t.Kind == PwshTokenKind.UnparseableSentinel);
    }

    [Fact]
    public void Hash_mid_word_is_literal()
    {
        var t = Assert.Single(Significant("abc#def"));
        Assert.Equal(PwshTokenKind.Word, t.Kind);
        Assert.Equal("abc#def", t.Value);
    }

    [Fact]
    public void Script_block_lexes_whole()
    {
        var t = Assert.Single(Significant("{ $_.Length -gt 1mb }"));
        Assert.Equal(PwshTokenKind.ScriptBlock, t.Kind);
        Assert.Equal("{ $_.Length -gt 1mb }", t.Value);
    }

    [Fact]
    public void Subexpression_lexes_whole()
    {
        var t = Assert.Single(Significant("$(Get-Date)"));
        Assert.Equal(PwshTokenKind.Subexpression, t.Kind);
        Assert.Equal("$(Get-Date)", t.Value);
    }

    [Fact]
    public void Array_subexpression_lexes_whole()
    {
        var t = Assert.Single(Significant("@(1,2,3)"));
        Assert.Equal(PwshTokenKind.Subexpression, t.Kind);
    }

    [Fact]
    public void Hash_literal_lexes_whole()
    {
        var t = Assert.Single(Significant("@{ Name = 'x' }"));
        Assert.Equal(PwshTokenKind.Subexpression, t.Kind);
    }

    [Fact]
    public void Splat_lexes_as_splat()
    {
        var tokens = Significant("Copy-Item @params");
        Assert.Equal(PwshTokenKind.Splat, tokens[1].Kind);
        Assert.Equal("@params", tokens[1].Value);
    }

    [Fact]
    public void Stop_parsing_token_swallows_the_line_remainder()
    {
        var tokens = Significant("git --% commit -m \"x\" | foo");
        var stop = Assert.Single(tokens, t => t.Kind == PwshTokenKind.StopParsing);
        Assert.Equal("--% commit -m \"x\" | foo", stop.Value);
    }

    [Fact]
    public void Expandable_here_string_lexes_as_quoted_string()
    {
        var t = Assert.Single(Significant("@\"\nline one\nline two\n\"@"));
        Assert.Equal(PwshTokenKind.QuotedString, t.Kind);
        Assert.True(t.IsHereString);
        Assert.False(t.IsSingleQuoted);
        Assert.Equal("line one\nline two", t.Value);
    }

    [Fact]
    public void Literal_here_string_is_single_quoted()
    {
        var t = Assert.Single(Significant("@'\n$literal\n'@"));
        Assert.True(t.IsHereString);
        Assert.True(t.IsSingleQuoted);
        Assert.Equal("$literal", t.Value);
    }

    [Fact]
    public void Unterminated_here_string_emits_sentinel()
    {
        var tokens = Lex("@\"\nnever closed\n");
        Assert.Contains(tokens, t => t.Kind == PwshTokenKind.UnparseableSentinel);
    }

    [Fact]
    public void Drive_qualified_path_is_one_word()
    {
        var tokens = Significant("Get-Item C:\\Users\\user\\file.txt");
        Assert.Equal("C:\\Users\\user\\file.txt", tokens[1].Value);
    }

    [Fact]
    public void Operators_terminate_a_word_without_whitespace()
    {
        var tokens = Significant("gci|rm");
        Assert.Equal(3, tokens.Length);
        Assert.Equal("gci", tokens[0].Value);
        Assert.Equal("|", tokens[1].OperatorText);
        Assert.Equal("rm", tokens[2].Value);
    }

    [Fact]
    public void Env_variable_is_absorbed_into_a_word()
    {
        var t = Assert.Single(Significant("$env:TEMP"));
        Assert.Equal(PwshTokenKind.Word, t.Kind);
        Assert.Equal("$env:TEMP", t.Value);
    }

    [Fact]
    public void Braced_variable_is_absorbed_into_a_word()
    {
        var t = Assert.Single(Significant("${my var}"));
        Assert.Equal(PwshTokenKind.Word, t.Kind);
        Assert.Equal("${my var}", t.Value);
    }
}
