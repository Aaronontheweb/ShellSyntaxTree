// -----------------------------------------------------------------------
// <copyright file="BashLexerTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using ShellSyntaxTree.Internal.Bash.Lexing;
using Xunit;

namespace ShellSyntaxTree.Tests.Lexing;

/// <summary>
/// Unit tests for <see cref="BashLexer"/>. Tests deliberately compare on
/// the meaningful (non-whitespace) tokens because whitespace is emitted
/// for source fidelity but is filtered by the parser. SPEC §5 is the
/// canonical contract; whenever a test pins a behavior, the rule it
/// pins is called out either inline or by the test name.
/// </summary>
public class BashLexerTests
{
    private static BashToken[] LexNonWs(string input) =>
        BashLexer.Tokenize(input)
            .Where(t => t.Kind != BashTokenKind.Whitespace
                     && t.Kind != BashTokenKind.Continuation)
            .ToArray();

    // ------------------------------------------------------------ basic shapes

    [Fact]
    public void Empty_input_returns_empty_token_list()
    {
        var tokens = BashLexer.Tokenize("");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Single_word_lexes_as_one_word_token()
    {
        var tokens = LexNonWs("git");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.Word, t.Kind);
        Assert.Equal("git", t.Value);
        Assert.Equal(0, t.SourceStart);
        Assert.Equal(3, t.SourceLength);
    }

    [Fact]
    public void Whitespace_separates_words()
    {
        var tokens = LexNonWs("git push");
        Assert.Equal(2, tokens.Length);
        Assert.Equal("git", tokens[0].Value);
        Assert.Equal("push", tokens[1].Value);
    }

    [Fact]
    public void Whitespace_token_is_emitted_between_words()
    {
        var tokens = BashLexer.Tokenize("a b");
        // Expected: Word("a"), Whitespace, Word("b")
        Assert.Equal(3, tokens.Count);
        Assert.Equal(BashTokenKind.Word, tokens[0].Kind);
        Assert.Equal(BashTokenKind.Whitespace, tokens[1].Kind);
        Assert.Equal(BashTokenKind.Word, tokens[2].Kind);
    }

    // ------------------------------------------------------------ operators

