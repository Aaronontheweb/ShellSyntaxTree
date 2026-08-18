// -----------------------------------------------------------------------
// <copyright file="FreshSessionApprovalFactTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class FreshSessionApprovalFactTests
{
    public static TheoryData<string, string, bool, string, string, int> SampledCommands => new()
    {
        {
            "git status && git branch --show-current && git log --oneline -5",
            "/work/project",
            false,
            "git status|git branch|git log",
            "Ordinary|Ordinary|Ordinary",
            0
        },
        {
            "git push origin topic && git push upstream topic && git push upstream deploy-example",
            "/work/project",
            false,
            "git push origin topic|git push upstream topic|git push upstream deploy-example",
            "Ordinary|Ordinary|Ordinary",
            0
        },
        {
            "docker compose -f docker-compose.local.yml up -d database service",
            "/work/project",
            false,
            "docker compose up",
            "Ordinary",
            0
        },
        {
            "git -C /work/project log --oneline -8 dev -- src/Client.cs; "
            + "git -C /work/project merge-base dev topic",
            "/session",
            false,
            "git log|git merge-base dev topic",
            "Ordinary|Ordinary",
            0
        },
        {
            "cat /work/project/src/Project.csproj 2>/dev/null; "
            + "echo \"---PACKAGE---\"; "
            + "rg -n \"PackageVersion\" /work/project/Directory.Packages.props "
            + "/work/project/src/*.csproj 2>/dev/null",
            "/session",
            false,
            "cat|echo|rg",
            "Ordinary|Ordinary|Ordinary",
            2
        },
        {
            "curl -sL https://service.example.invalid/tree "
            + "-o /external/cache/tree.json 2>&1; "
            + "python3 -c \"import json; data=json.load(open('/external/cache/tree.json')); "
            + "print([item['path'] for item in data.get('tree', [])])\" | head -30",
            "/session",
            false,
            "curl|python3|head",
            "Ordinary|PipelineStage|PipelineStage",
            1
        },
        {
            "PRIMARY_BRANCH=$(git symbolic-ref refs/remotes/origin/HEAD 2>/dev/null "
            + "| sed 's@^refs/remotes/origin/@@'); "
            + "[ -z \"$PRIMARY_BRANCH\" ] && PRIMARY_BRANCH=$(git branch --show-current); "
            + "echo \"$PRIMARY_BRANCH\"; git remote -v",
            "/session",
            true,
            string.Empty,
            string.Empty,
            0
        },
        {
            "for file in /external/cache/reminders/*.history.jsonl; "
            + "do jq -c -r 'select(.success == false)' \"$file\" 2>/dev/null "
            + "| tail -1; done | sort",
            "/session",
            false,
            "jq|tail|sort",
            "PipelineStage|PipelineStage|PipelineStage",
            1
        },
        {
            "grep -rn \"Mode B\" docs/ *.md 2>/dev/null | head -20",
            "/work/project",
            false,
            "grep|head",
            "PipelineStage|PipelineStage",
            1
        },
        {
            "cat /protected/control.json",
            "/session",
            false,
            "cat",
            "Ordinary",
            0
        },
    };

    [Theory]
    [MemberData(nameof(SampledCommands))]
    public void Sampled_commands_publish_expected_occurrence_shape(
        string source,
        string workingDirectory,
        bool expectedUnparseable,
        string expectedVerbs,
        string expectedRoles,
        int expectedRedirects)
    {
        var result = Parse(source, workingDirectory);

        Assert.Equal(expectedUnparseable, result.IsUnparseable);
        Assert.Equal(Split(expectedVerbs), result.Commands.Select(CommandVerb));
        Assert.Equal(
            Split(expectedRoles),
            result.Commands.Select(command => command.ImmediateRole.ToString()));
        Assert.Equal(expectedRedirects, result.Commands.Sum(command => command.Redirects.Count));

        Assert.All(result.Commands, command =>
        {
            Assert.True(command.IsComplete);
            Assert.Equal(
                workingDirectory,
                Assert.IsType<ShellValueDomain.Exact>(command.WorkingDirectory).Value);
            Assert.IsType<ShellWorkingDirectoryEffect.Unchanged>(command.WorkingDirectoryEffect);
            Assert.All(
                command.Arguments,
                argument => Assert.True(argument.Element.PrecedingVerbElementCount > 0));
        });
    }

    [Fact]
    public void Git_global_directory_option_preserves_authored_order_and_path_shape()
    {
        var result = Parse(
            "git -C /work/project log --oneline -8 dev -- src/Client.cs; "
            + "git -C /work/project merge-base dev topic",
            "/session");
        var first = result.Commands[0];
        var option = Assert.Single(first.Arguments, argument => argument.Argument.Raw == "-C");
        var directory = Assert.Single(
            first.Arguments,
            argument => argument.Argument.Raw == "/work/project");

        Assert.Equal(1, option.Element.PrecedingVerbElementCount);
        Assert.Equal(1, directory.Element.PrecedingVerbElementCount);
        Assert.Equal(
            2,
            Assert.Single(first.Arguments, argument => argument.Argument.Raw == "--oneline")
                .Element.PrecedingVerbElementCount);
        Assert.True(directory.Argument.IsPath);
        Assert.Equal("/work/project", directory.Argument.Resolved);
        Assert.Equal(ShellPathShape.Posix, directory.AuthoredPathShape);
        Assert.Equal("/work/project", Assert.IsType<ShellValueDomain.Exact>(directory.Value).Value);
        Assert.Equal(
            "/work/project",
            Assert.IsType<ShellValueDomain.Exact>(directory.AuthoredValue).Value);
        Assert.IsType<ShellValueDomain.Unknown>(directory.AuthoredFileSystemValue);
    }

    [Theory]
    [InlineData("cat /work/project/src/Project.csproj", "/work/project/src/Project.csproj")]
    [InlineData("cat /protected/control.json", "/protected/control.json")]
    public void Audited_cat_operands_publish_strong_filesystem_values(
        string source,
        string expectedPath)
    {
        var argument = Assert.Single(Assert.Single(Parse(source, "/session").Commands).Arguments);

        Assert.Equal(ShellPathShape.Posix, argument.AuthoredPathShape);
        Assert.Equal(
            expectedPath,
            Assert.IsType<ShellValueDomain.Exact>(argument.AuthoredFileSystemValue).Value);
        Assert.IsType<ShellValueDomain.Unknown>(argument.AuthoredNonFileSystemValue);
    }

    [Fact]
    public void Loop_path_keeps_unknown_effective_value_and_bounded_authored_pattern()
    {
        var result = Parse(
            "for file in /external/cache/reminders/*.history.jsonl; "
            + "do jq -c -r 'select(.success == false)' \"$file\" 2>/dev/null "
            + "| tail -1; done | sort",
            "/session");
        var jq = result.Commands[0];
        var path = Assert.Single(jq.Arguments, argument => argument.Argument.Raw == "\"$file\"");

        Assert.IsType<ShellValueDomain.Unknown>(path.Value);
        Assert.IsType<ShellValueDomain.PathPattern>(path.AuthoredValue);
        Assert.IsType<ShellValueDomain.Unknown>(path.AuthoredFileSystemValue);
        Assert.IsType<ShellValueDomain.Unknown>(path.AuthoredNonFileSystemValue);
        AssertCompleteNullRedirect(jq);
    }

    [Fact]
    public void Recursive_search_keeps_static_structure_and_unknown_glob_domain()
    {
        var result = Parse(
            "grep -rn \"Mode B\" docs/ *.md 2>/dev/null | head -20",
            "/work/project");
        var grep = result.Commands[0];
        var directory = Assert.Single(grep.Arguments, argument => argument.Argument.Raw == "docs/");
        var glob = Assert.Single(grep.Arguments, argument => argument.Argument.Raw == "*.md");

        Assert.Equal(ShellPathShape.Posix, directory.AuthoredPathShape);
        Assert.Equal("/work/project/docs", directory.Argument.Resolved);
        Assert.Equal(ArgKind.Glob, glob.Argument.Kind);
        Assert.True(glob.Argument.IsPath);
        Assert.IsType<ShellValueDomain.Unknown>(glob.Value);
        Assert.IsType<ShellValueDomain.Unknown>(glob.AuthoredValue);
        AssertCompleteNullRedirect(grep);
    }

    [Fact]
    public void Descriptor_duplicate_remains_distinct_from_file_output()
    {
        var result = Parse(
            "curl -sL https://service.example.invalid/tree "
            + "-o /external/cache/tree.json 2>&1; "
            + "python3 -c \"print('done')\" | head -30",
            "/session");
        var redirect = Assert.IsType<DescriptorDuplicateRedirectAnalysis>(
            Assert.Single(result.Commands[0].Redirects));

        Assert.True(redirect.IsComplete);
        Assert.Equal(1, redirect.TargetDescriptor);
    }

    private static void AssertCompleteNullRedirect(CommandOccurrence command)
    {
        var redirect = Assert.IsType<FileRedirectAnalysis>(Assert.Single(command.Redirects));
        Assert.True(redirect.IsComplete);
        Assert.Equal(FileRedirectMode.Output, redirect.Mode);
        Assert.Equal(2, Assert.IsType<RedirectSource.Descriptor>(redirect.Source).Value);
        Assert.Equal("/dev/null", Assert.IsType<ShellValueDomain.Exact>(redirect.Target).Value);
    }

    private static ParsedCommand Parse(string source, string workingDirectory)
        => new BashParser(new BashParserOptions
        {
            WorkingDirectory = workingDirectory,
            InitialStateMode = BashInitialStateMode.Unknown,
            PublishAuthoredSourceFacts = true,
        }).Parse(source);

    private static string CommandVerb(CommandOccurrence command) => command.Clause.Verb.Joined;

    private static string[] Split(string value)
        => value.Split('|', StringSplitOptions.RemoveEmptyEntries);
}
