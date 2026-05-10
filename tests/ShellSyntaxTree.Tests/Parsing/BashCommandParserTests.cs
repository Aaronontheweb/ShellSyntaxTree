// -----------------------------------------------------------------------
// <copyright file="BashCommandParserTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Unit tests for the PR 3 BashCommandParser core. These focus on the
/// shape that the parser produces — verb chains, args, redirects,
/// compound splitting, and SPEC §11 anomaly safe-fail. Path classification
/// (PR 4) and cd-attribution / subshell-flagging (PR 5) are intentionally
/// out of scope for this test file.
/// </summary>
public class BashCommandParserTests
{
    private static ParsedCommand Parse(string input)
    {
        var parser = new BashParser();
        return parser.Parse(input);
    }

    // ---------------- Single-clause: simple ----------------

    [Fact]
    public void Single_word_verb()
    {
        var result = Parse("ls");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(CompoundOperator.None, clause.Operator);
        Assert.Equal(new[] { "ls" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
        Assert.Empty(clause.Redirects);
    }

    [Fact]
    public void Single_verb_with_flag_and_path_arg()
    {
        var result = Parse("ls -la /tmp");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "ls" }, clause.Verb.Tokens);
        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("-la", clause.Args[0].Raw);
        Assert.True(clause.Args[0].IsFlag);
        Assert.Equal("/tmp", clause.Args[1].Raw);
        Assert.False(clause.Args[1].IsFlag);
    }

