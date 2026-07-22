// -----------------------------------------------------------------------
// <copyright file="PwshCommandParserTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Unit tests for <see cref="PwshParser"/> — SPEC.POWERSHELL.md §4–§11
/// behavior: verb classification, alias resolution, the §6.5 parameter
/// binding model, redirects, <c>Set-Location</c> propagation, recursion,
/// and the safe-fail anomaly contract.
/// </summary>
public class PwshCommandParserTests
{
    private static readonly PwshParser Parser = new(new PwshParserOptions
    {
        HomeDirectory = "C:/Users/user",
        WorkingDirectory = "C:/work",
    });

    private static ParsedCommand Parse(string input) => Parser.Parse(input);

    // ---------------------------------------------------------------- basics

    [Fact]
    public void Empty_input_parses_to_no_clauses()
    {
        var result = Parse("");
        Assert.False(result.IsUnparseable);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Comment_only_input_parses_to_no_clauses()
    {
        var result = Parse("# nothing here");
        Assert.False(result.IsUnparseable);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Simple_cmdlet_parses_to_one_clause()
    {
        var result = Parse("Get-Date");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "Get-Date" }, clause.Verb.Tokens);
        Assert.Null(clause.Verb.CanonicalVerb);
    }

    // ---------------------------------------------------------------- alias resolution

    [Theory]
    [InlineData("gci", "Get-ChildItem")]
    [InlineData("ls", "Get-ChildItem")]
    [InlineData("rm", "Remove-Item")]
    [InlineData("del", "Remove-Item")]
    [InlineData("cat", "Get-Content")]
    [InlineData("cd", "Set-Location")]
    [InlineData("echo", "Write-Output")]
    [InlineData("%", "ForEach-Object")]
    [InlineData("?", "Where-Object")]
    public void Alias_resolves_to_canonical_verb(string alias, string canonical)
    {
        var clause = Assert.Single(Parse(alias).Clauses);
        Assert.Equal(new[] { alias }, clause.Verb.Tokens);
        Assert.Equal(canonical, clause.Verb.CanonicalVerb);
    }

    [Fact]
    public void Canonical_cmdlet_leaves_canonical_verb_null()
    {
        var clause = Assert.Single(Parse("Get-ChildItem").Clauses);
        Assert.Null(clause.Verb.CanonicalVerb);
    }

    [Fact]
    public void Never_aliased_set_is_a_native_command()
    {
        // `where` is never aliased (§6.3 collision rule 3).
        var clause = Assert.Single(Parse("where foo").Clauses);
        Assert.Null(clause.Verb.CanonicalVerb);
    }

    // ---------------------------------------------------------------- native chains

    [Fact]
    public void Native_command_uses_greedy_verb_chain()
    {
        var clause = Assert.Single(Parse("git push origin main").Clauses);
        Assert.Equal(new[] { "git", "push", "origin", "main" }, clause.Verb.Tokens);
    }

    [Fact]
    public void Native_chain_stops_at_a_capitalized_token()
    {
        var clause = Assert.Single(Parse("dotnet ef migrations add InitialCreate").Clauses);
        Assert.Equal(new[] { "dotnet", "ef", "migrations", "add" }, clause.Verb.Tokens);
        Assert.Equal("InitialCreate", clause.Args[0].Raw);
    }

    [Fact]
    public void Native_hyphenated_flag_consumes_spaced_path_value()
    {
        var clause = Assert.Single(Parse("git --work-tree repo status").Clauses);

        Assert.Equal(new[] { "git", "status" }, clause.Verb.Tokens);
        Assert.Equal("--work-tree", clause.Args[0].Raw);
        Assert.Equal("repo", clause.Args[1].Raw);
        Assert.True(clause.Args[1].IsPath);
        Assert.Equal("C:/work/repo", clause.Args[1].Resolved);
    }

    [Theory]
    [InlineData("--work-tree=../test", "--work-tree", "../test", "C:/test")]
    [InlineData("--git-dir=repo", "--git-dir", "repo", "C:/work/repo")]
    public void Native_equals_flag_splits_and_classifies_its_path_value(
        string option, string expectedFlag, string expectedValue, string expectedResolved)
    {
        var clause = Assert.Single(Parse($"git {option} add somefile").Clauses);

        Assert.Equal(new[] { "git" }, clause.Verb.Tokens);
        Assert.Equal(4, clause.Args.Count);
        Assert.Equal(expectedFlag, clause.Args[0].Raw);
        Assert.Equal(expectedValue, clause.Args[1].Raw);
        Assert.True(clause.Args[1].IsPath);
        Assert.Equal(expectedResolved, clause.Args[1].Resolved);
        Assert.Equal("add", clause.Args[2].Raw);
        Assert.Equal("somefile", clause.Args[3].Raw);
    }

