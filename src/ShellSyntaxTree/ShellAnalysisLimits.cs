// -----------------------------------------------------------------------
// <copyright file="ShellAnalysisLimits.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>Fixed resource and proof bounds for structured shell analysis.</summary>
public static class ShellAnalysisLimits
{
    /// <summary>Gets the maximum number of finite value candidates.</summary>
    public static int MaxValueCandidates => 32;

    /// <summary>Gets the maximum structural container depth.</summary>
    public static int MaxStructuralNesting => 16;

    /// <summary>Gets the maximum decoded command-string wrapper depth.</summary>
    public static int MaxWrapperRecursionDepth => 5;
}