    [Fact]
    public void Mkdir_with_default_arity_one()
    {
        var result = Parse("mkdir /tmp");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "mkdir" }, clause.Verb.Tokens);
        Assert.Equal(new[] { "/tmp" }, clause.Args.Select(a => a.Raw).ToArray());
    }

    [Fact]
    public void Echo_with_quoted_arg_preserves_source_raw()
    {
        var result = Parse("echo \"hello world\"");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "echo" }, clause.Verb.Tokens);
        // Raw preserves the surrounding quotes; resolved/value strips them.
        Assert.Equal("\"hello world\"", clause.Args[0].Raw);
    }

    [Fact]
    public void Single_quoted_arg_preserves_quotes_in_raw()
    {
        var result = Parse("echo 'hello world'");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal("'hello world'", clause.Args[0].Raw);
    }

    // ---------------- Multi-token verb chains ----------------

    [Fact]
    public void Two_token_verb_git_push()
    {
        var result = Parse("git push origin main");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "git", "push" }, clause.Verb.Tokens);
        Assert.Equal(new[] { "origin", "main" }, clause.Args.Select(a => a.Raw).ToArray());
    }

    [Fact]
    public void Two_token_verb_dotnet_test()
    {
        var result = Parse("dotnet test");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "dotnet", "test" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
    }

    [Fact]
    public void Three_token_verb_docker_compose_up()
    {
        var result = Parse("docker compose up nginx");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "docker", "compose", "up" }, clause.Verb.Tokens);
        Assert.Equal(new[] { "nginx" }, clause.Args.Select(a => a.Raw).ToArray());
    }

    [Fact]
    public void Three_token_verb_bun_run_my_script()
    {
        var result = Parse("bun run my-script");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "bun", "run", "my-script" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
    }

    [Fact]
    public void Two_token_verb_docker_run()
    {
        var result = Parse("docker run nginx");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "docker", "run" }, clause.Verb.Tokens);
        Assert.Equal(new[] { "nginx" }, clause.Args.Select(a => a.Raw).ToArray());
    }

    [Fact]
    public void Verb_chain_capped_by_available_tokens()
    {
        // "git" alone (no second token) — chain must collapse to 1.
        var result = Parse("git");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "git" }, clause.Verb.Tokens);
    }

    [Fact]
    public void Default_arity_when_verb_unknown()
    {
        var result = Parse("totally-unknown-verb foo bar");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "totally-unknown-verb" }, clause.Verb.Tokens);
        Assert.Equal(new[] { "foo", "bar" }, clause.Args.Select(a => a.Raw).ToArray());
    }

    [Fact]
    public void Verb_lookup_is_case_insensitive()
    {
        var result = Parse("GIT push");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "GIT", "push" }, clause.Verb.Tokens);
    }

    // ---------------- Compound operators ----------------

    [Fact]
    public void Compound_AndIf_two_clauses()
    {
        var result = Parse("ls && pwd");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(new[] { "ls" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.Equal(new[] { "pwd" }, result.Clauses[1].Verb.Tokens);
    }

    [Fact]
    public void Compound_OrIf_two_clauses()
    {
        var result = Parse("ls || echo nope");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.OrIf, result.Clauses[1].Operator);
    }

    [Fact]
    public void Compound_Sequence_two_clauses()
    {
        var result = Parse("ls ; pwd");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[1].Operator);
    }

    [Fact]
    public void Compound_Pipe_two_clauses()
    {
        var result = Parse("ls | grep foo");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.Pipe, result.Clauses[1].Operator);
        Assert.Equal(new[] { "grep" }, result.Clauses[1].Verb.Tokens);
    }

    [Fact]
    public void Compound_mixed_operators()
    {
        var result = Parse("cmd1 && cmd2 || cmd3");
        Assert.False(result.IsUnparseable);
        Assert.Equal(3, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.Equal(CompoundOperator.OrIf, result.Clauses[2].Operator);
    }

    [Fact]
    public void Compound_no_whitespace_around_operator()
    {
        // The lexer must split `cd /tmp&&ls` per SPEC §5.
        var result = Parse("cd /tmp&&ls");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "cd" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(new[] { "ls" }, result.Clauses[1].Verb.Tokens);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
    }

    [Fact]
    public void Compound_three_clauses_all_AndIf()
    {
        var result = Parse("a && b && c");
        Assert.Equal(3, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[2].Operator);
    }

    // ---------------- Redirects ----------------

    [Fact]
    public void Redirect_Out()
    {
        var result = Parse("cmd > /tmp/out");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.Out, redirect.Direction);
        Assert.Equal("/tmp/out", redirect.Target);
        Assert.False(redirect.IsDynamicSkip);
    }

    [Fact]
    public void Redirect_Append()
    {
        var result = Parse("cmd >> log");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.Append, redirect.Direction);
        Assert.Equal("log", redirect.Target);
    }

    [Fact]
    public void Redirect_In()
    {
        var result = Parse("cmd < input");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.In, redirect.Direction);
        Assert.Equal("input", redirect.Target);
    }

    [Fact]
    public void Redirect_ErrOut()
    {
        var result = Parse("cmd 2> err");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.ErrOut, redirect.Direction);
        Assert.Equal("err", redirect.Target);
    }

    [Fact]
    public void Redirect_ErrAppend()
    {
        var result = Parse("cmd 2>> err");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.ErrAppend, redirect.Direction);
        Assert.Equal("err", redirect.Target);
    }

    [Fact]
    public void Multiple_redirects_on_one_clause()
    {
        var result = Parse("cmd > out 2> err");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(2, clause.Redirects.Count);
        Assert.Equal(RedirectDirection.Out, clause.Redirects[0].Direction);
        Assert.Equal("out", clause.Redirects[0].Target);
        Assert.Equal(RedirectDirection.ErrOut, clause.Redirects[1].Direction);
        Assert.Equal("err", clause.Redirects[1].Target);
    }

    [Fact]
    public void Redirect_target_is_dynamic_when_opaque_substitution()
    {
        var result = Parse("cmd > $(date +log)");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.True(redirect.IsDynamicSkip);
    }

    [Fact]
    public void Pipe_to_grep_with_output_redirect_on_last_clause()
    {
        var result = Parse("cmd1 | cmd2 > /tmp/out");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Empty(result.Clauses[0].Redirects);
        var lastRedirect = Assert.Single(result.Clauses[1].Redirects);
        Assert.Equal("/tmp/out", lastRedirect.Target);
    }

    // ---------------- OpaqueSubstitution → DynamicSkip ----------------

    [Fact]
    public void Command_substitution_becomes_DynamicSkip_arg()
    {
        var result = Parse("rm $(find /tmp)");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "rm" }, clause.Verb.Tokens);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.DynamicSkip, arg.Kind);
        Assert.False(arg.IsPath);
        Assert.Null(arg.Resolved);
    }

    [Fact]
    public void Backtick_substitution_becomes_DynamicSkip_arg()
    {
        var result = Parse("rm `find /tmp`");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.DynamicSkip, arg.Kind);
    }

    // ---------------- Unparseable ----------------

    [Fact]
    public void Arithmetic_expansion_marks_outer_unparseable()
    {
        var result = Parse("echo $((1+2))");
        Assert.True(result.IsUnparseable);
        Assert.NotNull(result.UnparseableReason);
        Assert.Contains("arithmetic", result.UnparseableReason!);
    }

    [Fact]
    public void Complex_param_expansion_marks_outer_unparseable()
    {
        var result = Parse("echo ${PATH//:/\\n}");
        Assert.True(result.IsUnparseable);
        Assert.NotNull(result.UnparseableReason);
        Assert.Contains("complex parameter expansion", result.UnparseableReason!);
    }

    [Fact]
    public void Unbalanced_double_quote_marks_outer_unparseable()
    {
        var result = Parse("cmd \"unbalanced");
        Assert.True(result.IsUnparseable);
        Assert.Contains("unbalanced quote", result.UnparseableReason!);
    }

    [Fact]
    public void Unbalanced_single_quote_marks_outer_unparseable()
    {
        var result = Parse("cmd 'unbalanced");
        Assert.True(result.IsUnparseable);
    }

    [Fact]
    public void Unbalanced_open_paren_marks_outer_unparseable()
    {
        var result = Parse("(cmd1 && cmd2");
        Assert.True(result.IsUnparseable);
        Assert.Contains("unbalanced parens", result.UnparseableReason!);
    }

    [Fact]
    public void Unbalanced_close_paren_marks_outer_unparseable()
    {
        var result = Parse("cmd1 && cmd2)");
        Assert.True(result.IsUnparseable);
        Assert.Contains("unbalanced parens", result.UnparseableReason!);
    }

    [Fact]
    public void Control_flow_for_keyword_marks_outer_unparseable()
    {
        var result = Parse("for i in 1 2 3; do echo $i; done");
        Assert.True(result.IsUnparseable);
        Assert.Contains("'for'", result.UnparseableReason!);
    }

    [Fact]
    public void Control_flow_while_keyword_marks_outer_unparseable()
    {
        var result = Parse("while true; do echo hi; done");
        Assert.True(result.IsUnparseable);
        Assert.Contains("'while'", result.UnparseableReason!);
    }

    [Fact]
    public void Function_definition_marks_outer_unparseable()
    {
        var result = Parse("name() { echo hi; }");
        Assert.True(result.IsUnparseable);
        Assert.Contains("function definition", result.UnparseableReason!);
    }

    [Fact]
    public void Process_substitution_input_marks_outer_unparseable()
    {
        var result = Parse("cmd <(other)");
        Assert.True(result.IsUnparseable);
        Assert.Contains("process substitution", result.UnparseableReason!);
    }

    [Fact]
    public void Process_substitution_output_marks_outer_unparseable()
    {
        var result = Parse("tee >(other)");
        Assert.True(result.IsUnparseable);
        Assert.Contains("process substitution", result.UnparseableReason!);
    }

    // ---------------- bash -c framework ----------------

    [Fact]
    public void Bash_c_treated_as_single_clause_in_pr3()
    {
        // PR 3: framework only — no recursion into the inner string. The
        // outer clause is verb=[bash] with `-c` flag and a quoted-string
        // arg. PR 5 will surface the inner clauses.
        var result = Parse("bash -c \"cd /a && cmd\"");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "bash" }, clause.Verb.Tokens);
        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("-c", clause.Args[0].Raw);
        Assert.True(clause.Args[0].IsFlag);
        // The inner string is preserved with quotes in Raw.
        Assert.Equal("\"cd /a && cmd\"", clause.Args[1].Raw);
        Assert.False(clause.IsBashCWrapped);
    }

    // ---------------- Empty / whitespace ----------------

    [Fact]
    public void Empty_input_returns_empty_clauses()
    {
        var result = Parse("");
        Assert.Equal("", result.Source);
        Assert.Empty(result.Clauses);
        Assert.False(result.IsUnparseable);
    }

    [Fact]
    public void Whitespace_only_input_returns_empty_clauses()
    {
        var result = Parse("   \t  ");
        Assert.Empty(result.Clauses);
        Assert.False(result.IsUnparseable);
    }

    // ---------------- Subshell framework ----------------

    [Fact]
    public void Subshell_inner_clauses_surface_inline()
    {
        // PR 3: subshell parens are recognized; inner clauses are
        // surfaced inline with IsSubshell=false (PR 5 will set the flag
        // and add cd-attribution semantics).
        var result = Parse("(cmd1 && cmd2)");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "cmd1" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(new[] { "cmd2" }, result.Clauses[1].Verb.Tokens);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.False(result.Clauses[0].IsSubshell);
        Assert.False(result.Clauses[1].IsSubshell);
    }

    // ---------------- Quoted args round-trip ----------------

    [Fact]
    public void Quoted_path_arg_keeps_raw_quotes()
    {
        var result = Parse("ls \"/path with space\"");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal("\"/path with space\"", clause.Args[0].Raw);
    }

    // ---------------- Equals-form flag splitting ----------------

    [Fact]
    public void Equals_form_flag_splits_into_two_args()
    {
        var result = Parse("curl --output=file.txt http://example.com");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "curl" }, clause.Verb.Tokens);
        Assert.Equal(3, clause.Args.Count);
        Assert.Equal("--output", clause.Args[0].Raw);
        Assert.Equal("file.txt", clause.Args[1].Raw);
        Assert.Equal("http://example.com", clause.Args[2].Raw);
    }

    [Fact]
    public void Equals_form_only_splits_when_starts_with_dash()
    {
        // KEY=value (no leading -) stays a single arg.
        var result = Parse("env KEY=value cmd");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "env" }, clause.Verb.Tokens);
        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("KEY=value", clause.Args[0].Raw);
        Assert.Equal("cmd", clause.Args[1].Raw);
    }

    // ---------------- Source slicing fidelity ----------------

    [Fact]
    public void Source_field_matches_input()
    {
        var result = Parse("ls -la /tmp");
        Assert.Equal("ls -la /tmp", result.Source);
    }

    [Fact]
    public void Arg_raw_uses_source_slice()
    {
        // With a single-quoted token, Raw must include the quotes.
        var result = Parse("echo 'literal $VAR'");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal("'literal $VAR'", clause.Args[0].Raw);
    }

    // ---------------- Pipe with three stages ----------------

    [Fact]
    public void Pipe_three_stages()
    {
        var result = Parse("cmd1 | cmd2 | cmd3");
        Assert.Equal(3, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(CompoundOperator.Pipe, result.Clauses[1].Operator);
        Assert.Equal(CompoundOperator.Pipe, result.Clauses[2].Operator);
    }

    // ---------------- Trailing semicolon tolerated ----------------

    [Fact]
    public void Trailing_semicolon_does_not_create_empty_clause()
    {
        var result = Parse("ls;");
        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
        Assert.Equal(new[] { "ls" }, result.Clauses[0].Verb.Tokens);
    }

    // ---------------- Git -C flag-with-value retained as args ----------------

    [Fact]
    public void Git_dash_C_keeps_flag_and_value_as_separate_args()
    {
        // PR 3: pairing exists but does not change Arg shape; PR 4 will
        // mark the value as IsPath=true.
        var result = Parse("git -C /repo log");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "git", "-C" }, clause.Verb.Tokens.Take(1).Concat(new[] { clause.Args[0].Raw }).ToArray());
        // git's verb chain probe: first token "git", second token "-C" — but
        // -C is a flag-shaped token and stops verb-chain probing. So verb
        // chain is just ["git"], and -C / /repo / log are all args.
        Assert.Equal(new[] { "git" }, clause.Verb.Tokens);
        Assert.Equal(3, clause.Args.Count);
        Assert.Equal("-C", clause.Args[0].Raw);
        Assert.Equal("/repo", clause.Args[1].Raw);
        Assert.Equal("log", clause.Args[2].Raw);
    }
}