    [Fact]
    public void Native_colon_option_is_preserved_verbatim()
    {
        var clause = Assert.Single(Parse("git --option:value status").Clauses);

        Assert.Equal("--option:value", clause.Args[0].Raw);
        Assert.True(clause.Args[0].IsFlag);
    }

    [Fact]
    public void Unknown_native_equals_flag_does_not_gain_path_semantics()
    {
        var clause = Assert.Single(Parse("git --destination=repo status").Clauses);

        Assert.Equal("--destination", clause.Args[0].Raw);
        Assert.Equal("repo", clause.Args[1].Raw);
        Assert.False(clause.Args[1].IsPath);
    }

    [Fact]
    public void Dynamic_native_equals_path_value_safe_fails()
    {
        var clause = Assert.Single(Parse("git --work-tree=$repo status").Clauses);

        Assert.Equal("--work-tree", clause.Args[0].Raw);
        Assert.Equal("$repo", clause.Args[1].Raw);
        Assert.Equal(ArgKind.DynamicSkip, clause.Args[1].Kind);
        Assert.False(clause.Args[1].IsPath);
    }

    // ---------------------------------------------------------------- pipelines

    [Fact]
    public void Pipeline_splits_into_clauses()
    {
        var result = Parse("gci | Where-Object Name | rm");
        Assert.Equal(3, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(CompoundOperator.Pipe, result.Clauses[1].Operator);
        Assert.Equal(CompoundOperator.Pipe, result.Clauses[2].Operator);
    }

    [Theory]
    [InlineData("a; b", CompoundOperator.Sequence)]
    [InlineData("a && b", CompoundOperator.AndIf)]
    [InlineData("a || b", CompoundOperator.OrIf)]
    public void Statement_separators_map_to_operators(string input, CompoundOperator op)
    {
        var result = Parse(input);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(op, result.Clauses[1].Operator);
    }

    [Fact]
    public void Newline_separates_statements()
    {
        var result = Parse("Get-Date\nGet-Location");
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.Sequence, result.Clauses[1].Operator);
    }

    // ---------------------------------------------------------------- parameter binding

    [Fact]
    public void Switch_then_positional_keeps_positional_index()
    {
        // Copy-Item -Force a -Verbose b — a is positional 0, b positional 1.
        var clause = Assert.Single(Parse("Copy-Item -Force a -Verbose b").Clauses);
        var positionals = clause.Args.Where(a => !a.IsFlag).ToArray();
        Assert.Equal(2, positionals.Length);
        Assert.True(positionals[0].IsPath);
        Assert.True(positionals[1].IsPath);
    }

    [Fact]
    public void Value_binding_parameter_consumes_the_next_token()
    {
        // -Depth is value-binding; 3 is its value, C:\logs the positional path.
        var clause = Assert.Single(Parse("Get-ChildItem -Depth 3 C:\\logs").Clauses);
        var depthValue = clause.Args[1];
        Assert.Equal("3", depthValue.Raw);
        Assert.False(depthValue.IsPath);
        var path = clause.Args[2];
        Assert.Equal("C:\\logs", path.Raw);
        Assert.True(path.IsPath);
    }

    [Fact]
    public void Switch_parameter_does_not_consume_the_next_token()
    {
        // -Recurse is a switch; C:\logs stays a positional path.
        var clause = Assert.Single(Parse("Get-ChildItem -Recurse C:\\logs").Clauses);
        var path = clause.Args.Single(a => !a.IsFlag);
        Assert.True(path.IsPath);
        Assert.Equal("C:/logs", path.Resolved);
    }

    [Fact]
    public void Colon_form_parameter_binds_its_value()
    {
        var clause = Assert.Single(Parse("Get-ChildItem -Path:C:\\logs").Clauses);
        Assert.Equal("-Path", clause.Args[0].Raw);
        Assert.True(clause.Args[0].IsFlag);
        Assert.Equal("C:\\logs", clause.Args[1].Raw);
        Assert.True(clause.Args[1].IsPath);
    }

