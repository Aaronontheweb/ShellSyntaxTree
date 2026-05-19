// -----------------------------------------------------------------------
// <copyright file="BashParserOptions.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// Configuration knobs for <see cref="BashParser"/>. The resolver knobs
/// (<see cref="ShellParserOptions.HomeDirectory"/> /
/// <see cref="ShellParserOptions.WorkingDirectory"/>) live on the shared
/// <see cref="ShellParserOptions"/> base; the shape stays source-compatible
/// with v0.1 — <c>new BashParserOptions { HomeDirectory = ... }</c> still
/// compiles. See SPEC.POWERSHELL.md §2.
/// </summary>
public sealed record BashParserOptions : ShellParserOptions;
