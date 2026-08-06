// -----------------------------------------------------------------------
// <copyright file="PwshBindingTables.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Pwsh.Verbs;

/// <summary>How a <c>-Name</c> parameter token binds (SPEC.POWERSHELL.md §6.5).</summary>
internal enum PwshBinding
{
    /// <summary>Consumes no following token.</summary>
    Switch,

    /// <summary>Consumes exactly the next significant token as its value.</summary>
    Value,
}

internal readonly record struct PwshBindingResult(
    PwshBinding Binding,
    string? CanonicalName,
    bool IsKnown,
    bool IsAmbiguous);

/// <summary>
/// The static parameter-binding tables from SPEC.POWERSHELL.md §6.5.2. The
/// parser has no compiled cmdlet metadata, so it decides whether a
/// <c>-Name</c> token consumes the next token from these tables. The
/// default for an unknown parameter is <see cref="PwshBinding.Switch"/> —
/// the security-conservative choice (§6.5.3 rule 4): a misclassified value
/// stays a positional, where §7's positional rules can still catch a real
/// path.
/// </summary>
internal static class PwshBindingTables
{
    /// <summary>
    /// Parameters that consume the next token. Stored with the leading
    /// dash; matched case-insensitively. SPEC.POWERSHELL.md §6.5.2.
    /// </summary>
    private static readonly HashSet<string> ValueParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Value-bearing common parameters.
            "-ErrorAction", "-WarningAction", "-InformationAction",
            "-ProgressAction", "-ErrorVariable", "-WarningVariable",
            "-InformationVariable", "-OutVariable", "-OutBuffer",
            "-PipelineVariable",

            // Every parameter in the §7.1 path-parameter table.
            "-Path", "-LiteralPath", "-PSPath", "-FilePath", "-OutFile",
            "-InFile", "-Destination", "-Source", "-Filter", "-Include",
            "-Exclude", "-Value", "-ItemType",

            // Frequently value-bearing cmdlet parameters.
            "-Name", "-Encoding", "-Depth", "-Stream", "-Delimiter",
            "-Command", "-EncodedCommand", "-File", "-ArgumentList",
        };

    /// <summary>
    /// Parameters known to consume nothing. SPEC.POWERSHELL.md §6.5.2.
    /// </summary>
    private static readonly HashSet<string> SwitchParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Switch common parameters.
            "-Verbose", "-Debug", "-WhatIf", "-Confirm",

            // Frequent cmdlet switches.
            "-Recurse", "-Force", "-Append", "-NoNewline", "-PassThru",
            "-Wait", "-Quiet", "-CaseSensitive", "-SimpleMatch",
            "-NoClobber", "-AsByteStream", "-Hidden", "-Directory",
        };

    /// <summary>
    /// Per-<c>(canonicalVerb, parameterName)</c> binding overrides — the
    /// §6.5.4 <c>-File</c> collision. <c>-File</c> is a value-binding
    /// parameter verb-agnostically (so <c>pwsh -File script.ps1</c> binds),
    /// but it is a <em>switch</em> on <c>Get-ChildItem</c>; the override
    /// row encodes that.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Verb, string Name), PwshBinding>
        Overrides = new Dictionary<(string, string), PwshBinding>(VerbNameComparer.Instance)
        {
            [("Get-ChildItem", "-File")] = PwshBinding.Switch,
        };

    /// <summary>
    /// Resolve how the parameter token <paramref name="paramName"/> (with
    /// its leading dash) binds for clause verb <paramref name="canonicalVerb"/>.
    /// Implements the §6.5.3 decision: <c>(verb, name)</c> override → exact
    /// table match → unambiguous prefix match → unknown defaults to switch.
    /// </summary>
    internal static PwshBinding ResolveBinding(string? canonicalVerb, string paramName)
        => Resolve(canonicalVerb, paramName).Binding;

    internal static PwshBindingResult Resolve(string? canonicalVerb, string paramName)
    {
        if (string.IsNullOrEmpty(paramName))
        {
            return new PwshBindingResult(PwshBinding.Switch, null, false, false);
        }

        // 1. (verb, name) override row.
        if (!string.IsNullOrEmpty(canonicalVerb)
            && Overrides.TryGetValue((canonicalVerb!, paramName), out var overridden))
        {
            return new PwshBindingResult(overridden, paramName, true, false);
        }

        // 2. Exact table match.
        if (ValueParameters.Contains(paramName))
        {
            return new PwshBindingResult(PwshBinding.Value, paramName, true, false);
        }

        if (SwitchParameters.Contains(paramName))
        {
            return new PwshBindingResult(PwshBinding.Switch, paramName, true, false);
        }

        // 3. Unambiguous prefix match. PowerShell prefix matching: the token
        // must prefix exactly one entry across both tables; two or more is
        // ambiguous and treated as unknown.
        var valueHits = FindPrefixMatches(ValueParameters, paramName);
        var switchHits = FindPrefixMatches(SwitchParameters, paramName);
        if (valueHits + switchHits == 1)
        {
            return valueHits == 1
                ? new PwshBindingResult(
                    PwshBinding.Value,
                    FindPrefixMatch(ValueParameters, paramName),
                    true,
                    false)
                : new PwshBindingResult(
                    PwshBinding.Switch,
                    FindPrefixMatch(SwitchParameters, paramName),
                    true,
                    false);
        }

        // 4. Unknown (or ambiguous) → switch. §6.5.3 rule 4.
        return new PwshBindingResult(
            PwshBinding.Switch,
            null,
            false,
            valueHits + switchHits > 1);
    }

    private static int FindPrefixMatches(HashSet<string> table, string prefix)
    {
        var count = 0;
        foreach (var entry in table)
        {
            if (entry.Length > prefix.Length
                && entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static string? FindPrefixMatch(HashSet<string> table, string prefix)
    {
        foreach (var entry in table)
        {
            if (entry.Length > prefix.Length
                && entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private sealed class VerbNameComparer : IEqualityComparer<(string Verb, string Name)>
    {
        internal static readonly VerbNameComparer Instance = new();

        public bool Equals((string Verb, string Name) x, (string Verb, string Name) y) =>
            string.Equals(x.Verb, y.Verb, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Verb, string Name) obj)
        {
            unchecked
            {
                var h1 = obj.Verb is null
                    ? 0
                    : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Verb);
                var h2 = obj.Name is null
                    ? 0
                    : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
                return (h1 * 397) ^ h2;
            }
        }
    }
}
