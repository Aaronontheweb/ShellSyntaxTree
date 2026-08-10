// -----------------------------------------------------------------------
// <copyright file="PwshRedirectAnalysisTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>Pins the lossless v0.3 PowerShell redirect projection.</summary>
public class PwshRedirectAnalysisTests
{
    [Theory]
    [InlineData(">", RedirectSourceKind.Default, null, RedirectOperation.FileOutput)]
    [InlineData(">>", RedirectSourceKind.Default, null, RedirectOperation.FileAppend)]
    [InlineData("1>", RedirectSourceKind.Descriptor, 1, RedirectOperation.FileOutput)]
    [InlineData("2>>", RedirectSourceKind.Descriptor, 2, RedirectOperation.FileAppend)]
    [InlineData("6>", RedirectSourceKind.Descriptor, 6, RedirectOperation.FileOutput)]
    [InlineData("*>", RedirectSourceKind.PowerShellAllStreams, null, RedirectOperation.FileOutput)]
    [InlineData("*>>", RedirectSourceKind.PowerShellAllStreams, null, RedirectOperation.FileAppend)]
    public void File_redirect_preserves_source_operation_and_path(
        string op,
        object sourceKind,
        int? descriptor,
        object operation)
    {
        var result = Parse($"Get-Date {op} out.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.True(occurrence.IsComplete);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(0, redirect.RedirectIndex);
        Assert.Equal((RedirectSourceKind)sourceKind, redirect.Source.Kind);
        Assert.Equal(descriptor, redirect.Source.DescriptorValue);
        Assert.Equal((RedirectOperation)operation, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Exact, redirect.Target.Kind);
        Assert.Equal("C:/work/out.txt", Assert.Single(redirect.Target.Values));
        Assert.True(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
    }

    [Theory]
    [InlineData("2>&1", RedirectSourceKind.Descriptor, 2)]
    [InlineData("6>&1", RedirectSourceKind.Descriptor, 6)]
    [InlineData("*>&1", RedirectSourceKind.PowerShellAllStreams, null)]
    public void Stream_merge_preserves_source_and_success_stream_target(
        string op,
        object sourceKind,
        int? sourceDescriptor)
    {
        var result = Parse($"Get-Date {op}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.True(occurrence.IsComplete);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal((RedirectSourceKind)sourceKind, redirect.Source.Kind);
        Assert.Equal(sourceDescriptor, redirect.Source.DescriptorValue);
        Assert.Equal(RedirectOperation.DescriptorDuplicate, redirect.Operation);
        Assert.Equal(1, redirect.TargetDescriptor);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.False(redirect.IsPathRelevant);
        Assert.True(redirect.IsComplete);
    }

    [Fact]
    public void Multiple_redirects_preserve_authored_order_and_coordinates()
    {
        var result = Parse("Get-Date > out.txt 2> err.txt");

        var redirects = Assert.Single(result.Commands).Redirects;
        Assert.Equal(2, redirects.Count);
        Assert.Equal(new[] { 0, 1 }, redirects.Select(item => item.RedirectIndex));
        Assert.Equal(
            new[] { 1, 2 },
            redirects.Select(item => item.Source.DescriptorValue ?? 1));
        Assert.Equal(
            new[] { "C:/work/out.txt", "C:/work/err.txt" },
            redirects.Select(item => Assert.Single(item.Target.Values)));
    }

    [Fact]
    public void Dynamic_file_target_is_complete_but_has_unknown_value()
    {
        var result = Parse("Get-Date > $name");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.True(occurrence.IsComplete);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(RedirectOperation.FileOutput, redirect.Operation);
        Assert.Equal(ShellValueDomainKind.Unknown, redirect.Target.Kind);
        Assert.True(redirect.IsComplete);
    }

    [Theory]
    [InlineData("$null")]
    [InlineData("${null}")]
    public void Null_sink_is_visible_but_incomplete_without_a_public_sink_operation(
        string target)
    {
        var result = Parse($"Get-Date > {target}");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var occurrence = Assert.Single(result.Commands);
        Assert.False(occurrence.IsComplete);
        var redirect = Assert.Single(occurrence.Redirects);
        Assert.Equal(RedirectOperation.Unknown, redirect.Operation);
        Assert.False(redirect.IsComplete);
    }

    [Theory]
    [InlineData("Get-Date < input.txt")]
    [InlineData("Get-Date 1>&1")]
    [InlineData("Get-Date 2>&3")]
    [InlineData("Get-Date 2>&-")]
    [InlineData("Get-Date > a > b")]
    [InlineData("Get-Date > a 1> b")]
    [InlineData("Get-Date 2>&1 2> b")]
    [InlineData("Get-Date *> a *> b")]
    public void Native_parser_errors_fail_closed(string input)
    {
        var result = Parse(input);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void All_streams_and_numbered_stream_redirects_remain_independent()
    {
        var result = Parse("Get-Date *> all.txt 2> err.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var redirects = Assert.Single(result.Commands).Redirects;
        Assert.Equal(2, redirects.Count);
        Assert.Equal(RedirectSourceKind.PowerShellAllStreams, redirects[0].Source.Kind);
        Assert.Equal(2, redirects[1].Source.DescriptorValue);
    }

    [Fact]
    public void Merge_prefix_does_not_consume_trailing_argument_text()
    {
        var result = Parse("Get-Date 2>&1-");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.True(Assert.Single(result.Commands).IsComplete);
        Assert.Equal("-", Assert.Single(Assert.Single(result.Clauses).Args).Raw);
        Assert.Equal(
            RedirectOperation.DescriptorDuplicate,
            Assert.Single(Assert.Single(result.Commands).Redirects).Operation);
    }

    private static ParsedCommand Parse(string source) => new PwshParser(
        new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/work",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
        }).Parse(source);
}
