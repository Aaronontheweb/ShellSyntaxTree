// -----------------------------------------------------------------------
// <copyright file="BashFiniteScopeProjectionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class BashFiniteScopeProjectionTests
{
    private static readonly BashParser Parser = new(new BashParserOptions
    {
        WorkingDirectory = "/work",
    });

    [Fact]
    public void A_static_list_has_exact_path_facts_in_each_reachable_scope()
    {
        const string source = "cd /work/sub && cat result.txt | sed -n '1p'; ls .";

        Assert.True(Parser.TryProjectFiniteScopes(source, out var projection));

        Assert.NotNull(projection);
        Assert.Equal(5, projection.Commands.Count);
        Assert.All(projection.Commands, scoped =>
        {
            Assert.Equal(scoped.Source,
                source.Substring(scoped.SourceStart, scoped.Source.Length));
            Assert.Equal(scoped.WorkingDirectory,
                Assert.IsType<ShellValueDomain.Exact>(
                    scoped.ScopedOccurrence.WorkingDirectory).Value);
        });
        Assert.Equal("/work/sub", projection.Commands.Single(scoped =>
            scoped.Source == "cat result.txt").WorkingDirectory);
        Assert.Equal(new[] { "/work", "/work/sub" }, projection.Commands
            .Where(scoped => scoped.Source == "ls .")
            .Select(scoped => scoped.WorkingDirectory)
            .OrderBy(value => value));
        Assert.Equal("/work/sub/result.txt", projection.Commands.Single(scoped =>
            scoped.Source == "cat result.txt").ScopedOccurrence.Arguments[0].Argument.Resolved);
    }

    [Fact]
    public void A_failed_directory_change_keeps_the_original_directory()
    {
        Assert.True(Parser.TryProjectFiniteScopes(
            "cd /work/sub && true; touch marker.txt", out var projection));

        Assert.Equal(new[] { "/work", "/work/sub" }, projection!.Commands
            .Where(scoped => scoped.Source == "touch marker.txt")
            .Select(scoped => scoped.WorkingDirectory)
            .OrderBy(value => value));
        Assert.Equal(new[] { "/work/marker.txt", "/work/sub/marker.txt" },
            projection.Commands
                .Where(scoped => scoped.Source == "touch marker.txt")
                .Select(scoped => scoped.ScopedOccurrence.Arguments[0].Argument.Resolved)
                .OrderBy(value => value));
    }

    [Fact]
    public void A_glob_path_does_not_become_exact_inside_an_exact_directory()
    {
        Assert.True(Parser.TryProjectFiniteScopes(
            "cd /work/sub && cat */result.txt; ls .", out var projection));
        var scoped = projection!.Commands.Single(command =>
            command.Source == "cat */result.txt");
        Assert.Equal("/work/sub", scoped.WorkingDirectory);
        Assert.True(scoped.ScopedOccurrence.Arguments[0].Value is
            ShellValueDomain.Unknown or ShellValueDomain.PathPattern);
    }

    [Fact]
    public void Too_many_reachable_directories_have_no_projection()
    {
        var source = string.Join("; ", Enumerable.Range(0, 33)
            .Select(index => $"cd /work/sub{index}")) + "; ls .";

        Assert.False(Parser.TryProjectFiniteScopes(source, out var projection));
        Assert.Null(projection);
    }

    [Theory]
    [InlineData("cd \"$target\" && cat file.txt; ls .")]
    [InlineData("cd /work/sub && cat \"$target\"; ls .")]
    [InlineData("cd /work/sub | cat; ls .")]
    [InlineData("for d in /work/sub; do cd \"$d\"; done; ls .")]
    [InlineData("cd /work/sub && cat $(date); ls .")]
    public void An_unproved_scope_has_no_finite_projection(string source)
    {
        Assert.False(Parser.TryProjectFiniteScopes(source, out var projection));
        Assert.Null(projection);
    }
}
