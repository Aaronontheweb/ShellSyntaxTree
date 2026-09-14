// -----------------------------------------------------------------------
// <copyright file="PwshFileSystemTreeAccessTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Linq;
using ShellSyntaxTree.Internal.Pwsh.Parsing;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class PwshFileSystemTreeAccessTests
{
    [Theory]
    [InlineData(PwshDialect.PowerShell7, ShellTreeTraversalMode.RecursiveWithoutFollowingLinks)]
    [InlineData(PwshDialect.WindowsPowerShell51, ShellTreeTraversalMode.RecursiveMayFollowLinks)]
    public void Exact_recursive_root_publishes_dialect_specific_tree_access(
        PwshDialect dialect,
        ShellTreeTraversalMode expected)
    {
        var command = Command("Get-ChildItem -Path C:\\repo\\src -Recurse", dialect);

        var access = Assert.Single(command.FileSystemTreeAccesses);
        var root = Assert.IsType<ShellValueDomain.Exact>(access.Root);
        Assert.Equal("C:/repo/src", root.Value);
        Assert.Equal(expected, access.Traversal);
        Assert.Same(PathArgument(command), access.RootArgument);
    }

    [Theory]
    [InlineData("Get-ChildItem C:\\repo\\src", ShellTreeTraversalMode.DirectChildren)]
    [InlineData("Get-ChildItem -Name C:\\repo\\src", ShellTreeTraversalMode.DirectChildren)]
    [InlineData("Get-ChildItem C:\\repo\\src -Recurse:$false", ShellTreeTraversalMode.DirectChildren)]
    [InlineData("Get-ChildItem C:\\repo\\src -Recurse:${false}", ShellTreeTraversalMode.DirectChildren)]
    [InlineData("Get-ChildItem C:\\repo\\src -Recurse:$true", ShellTreeTraversalMode.RecursiveWithoutFollowingLinks)]
    [InlineData("Get-ChildItem C:\\repo\\src -Depth 0", ShellTreeTraversalMode.DirectChildren)]
    [InlineData("Get-ChildItem C:\\repo\\src -Depth 3", ShellTreeTraversalMode.RecursiveWithoutFollowingLinks)]
    [InlineData("Get-ChildItem C:\\repo\\src -Recurse -FollowSymlink", ShellTreeTraversalMode.RecursiveMayFollowLinks)]
    [InlineData("Get-ChildItem C:\\repo\\src -Recurse -FollowSymlink:$false", ShellTreeTraversalMode.RecursiveWithoutFollowingLinks)]
    public void PowerShell7_exact_traversal_controls_are_projected(
        string source,
        ShellTreeTraversalMode expected)
    {
        var access = Assert.Single(Command(source, PwshDialect.PowerShell7).FileSystemTreeAccesses);

        Assert.Equal(expected, access.Traversal);
    }

    [Theory]
    [InlineData("Get-ChildItem C:\\repo -Depth 0", ShellTreeTraversalMode.DirectChildren)]
    [InlineData("Get-ChildItem C:\\repo -Depth 2", ShellTreeTraversalMode.RecursiveMayFollowLinks)]
    public void WindowsPowerShell51_depth_uses_its_pinned_link_behavior(
        string source,
        ShellTreeTraversalMode expected)
    {
        var access = Assert.Single(
            Command(source, PwshDialect.WindowsPowerShell51).FileSystemTreeAccesses);

        Assert.Equal(expected, access.Traversal);
    }

    [Theory]
    [InlineData("Get-ChildItem C:\\repo\\src -Recurse:$flag")]
    [InlineData("Get-ChildItem C:\\repo\\src -Depth $depth")]
    [InlineData("Get-ChildItem C:\\repo\\src -Recurse -FollowSymlink:$flag")]
    public void Dynamic_traversal_controls_publish_an_unknown_access(string source)
    {
        var command = Command(source, PwshDialect.PowerShell7);

        AssertUnknownMarker(command);
    }

    [Theory]
    [InlineData("Get-ChildItem C:\\repo -Recurse:$false -Recurse")]
    [InlineData("Get-ChildItem C:\\repo -FollowSymlink:$false -FollowSymlink -Recurse")]
    [InlineData("Get-ChildItem C:\\repo -Depth $depth -Depth 2")]
    [InlineData("Get-ChildItem C:\\repo -Recurse:'$false'")]
    [InlineData("Get-ChildItem C:\\repo -Recurse:\"$false\"")]
    [InlineData("Get-ChildItem C:\\repo -Recurse:`$false")]
    public void Duplicate_or_non_authentic_traversal_controls_are_unknown(string source)
    {
        AssertUnknownMarker(Command(source, PwshDialect.PowerShell7));
    }

    [Theory]
    [InlineData("Get-ChildItem -Path C:\\repo\\src\\*.cs", "C:\\repo\\src\\*.cs", "C:/repo/src")]
    [InlineData("Get-ChildItem -Path \"C:\\repo\\src\\*.cs\"", "C:\\repo\\src\\*.cs", "C:/repo/src")]
    [InlineData("Get-ChildItem -Path:\"C:\\repo\\src\\*.cs\"", "C:\\repo\\src\\*.cs", "C:/repo/src")]
    [InlineData("Get-ChildItem -Path C:\\*.cs", "C:\\*.cs", "C:/")]
    [InlineData("Get-ChildItem -Path \\\\server\\share\\*.cs", "\\\\server\\share\\*.cs", "//server/share")]
    [InlineData("Get-ChildItem *.cs", "*.cs", "C:/work")]
    public void Leaf_glob_publishes_pattern_and_covering_directory(
        string source,
        string expectedPattern,
        string expectedCover)
    {
        var command = Command(source, PwshDialect.PowerShell7);

        var access = Assert.Single(command.FileSystemTreeAccesses);
        var root = Assert.IsType<ShellValueDomain.PathPattern>(access.Root);
        Assert.Equal(expectedPattern, root.Pattern);
        Assert.Equal(expectedCover, root.CoveringDirectory);
        Assert.Same(PathArgument(command), access.RootArgument);
    }

    [Theory]
    [InlineData("Get-ChildItem -Path C:\\repo\\*\\src")]
    [InlineData("Get-ChildItem -Path C:\\one,C:\\two")]
    [InlineData("Get-ChildItem -Path $root")]
    [InlineData("Get-ChildItem -P C:\\repo")]
    [InlineData("Get-ChildItem -Path HKLM:\\Software")]
    [InlineData("Get-ChildItem -FollowSymlink C:\\repo")]
    [InlineData("Get-ChildItem -Path \\*.cs")]
    [InlineData("Get-ChildItem -Path /*.cs")]
    [InlineData("Get-ChildItem -Path C:*.cs")]
    [InlineData("Get-ChildItem C:*.cs")]
    [InlineData("Get-ChildItem -Path C:folder\\*.cs")]
    [InlineData("Get-ChildItem -Path \\\\server\\*.cs")]
    [InlineData("Get-ChildItem -Path \\\\*.cs")]
    [InlineData("Get-ChildItem -Path \\\\?\\C:\\*.cs")]
    public void Unproved_roots_never_publish_a_positive_root(string source)
    {
        var dialect = source.Contains("FollowSymlink", StringComparison.Ordinal)
            ? PwshDialect.WindowsPowerShell51
            : PwshDialect.PowerShell7;
        var command = Command(source, dialect);

        var marker = Assert.Single(command.FileSystemTreeAccesses);
        Assert.Null(marker.RootArgument);
        Assert.IsType<ShellValueDomain.Unknown>(marker.Root);
        Assert.Equal(ShellTreeTraversalMode.Unknown, marker.Traversal);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "Get-ChildItem -Path C:*.cs")]
    [InlineData(PwshDialect.PowerShell7, "Get-ChildItem -Path C:folder\\*.cs")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-ChildItem -Path C:*.cs")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-ChildItem -Path C:folder\\*.cs")]
    public void Drive_relative_leaf_globs_never_publish_a_covering_directory(
        PwshDialect dialect,
        string source)
    {
        AssertUnknownMarker(Command(source, dialect));
    }

    [Fact]
    public void LiteralPath_wildcards_are_an_exact_literal_root()
    {
        var command = Command(
            "Get-ChildItem -LiteralPath C:\\repo\\*.cs",
            PwshDialect.PowerShell7);

        var access = Assert.Single(command.FileSystemTreeAccesses);
        Assert.Equal("C:/repo/*.cs", Assert.IsType<ShellValueDomain.Exact>(access.Root).Value);
    }

    [Fact]
    public void FileSystem_provider_qualified_root_is_exact()
    {
        var command = Command(
            "Get-ChildItem -Path FileSystem::C:\\repo",
            PwshDialect.PowerShell7);

        Assert.Equal(
            "C:/repo",
            Assert.IsType<ShellValueDomain.Exact>(
                Assert.Single(command.FileSystemTreeAccesses).Root).Value);
    }

    [Theory]
    [InlineData("Get-ChildItem", PwshDialect.PowerShell7, ShellTreeTraversalMode.DirectChildren)]
    [InlineData("Get-ChildItem -Recurse", PwshDialect.WindowsPowerShell51, ShellTreeTraversalMode.RecursiveMayFollowLinks)]
    public void Implicit_root_is_the_exact_working_directory(
        string source,
        PwshDialect dialect,
        ShellTreeTraversalMode expected)
    {
        var command = Command(source, dialect);

        var access = Assert.Single(command.FileSystemTreeAccesses);
        Assert.Null(access.RootArgument);
        Assert.Equal("C:/work", Assert.IsType<ShellValueDomain.Exact>(access.Root).Value);
        Assert.Equal(expected, access.Traversal);
    }

    [Theory]
    [InlineData("gci")]
    [InlineData("dir")]
    [InlineData("ls")]
    public void Dialect_aliases_publish_the_same_access(string alias)
    {
        var command = Command($"{alias} C:\\repo", PwshDialect.PowerShell7);

        Assert.Equal("Get-ChildItem", command.Clause.Verb.CanonicalVerb);
        Assert.Single(command.FileSystemTreeAccesses);
    }

    [Theory]
    [InlineData(
        PwshDialect.PowerShell7,
        "pwsh -Command 'Get-ChildItem C:\\repo -Recurse'",
        ShellTreeTraversalMode.RecursiveWithoutFollowingLinks)]
    [InlineData(
        PwshDialect.WindowsPowerShell51,
        "powershell.exe -Command 'Get-ChildItem C:\\repo -Recurse'",
        ShellTreeTraversalMode.RecursiveMayFollowLinks)]
    public void Static_wrappers_preserve_inner_tree_access_provenance(
        PwshDialect dialect,
        string source,
        ShellTreeTraversalMode expected)
    {
        var command = Command(source, dialect);

        Assert.True(command.Clause.IsCommandStringWrapped);
        Assert.All(command.Clause.Elements, element => Assert.Null(element.SourceStart));
        var access = Assert.Single(command.FileSystemTreeAccesses);
        Assert.Equal("C:/repo", Assert.IsType<ShellValueDomain.Exact>(access.Root).Value);
        Assert.Equal(expected, access.Traversal);
        Assert.Same(PathArgument(command), access.RootArgument);
    }

    [Theory]
    [InlineData(
        PwshDialect.PowerShell7,
        "pwsh -Command 'Get-ChildItem C:\\repo -Recurse:$flag'")]
    [InlineData(
        PwshDialect.WindowsPowerShell51,
        "powershell.exe -Command 'Get-ChildItem C:\\repo -Recurse:$flag'")]
    public void Static_wrappers_preserve_unknown_traversal_markers(
        PwshDialect dialect,
        string source)
    {
        var command = Command(source, dialect);

        Assert.True(command.Clause.IsCommandStringWrapped);
        AssertUnknownMarker(command);
    }

    [Fact]
    public void Non_tree_commands_keep_an_empty_access_list()
    {
        var command = Command("Get-Content C:\\repo\\a.txt", PwshDialect.PowerShell7);

        Assert.Empty(command.FileSystemTreeAccesses);
    }

    [Fact]
    public void Access_validator_rejects_foreign_root_references_and_unknown_enums()
    {
        var command = Command("Get-ChildItem C:\\repo", PwshDialect.PowerShell7);
        var foreign = Command("Get-ChildItem C:\\other", PwshDialect.PowerShell7).Arguments.Single();

        Assert.False(PwshFileSystemTreeAccessProjection.HasValidAccesses(command with
        {
            FileSystemTreeAccesses = new[]
            {
                new ShellFileSystemTreeAccess
                {
                    RootArgument = foreign,
                    Root = new ShellValueDomain.Exact("C:/repo"),
                    Traversal = ShellTreeTraversalMode.DirectChildren,
                },
            },
        }));
        Assert.False(PwshFileSystemTreeAccessProjection.HasValidAccesses(command with
        {
            FileSystemTreeAccesses = new[]
            {
                new ShellFileSystemTreeAccess
                {
                    RootArgument = command.Arguments.Single(argument => !argument.Argument.IsFlag),
                    Root = new ShellValueDomain.Exact("C:/repo"),
                    Traversal = (ShellTreeTraversalMode)999,
                },
            },
        }));
    }

    [Fact]
    public void Access_validator_rejects_flag_mismatched_root_and_malformed_pattern()
    {
        var command = Command(
            "Get-ChildItem -Path C:\\repo\\*.cs",
            PwshDialect.PowerShell7);
        var rootArgument = PathArgument(command);
        var flag = command.Arguments.Single(argument => argument.Argument.IsFlag);

        AssertInvalid(command, flag, new ShellValueDomain.Exact("C:/repo"));
        AssertInvalid(command, rootArgument, new ShellValueDomain.Exact("C:/other"));
        AssertInvalid(
            command,
            rootArgument,
            new ShellValueDomain.PathPattern("C:\\repo\\*.json", "C:/repo"));
        AssertInvalid(
            command,
            rootArgument,
            new ShellValueDomain.PathPattern("C:\\repo\\*.cs", "C:/other"));
    }

    [Fact]
    public void Access_validator_accepts_only_the_exact_null_unknown_marker()
    {
        var command = Command("Get-ChildItem C:\\repo", PwshDialect.PowerShell7);
        var marker = command with
        {
            FileSystemTreeAccesses = new[]
            {
                new ShellFileSystemTreeAccess
                {
                    Root = new ShellValueDomain.Unknown(),
                    Traversal = ShellTreeTraversalMode.Unknown,
                },
            },
        };

        Assert.True(PwshFileSystemTreeAccessProjection.HasValidAccesses(marker));
        Assert.False(PwshFileSystemTreeAccessProjection.HasValidAccesses(marker with
        {
            FileSystemTreeAccesses = new[]
            {
                new ShellFileSystemTreeAccess
                {
                    Root = new ShellValueDomain.Unknown(),
                    Traversal = ShellTreeTraversalMode.DirectChildren,
                },
            },
        }));
    }

    private static void AssertInvalid(
        CommandOccurrence command,
        AnalyzedArgument rootArgument,
        ShellValueDomain root) =>
        Assert.False(PwshFileSystemTreeAccessProjection.HasValidAccesses(command with
        {
            FileSystemTreeAccesses = new[]
            {
                new ShellFileSystemTreeAccess
                {
                    RootArgument = rootArgument,
                    Root = root,
                    Traversal = ShellTreeTraversalMode.DirectChildren,
                },
            },
        }));

    private static AnalyzedArgument PathArgument(CommandOccurrence command) =>
        Assert.Single(command.Arguments, argument => argument.Argument.IsPath);

    private static void AssertUnknownMarker(CommandOccurrence command)
    {
        var marker = Assert.Single(command.FileSystemTreeAccesses);
        Assert.Null(marker.RootArgument);
        Assert.IsType<ShellValueDomain.Unknown>(marker.Root);
        Assert.Equal(ShellTreeTraversalMode.Unknown, marker.Traversal);
    }

    private static CommandOccurrence Command(string source, PwshDialect dialect)
    {
        var result = new PwshParser(new PwshParserOptions
        {
            WorkingDirectory = "C:/work",
            HomeDirectory = "C:/Users/test",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
            Dialect = dialect,
        }).Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        return Assert.Single(result.Commands);
    }
}
