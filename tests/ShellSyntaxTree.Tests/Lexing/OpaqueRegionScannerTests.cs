// -----------------------------------------------------------------------
// <copyright file="OpaqueRegionScannerTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using ShellSyntaxTree.Internal.Lexing;
using Xunit;

namespace ShellSyntaxTree.Tests.Lexing;

/// <summary>
/// Unit tests for <see cref="OpaqueRegionScanner"/>. The scanner is
/// grammar-agnostic; tests use bash-style inputs because that's what the
/// v0.1 lexer drives, but the assertions don't depend on bash semantics.
/// </summary>
public class OpaqueRegionScannerTests
{
    [Fact]
    public void Scan_balanced_parens_returns_close_index()
    {
        // 0123456
        // (foo)
        var input = "(foo)".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(4, result.EndIndex);
    }

    [Fact]
    public void Scan_nested_parens_returns_outer_close()
    {
        // 0         1
        // 0123456789012345
        // (foo (bar) baz)
        var input = "(foo (bar) baz)".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(14, result.EndIndex);
    }

    [Fact]
    public void Scan_close_inside_double_quotes_is_ignored()
    {
        // (foo "ignored ) close" bar)
        var input = "(foo \"ignored ) close\" bar)".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(input.Length - 1, result.EndIndex);
        Assert.Equal(')', input[result.EndIndex]);
    }

    [Fact]
    public void Scan_close_inside_single_quotes_is_ignored()
    {
        // Single quotes do NOT honor escapes, but they DO swallow ')'.
        // (foo ')' bar)
        var input = "(foo ')' bar)".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(input.Length - 1, result.EndIndex);
    }

    [Fact]
    public void Scan_escaped_close_outside_quotes_is_skipped()
    {
        // (foo \) bar)
        var input = "(foo \\) bar)".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(input.Length - 1, result.EndIndex);
    }

    [Fact]
    public void Scan_escaped_open_outside_quotes_is_skipped()
    {
        // ( \( ) — the escaped '(' must not push depth.
        var input = "( \\( )".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(input.Length - 1, result.EndIndex);
    }

    [Fact]
    public void Scan_unclosed_returns_input_length_and_not_closed()
    {
        var input = "(foo bar baz".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.False(result.Closed);
        Assert.Equal(input.Length, result.EndIndex);
    }

    [Fact]
    public void Scan_empty_body_closes_immediately()
    {
        var input = "()".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(1, result.EndIndex);
    }

    [Fact]
    public void Scan_deep_nesting_finds_outer_close()
    {
        var input = "((((( inner )))))".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(input.Length - 1, result.EndIndex);
    }

    [Fact]
    public void Scan_braces_returns_close_index()
    {
        // {a}
        var input = "{a}".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '{', '}');
        Assert.True(result.Closed);
        Assert.Equal(2, result.EndIndex);
    }

    [Fact]
    public void Scan_offset_start_index_uses_only_inner_region()
    {
        // foo (bar) baz — start at the '(' at index 4.
        var input = "foo (bar) baz".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 4, '(', ')');
        Assert.True(result.Closed);
        Assert.Equal(8, result.EndIndex);
    }

    [Fact]
    public void Scan_returns_unclosed_when_start_is_not_open_char()
    {
        var input = "foo )".AsSpan();
        var result = OpaqueRegionScanner.Scan(input, 0, '(', ')');
        Assert.False(result.Closed);
        Assert.Equal(input.Length, result.EndIndex);
    }

    [Fact]
    public void ScanSymmetric_backtick_returns_close_index()
    {
        // `foo`
        var input = "`foo`".AsSpan();
        var result = OpaqueRegionScanner.ScanSymmetric(input, 0, '`');
        Assert.True(result.Closed);
        Assert.Equal(4, result.EndIndex);
    }

    [Fact]
    public void ScanSymmetric_escaped_delimiter_is_skipped()
    {
        // `foo \` bar`
        var input = "`foo \\` bar`".AsSpan();
        var result = OpaqueRegionScanner.ScanSymmetric(input, 0, '`');
        Assert.True(result.Closed);
        Assert.Equal(input.Length - 1, result.EndIndex);
    }

    [Fact]
    public void ScanSymmetric_unclosed_returns_input_length_and_not_closed()
    {
        var input = "`foo bar".AsSpan();
        var result = OpaqueRegionScanner.ScanSymmetric(input, 0, '`');
        Assert.False(result.Closed);
        Assert.Equal(input.Length, result.EndIndex);
    }

    [Fact]
    public void ScanSymmetric_empty_body_closes_immediately()
    {
        var input = "``".AsSpan();
        var result = OpaqueRegionScanner.ScanSymmetric(input, 0, '`');
        Assert.True(result.Closed);
        Assert.Equal(1, result.EndIndex);
    }
}
