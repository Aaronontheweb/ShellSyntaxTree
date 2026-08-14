// -----------------------------------------------------------------------
// <copyright file="AuthoredOperandSemanticsTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Linq;
using ShellSyntaxTree.Internal.Resolving;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class AuthoredOperandSemanticsTests
{
    [Fact]
    public void Audited_commands_opt_into_reusable_binding_categories_through_data()
    {
        var operandArguments = Assert.Single(
            Bash().Parse("example-command README.md").Commands).Arguments;
        var operandEntry = new AuditedOperandBindingCatalogEntry(
            ShellProjectionLanguage.Bash,
            "example-command",
            StringComparison.Ordinal,
            AuditedOperandBindingCategory.AllNonOptionOperands,
            AuditedOperandSemantic.LocalFileSystem);

        Assert.Equal(
            new[]
            {
                new AuditedOperandBinding(
                    AuditedOperandBindingCategory.AllNonOptionOperands,
                    AuditedOperandSemantic.LocalFileSystem),
            },
            AuditedOperandBindingCatalog.Bind(operandEntry, operandArguments));

        var parameterArguments = Assert.Single(
            PowerShell(PwshDialect.PowerShell7)
                .Parse("Example-Command -Source C:\\work\\a.txt")
                .Commands).Arguments;
        var parameterEntry = new AuditedOperandBindingCatalogEntry(
            ShellProjectionLanguage.PowerShell,
            "Example-Command",
            StringComparison.OrdinalIgnoreCase,
            AuditedOperandBindingCategory.ExactNamedParameterValue,
            AuditedOperandSemantic.LocalFileSystem,
            "-Source");

        Assert.Equal(
            new[]
            {
                default,
                new AuditedOperandBinding(
                    AuditedOperandBindingCategory.ExactNamedParameterValue,
                    AuditedOperandSemantic.LocalFileSystem),
            },
            AuditedOperandBindingCatalog.Bind(parameterEntry, parameterArguments));
    }

    [Theory]
    [InlineData("tr abc def", "abc", "def")]
    [InlineData("tr -d '\\n'", "-d", "\\n")]
    [InlineData("tr -- -d x", "--", "-d", "x")]
    public void Audited_bash_tr_arguments_publish_non_filesystem_values(
        string source,
        params string[] expected)
    {
        var command = Assert.Single(Bash().Parse(source).Commands);

        Assert.Equal(new[] { "tr" }, command.Clause.Verb.Tokens);
        Assert.Equal(expected.Length, command.Arguments.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var argument = command.Arguments[index];
            Assert.Equal(
                expected[index],
                Assert.IsType<ShellValueDomain.Exact>(argument.AuthoredValue).Value);
            Assert.False(argument.Argument.IsPath);
            Assert.Null(argument.Argument.Resolved);
            AssertNonFileSystemExact(argument, expected[index]);
            AssertUnknownFileSystem(argument);
        }
    }

    [Fact]
    public void Path_shaped_tr_data_keeps_lexical_shape_without_path_semantics()
    {
        var arguments = Assert.Single(
            Bash().Parse("tr /etc/passwd 'C:\\temp'").Commands).Arguments;

        Assert.Equal(ShellPathShape.Posix, arguments[0].AuthoredPathShape);
        Assert.Equal(ShellPathShape.Windows, arguments[1].AuthoredPathShape);
        Assert.All(arguments, argument =>
        {
            Assert.False(argument.Argument.IsPath);
            Assert.Null(argument.Argument.Resolved);
            Assert.IsType<ShellValueDomain.Exact>(argument.AuthoredNonFileSystemValue);
        });
    }

    [Fact]
    public void Active_glob_and_dynamic_tr_data_remain_unknown()
    {
        var glob = Assert.Single(Bash().Parse("tr *.txt x").Commands).Arguments;
        var dynamic = Bash().Parse("tr \"$chars\" x");

        AssertUnknownNonFileSystem(glob[0]);
        AssertNonFileSystemExact(glob[1], "x");
        Assert.True(dynamic.IsUnparseable);
        Assert.Empty(dynamic.Commands);
    }

    [Fact]
    public void Tr_substitution_and_redirect_remain_independent_facts()
    {
        var substitution = Bash().Parse("tr \"$(printf x)\" y");
        var outer = Assert.Single(
            substitution.Commands,
            command => command.Clause.Verb.Joined == "tr");
        AssertUnknownNonFileSystem(outer.Arguments[0]);
        AssertNonFileSystemExact(outer.Arguments[1], "y");
        Assert.Contains(
            substitution.Commands,
            command => command.ImmediateRole == CommandOccurrenceRole.Substitution);

        var redirected = Assert.Single(
            Bash().Parse("tr -d '\\n' > /outside/result").Commands);
        Assert.All(redirected.Arguments, argument =>
            Assert.IsType<ShellValueDomain.Exact>(argument.AuthoredNonFileSystemValue));
        Assert.Equal(
            "/outside/result",
            Assert.IsType<ShellValueDomain.Exact>(
                Assert.IsType<FileRedirectAnalysis>(
                    Assert.Single(redirected.Redirects)).Target).Value);
    }

    [Fact]
    public void Unknown_command_keeps_backslash_argument_on_the_compatibility_path()
    {
        var argument = Assert.Single(
            Assert.Single(Bash().Parse("tool -d '\\n'").Commands).Arguments,
            candidate => candidate.AuthoredValue is ShellValueDomain.Exact
            {
                Value: "\\n",
            });

        Assert.True(argument.Argument.IsPath);
        Assert.Equal("/work/n", argument.Argument.Resolved);
        AssertUnknownNonFileSystem(argument);
    }

    [Fact]
    public void Over_limit_tr_join_remains_unknown()
    {
        var values = Enumerable.Range(1, ShellAnalysisLimits.MaxValueCandidates + 1)
            .Select(index => $"v{index:00}");
        var result = Bash(authoredFacts: true).Parse(
            $"for f in {string.Join(" ", values)}; do tr \"$f\" x; done");

        var arguments = Assert.Single(result.Commands).Arguments;
        AssertUnknownNonFileSystem(arguments[0]);
        AssertNonFileSystemExact(arguments[1], "x");
    }

    [Fact]
    public void Finite_tr_loop_publishes_a_non_filesystem_value_set()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in a b; do tr \"$f\" x; done");

        var arguments = Assert.Single(result.Commands).Arguments;
        AssertFinite(arguments[0].AuthoredNonFileSystemValue, "a", "b");
        AssertUnknownFileSystem(arguments[0]);
        AssertNonFileSystemExact(arguments[1], "x");
        AssertUnknownFileSystem(arguments[1]);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void PowerShell_tr_arguments_do_not_gain_bash_semantics(PwshDialect dialect)
    {
        var result = PowerShell(dialect).Parse("tr -d '\\n'");

        var arguments = Assert.Single(result.Commands).Arguments;
        Assert.All(arguments, AssertUnknownNonFileSystem);
        Assert.True(arguments[^1].Argument.IsPath);
    }

    [Fact]
    public void Audited_filesystem_and_non_filesystem_domains_are_mutually_exclusive()
    {
        var argument = new AnalyzedArgument
        {
            AuthoredFileSystemValue = new ShellValueDomain.Exact("/work/a.txt"),
            AuthoredNonFileSystemValue = new ShellValueDomain.Exact("data"),
        };

        Assert.False(AuthoredOperandSemanticsProjection.HasValidDomains([argument]));
    }

    [Fact]
    public void Unsupported_authored_operand_domains_fail_closed()
    {
        ShellValueDomain[] unsupported =
        [
            new ShellValueDomain.IntegerRange(0, 1),
            new ShellValueDomain.Concatenation(
            [
                new ShellValueDomain.Exact("a"),
                new ShellValueDomain.Exact("b"),
            ]),
            new ShellValueDomain.PathPattern("*.txt", "/work"),
        ];

        foreach (var domain in unsupported)
        {
            Assert.False(AuthoredOperandSemanticsProjection.HasValidDomains(
            [
                new AnalyzedArgument { AuthoredFileSystemValue = domain },
            ]));
            Assert.False(AuthoredOperandSemanticsProjection.HasValidDomains(
            [
                new AnalyzedArgument { AuthoredNonFileSystemValue = domain },
            ]));
        }
    }

    [Fact]
    public void Unsupported_domain_rejects_the_entire_audited_projection()
    {
        var command = Assert.Single(Bash().Parse("tr a b").Commands);
        var corrupted = command.Arguments
            .Select((argument, index) => index == 0
                ? argument with
                {
                    AuthoredFileSystemValue = new ShellValueDomain.IntegerRange(0, 1),
                }
                : argument)
            .ToArray();

        var projected = AuthoredOperandSemanticsProjection.Apply(
            ShellProjectionLanguage.Bash,
            command.Clause,
            Array.Empty<ShellValueElementProvenance>(),
            ShellValueDomainFacts.Unknown,
            corrupted);

        Assert.Empty(projected);
    }

    [Theory]
    [InlineData("cat README.md", "/work/README.md")]
    [InlineData("cat /work/README.md", "/work/README.md")]
    [InlineData("cat \"/work/file name.txt\"", "/work/file name.txt")]
    public void Audited_bash_cat_operands_publish_normalized_local_paths(
        string source,
        string expected)
    {
        var result = Bash().Parse(source);

        AssertExact(Assert.Single(Assert.Single(result.Commands).Arguments), expected);
    }

    [Theory]
    [InlineData("cat -")]
    [InlineData("cat -- -")]
    [InlineData("cat *.txt")]
    public void Cat_stream_and_active_glob_arguments_remain_unknown(string source)
    {
        var result = Bash().Parse(source);

        AssertUnknown(Assert.Single(result.Commands).Arguments.Last());
    }

    [Fact]
    public void Cat_option_state_preserves_only_proved_operands()
    {
        var result = Bash().Parse("cat -n README -- -n");

        var arguments = Assert.Single(result.Commands).Arguments;
        Assert.Equal(4, arguments.Count);
        AssertUnknown(arguments[0]);
        AssertExact(arguments[1], "/work/README");
        AssertUnknown(arguments[2]);
        AssertExact(arguments[3], "/work/-n");
    }

    [Fact]
    public void Bash_native_verb_binding_is_case_sensitive()
    {
        var result = Bash().Parse("CAT README");

        Assert.All(Assert.Single(result.Commands).Arguments, AssertUnknown);
    }

    [Theory]
    [InlineData("show /api/v1")]
    [InlineData("python -c 'print(1)'")]
    [InlineData("test 1 -eq 1")]
    [InlineData("head -n 10 README")]
    [InlineData("scp user@example.invalid:/srv/file .")]
    [InlineData("curl --output=- https://example.invalid")]
    [InlineData("curl --data=/api/v1 https://example.invalid")]
    [InlineData("tar --file=- -cf - README")]
    public void Compatibility_path_heuristics_do_not_publish_the_strong_fact(string source)
    {
        var result = Bash().Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.All(result.Commands.SelectMany(command => command.Arguments), AssertUnknown);
    }

    [Fact]
    public void D14_publishes_a_finite_authored_filesystem_domain()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in src/A.cs src/B.cs; do cat /work/$f; done");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        Assert.False(argument.Argument.IsPath);
        AssertFinite(
            argument.AuthoredFileSystemValue,
            "/work/src/A.cs",
            "/work/src/B.cs");
    }

    [Fact]
    public void Unquoted_field_split_candidate_remains_unknown()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in 'src/A.cs /etc/passwd'; do cat /work/$f; done");

        AssertUnknown(Assert.Single(Assert.Single(result.Commands).Arguments));
    }

    [Fact]
    public void Unquoted_empty_expansion_does_not_claim_one_filesystem_argument()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in ''; do cat $f; done");

        AssertUnknown(Assert.Single(Assert.Single(result.Commands).Arguments));
    }

    [Fact]
    public void Quoted_binding_text_is_not_recursively_expanded()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in '$HOME/literal'; do cat \"$f\"; done");

        AssertExact(
            Assert.Single(Assert.Single(result.Commands).Arguments),
            "/work/$HOME/literal");
    }

    [Fact]
    public void Quoted_and_unquoted_binding_globs_have_different_provenance()
    {
        var quoted = Bash(authoredFacts: true).Parse(
            "for f in '*.txt'; do cat \"$f\"; done");
        var unquoted = Bash(authoredFacts: true).Parse(
            "for f in '*.txt'; do cat $f; done");

        AssertExact(
            Assert.Single(Assert.Single(quoted.Commands).Arguments),
            "/work/*.txt");
        AssertUnknown(Assert.Single(Assert.Single(unquoted.Commands).Arguments));
    }

    [Fact]
    public void All_path_loop_visits_join_to_a_finite_domain()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in README LICENSE; do cat \"$f\"; done");

        AssertFinite(
            Assert.Single(Assert.Single(result.Commands).Arguments)
                .AuthoredFileSystemValue,
            "/work/README",
            "/work/LICENSE");
    }

    [Fact]
    public void Option_and_path_loop_visits_join_to_unknown()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in -n README; do cat \"$f\"; done");

        AssertUnknown(Assert.Single(Assert.Single(result.Commands).Arguments));
    }

    [Fact]
    public void Runtime_iterator_keeps_cat_filesystem_value_unknown()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in $(find /work -name '*.cs'); do cat \"$f\"; done");

        var cat = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Joined == "cat");
        AssertUnknown(Assert.Single(cat.Arguments));
    }

    [Theory]
    [InlineData("IFS=/; for f in a b; do cat \"$f\"; done")]
    [InlineData("for f in a b; do \"$f\" README; done")]
    public void State_mutation_and_dynamic_identity_do_not_gain_the_fact(string source)
    {
        var result = Bash(authoredFacts: true).Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Authored_operand_fact_does_not_relax_an_unresolved_redirect()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for f in a b; do cat \"$f\" > \"$f\"; done");

        var command = Assert.Single(result.Commands);
        AssertFinite(
            Assert.Single(command.Arguments).AuthoredFileSystemValue,
            "/work/a",
            "/work/b");
        Assert.IsType<ShellValueDomain.Unknown>(
            Assert.IsType<FileRedirectAnalysis>(Assert.Single(command.Redirects)).Target);
    }

    [Fact]
    public void Authored_operand_fact_does_not_relax_command_substitution()
    {
        var result = Bash().Parse("cat \"$(printf README)\"");

        Assert.Contains(
            result.Commands,
            command => command.ImmediateRole == CommandOccurrenceRole.Substitution);
        var cat = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Joined == "cat");
        AssertUnknown(Assert.Single(cat.Arguments));
    }

    [Theory]
    [InlineData("cat README |")]
    [InlineData("if cat README; then echo ok; fi")]
    public void Incomplete_or_unsupported_control_flow_publishes_no_filesystem_fact(
        string source)
    {
        var result = Bash(authoredFacts: true).Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Unknown_occurrence_cwd_keeps_relative_operand_unknown()
    {
        var result = Bash(authoredFacts: true).Parse(
            "for d in /work /tmp; do cd \"$d\"; cat README; done");

        var cat = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Joined == "cat");
        Assert.IsType<ShellValueDomain.Unknown>(cat.WorkingDirectory);
        AssertUnknown(Assert.Single(cat.Arguments));
    }

    [Fact]
    public void Over_limit_authored_union_remains_unknown()
    {
        var values = Enumerable.Range(1, ShellAnalysisLimits.MaxValueCandidates + 1)
            .Select(index => $"f{index:00}");
        var result = Bash(authoredFacts: true).Parse(
            $"for f in {string.Join(" ", values)}; do cat \"$f\"; done");

        AssertUnknown(Assert.Single(Assert.Single(result.Commands).Arguments));
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void PowerShell_literal_path_uses_the_selected_dialect(PwshDialect dialect)
    {
        var result = PowerShell(dialect).Parse(
            "Get-Content -LiteralPath C:\\work\\a.txt");

        AssertExact(Assert.Single(result.Commands).Arguments.Last(), "C:/work/a.txt");
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void PowerShell_inline_literal_path_preserves_flag_value_coordinates(
        PwshDialect dialect)
    {
        var result = PowerShell(dialect).Parse(
            "Get-Content -LiteralPath:C:\\work\\a.txt");

        var arguments = Assert.Single(result.Commands).Arguments;
        Assert.Equal(2, arguments.Count);
        AssertUnknown(arguments[0]);
        AssertExact(arguments[1], "C:/work/a.txt");
        Assert.Same(arguments[0].Element, arguments[1].Element);
    }

    [Theory]
    [InlineData("Get-ChildItem C:\\work *.cs")]
    [InlineData("Rename-Item C:\\old new")]
    [InlineData("Get-Content -LiteralPath Registry::HKEY_LOCAL_MACHINE")]
    [InlineData("scp user@example.invalid:/srv/file .")]
    [InlineData("curl --output=- https://example.invalid")]
    [InlineData("Write-Output /api/v1")]
    public void PowerShell_ambiguous_or_nonlocal_positions_remain_unknown(string source)
    {
        var result = PowerShell(PwshDialect.PowerShell7).Parse(source);

        Assert.False(result.IsUnparseable);
        Assert.All(result.Commands.SelectMany(command => command.Arguments), AssertUnknown);
    }

    [Theory]
    [InlineData("Get-Content -LiteralPath -Force C:\\work\\a.txt")]
    [InlineData("Get-Content -LiteralPath")]
    [InlineData("Get-Content -Lit C:\\work\\a.txt")]
    public void PowerShell_incomplete_literal_path_binding_remains_unknown(string source)
    {
        var result = PowerShell(PwshDialect.PowerShell7).Parse(source);

        Assert.All(result.Commands.SelectMany(command => command.Arguments), AssertUnknown);
    }

    private static BashParser Bash(bool authoredFacts = false) => new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        PublishAuthoredSourceFacts = authoredFacts,
    });

    private static PwshParser PowerShell(PwshDialect dialect) => new(new PwshParserOptions
    {
        WorkingDirectory = "C:/work",
        Dialect = dialect,
    });

    private static void AssertUnknown(AnalyzedArgument argument) =>
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredFileSystemValue);

    private static void AssertUnknownFileSystem(AnalyzedArgument argument) =>
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredFileSystemValue);

    private static void AssertUnknownNonFileSystem(AnalyzedArgument argument) =>
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredNonFileSystemValue);

    private static void AssertNonFileSystemExact(
        AnalyzedArgument argument,
        string expected) =>
        Assert.Equal(
            expected,
            Assert.IsType<ShellValueDomain.Exact>(
                argument.AuthoredNonFileSystemValue).Value);

    private static void AssertExact(AnalyzedArgument argument, string expected) =>
        Assert.Equal(
            expected,
            Assert.IsType<ShellValueDomain.Exact>(argument.AuthoredFileSystemValue).Value);

    private static void AssertFinite(ShellValueDomain domain, params string[] expected) =>
        Assert.Equal(expected, Assert.IsType<ShellValueDomain.FiniteSet>(domain).Values);
}
