// -----------------------------------------------------------------------
// <copyright file="PwshParserOptions.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>
/// Configuration knobs for <see cref="PwshParser"/>. Empty in v0.2.0 —
/// alias resolution is unconditional (SPEC.POWERSHELL.md §6.3) and the
/// resolver knobs live on the shared <see cref="ShellParserOptions"/> base.
/// Kept as a distinct type so a future PowerShell-only knob is an additive
/// change, not a new type.
/// </summary>
public sealed record PwshParserOptions : ShellParserOptions;