    [Fact]
    public void Hyphenated_cmdlet_parameter_colon_form_stays_one_parameter()
    {
        var clause = Assert.Single(Parse("Get-Thing -Name-Part:value").Clauses);

        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("-Name-Part", clause.Args[0].Raw);
        Assert.Equal("value", clause.Args[1].Raw);
    }

    [Fact]
    public void Equals_does_not_bind_a_cmdlet_parameter_value()
    {
        var clause = Assert.Single(Parse("Get-Item -Path=foo bar").Clauses);

        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("-Path=foo", clause.Args[0].Raw);
        Assert.True(clause.Args[0].IsFlag);
        Assert.Equal("bar", clause.Args[1].Raw);
        Assert.True(clause.Args[1].IsPath);
    }

    [Fact]
    public void Equals_in_a_parameter_name_makes_its_colon_value_dynamic()
    {
        // PowerShell tokenizes this as parameter `-Path=C:` plus argument
        // `\Windows` — a name that can never bind, so the value's role is
        // unknowable and must not surface as a confident literal.
        var clause = Assert.Single(Parse("Remove-Item -Path=C:\\Windows").Clauses);

        Assert.Equal(2, clause.Args.Count);
        Assert.Equal("-Path=C", clause.Args[0].Raw);
        Assert.Equal("\\Windows", clause.Args[1].Raw);
        Assert.Equal(ArgKind.DynamicSkip, clause.Args[1].Kind);
        Assert.False(clause.Args[1].IsPath);
    }

    [Fact]
    public void Question_mark_help_parameter_is_one_flag()
    {
        var clause = Assert.Single(Parse("Get-Help -?").Clauses);

        var arg = Assert.Single(clause.Args);
        Assert.Equal("-?", arg.Raw);
        Assert.True(arg.IsFlag);
    }

    [Fact]
    public void Unknown_parameter_defaults_to_switch()
    {
        // -Whatever is unknown → switch; C:\logs stays a positional path.
        var clause = Assert.Single(Parse("Get-ChildItem -Whatever C:\\logs").Clauses);
        var path = clause.Args.Single(a => !a.IsFlag);
        Assert.True(path.IsPath);
    }

    [Fact]
    public void Path_parameter_value_is_a_path()
    {
        var clause = Assert.Single(Parse("Get-Content -Path C:\\logs\\app.log").Clauses);
        var value = clause.Args[1];
        Assert.True(value.IsPath);
        Assert.Equal("C:/logs/app.log", value.Resolved);
    }

    [Fact]
    public void Splat_argument_is_dynamic_skip()
    {
        var clause = Assert.Single(Parse("Copy-Item @params").Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.DynamicSkip, arg.Kind);
        Assert.False(arg.IsPath);
    }

