// -----------------------------------------------------------------------
// <copyright file="PwshSetLocationParameterCatalog.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Pwsh.Verbs;

internal enum PwshSetLocationParameterRole
{
    Invalid,
    Switch,
    NonPathValue,
    PathValue,
    LiteralPathValue,
    StackNameValue,
}

/// <summary>
/// Selected-dialect parameter metadata for the parser-owned Set-Location
/// transfer. This keeps abbreviation and alias handling aligned with the
/// cmdlet rather than the broader compatibility binding table.
/// </summary>
internal static class PwshSetLocationParameterCatalog
{
    private static readonly IReadOnlyDictionary<string, PwshSetLocationParameterRole>
        Parameters = new Dictionary<string, PwshSetLocationParameterRole>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Debug"] = PwshSetLocationParameterRole.Switch,
            ["ErrorAction"] = PwshSetLocationParameterRole.NonPathValue,
            ["ErrorVariable"] = PwshSetLocationParameterRole.NonPathValue,
            ["InformationAction"] = PwshSetLocationParameterRole.NonPathValue,
            ["InformationVariable"] = PwshSetLocationParameterRole.NonPathValue,
            ["LiteralPath"] = PwshSetLocationParameterRole.LiteralPathValue,
            ["OutBuffer"] = PwshSetLocationParameterRole.NonPathValue,
            ["OutVariable"] = PwshSetLocationParameterRole.NonPathValue,
            ["PassThru"] = PwshSetLocationParameterRole.Switch,
            ["Path"] = PwshSetLocationParameterRole.PathValue,
            ["PipelineVariable"] = PwshSetLocationParameterRole.NonPathValue,
            ["ProgressAction"] = PwshSetLocationParameterRole.NonPathValue,
            ["StackName"] = PwshSetLocationParameterRole.StackNameValue,
            ["Verbose"] = PwshSetLocationParameterRole.Switch,
            ["WarningAction"] = PwshSetLocationParameterRole.NonPathValue,
            ["WarningVariable"] = PwshSetLocationParameterRole.NonPathValue,
        };

    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["db"] = "Debug",
            ["ea"] = "ErrorAction",
            ["ev"] = "ErrorVariable",
            ["infa"] = "InformationAction",
            ["iv"] = "InformationVariable",
            ["lp"] = "LiteralPath",
            ["ob"] = "OutBuffer",
            ["ov"] = "OutVariable",
            ["proga"] = "ProgressAction",
            ["pspath"] = "LiteralPath",
            ["pv"] = "PipelineVariable",
            ["vb"] = "Verbose",
            ["wa"] = "WarningAction",
            ["wv"] = "WarningVariable",
        };

    internal static PwshSetLocationParameterRole Resolve(
        string parameter,
        PwshDialect dialect)
    {
        if (parameter.Length < 2 || parameter[0] != '-')
        {
            return PwshSetLocationParameterRole.Invalid;
        }

        var name = parameter.Substring(1);
        if (Aliases.TryGetValue(name, out var canonicalAlias))
        {
            return IsAvailable(canonicalAlias, dialect)
                ? Parameters[canonicalAlias]
                : PwshSetLocationParameterRole.Invalid;
        }

        if (Parameters.TryGetValue(name, out var exact))
        {
            return IsAvailable(name, dialect)
                ? exact
                : PwshSetLocationParameterRole.Invalid;
        }

        string? canonical = null;
        foreach (var candidate in Parameters.Keys)
        {
            if (!IsAvailable(candidate, dialect) ||
                !candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (canonical is not null)
            {
                return PwshSetLocationParameterRole.Invalid;
            }

            canonical = candidate;
        }

        foreach (var alias in Aliases)
        {
            if (!IsAvailable(alias.Value, dialect) ||
                !alias.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (canonical is not null &&
                !canonical.Equals(alias.Value, StringComparison.OrdinalIgnoreCase))
            {
                return PwshSetLocationParameterRole.Invalid;
            }

            canonical = alias.Value;
        }

        return canonical is null
            ? PwshSetLocationParameterRole.Invalid
            : Parameters[canonical];
    }

    private static bool IsAvailable(string parameter, PwshDialect dialect) =>
        !parameter.Equals("ProgressAction", StringComparison.OrdinalIgnoreCase) ||
        dialect == PwshDialect.PowerShell7;
}
