// -----------------------------------------------------------------------
// <copyright file="PwshPerVerbRules.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Pwsh.Verbs;

/// <summary>
/// Per-cmdlet / per-parameter path-arg classification rules from
/// SPEC.POWERSHELL.md §7. PowerShell classifies path arguments in two
/// layers — a parameter-value layer (dominant) and a positional layer.
/// Rules are keyed by the <em>canonical</em> verb (§6.3), so <c>rm x</c> and
/// <c>Remove-Item x</c> classify identically. Native-command path rules are
/// not here: §7.3 reuses the bash per-verb table verbatim
/// (<see cref="BashPerVerbRules"/>).
/// </summary>
internal static class PwshPerVerbRules
{
    /// <summary>
    /// Verb-agnostic path-typed parameter names (SPEC.POWERSHELL.md §7.1) —
    /// the value bound to one of these is a filesystem path.
    /// </summary>
    private static readonly HashSet<string> PathParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "-Path", "-LiteralPath", "-PSPath",
            "-FilePath", "-OutFile", "-InFile",
            "-Destination", "-Source",
        };

    /// <summary>
    /// <c>-Name</c> is context-dependent (§7.1): a filesystem leaf for
    /// <c>New-Item</c> / <c>Rename-Item</c>; not a path elsewhere
    /// (<c>Get-Process -Name</c>).
    /// </summary>
    private static readonly HashSet<string> NameIsPathVerbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "New-Item", "Rename-Item",
        };

    /// <summary>
    /// Per-cmdlet positional path overrides (SPEC.POWERSHELL.md §7.2).
    /// Returns true when the i-th non-flag positional is a path. The
    /// FileVerb default ("all non-flag positionals are paths") covers
    /// everything not listed here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<int, bool>> PositionalOverrides =
        new Dictionary<string, Func<int, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            // pos 0 = source, pos 1 = destination; no further path positionals.
            ["Copy-Item"] = i => i <= 1,
            ["Move-Item"] = i => i <= 1,

            // pos 0 = path, pos 1 = new name (a path fragment).
            ["Rename-Item"] = i => i <= 1,

            // pos 0 = path; -Value (pos 1) is content.
            ["New-Item"] = i => i == 0,
            ["Set-Content"] = i => i == 0,
            ["Add-Content"] = i => i == 0,
            ["Clear-Content"] = i => i == 0,

            // pos 0 = pattern, rest = paths (mirrors bash grep).
            ["Select-String"] = i => i >= 1,

            // pos 0 is a script block; no path positionals.
            ["ForEach-Object"] = _ => false,
            ["Where-Object"] = _ => false,
        };

    /// <summary>
    /// SPEC.POWERSHELL.md §7.1: whether the value bound to
    /// <paramref name="parameterName"/> (with leading dash) for
    /// <paramref name="canonicalVerb"/> should be classified as a path.
    /// <c>-Filter</c> / <c>-Include</c> / <c>-Exclude</c> return false —
    /// they are glob slots, and the resolver still tags <c>Kind=Glob</c>
    /// from any metacharacters.
    /// </summary>
    internal static bool ParameterValueIsPath(string? canonicalVerb, string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName))
        {
            return false;
        }

        if (PathParameters.Contains(parameterName))
        {
            return true;
        }

        if (string.Equals(parameterName, "-Name", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrEmpty(canonicalVerb)
                && NameIsPathVerbs.Contains(canonicalVerb!);
        }

        // `pwsh -File <script>` — the -File value is a script path
        // (SPEC.POWERSHELL.md §10). -File is otherwise content/switch.
        if (string.Equals(parameterName, "-File", StringComparison.OrdinalIgnoreCase))
        {
            return PwshVerbs.IsPwshHost(canonicalVerb);
        }

        // -Value, -ItemType, -Encoding, -Depth, every other value parameter:
        // not a path.
        return false;
    }

    /// <summary>
    /// SPEC.POWERSHELL.md §7.2: whether the <paramref name="positionalIndex"/>-th
    /// (0-based) non-flag positional of a cmdlet/alias clause is a path.
    /// <paramref name="canonicalVerb"/> is the alias-resolved verb;
    /// <paramref name="isFileVerb"/> is its <see cref="PwshVerbs.FileVerbs"/>
    /// membership. A canonical FileVerb defaults to "all positionals are
    /// paths"; a non-FileVerb cmdlet falls back to the SPEC.md §8
    /// <c>LooksLikePath</c> heuristic so a path-shaped token is still
    /// caught.
    /// </summary>
    internal static bool IsPositionalPathArg(
        string? canonicalVerb, bool isFileVerb, int positionalIndex, string token)
    {
        if (!string.IsNullOrEmpty(canonicalVerb)
            && PositionalOverrides.TryGetValue(canonicalVerb!, out var rule))
        {
            return rule(positionalIndex);
        }

        if (isFileVerb)
        {
            // FileVerb default: every non-flag positional is a path.
            return true;
        }

        // Non-FileVerb cmdlet: keep the path-shape heuristic so an explicit
        // path argument is recognized even for a cmdlet absent from the
        // FileVerbs table.
        return BashResolver.LooksLikePath(token ?? "");
    }
}
