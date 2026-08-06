// -----------------------------------------------------------------------
// <copyright file="ShellPathNormalizationTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree.Internal.Resolving;
using Xunit;

namespace ShellSyntaxTree.Tests.Resolving;

public class ShellPathNormalizationTests
{
    [Theory]
    [InlineData("/base", "", "/base")]
    [InlineData("/base/", "/child\\file", "/base/child/file")]
    [InlineData("C:\\base\\", "child\\file", "C:\\base/child/file")]
    [InlineData("/base", "//child", "/base//child")]
    public void Join_preserves_the_existing_shared_string_semantics(
        string baseDirectory,
        string subpath,
        string expected)
    {
        Assert.Equal(expected, ShellPathNormalization.Join(baseDirectory, subpath));
    }

    [Theory]
    [InlineData("\\\\server\\share\\file", "//server/share/file")]
    [InlineData("\\root\\file", "/root/file")]
    [InlineData("/root\\file", "/root/file")]
    public void NormalizeSeparators_preserves_UNC_and_normalizes_backslashes(
        string path,
        string expected)
    {
        Assert.Equal(expected, ShellPathNormalization.NormalizeSeparators(path));
    }
}