    [Fact]
    public void Script_block_argument_is_dynamic_skip()
    {
        var clause = Parse("gci | ? { $_.Length -gt 1mb }").Clauses[1];
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.DynamicSkip, arg.Kind);
    }

    // ---------------------------------------------------------------- resolver

    [Fact]
    public void Drive_qualified_path_normalizes_slashes()
    {
        var clause = Assert.Single(Parse("Get-Item C:\\Users\\user\\file.txt").Clauses);
        Assert.Equal("C:/Users/user/file.txt", clause.Args[0].Resolved);
    }

    [Fact]
    public void Registry_provider_path_is_not_a_filesystem_path()
    {
        var clause = Assert.Single(Parse("Remove-Item HKLM:\\Software\\X").Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.Literal, arg.Kind);
        Assert.False(arg.IsPath);
    }

    [Fact]
    public void Env_variable_in_a_path_slot_is_dynamic_skip()
    {
        var clause = Assert.Single(Parse("Get-Content $env:TEMP\\log.txt").Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.DynamicSkip, arg.Kind);
        Assert.False(arg.IsPath);
    }

    [Fact]
    public void Home_variable_expands()
    {
        var clause = Assert.Single(Parse("Get-Content $HOME\\notes.txt").Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.True(arg.IsPath);
        Assert.Equal("C:/Users/user/notes.txt", arg.Resolved);
    }

    [Fact]
    public void Tilde_expands_to_home()
    {
        var clause = Assert.Single(Parse("Set-Location ~").Clauses);
        var arg = clause.Args.Single(a => !a.IsCwdAttribution);
        Assert.Equal("C:/Users/user", arg.Resolved);
    }

    [Fact]
    public void Single_quoted_path_does_not_expand()
    {
        var clause = Assert.Single(Parse("Get-Content 'C:\\logs\\app.log'").Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal("C:/logs/app.log", arg.Resolved);
    }

    [Fact]
    public void Glob_argument_in_a_path_slot_is_glob_kind()
    {
        var clause = Assert.Single(Parse("Get-ChildItem C:\\logs\\*.log").Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.Glob, arg.Kind);
        Assert.True(arg.IsPath);
    }

    [Fact]
    public void Comma_array_path_token_is_dynamic_skip()
    {
        var clause = Assert.Single(Parse("Remove-Item a.txt,b.txt").Clauses);
        var arg = Assert.Single(clause.Args);
        Assert.Equal(ArgKind.DynamicSkip, arg.Kind);
        Assert.False(arg.IsPath);
    }

    // ---------------------------------------------------------------- redirects

    [Theory]
    [InlineData("Get-Date > out.txt", RedirectDirection.Out)]
    [InlineData("Get-Date >> out.txt", RedirectDirection.Append)]
    [InlineData("Get-Date 2> err.txt", RedirectDirection.ErrOut)]
    [InlineData("Get-Date 2>> err.txt", RedirectDirection.ErrAppend)]
    public void Redirect_direction_maps(string input, RedirectDirection direction)
    {
        var clause = Assert.Single(Parse(input).Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(direction, redirect.Direction);
    }

    [Fact]
    public void Verbose_stream_redirect_maps_lossily_to_out()
    {
        var clause = Assert.Single(Parse("Get-Date 3> verbose.txt").Clauses);
        Assert.Equal(RedirectDirection.Out, clause.Redirects[0].Direction);
    }

    [Fact]
    public void Stream_merge_carries_dynamic_skip_target()
    {
        var clause = Assert.Single(Parse("Get-Date 2>&1").Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.Equal(RedirectDirection.ErrOut, redirect.Direction);
        Assert.True(redirect.IsDynamicSkip);
        Assert.Equal("&1", redirect.Target);
    }

    [Fact]
    public void Null_redirect_target_is_dynamic_skip()
    {
        var clause = Assert.Single(Parse("Get-Date > $null").Clauses);
        var redirect = Assert.Single(clause.Redirects);
        Assert.True(redirect.IsDynamicSkip);
        Assert.Equal("$null", redirect.Target);
    }

    // ---------------------------------------------------------------- Set-Location

    [Fact]
    public void Set_location_attributes_cwd_to_subsequent_clauses()
    {
        var result = Parse("cd C:\\repo; git status");
        var attribution = result.Clauses[1].Args.Single(a => a.IsCwdAttribution);
        Assert.True(attribution.IsPath);
        Assert.Equal("C:/repo", attribution.Resolved);
    }

    [Fact]
    public void Dynamic_set_location_target_yields_dynamic_attribution()
    {
        var result = Parse("cd $target; gci");
        var attribution = result.Clauses[1].Args.Single(a => a.IsCwdAttribution);
        Assert.Equal(ArgKind.DynamicSkip, attribution.Kind);
        Assert.False(attribution.IsPath);
    }

    [Fact]
    public void Set_location_dash_yields_dynamic_attribution()
    {
        var result = Parse("cd -; gci");
        var attribution = result.Clauses[1].Args.Single(a => a.IsCwdAttribution);
        Assert.Equal(ArgKind.DynamicSkip, attribution.Kind);
    }

    [Fact]
    public void Set_location_propagates_through_a_group()
    {
        // PowerShell ( ) is a grouping operator, not a subshell — attribution
        // propagates through it (§9 rule 4).
        var result = Parse("(cd C:\\sensitive); Remove-Item *");
        var rm = result.Clauses.Last();
        Assert.Contains(rm.Args, a => a.IsCwdAttribution && a.Resolved == "C:/sensitive");
    }

    // ---------------------------------------------------------------- recursion

    [Fact]
    public void Pwsh_command_recursion_surfaces_inner_clause()
    {
        var result = Parse("pwsh -Command \"Remove-Item C:\\tmp\\x\"");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "Remove-Item" }, clause.Verb.Tokens);
        Assert.True(clause.IsCommandStringWrapped);
    }

    [Fact]
    public void Pwsh_command_recursion_handles_the_bare_form()
    {
        var result = Parse("pwsh -Command Remove-Item C:\\tmp\\x");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "Remove-Item" }, clause.Verb.Tokens);
        Assert.True(clause.IsCommandStringWrapped);
    }

    [Fact]
    public void Pwsh_command_recursion_handles_the_script_block_form()
    {
        var result = Parse("pwsh -Command { Get-Date }");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "Get-Date" }, clause.Verb.Tokens);
        Assert.True(clause.IsCommandStringWrapped);
    }

    [Fact]
    public void Encoded_command_decodes_and_recurses()
    {
        // base64(UTF-16LE("Get-Date")).
        var result = Parse("pwsh -EncodedCommand RwBlAHQALQBEAGEAdABlAA==");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "Get-Date" }, clause.Verb.Tokens);
        Assert.True(clause.IsCommandStringWrapped);
    }

    [Fact]
    public void Bad_base64_encoded_command_is_unparseable()
    {
        var result = Parse("pwsh -EncodedCommand not-valid-base64!!!");
        Assert.True(result.IsUnparseable);
    }

    [Fact]
    public void Short_c_flag_is_recognized_as_command()
    {
        var result = Parse("pwsh -c \"Get-Date\"");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "Get-Date" }, clause.Verb.Tokens);
    }

    [Fact]
    public void Pwsh_file_is_not_recursion()
    {
        var result = Parse("pwsh -File C:\\scripts\\deploy.ps1");
        var clause = Assert.Single(result.Clauses);
        Assert.Equal(new[] { "pwsh" }, clause.Verb.Tokens);
        Assert.False(clause.IsCommandStringWrapped);
        var path = clause.Args.Single(a => !a.IsFlag);
        Assert.True(path.IsPath);
    }

    [Fact]
    public void Inner_unparseable_command_propagates_to_the_outer()
    {
        var result = Parse("pwsh -Command \"foreach ($x in $y) { $x }\"");
        Assert.True(result.IsUnparseable);
    }

    // ---------------------------------------------------------------- anomalies

    [Theory]
    [InlineData("foreach ($f in $list) { $f }")]
    [InlineData("if ($true) { Get-Date }")]
    [InlineData("while ($true) { Get-Date }")]
    [InlineData("function Foo { }")]
    [InlineData("return 0")]
    [InlineData("$x = 1")]
    [InlineData("[System.IO.File]::ReadAllText('x')")]
    [InlineData("git status &")]
    public void Out_of_scope_constructs_are_unparseable(string input)
    {
        var result = Parse(input);
        Assert.True(result.IsUnparseable);
        Assert.NotNull(result.UnparseableReason);
    }

    [Fact]
    public void Foreach_as_an_alias_after_a_pipe_is_not_unparseable()
    {
        // `foreach` not followed by `(` is the ForEach-Object alias.
        var result = Parse("gci | foreach { $_ }");
        Assert.False(result.IsUnparseable);
        Assert.Equal("ForEach-Object", result.Clauses[1].Verb.CanonicalVerb);
    }

    [Fact]
    public void Unbalanced_quote_is_unparseable()
    {
        Assert.True(Parse("Get-Content 'unclosed").IsUnparseable);
    }

    [Fact]
    public void Oversized_input_is_unparseable()
    {
        var huge = "Get-Date " + new string('x', 70 * 1024);
        var result = Parse(huge);
        Assert.True(result.IsUnparseable);
        Assert.Contains("64 KiB", result.UnparseableReason);
    }

    [Fact]
    public void Dynamic_call_operator_command_is_marked_dynamic()
    {
        var clause = Assert.Single(Parse("& $exe arg1").Clauses);
        Assert.True(clause.Verb.IsDynamic);
        Assert.Null(clause.Verb.CanonicalVerb);
    }

    [Fact]
    public void Call_operator_before_a_literal_command_parses()
    {
        var clause = Assert.Single(Parse("& git status").Clauses);
        Assert.False(clause.Verb.IsDynamic);
        Assert.Equal(new[] { "git", "status" }, clause.Verb.Tokens);
    }

    [Fact]
    public void Grouped_pipeline_marks_clauses_as_subshell()
    {
        var result = Parse("(Get-ChildItem)");
        var clause = Assert.Single(result.Clauses);
        Assert.True(clause.IsSubshell);
    }

    [Fact]
    public void Never_throws_on_a_wellformed_string()
    {
        // A grab-bag that must not throw.
        foreach (var input in new[]
                 {
                     "gci", "rm -Recurse C:\\x", "a | b | c", "pwsh -Command \"\"",
                     "Get-Date 2>&1 > out", "{ }", "$( )", "--%",
                 })
        {
            var ex = Record.Exception(() => Parser.Parse(input));
            Assert.Null(ex);
        }
    }
}