    [Theory]
    [InlineData("&&")]
    [InlineData("||")]
    [InlineData(";")]
    [InlineData("|")]
    [InlineData(">")]
    [InlineData(">>")]
    [InlineData("<")]
    [InlineData("2>")]
    [InlineData("2>>")]
    [InlineData("(")]
    [InlineData(")")]
    public void Each_operator_lexes_in_isolation(string op)
    {
        var tokens = LexNonWs(op);
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.Operator, t.Kind);
        Assert.Equal(op, t.OperatorText);
    }

    [Fact]
    public void Operator_boundary_does_not_require_whitespace_and_if()
    {
        // cd /tmp&&ls
        var tokens = LexNonWs("cd /tmp&&ls");
        Assert.Equal(4, tokens.Length);
        Assert.Equal("cd", tokens[0].Value);
        Assert.Equal("/tmp", tokens[1].Value);
        Assert.Equal(BashTokenKind.Operator, tokens[2].Kind);
        Assert.Equal("&&", tokens[2].OperatorText);
        Assert.Equal("ls", tokens[3].Value);
    }

    [Fact]
    public void Operator_boundary_does_not_require_whitespace_pipe()
    {
        var tokens = LexNonWs("a|b");
        Assert.Equal(3, tokens.Length);
        Assert.Equal("a", tokens[0].Value);
        Assert.Equal("|", tokens[1].OperatorText);
        Assert.Equal("b", tokens[2].Value);
    }

    [Fact]
    public void Operator_boundary_does_not_require_whitespace_semicolon()
    {
        var tokens = LexNonWs("a;b");
        Assert.Equal(3, tokens.Length);
        Assert.Equal("a", tokens[0].Value);
        Assert.Equal(";", tokens[1].OperatorText);
        Assert.Equal("b", tokens[2].Value);
    }

    [Fact]
    public void Redirect_operator_attaches_to_filename_word()
    {
        var tokens = LexNonWs("cmd>file");
        Assert.Equal(3, tokens.Length);
        Assert.Equal("cmd", tokens[0].Value);
        Assert.Equal(">", tokens[1].OperatorText);
        Assert.Equal("file", tokens[2].Value);
    }

    [Fact]
    public void Append_redirect_prefers_double_arrow()
    {
        // cmd >> log — must be `>>`, not `>` followed by `>`
        var tokens = LexNonWs("cmd >> log");
        Assert.Equal(3, tokens.Length);
        Assert.Equal(">>", tokens[1].OperatorText);
    }

    [Fact]
    public void Stderr_redirect_2gtgt_prefers_long_form()
    {
        var tokens = LexNonWs("cmd 2>> log");
        Assert.Equal(3, tokens.Length);
        Assert.Equal("2>>", tokens[1].OperatorText);
    }

    [Fact]
    public void Heredoc_dash_is_recognized()
    {
        // cmd <<-EOF\n\tbody\n\tEOF\nrest
        var input = "cmd <<-EOF\n\tbody\n\tEOF\nrest";
        var tokens = LexNonWs(input);
        // Expected meaningful tokens: cmd, <<-, EOF, rest.
        Assert.Equal(4, tokens.Length);
        Assert.Equal("cmd", tokens[0].Value);
        Assert.Equal("<<-", tokens[1].OperatorText);
        Assert.Equal("EOF", tokens[2].Value);
        Assert.Equal("rest", tokens[3].Value);
    }

    // ------------------------------------------------------------ quoting

    [Fact]
    public void Single_quoted_string_strips_delimiters()
    {
        var tokens = LexNonWs("'hello world'");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.QuotedString, t.Kind);
        Assert.Equal("hello world", t.Value);
        Assert.Equal(0, t.SourceStart);
        Assert.Equal("'hello world'".Length, t.SourceLength);
    }

    [Fact]
    public void Single_quoted_preserves_dollar_literally()
    {
        // SPEC §5: single quotes preserve bytes literally.
        var tokens = LexNonWs("'foo$bar'");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.QuotedString, t.Kind);
        Assert.Equal("foo$bar", t.Value);
    }

    [Fact]
    public void Single_quoted_preserves_backslash_literally()
    {
        // No escape processing in single quotes.
        var tokens = LexNonWs(@"'a\nb'");
        var t = Assert.Single(tokens);
        Assert.Equal(@"a\nb", t.Value);
    }

    [Fact]
    public void Double_quoted_string_strips_delimiters()
    {
        var tokens = LexNonWs("\"hello world\"");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.QuotedString, t.Kind);
        Assert.Equal("hello world", t.Value);
    }

    [Fact]
    public void Double_quoted_with_escaped_quote()
    {
        var tokens = LexNonWs("\"hello \\\"world\\\"\"");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.QuotedString, t.Kind);
        Assert.Equal("hello \"world\"", t.Value);
    }

    [Fact]
    public void Double_quoted_with_escaped_backslash()
    {
        var tokens = LexNonWs("\"a\\\\b\"");
        var t = Assert.Single(tokens);
        Assert.Equal("a\\b", t.Value);
    }

    [Fact]
    public void Double_quoted_with_escaped_dollar()
    {
        var tokens = LexNonWs("\"\\$HOME\"");
        var t = Assert.Single(tokens);
        Assert.Equal("$HOME", t.Value);
    }

    [Fact]
    public void Double_quoted_preserves_unexpanded_env_var()
    {
        // SPEC §5: $VAR is recognized but NOT expanded by the lexer.
        var tokens = LexNonWs("\"$HOME\"");
        var t = Assert.Single(tokens);
        Assert.Equal("$HOME", t.Value);
    }

    // ------------------------------------------------------------ words / paths

    [Fact]
    public void Tilde_is_part_of_word()
    {
        var tokens = LexNonWs("~/path");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.Word, t.Kind);
        Assert.Equal("~/path", t.Value);
    }

    [Fact]
    public void Long_flag_is_a_word()
    {
        var tokens = LexNonWs("--force");
        var t = Assert.Single(tokens);
        Assert.Equal("--force", t.Value);
    }

    [Fact]
    public void Absolute_path_with_slashes_is_a_word()
    {
        var tokens = LexNonWs("/etc/foo");
        var t = Assert.Single(tokens);
        Assert.Equal("/etc/foo", t.Value);
    }

    [Fact]
    public void Continuation_collapses_to_whitespace()
    {
        // cmd \\\n foo  -- meaningful tokens are [cmd, foo].
        var tokens = LexNonWs("cmd \\\nfoo");
        Assert.Equal(2, tokens.Length);
        Assert.Equal("cmd", tokens[0].Value);
        Assert.Equal("foo", tokens[1].Value);
    }

    [Fact]
    public void Backslash_escapes_dollar_outside_quotes()
    {
        // SPEC §5 `echo \$HOME` — produces a Word with value $HOME (the
        // backslash is consumed; the parser treats this as Literal).
        var tokens = LexNonWs("echo \\$HOME");
        Assert.Equal(2, tokens.Length);
        Assert.Equal("echo", tokens[0].Value);
        Assert.Equal("$HOME", tokens[1].Value);
        Assert.Equal(BashTokenKind.Word, tokens[1].Kind);
    }

    [Fact]
    public void Backslash_escapes_space_outside_quotes()
    {
        // `cmd\ foo` is a single token "cmd foo".
        var tokens = LexNonWs("cmd\\ foo");
        var t = Assert.Single(tokens);
        Assert.Equal("cmd foo", t.Value);
    }

    [Fact]
    public void Simple_brace_expansion_stays_as_word()
    {
        var tokens = LexNonWs("cmd ${HOME}/path");
        Assert.Equal(2, tokens.Length);
        Assert.Equal("cmd", tokens[0].Value);
        Assert.Equal("${HOME}/path", tokens[1].Value);
        Assert.Equal(BashTokenKind.Word, tokens[1].Kind);
    }

    [Fact]
    public void Bare_dollar_var_stays_as_word()
    {
        var tokens = LexNonWs("echo $HOME");
        Assert.Equal(2, tokens.Length);
        Assert.Equal("$HOME", tokens[1].Value);
    }

    // ------------------------------------------------------------ opaque regions

    [Fact]
    public void Dollar_paren_command_substitution_is_one_opaque_token()
    {
        var tokens = LexNonWs("$(echo foo)");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.OpaqueSubstitution, t.Kind);
        Assert.Equal("$(echo foo)", t.Value);
        Assert.Equal(0, t.SourceStart);
        Assert.Equal("$(echo foo)".Length, t.SourceLength);
    }

    [Fact]
    public void Nested_command_substitutions_are_one_opaque_token()
    {
        var tokens = LexNonWs("$(echo $(date))");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.OpaqueSubstitution, t.Kind);
        Assert.Equal("$(echo $(date))", t.Value);
    }

    [Fact]
    public void Backtick_substitution_is_one_opaque_token()
    {
        var tokens = LexNonWs("`pgrep nginx`");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.OpaqueSubstitution, t.Kind);
        Assert.Equal("`pgrep nginx`", t.Value);
    }

    [Fact]
    public void Mixed_word_and_command_substitution()
    {
        var tokens = LexNonWs("kill $(pgrep -f foo)");
        Assert.Equal(2, tokens.Length);
        Assert.Equal(BashTokenKind.Word, tokens[0].Kind);
        Assert.Equal("kill", tokens[0].Value);
        Assert.Equal(BashTokenKind.OpaqueSubstitution, tokens[1].Kind);
        Assert.Equal("$(pgrep -f foo)", tokens[1].Value);
    }

    [Fact]
    public void Command_substitution_with_quoted_close_paren_is_balanced()
    {
        // The ')' inside the double-quoted string must not close the region.
        var tokens = LexNonWs("$(echo \"a)b\")");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.OpaqueSubstitution, t.Kind);
        Assert.Equal("$(echo \"a)b\")", t.Value);
    }

    // ------------------------------------------------------------ unparseables

    [Fact]
    public void Arithmetic_expansion_emits_unparseable_sentinel()
    {
        var tokens = LexNonWs("$((1 + 2))");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.UnparseableSentinel, t.Kind);
        Assert.Equal("$((1 + 2))", t.Value);
        Assert.Contains("arithmetic", t.UnparseableReason);
    }

    [Fact]
    public void Complex_param_expansion_emits_unparseable_sentinel()
    {
        var tokens = LexNonWs("${PATH//:/\\n}");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.UnparseableSentinel, t.Kind);
        Assert.Contains("complex parameter expansion", t.UnparseableReason);
    }

    [Fact]
    public void Unbalanced_double_quote_emits_unparseable_sentinel()
    {
        var tokens = LexNonWs("cmd \"foo");
        // First a Word("cmd"), then the sentinel.
        Assert.Equal(2, tokens.Length);
        Assert.Equal(BashTokenKind.Word, tokens[0].Kind);
        Assert.Equal(BashTokenKind.UnparseableSentinel, tokens[1].Kind);
        Assert.Contains("unbalanced quote", tokens[1].UnparseableReason);
    }

    [Fact]
    public void Unbalanced_single_quote_emits_unparseable_sentinel()
    {
        var tokens = LexNonWs("cmd 'foo");
        Assert.Equal(2, tokens.Length);
        Assert.Equal(BashTokenKind.UnparseableSentinel, tokens[1].Kind);
        Assert.Contains("unbalanced quote", tokens[1].UnparseableReason);
    }

    [Fact]
    public void Unbalanced_command_substitution_emits_unparseable_sentinel()
    {
        var tokens = LexNonWs("$(echo foo");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.UnparseableSentinel, t.Kind);
        Assert.Contains("unbalanced", t.UnparseableReason);
    }

    [Fact]
    public void Unbalanced_backtick_emits_unparseable_sentinel()
    {
        var tokens = LexNonWs("`pgrep nginx");
        var t = Assert.Single(tokens);
        Assert.Equal(BashTokenKind.UnparseableSentinel, t.Kind);
        Assert.Contains("unbalanced", t.UnparseableReason);
    }

    // ------------------------------------------------------------ heredoc

    [Fact]
    public void Heredoc_body_is_skipped()
    {
        var input = "cmd <<EOF\nbody1\nbody2\nEOF\nrest";
        var tokens = LexNonWs(input);
        // Meaningful tokens: cmd, <<, EOF, rest.
        Assert.Equal(4, tokens.Length);
        Assert.Equal("cmd", tokens[0].Value);
        Assert.Equal(BashTokenKind.Operator, tokens[1].Kind);
        Assert.Equal("<<", tokens[1].OperatorText);
        Assert.Equal("EOF", tokens[2].Value);
        Assert.Equal("rest", tokens[3].Value);
    }

    [Fact]
    public void Heredoc_unterminated_emits_unparseable_sentinel()
    {
        // No closing EOF line.
        var input = "cmd <<EOF\nbody1\nbody2";
        var tokens = LexNonWs(input);
        // Expected: Word("cmd"), <<, Word("EOF"), UnparseableSentinel.
        Assert.Equal(BashTokenKind.UnparseableSentinel, tokens[^1].Kind);
        Assert.Contains("not terminated", tokens[^1].UnparseableReason);
    }

    [Fact]
    public void Heredoc_clause_separator_is_preserved()
    {
        // After the body is skipped, the parser still needs to see a
        // clause boundary between `cmd` and `rest`. The lexer guarantees
        // a Whitespace token covers the newline after the EOF terminator.
        var input = "cmd <<EOF\nbody\nEOF\nrest";
        var tokens = BashLexer.Tokenize(input);
        // There must be at least one Whitespace token between the EOF
        // delimiter word and the `rest` word.
        var eofIdx = -1;
        var restIdx = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == BashTokenKind.Word && tokens[i].Value == "EOF") eofIdx = i;
            if (tokens[i].Kind == BashTokenKind.Word && tokens[i].Value == "rest") restIdx = i;
        }

        Assert.True(eofIdx >= 0);
        Assert.True(restIdx > eofIdx);
        var hasSeparator = false;
        for (var i = eofIdx + 1; i < restIdx; i++)
        {
            if (tokens[i].Kind == BashTokenKind.Whitespace) { hasSeparator = true; break; }
        }

        Assert.True(hasSeparator, "expected a Whitespace token between heredoc terminator and following clause");
    }

    // ------------------------------------------------------------ compound clause

    [Fact]
    public void Compound_clause_lexes_all_pieces()
    {
        // cd /target && cmd file.txt
        var tokens = LexNonWs("cd /target && cmd file.txt");
        Assert.Equal(5, tokens.Length);
        Assert.Equal("cd", tokens[0].Value);
        Assert.Equal("/target", tokens[1].Value);
        Assert.Equal("&&", tokens[2].OperatorText);
        Assert.Equal("cmd", tokens[3].Value);
        Assert.Equal("file.txt", tokens[4].Value);
    }

    [Fact]
    public void Subshell_parens_are_individual_operators()
    {
        var tokens = LexNonWs("(cd /tmp && ls)");
        // ( cd /tmp && ls )
        Assert.Equal(6, tokens.Length);
        Assert.Equal("(", tokens[0].OperatorText);
        Assert.Equal("cd", tokens[1].Value);
        Assert.Equal("/tmp", tokens[2].Value);
        Assert.Equal("&&", tokens[3].OperatorText);
        Assert.Equal("ls", tokens[4].Value);
        Assert.Equal(")", tokens[5].OperatorText);
    }

    [Fact]
    public void Pipe_with_quoted_argument()
    {
        var tokens = LexNonWs("grep \"foo bar\" | wc -l");
        Assert.Equal(5, tokens.Length);
        Assert.Equal("grep", tokens[0].Value);
        Assert.Equal(BashTokenKind.QuotedString, tokens[1].Kind);
        Assert.Equal("foo bar", tokens[1].Value);
        Assert.Equal("|", tokens[2].OperatorText);
        Assert.Equal("wc", tokens[3].Value);
        Assert.Equal("-l", tokens[4].Value);
    }

    // ------------------------------------------------------------ source positions

    [Fact]
    public void Source_positions_are_populated_for_every_token()
    {
        const string input = "cd /tmp && ls";
        var tokens = BashLexer.Tokenize(input);
        foreach (var t in tokens)
        {
            Assert.True(t.SourceStart >= 0);
            Assert.True(t.SourceStart + t.SourceLength <= input.Length);
        }

        // First Word starts at 0.
        var firstWord = tokens.First(t => t.Kind == BashTokenKind.Word);
        Assert.Equal(0, firstWord.SourceStart);

        // The `&&` operator starts where the source `&&` is.
        var op = tokens.First(t => t.Kind == BashTokenKind.Operator);
        Assert.Equal(input.IndexOf("&&"), op.SourceStart);
        Assert.Equal(2, op.SourceLength);
    }

    [Fact]
    public void Word_source_length_includes_escape_sequence()
    {
        // The escape-collapsed Value is shorter than the source slice.
        const string input = "\\$HOME";
        var tokens = LexNonWs(input);
        var t = Assert.Single(tokens);
        Assert.Equal("$HOME", t.Value);
        Assert.Equal(0, t.SourceStart);
        Assert.Equal(input.Length, t.SourceLength);
    }

    [Fact]
    public void Operator_text_is_set_exactly_for_each_kind()
    {
        // Sweep all operators in one input to lock the OperatorText shape.
        var tokens = LexNonWs("a&&b||c;d|e>f>>g<h2>i2>>j(k)");
        var ops = tokens.Where(t => t.Kind == BashTokenKind.Operator)
            .Select(t => t.OperatorText).ToArray();
        Assert.Equal(
            new[] { "&&", "||", ";", "|", ">", ">>", "<", "2>", "2>>", "(", ")" },
            ops);
    }

    // ------------------------------------------------------------ misc

    [Fact]
    public void Multiple_redirects_lex_independently()
    {
        var tokens = LexNonWs("cmd > out 2> err");
        Assert.Equal(5, tokens.Length);
        Assert.Equal("cmd", tokens[0].Value);
        Assert.Equal(">", tokens[1].OperatorText);
        Assert.Equal("out", tokens[2].Value);
        Assert.Equal("2>", tokens[3].OperatorText);
        Assert.Equal("err", tokens[4].Value);
    }

    [Fact]
    public void Empty_quoted_strings_are_preserved()
    {
        var tokens = LexNonWs("cmd '' \"\"");
        Assert.Equal(3, tokens.Length);
        Assert.Equal(BashTokenKind.Word, tokens[0].Kind);
        Assert.Equal(BashTokenKind.QuotedString, tokens[1].Kind);
        Assert.Equal("", tokens[1].Value);
        Assert.Equal(BashTokenKind.QuotedString, tokens[2].Kind);
        Assert.Equal("", tokens[2].Value);
    }

    [Fact]
    public void Adjacent_word_and_quoted_string_lex_separately()
    {
        // bash actually concatenates these into one argument, but the
        // lexer emits them as two adjacent tokens — concatenation is the
        // parser's job. This test pins the lexer behavior.
        var tokens = LexNonWs("cmd foo'bar'");
        Assert.Equal(3, tokens.Length);
        Assert.Equal(BashTokenKind.Word, tokens[0].Kind);
        Assert.Equal(BashTokenKind.Word, tokens[1].Kind);
        Assert.Equal("foo", tokens[1].Value);
        Assert.Equal(BashTokenKind.QuotedString, tokens[2].Kind);
        Assert.Equal("bar", tokens[2].Value);
    }

    [Fact]
    public void Tokenize_throws_on_null_input()
    {
        Assert.Throws<System.ArgumentNullException>(() => BashLexer.Tokenize(null!));
    }
}
