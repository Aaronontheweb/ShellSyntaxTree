// -----------------------------------------------------------------------
// <copyright file="BashAuthoredValueTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class BashAuthoredValueTests
{
    [Fact]
    public void Quoted_exit_status_is_an_inclusive_integer_range()
    {
        var result = new BashParser().Parse("status-report \"$?\"");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        var range = Assert.IsType<ShellValueDomain.IntegerRange>(argument.Value);
        Assert.Equal(0, range.MinimumInclusive);
        Assert.Equal(255, range.MaximumInclusive);
        Assert.Equal(argument.Value, argument.AuthoredValue);
    }

    [Fact]
    public void Embedded_quoted_exit_status_preserves_literal_parts()
    {
        var result = new BashParser().Parse("echo \"---EXIT $?---\"");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        var concatenation = Assert.IsType<ShellValueDomain.Concatenation>(argument.Value);
        Assert.Collection(
            concatenation.Parts,
            part => Assert.Equal("---EXIT ", Assert.IsType<ShellValueDomain.Exact>(part).Value),
            part =>
            {
                var range = Assert.IsType<ShellValueDomain.IntegerRange>(part);
                Assert.Equal(0, range.MinimumInclusive);
                Assert.Equal(255, range.MaximumInclusive);
            },
            part => Assert.Equal("---", Assert.IsType<ShellValueDomain.Exact>(part).Value));
    }

    [Theory]
    [InlineData(
        "gh run view 123456 --repo example/project --log-failed --verbose 2>&1 | head -200; echo \"---EXIT $?---\"",
        "---EXIT ",
        "---")]
    [InlineData(
        "cd /work && git fetch upstream feature/update 2>&1 | tail -2 && echo \"===REMOTE TIP===\" && git rev-parse FETCH_HEAD && git log --oneline -3 FETCH_HEAD && echo \"===HAS FIX?===\" && git show FETCH_HEAD:src/App/App.csproj | grep -n \"ProtocolPackage\"; echo \"exit: $?\"",
        "exit: ",
        null)]
    public void Harvested_status_cases_publish_symbolic_data(
        string source,
        string prefix,
        string? suffix)
    {
        var result = new BashParser().Parse(source);

        Assert.False(result.IsUnparseable);
        var echo = result.Commands[result.Commands.Count - 1];
        var argument = Assert.Single(echo.Arguments);
        var concatenation = Assert.IsType<ShellValueDomain.Concatenation>(argument.Value);
        Assert.Equal(prefix, Assert.IsType<ShellValueDomain.Exact>(concatenation.Parts[0]).Value);
        Assert.IsType<ShellValueDomain.IntegerRange>(concatenation.Parts[1]);
        if (suffix is null)
        {
            Assert.Equal(2, concatenation.Parts.Count);
        }
        else
        {
            Assert.Equal(
                suffix,
                Assert.IsType<ShellValueDomain.Exact>(concatenation.Parts[2]).Value);
        }
    }

    [Fact]
    public void Multiple_quoted_status_expansions_remain_symbolic()
    {
        var result = new BashParser().Parse("status-report \"left=$?;right=$?\"");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        var concatenation = Assert.IsType<ShellValueDomain.Concatenation>(argument.Value);
        Assert.Equal(4, concatenation.Parts.Count);
        Assert.Equal(2, CountIntegerRanges(concatenation));
    }

    [Theory]
    [InlineData("status-report $?", typeof(ShellValueDomain.Unknown))]
    [InlineData("status-report '$?'", typeof(ShellValueDomain.Exact))]
    [InlineData("status-report \"$1\"", typeof(ShellValueDomain.Unknown))]
    public void Non_quoted_status_boundaries_keep_existing_results(
        string source,
        Type expectedType)
    {
        var result = new BashParser().Parse(source);

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        Assert.IsType(expectedType, argument.Value);
    }

    [Fact]
    public void Exit_status_cannot_select_the_command_identity()
    {
        var result = new BashParser().Parse("\"$?\" --version");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Exit_status_cannot_name_a_redirect_target()
    {
        var result = new BashParser().Parse("status-report ok > \"$?\"");

        var command = Assert.Single(result.Commands);
        var redirect = Assert.Single(command.Redirects);
        Assert.IsType<ShellValueDomain.Unknown>(
            Assert.IsType<FileRedirectAnalysis>(redirect).Target);
    }

    [Fact]
    public void PowerShell_authored_projection_is_compatible()
    {
        var result = new PwshParser().Parse("Write-Output 'C:/work/file.txt'");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        Assert.Equal(argument.Value, argument.AuthoredValue);
        Assert.Equal(ShellPathShape.Unknown, argument.AuthoredPathShape);
    }

    [Fact]
    public void Integer_range_rejects_reversed_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ShellValueDomain.IntegerRange(1, 0));
    }

    [Fact]
    public void Concatenation_factory_normalizes_literal_parts()
    {
        var domain = ShellValueDomainFacts.Concatenate(new[]
        {
            Exact(string.Empty),
            Exact("prefix"),
            Exact("-"),
            ShellValueDomainFacts.IntegerRange(0, 255),
            Exact(string.Empty),
        });

        Assert.Equal(ShellValueDomainKind.Concatenation, domain.Kind);
        Assert.Collection(
            domain.Parts,
            part => Assert.Equal("prefix-", Assert.Single(part.Values)),
            part => Assert.Equal(ShellValueDomainKind.IntegerRange, part.Kind));
    }

    [Fact]
    public void Concatenation_factory_rejects_unbounded_or_oversized_parts()
    {
        Assert.Same(
            ShellValueDomainFacts.Unknown,
            ShellValueDomainFacts.Concatenate(new[]
            {
                Exact("prefix"),
                ShellValueDomainFacts.Unknown,
            }));
        Assert.Same(
            ShellValueDomainFacts.Unknown,
            ShellValueDomainFacts.Concatenate(new[]
            {
                new ShellValueDomainFacts { Kind = ShellValueDomainKind.Exact },
                ShellValueDomainFacts.IntegerRange(0, 255),
            }));

        var parts = new List<ShellValueDomainFacts>();
        for (var index = 0; index < 17; index++)
        {
            parts.Add(ShellValueDomainFacts.IntegerRange(index, index));
        }

        Assert.Same(
            ShellValueDomainFacts.Unknown,
            ShellValueDomainFacts.Concatenate(parts));
    }

    [Fact]
    public void Internal_domain_equality_includes_range_bounds_and_parts()
    {
        var zeroToOne = ShellValueDomainFacts.IntegerRange(0, 1);
        var zeroToTwo = ShellValueDomainFacts.IntegerRange(0, 2);
        var left = ShellValueDomainFacts.Concatenate(new[]
        {
            Exact("status="),
            zeroToOne,
        });
        var same = ShellValueDomainFacts.Concatenate(new[]
        {
            Exact("status="),
            ShellValueDomainFacts.IntegerRange(0, 1),
        });
        var different = ShellValueDomainFacts.Concatenate(new[]
        {
            Exact("status="),
            zeroToTwo,
        });

        Assert.True(ShellValueDomainFacts.AreEqual(left, same));
        Assert.False(ShellValueDomainFacts.AreEqual(left, different));
    }

    [Fact]
    public void Default_options_preserve_static_loop_rejection()
    {
        var result = new BashParser().Parse("for f in a b; do show \"$f\"; done");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Opt_in_loop_publishes_authored_value_without_effective_value()
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });

        var result = parser.Parse("for f in a b; do show \"$f\"; done");

        Assert.False(result.IsUnparseable);
        var command = Assert.Single(result.Commands);
        Assert.True(command.IsComplete);
        var argument = Assert.Single(command.Arguments);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        AssertFinite(argument.AuthoredValue, "a", "b");
        Assert.Equal(ShellPathShape.Unknown, argument.AuthoredPathShape);
    }

    [Fact]
    public void Opt_in_loop_composes_unquoted_words_before_field_splitting()
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });

        var result = parser.Parse(
            "for f in src/A.cs src/B.cs; do cat /work/$f; done");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        AssertFinite(argument.AuthoredValue, "/work/src/A.cs", "/work/src/B.cs");
        Assert.Equal(ShellPathShape.Posix, argument.AuthoredPathShape);
    }

    [Fact]
    public void Harvested_loop_case_publishes_four_authored_paths()
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });
        const string source =
            "for f in src/App/App.csproj src/Hosting/Hosting.csproj " +
            "src/Discovery/Discovery.csproj tests/Hosting.Tests/Hosting.Tests.csproj; " +
            "do echo \"=== $f ===\"; cat /work/$f; done";

        var result = parser.Parse(source);

        Assert.False(result.IsUnparseable);
        var cat = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Joined == "cat");
        var argument = Assert.Single(cat.Arguments);
        Assert.IsType<ShellValueDomain.Unknown>(argument.Value);
        AssertFinite(
            argument.AuthoredValue,
            "/work/src/App/App.csproj",
            "/work/src/Hosting/Hosting.csproj",
            "/work/src/Discovery/Discovery.csproj",
            "/work/tests/Hosting.Tests/Hosting.Tests.csproj");
        Assert.Equal(ShellPathShape.Posix, argument.AuthoredPathShape);
    }

    [Fact]
    public void Isolated_loop_keeps_effective_proof()
    {
        var parser = new BashParser(new BashParserOptions
        {
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        });

        var result = parser.Parse("for f in a b; do show \"$f\"; done");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        AssertFinite(argument.Value, "a", "b");
        Assert.Equal(argument.Value, argument.AuthoredValue);
    }

    [Theory]
    [InlineData("C:/work/file.txt", ShellPathShape.Windows)]
    [InlineData("C:\\work\\file.txt", ShellPathShape.Windows)]
    [InlineData("/work/file.txt", ShellPathShape.Posix)]
    [InlineData("./file.txt", ShellPathShape.Posix)]
    [InlineData("../file.txt", ShellPathShape.Posix)]
    [InlineData("work/file.txt", ShellPathShape.Posix)]
    [InlineData("example/project", ShellPathShape.Posix)]
    [InlineData("https://example.invalid/api/v1", ShellPathShape.Unknown)]
    [InlineData("bare-name", ShellPathShape.Unknown)]
    public void Bash_arguments_publish_lexical_shape_only(
        string value,
        ShellPathShape expected)
    {
        var result = new BashParser().Parse($"show '{value}'");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        Assert.Equal(expected, argument.AuthoredPathShape);
    }

    [Theory]
    [InlineData("~/repo/A.cs", "~/repo/B.cs")]
    [InlineData("/home/user/repo/A.cs", "/home/user/repo/B.cs")]
    public void Opt_in_loop_preserves_quoted_tilde_or_expands_eligible_tilde(
        string first,
        string second)
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
            HomeDirectory = "/home/user",
        });
        var quoted = first[0] == '~';
        var word = quoted ? "\"~/repo/$f\"" : "~/\"repo/$f\"";

        var result = parser.Parse($"for f in A.cs B.cs; do cat {word}; done");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        AssertFinite(argument.AuthoredValue, first, second);
        Assert.Equal(ShellPathShape.Posix, argument.AuthoredPathShape);
    }

    [Fact]
    public void Mixed_path_shapes_and_partially_unknown_sets_are_unknown()
    {
        var mixed = new ShellValueDomain.FiniteSet(
            new[] { "/tmp/a", "C:\\work\\b" });
        var partial = new ShellValueDomain.FiniteSet(
            new[] { "/tmp/a", "bare-name" });

        Assert.Equal(ShellPathShape.Unknown, ShellPathShapeClassifier.Classify(mixed));
        Assert.Equal(ShellPathShape.Unknown, ShellPathShapeClassifier.Classify(partial));
    }

    [Fact]
    public void Path_prefixed_concatenation_has_a_uniform_shape()
    {
        var value = new ShellValueDomain.Concatenation(new ShellValueDomain[]
        {
            new ShellValueDomain.Exact("/tmp/status-"),
            new ShellValueDomain.IntegerRange(0, 255),
        });

        Assert.Equal(ShellPathShape.Posix, ShellPathShapeClassifier.Classify(value));
    }

    [Fact]
    public void Path_prefixed_concatenation_does_not_ignore_windows_suffixes()
    {
        var value = new ShellValueDomain.Concatenation(new ShellValueDomain[]
        {
            new ShellValueDomain.Exact("/tmp/"),
            new ShellValueDomain.FiniteSet(new[] { "a", "C:\\work\\b" }),
        });

        Assert.Equal(ShellPathShape.Unknown, ShellPathShapeClassifier.Classify(value));
    }

    [Fact]
    public void Unassigned_ambient_name_remains_strict_with_opt_in()
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });

        var result = parser.Parse("show \"$external_value\"");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Theory]
    [InlineData("declare -n f=target; for f in a b; do show \"$f\"; done")]
    [InlineData("declare -i f=0; for f in a b; do show \"$f\"; done")]
    [InlineData("IFS=/; for f in a b; do show \"$f\"; done")]
    public void Explicit_source_state_mutation_remains_strict(string source)
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });

        var result = parser.Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void Option_shaped_authored_values_are_reported_only_as_data()
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });

        var result = parser.Parse("for f in -o --execute; do show \"$f\"; done");

        var argument = Assert.Single(Assert.Single(result.Commands).Arguments);
        AssertFinite(argument.AuthoredValue, "-o", "--execute");
        Assert.Equal(ShellPathShape.Unknown, argument.AuthoredPathShape);
    }

    [Fact]
    public void Runtime_iterator_is_visible_but_authored_value_is_unknown()
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });

        var result = parser.Parse(
            "for f in $(find /work -name '*.cs'); do cat \"$f\"; done");

        Assert.False(result.IsUnparseable);
        Assert.Contains(
            result.Commands,
            command => command.Clause.Verb.Joined == "find" &&
                       command.ImmediateRole == CommandOccurrenceRole.Substitution);
        var cat = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Joined == "cat");
        var argument = Assert.Single(cat.Arguments);
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredValue);
        Assert.Equal(ShellPathShape.Unknown, argument.AuthoredPathShape);
    }

    [Theory]
    [InlineData("for f in echo; do \"$f\" safe; done")]
    [InlineData("for f in 'echo hidden'; do eval \"$f\"; done")]
    public void Authored_loop_fact_does_not_relax_hidden_command_identity(string source)
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });

        var result = parser.Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Theory]
    [InlineData("Write-Output $name")]
    [InlineData("Get-Content C:\\work\\file.txt")]
    [InlineData("Get-Item Env:PATH")]
    public void PowerShell_all_argument_categories_keep_compatible_projection(string source)
    {
        var result = new PwshParser().Parse(source);

        Assert.False(result.IsUnparseable);
        foreach (var argument in Assert.Single(result.Commands).Arguments)
        {
            Assert.Equal(argument.Value, argument.AuthoredValue);
            Assert.Equal(ShellPathShape.Unknown, argument.AuthoredPathShape);
        }
    }

    [Fact]
    public void Authored_only_loop_does_not_resolve_redirect_target()
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
        });

        var result = parser.Parse("for f in a b; do show ok > \"$f\"; done");

        var redirect = Assert.Single(Assert.Single(result.Commands).Redirects);
        Assert.IsType<ShellValueDomain.Unknown>(
            Assert.IsType<FileRedirectAnalysis>(redirect).Target);
    }

    [Fact]
    public void Authored_only_loop_does_not_resolve_cwd_transfer()
    {
        var parser = new BashParser(new BashParserOptions
        {
            PublishAuthoredSourceFacts = true,
            WorkingDirectory = "/work",
        });

        var result = parser.Parse("for f in a b; do cd \"$f\"; pwd; done");

        Assert.False(result.IsUnparseable);
        var pwd = Assert.Single(
            result.Commands,
            command => command.Clause.Verb.Joined == "pwd");
        Assert.IsType<ShellValueDomain.Unknown>(pwd.WorkingDirectory);
    }

    private static int CountIntegerRanges(ShellValueDomain.Concatenation value)
    {
        var count = 0;
        foreach (var part in value.Parts)
        {
            if (part is ShellValueDomain.IntegerRange)
            {
                count++;
            }
        }

        return count;
    }

    private static void AssertFinite(ShellValueDomain domain, params string[] expected)
    {
        var finite = Assert.IsType<ShellValueDomain.FiniteSet>(domain);
        Assert.Equal(expected, finite.Values);
    }

    private static ShellValueDomainFacts Exact(string value) => new()
    {
        Kind = ShellValueDomainKind.Exact,
        Values = new[] { value },
    };
}
