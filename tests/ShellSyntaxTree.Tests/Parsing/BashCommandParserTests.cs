// -----------------------------------------------------------------------
// <copyright file="BashCommandParserTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Unit tests for the BashCommandParser core. PR 3 wired verb chains,
/// args, redirects, compound splitting, and SPEC §11 anomaly safe-fail;
/// PR 4 layers per-verb path classification, the resolver, and the
/// flag-with-value-aware verb-chain probe on top. cd-attribution and
/// subshell-flagging arrive in PR 5.
/// </summary>
public class BashCommandParserTests
{
    /// <summary>
    /// Default-Parse helper that pins a fixed WorkingDirectory so resolved
    /// paths in the test assertions are stable across host environments.
    /// (The default <see cref="BashParserOptions"/> falls back to
    /// <c>Environment.CurrentDirectory</c>, which the test runner picks
    /// up as the test binary's working dir.)
    /// </summary>
    private static ParsedCommand Parse(string input)
    {
        var parser = new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
        });
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
    public void Greedy_verb_chain_absorbs_bare_word_args_git_push_origin_main()
    {
        // Issue #27: documented over-extraction. `origin` and `main` are
        // syntactically indistinguishable from subcommand verbs (lowercase
        // identifiers, no path-shape). Consumers gating on `git push *`
        // use pattern-prefix length (2) — see SPEC §6.1.1.
        var result = Parse("git push origin main");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "git", "push", "origin", "main" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
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
    public void Greedy_verb_chain_walks_through_docker_compose_up_nginx()
    {
        // Issue #27: `docker compose up nginx` over-extracts because `nginx`
        // is a verb-like lowercase identifier. The previous BashArity
        // approach capped the chain at 3 tokens for `docker compose`; the
        // greedy heuristic walks until a non-verb-like token. Consumers
        // gating on `docker compose up *` use pattern-prefix length (3).
        var result = Parse("docker compose up nginx");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "docker", "compose", "up", "nginx" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
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
    public void Greedy_verb_chain_absorbs_docker_run_image_name()
    {
        // Issue #27: `nginx` is a docker image name but syntactically a
        // verb-like identifier. A registry-qualified image like
        // `registry.example.com/ns/nginx:1.25` would stop the walk via
        // path-shape rejection.
        var result = Parse("docker run nginx");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "docker", "run", "nginx" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
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
    public void Unknown_verb_with_bare_word_args_extracts_full_chain()
    {
        // Issue #27: under the greedy heuristic, unknown verbs (not in any
        // table) extract the full chain of consecutive verb-like tokens.
        // This is the strict-better-than-default-arity-1 behavior the
        // issue motivates — `freshdesk ticket list` and similar private
        // CLIs surface their full subcommand stack without curation.
        var result = Parse("totally-unknown-verb foo bar");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "totally-unknown-verb", "foo", "bar" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
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
        // PR 4: relative redirect target resolves against WorkingDirectory.
        var result = Parse("cmd >> log");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.Append, redirect.Direction);
        Assert.Equal("/work/log", redirect.Target);
        Assert.False(redirect.IsDynamicSkip);
    }

    [Fact]
    public void Redirect_In()
    {
        var result = Parse("cmd < input");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.In, redirect.Direction);
        Assert.Equal("/work/input", redirect.Target);
    }

    [Fact]
    public void Redirect_ErrOut()
    {
        var result = Parse("cmd 2> err");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.ErrOut, redirect.Direction);
        Assert.Equal("/work/err", redirect.Target);
    }

    [Fact]
    public void Redirect_ErrAppend()
    {
        var result = Parse("cmd 2>> err");
        var clause = Assert.Single(result.Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.ErrAppend, redirect.Direction);
        Assert.Equal("/work/err", redirect.Target);
    }

    [Fact]
    public void Multiple_redirects_on_one_clause()
    {
        var result = Parse("cmd > out 2> err");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(2, clause.Redirects.Count);
        Assert.Equal(RedirectDirection.Out, clause.Redirects[0].Direction);
        Assert.Equal("/work/out", clause.Redirects[0].Target);
        Assert.Equal(RedirectDirection.ErrOut, clause.Redirects[1].Direction);
        Assert.Equal("/work/err", clause.Redirects[1].Target);
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

    // ---------------- bash -c recursion ----------------

    [Fact]
    public void Bash_c_inner_compound_surfaces_inline_with_wrapper_flag()
    {
        // PR 5: bash -c recursion. The outer bash -c clause is consumed and
        // the inner command's clauses surface inline, each with
        // IsCommandStringWrapped=true. The inner cd attributes only within the
        // inner shell — outer attribution does not propagate in or out
        // (v0.1 decision; bash -c spawns a fresh shell).
        var result = Parse("bash -c \"cd /a && cmd\"");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);

        Assert.Equal(new[] { "cd" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal("/a", result.Clauses[0].Args[0].Resolved);
        Assert.True(result.Clauses[0].IsCommandStringWrapped);

        Assert.Equal(new[] { "cmd" }, result.Clauses[1].Verb.Tokens);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.True(result.Clauses[1].IsCommandStringWrapped);

        // The inner cmd inherits /a from the inner cd via attribution.
        Assert.Single(result.Clauses[1].Args);
        Assert.True(result.Clauses[1].Args[0].IsCwdAttribution);
        Assert.Equal("/a", result.Clauses[1].Args[0].Resolved);
    }

    [Fact]
    public void Sh_c_recurses_same_as_bash_c()
    {
        var result = Parse("sh -c \"echo hi\"");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        // Issue #27: `hi` is verb-like and absorbed into the inner clause's
        // verb chain (echo is not a FILE verb).
        Assert.Equal(new[] { "echo", "hi" }, clause.Verb.Tokens);
        Assert.True(clause.IsCommandStringWrapped);
    }

    [Fact]
    public void Bash_c_without_quoted_arg_stays_a_regular_bash_clause()
    {
        // Plain `bash script.sh` is *not* a wrapper — `-c` is missing or
        // unfollowed by a quoted body. Parse as a normal bash clause.
        var result = Parse("bash script.sh");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "bash" }, clause.Verb.Tokens);
        Assert.False(clause.IsCommandStringWrapped);
    }

    [Fact]
    public void Bash_c_recursion_depth_exceeded_marks_outer_unparseable()
    {
        // Build a 6-level deep bash -c chain. At each level we wrap the
        // body in `bash -c "..."` and escape-quote the inner level.
        // Depth 6 > cap 5 → outer ParsedCommand.IsUnparseable=true per
        // locked interpretation #4.
        var inner = "echo hi";
        for (var depth = 0; depth < 6; depth++)
        {
            // Double-quote escape: each level escapes the existing
            // double-quotes in the inner body. Bash double-quote semantics:
            // `\"` represents a literal `"` inside a double-quoted string.
            var escaped = inner.Replace("\\", "\\\\").Replace("\"", "\\\"");
            inner = "bash -c \"" + escaped + "\"";
        }

        var result = Parse(inner);
        Assert.True(result.IsUnparseable);
        Assert.NotNull(result.UnparseableReason);
        Assert.Contains("bash -c recursion", result.UnparseableReason!);
    }

    [Fact]
    public void Bash_c_nested_depth_2_parses()
    {
        // `bash -c "bash -c \"echo hi\""` — depth 2, well under the cap.
        var result = Parse("bash -c \"bash -c \\\"echo hi\\\"\"");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        // Issue #27: `hi` absorbed into the verb chain.
        Assert.Equal(new[] { "echo", "hi" }, clause.Verb.Tokens);
        Assert.True(clause.IsCommandStringWrapped);
    }

    [Fact]
    public void Bash_c_with_outer_cd_does_not_propagate_into_inner_clauses()
    {
        // v0.1 decision: bash -c is a fresh shell. The outer `cd /outer`
        // attribution is NOT injected into the inner clauses (per the PR 5
        // brief). The inner `ls` has no IsCwdAttribution arg from /outer.
        var result = Parse("cd /outer && bash -c \"ls\"");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);

        // Outer cd.
        Assert.Equal(new[] { "cd" }, result.Clauses[0].Verb.Tokens);

        // Inner ls (surfaced from bash -c). No attribution arg.
        Assert.Equal(new[] { "ls" }, result.Clauses[1].Verb.Tokens);
        Assert.True(result.Clauses[1].IsCommandStringWrapped);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.Empty(result.Clauses[1].Args);
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

    // ---------------- Comments (SPEC §5, issue #25) ----------------

    [Fact]
    public void Comment_only_input_returns_empty_clauses()
    {
        // Mirrors the empty/whitespace-only path: zero clauses, not
        // unparseable. SPEC §5 — comments are whitespace-equivalent.
        var result = Parse("# just a note");
        Assert.Equal("# just a note", result.Source);
        Assert.Empty(result.Clauses);
        Assert.False(result.IsUnparseable);
    }

    [Fact]
    public void Leading_comment_does_not_pollute_verb_chain()
    {
        // The exact failure mode from issue #25: a leading explanatory
        // comment was being parsed as the verb of the next clause,
        // surfacing as `# Extract` in downstream approval prompts. Issue
        // #27 follow-up: the greedy verb-chain heuristic now captures
        // `worktree` and `list` together — the parser used to truncate
        // the chain at the BashArity=2 default for git.
        var result = Parse("# Extract worktree branches\ngit worktree list");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "git", "worktree", "list" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
        Assert.False(result.IsUnparseable);
    }

    [Fact]
    public void Inline_trailing_comment_is_dropped_from_clause()
    {
        var result = Parse("git pull   # update local");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "git", "pull" }, clause.Verb.Tokens);
        Assert.Empty(clause.Args);
    }

    [Fact]
    public void Comment_between_two_statements_preserves_both_clauses()
    {
        // The `;` and the trailing newline are both statement separators;
        // the comment between them must not pollute either verb chain. The
        // newline after the comment lands on the segment the `;` already
        // opened, so it collapses — still exactly two clauses.
        var result = Parse("git pull ; # now build\ndotnet build");
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "git", "pull" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(new[] { "dotnet", "build" }, result.Clauses[1].Verb.Tokens);
        Assert.False(result.IsUnparseable);
    }

    [Fact]
    public void Hash_inside_double_quotes_remains_literal_arg()
    {
        var result = Parse("echo \"hash is #1234\"");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "echo" }, clause.Verb.Tokens);
        var arg = Assert.Single(clause.Args);
        Assert.Equal("\"hash is #1234\"", arg.Raw);
    }

    [Fact]
    public void Hash_inside_single_quotes_remains_literal_arg()
    {
        var result = Parse("echo 'use #foo'");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "echo" }, clause.Verb.Tokens);
        var arg = Assert.Single(clause.Args);
        Assert.Equal("'use #foo'", arg.Raw);
    }

    [Fact]
    public void Hash_mid_word_remains_literal_arg()
    {
        // Per bash: `#` is a comment-start only at a word boundary.
        // `abc#def` is a single word with a literal `#`.
        var result = Parse("echo abc#def");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "echo" }, clause.Verb.Tokens);
        var arg = Assert.Single(clause.Args);
        Assert.Equal("abc#def", arg.Raw);
    }

    [Fact]
    public void Comment_inside_orif_compound_does_not_break_clause_split()
    {
        // Issue #25 follow-up comment: a leading comment + ||-fallback
        // would persist `[# Get, echo]` at one pass but `[# Get, curl, jq]`
        // at another. After the fix both passes see `curl`/`echo`.
        var result = Parse("# Get open PRs\ncurl example || echo \"failed\"");
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "curl" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(new[] { "echo" }, result.Clauses[1].Verb.Tokens);
        Assert.False(result.IsUnparseable);
    }

    // ---------------- Newline statement separators (SPEC §4) ----------------

    [Fact]
    public void Newline_separates_two_clauses_as_Sequence()
    {
        var result = Parse("ls\npwd");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(new[] { "ls" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[1].Operator);
        Assert.Equal(new[] { "pwd" }, result.Clauses[1].Verb.Tokens);
    }

    [Fact]
    public void Newline_separates_three_clauses()
    {
        var result = Parse("cmd1\ncmd2\ncmd3");
        Assert.False(result.IsUnparseable);
        Assert.Equal(3, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[1].Operator);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[2].Operator);
    }

    [Fact]
    public void Blank_lines_do_not_create_empty_clauses()
    {
        var result = Parse("cmd1\n\n\ncmd2");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "cmd1" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(new[] { "cmd2" }, result.Clauses[1].Verb.Tokens);
    }

    [Fact]
    public void Leading_newline_keeps_first_clause_operator_None()
    {
        var result = Parse("\ncmd1\ncmd2");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(new[] { "cmd1" }, result.Clauses[0].Verb.Tokens);
    }

    [Fact]
    public void Trailing_newline_does_not_create_a_phantom_clause()
    {
        var result = Parse("cmd1\n");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "cmd1" }, clause.Verb.Tokens);
    }

    [Fact]
    public void Newline_after_AndIf_does_not_create_an_empty_clause()
    {
        // `cmd1 &&\ncmd2` — bash allows a newline right after a compound
        // operator; the newline must not produce an empty clause, and the
        // && must survive onto cmd2.
        var result = Parse("cmd1 &&\ncmd2");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.Equal(new[] { "cmd2" }, result.Clauses[1].Verb.Tokens);
    }

    [Fact]
    public void Newline_after_Pipe_does_not_create_an_empty_clause()
    {
        var result = Parse("cmd1 |\ncmd2");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.Pipe, result.Clauses[1].Operator);
    }

    [Fact]
    public void Newline_after_Semicolon_does_not_create_an_empty_clause()
    {
        var result = Parse("cmd1 ;\ncmd2");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[1].Operator);
    }

    [Fact]
    public void Newline_inside_subshell_separates_inner_clauses()
    {
        var result = Parse("(cmd1\ncmd2)");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.True(result.Clauses[0].IsSubshell);
        Assert.True(result.Clauses[1].IsSubshell);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[1].Operator);
    }

    [Fact]
    public void Newline_inside_double_quotes_is_a_single_arg()
    {
        // A newline inside a quoted string is literal content, not a
        // separator — the whole thing stays one clause with one arg.
        var result = Parse("echo \"a\nb\"");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "echo" }, clause.Verb.Tokens);
        Assert.Single(clause.Args);
    }

    [Fact]
    public void Continuation_newline_is_not_a_separator()
    {
        // `\` + newline is a line continuation — the two source lines
        // remain a single clause.
        var result = Parse("echo one \\\ntwo");
        Assert.False(result.IsUnparseable);
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "echo", "one", "two" }, clause.Verb.Tokens);
    }

    [Fact]
    public void Comment_then_newline_separates_clauses()
    {
        // A trailing comment ends at the newline; the newline still
        // separates the two statements — the bare-newline form of the
        // explicit-`;` workaround in corpus entry 126.
        var result = Parse("git pull # done\ndotnet build");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "git", "pull" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(new[] { "dotnet", "build" }, result.Clauses[1].Verb.Tokens);
    }

    [Fact]
    public void Whitespace_and_newline_only_input_returns_empty_clauses()
    {
        var result = Parse("  \n  ");
        Assert.False(result.IsUnparseable);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Heredoc_terminator_newline_separates_following_clause()
    {
        // The newline after the heredoc terminator is a statement
        // separator: `rest` is its own clause, not an arg of `cmd`.
        var result = Parse("cmd <<EOF\nbody\nEOF\nrest");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "rest" }, result.Clauses[1].Verb.Tokens);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[1].Operator);
    }

    [Fact]
    public void Control_flow_keyword_after_newline_marks_outer_unparseable()
    {
        // A control-flow keyword opening a newline-separated clause must
        // still safe-fail per SPEC §11 — TryDetectAnomaly treats the
        // newline as a verb-slot boundary.
        var result = Parse("echo hi\nfor i in 1 2 3");
        Assert.True(result.IsUnparseable);
        Assert.Contains("'for'", result.UnparseableReason!);
    }

    // ---------------- Subshell ----------------

    [Fact]
    public void Subshell_inner_clauses_carry_IsSubshell_true()
    {
        // PR 5: every clause parsed inside a `(...)` subshell carries
        // IsSubshell=true so consumers can distinguish them from outer
        // clauses (SPEC §10).
        var result = Parse("(cmd1 && cmd2)");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "cmd1" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal(new[] { "cmd2" }, result.Clauses[1].Verb.Tokens);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.True(result.Clauses[0].IsSubshell);
        Assert.True(result.Clauses[1].IsSubshell);
    }

    [Fact]
    public void Subshell_isolates_inner_cd_from_outer_compound()
    {
        // SPEC §10 example: `cd /a && (cd /b && cmd1) && cmd2`.
        // - Clause 0: cd /a, outer.
        // - Clause 1: cd /b inside subshell — inherits /a attribution.
        // - Clause 2: cmd1 inside subshell — inherits /b (closer cd).
        // - Clause 3: cmd2 outside subshell — inherits /a (the subshell's
        //   /b doesn't leak out).
        var result = Parse("cd /a && (cd /b && cmd1) && cmd2");
        Assert.False(result.IsUnparseable);
        Assert.Equal(4, result.Clauses.Count);

        // cd /a
        Assert.Equal(new[] { "cd" }, result.Clauses[0].Verb.Tokens);
        Assert.False(result.Clauses[0].IsSubshell);
        Assert.Equal("/a", result.Clauses[0].Args[0].Resolved);

        // cd /b inside subshell — inherits /a from outer attribution.
        Assert.Equal(new[] { "cd" }, result.Clauses[1].Verb.Tokens);
        Assert.True(result.Clauses[1].IsSubshell);
        Assert.Equal("/b", result.Clauses[1].Args[0].Resolved);
        // The /a attribution arg follows the user-emitted /b.
        Assert.Equal(2, result.Clauses[1].Args.Count);
        Assert.True(result.Clauses[1].Args[1].IsCwdAttribution);
        Assert.Equal("/a", result.Clauses[1].Args[1].Resolved);

        // cmd1 inside subshell — sees /b.
        Assert.Equal(new[] { "cmd1" }, result.Clauses[2].Verb.Tokens);
        Assert.True(result.Clauses[2].IsSubshell);
        Assert.Single(result.Clauses[2].Args);
        Assert.True(result.Clauses[2].Args[0].IsCwdAttribution);
        Assert.Equal("/b", result.Clauses[2].Args[0].Resolved);

        // cmd2 outside — sees /a, NOT /b.
        Assert.Equal(new[] { "cmd2" }, result.Clauses[3].Verb.Tokens);
        Assert.False(result.Clauses[3].IsSubshell);
        Assert.Single(result.Clauses[3].Args);
        Assert.True(result.Clauses[3].Args[0].IsCwdAttribution);
        Assert.Equal("/a", result.Clauses[3].Args[0].Resolved);
    }

    [Fact]
    public void Sequential_cd_replaces_attribution()
    {
        // SPEC §9 rule 3: a second cd in the same compound replaces
        // attribution for clauses after it. SPEC §9 rule 2 + §10 example:
        // every clause after the first cd — including a second cd — gets
        // the outer attribution arg appended; the second cd then *updates*
        // the context for clauses that follow it.
        var result = Parse("cd /a && cmd1 && cd /b && cmd2");
        Assert.False(result.IsUnparseable);
        Assert.Equal(4, result.Clauses.Count);

        // cmd1 sees /a.
        Assert.Equal("/a", result.Clauses[1].Args[0].Resolved);
        Assert.True(result.Clauses[1].Args[0].IsCwdAttribution);

        // cd /b — receives /a attribution (rule 2) before becoming the new
        // source (rule 3). Args = [/b, /a-attribution].
        Assert.Equal(new[] { "cd" }, result.Clauses[2].Verb.Tokens);
        Assert.Equal(2, result.Clauses[2].Args.Count);
        Assert.Equal("/b", result.Clauses[2].Args[0].Resolved);
        Assert.False(result.Clauses[2].Args[0].IsCwdAttribution);
        Assert.True(result.Clauses[2].Args[1].IsCwdAttribution);
        Assert.Equal("/a", result.Clauses[2].Args[1].Resolved);

        // cmd2 sees /b (rule 3 — replaced).
        Assert.Single(result.Clauses[3].Args);
        Assert.Equal("/b", result.Clauses[3].Args[0].Resolved);
        Assert.True(result.Clauses[3].Args[0].IsCwdAttribution);
    }

    [Fact]
    public void Cd_relative_path_args_resolve_under_attributed_cwd()
    {
        // SPEC §9 example: `cd /target && cat file.txt` → file.txt resolves
        // to /target/file.txt, not to the daemon cwd's file.txt.
        var result = Parse("cd /target && cat file.txt");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);

        var cat = result.Clauses[1];
        Assert.Equal(new[] { "cat" }, cat.Verb.Tokens);
        Assert.Equal(2, cat.Args.Count);

        // file.txt resolves against /target, not /work.
        Assert.Equal("file.txt", cat.Args[0].Raw);
        Assert.Equal("/target/file.txt", cat.Args[0].Resolved);

        // Trailing synthetic attribution arg.
        Assert.True(cat.Args[1].IsCwdAttribution);
        Assert.Equal("/target", cat.Args[1].Resolved);
    }

    [Fact]
    public void Pushd_parses_as_cwd_verb_but_does_not_propagate()
    {
        // Locked interpretation #5: only cd/chdir propagate attribution.
        // pushd parses as a CwdVerb (its first positional is path-classified)
        // but the next clause receives NO synthetic attribution arg.
        var result = Parse("pushd /target && cmd");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(new[] { "pushd" }, result.Clauses[0].Verb.Tokens);
        Assert.Equal("/target", result.Clauses[0].Args[0].Resolved);
        Assert.True(result.Clauses[0].Args[0].IsPath);

        // The cmd clause has no synthetic attribution arg.
        Assert.Empty(result.Clauses[1].Args);
    }

    [Fact]
    public void Two_sibling_subshells_track_independent_attribution()
    {
        // `(cd /a && cmd1) && (cd /b && cmd2)` — each subshell has its own
        // attribution state; neither leaks to the other and the outer
        // compound's attribution stays unset throughout.
        var result = Parse("(cd /a && cmd1) && (cd /b && cmd2)");
        Assert.False(result.IsUnparseable);
        Assert.Equal(4, result.Clauses.Count);

        // First subshell: cd /a → cmd1 (sees /a).
        Assert.Equal(new[] { "cd" }, result.Clauses[0].Verb.Tokens);
        Assert.True(result.Clauses[0].IsSubshell);

        Assert.Equal(new[] { "cmd1" }, result.Clauses[1].Verb.Tokens);
        Assert.True(result.Clauses[1].IsSubshell);
        Assert.Single(result.Clauses[1].Args);
        Assert.True(result.Clauses[1].Args[0].IsCwdAttribution);
        Assert.Equal("/a", result.Clauses[1].Args[0].Resolved);

        // Second subshell: cd /b → cmd2 (sees /b, NOT /a).
        Assert.Equal(new[] { "cd" }, result.Clauses[2].Verb.Tokens);
        Assert.True(result.Clauses[2].IsSubshell);
        // cd /b shouldn't have a /a attribution (separate subshell).
        Assert.Single(result.Clauses[2].Args);

        Assert.Equal(new[] { "cmd2" }, result.Clauses[3].Verb.Tokens);
        Assert.True(result.Clauses[3].IsSubshell);
        Assert.Single(result.Clauses[3].Args);
        Assert.True(result.Clauses[3].Args[0].IsCwdAttribution);
        Assert.Equal("/b", result.Clauses[3].Args[0].Resolved);
    }

    [Fact]
    public void Cd_dynamic_target_marks_subsequent_relative_paths_as_dynamic_skip()
    {
        // Locked interpretation #6: `cd $REPO && rm file.txt` — the cwd is
        // statically unknown, so file.txt cannot be safely resolved.
        var result = Parse("cd $REPO && rm file.txt");
        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);

        // cd target is DynamicSkip.
        Assert.Equal(ArgKind.DynamicSkip, result.Clauses[0].Args[0].Kind);

        // rm's file.txt is a relative path → DynamicSkip with no resolution.
        var rm = result.Clauses[1];
        Assert.Equal(2, rm.Args.Count);
        Assert.Equal("file.txt", rm.Args[0].Raw);
        Assert.Equal(ArgKind.DynamicSkip, rm.Args[0].Kind);
        Assert.Null(rm.Args[0].Resolved);

        // Synthetic attribution arg is the DynamicSkip flavor.
        Assert.True(rm.Args[1].IsCwdAttribution);
        Assert.Equal(ArgKind.DynamicSkip, rm.Args[1].Kind);
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

    // ---------------- Git -C flag-with-value verb-chain probe ----------------

    [Fact]
    public void Git_dash_C_yields_two_token_verb_chain_per_spec_12_example()
    {
        // PR 4 + locked interpretation #8 + SPEC §12 worked example:
        // `git -C /repo log` skips the `-C /repo` flag-with-value pair while
        // probing arity, so the verb chain captures both `git` and `log`.
        // The flag and value still surface in Args in source order — and
        // /repo carries IsPath=true via the FlagValueIsPath table.
        var result = Parse("git -C /repo log");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "git", "log" }, clause.Verb.Tokens);
        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("-C", clause.Args[0].Raw);
        Assert.True(clause.Args[0].IsFlag);
        Assert.Equal("/repo", clause.Args[1].Raw);
        Assert.True(clause.Args[1].IsPath);
        Assert.Equal("/repo", clause.Args[1].Resolved);
        Assert.Equal(ArgKind.Literal, clause.Args[1].Kind);
    }

    // ---------------- Path classification + resolution ----------------

    [Fact]
    public void Absolute_path_arg_resolves_to_itself()
    {
        var result = Parse("cat /etc/hostname");
        var clause = Assert.Single(result.Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.True(arg.IsPath);
        Assert.Equal("/etc/hostname", arg.Resolved);
        Assert.Equal(ArgKind.Literal, arg.Kind);
    }

    [Fact]
    public void Tilde_path_arg_expands_to_home()
    {
        var result = Parse("cat ~/file.txt");
        var clause = Assert.Single(result.Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.True(arg.IsPath);
        Assert.Equal("/home/test/file.txt", arg.Resolved);
        Assert.Equal(ArgKind.Tilde, arg.Kind);
    }

    [Fact]
    public void Env_var_in_path_slot_becomes_dynamic_skip()
    {
        // SPEC §12 example: `rm $UNRESOLVED/foo`.
        var result = Parse("rm $UNRESOLVED/foo");
        var clause = Assert.Single(result.Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.DynamicSkip, arg.Kind);
        Assert.False(arg.IsPath);
        Assert.Null(arg.Resolved);
    }

    [Fact]
    public void Glob_in_path_slot_is_glob_kind_is_path_true()
    {
        // Locked interpretation #3: covering-directory signal preserved.
        var result = Parse("rm /tmp/*.bak");
        var clause = Assert.Single(result.Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.Glob, arg.Kind);
        Assert.True(arg.IsPath);
        Assert.Null(arg.Resolved);
    }

    [Fact]
    public void Chmod_mode_is_not_a_path()
    {
        var result = Parse("chmod 755 /etc/passwd");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(2, clause.Args.Count);
        Assert.False(clause.Args[0].IsPath);
        Assert.Equal("755", clause.Args[0].Raw);
        Assert.True(clause.Args[1].IsPath);
        Assert.Equal("/etc/passwd", clause.Args[1].Resolved);
    }

    [Fact]
    public void Grep_first_arg_is_pattern_not_path()
    {
        var result = Parse("grep pattern /etc/hosts");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(2, clause.Args.Count);
        Assert.False(clause.Args[0].IsPath);
        Assert.True(clause.Args[1].IsPath);
    }

    [Fact]
    public void Curl_url_is_not_a_path_but_output_flag_value_is()
    {
        var result = Parse("curl -o /tmp/out https://example.com");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(3, clause.Args.Count);
        Assert.Equal("-o", clause.Args[0].Raw);
        Assert.True(clause.Args[1].IsPath);
        Assert.Equal("/tmp/out", clause.Args[1].Resolved);
        Assert.False(clause.Args[2].IsPath);
        Assert.Equal("https://example.com", clause.Args[2].Raw);
    }

    [Fact]
    public void Find_root_is_path_predicate_args_are_not()
    {
        var result = Parse("find /var/log -name \"*.log\"");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(3, clause.Args.Count);
        Assert.True(clause.Args[0].IsPath);
        Assert.Equal("/var/log", clause.Args[0].Resolved);
        Assert.Equal("-name", clause.Args[1].Raw);
        Assert.False(clause.Args[2].IsPath);
    }

    [Fact]
    public void Docker_volume_value_is_not_a_path_per_locked_interpretation_8()
    {
        var result = Parse("docker run -v /host:/container nginx");
        var clause = Assert.Single(result.Clauses);
        // Issue #27 follow-up: the verb-chain walker consumes `-v
        // /host:/container` as a flag-with-value pair, then `nginx`
        // (verb-like) extends the chain. Verb = ["docker", "run", "nginx"];
        // the volume-mount value still surfaces as an arg with IsPath=false
        // per locked interpretation #8 (colon-joined target is not a path).
        Assert.Equal(new[] { "docker", "run", "nginx" }, clause.Verb.Tokens);
        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("-v", clause.Args[0].Raw);
        Assert.False(clause.Args[1].IsPath);
        Assert.Equal("/host:/container", clause.Args[1].Raw);
    }
}
