// -----------------------------------------------------------------------
// <copyright file="PwshVerbs.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Pwsh.Verbs;

/// <summary>
/// Static, case-insensitive verb data for the PowerShell parser
/// (SPEC.POWERSHELL.md §6.4). Mirrors <c>BashVerbs</c> — keyed by canonical
/// cmdlet plus raw aliases — so a membership check on
/// <c>CanonicalVerb ?? Tokens[0]</c> resolves uniformly.
/// </summary>
internal static class PwshVerbs
{
    /// <summary>
    /// Verbs whose target attributes the cwd for subsequent clauses
    /// (SPEC.POWERSHELL.md §9). Canonical cmdlets plus raw aliases.
    /// </summary>
    internal static readonly HashSet<string> CwdVerbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Set-Location", "Push-Location", "Pop-Location",
            "cd", "chdir", "sl", "pushd", "popd",
        };

    /// <summary>
    /// File cmdlets whose positional args classify as paths
    /// (SPEC.POWERSHELL.md §6.4). Canonical cmdlets plus the Windows native
    /// file utilities reserved in SPEC.md §6.4.
    /// </summary>
    internal static readonly HashSet<string> FileVerbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Cwd verbs are also file verbs (their target is a path).
            "Set-Location", "Push-Location", "Pop-Location",
            "cd", "chdir", "sl", "pushd", "popd",

            // File cmdlets.
            "Get-ChildItem", "Get-Content", "Set-Content", "Add-Content",
            "Clear-Content", "Remove-Item", "Copy-Item", "Move-Item",
            "Rename-Item", "New-Item", "Get-Item", "Invoke-Item",
            "Test-Path", "Resolve-Path", "Convert-Path", "Out-File",
            "Import-Csv", "Export-Csv", "Get-FileHash", "Compress-Archive",
            "Expand-Archive", "Select-String",

            // Windows native file utilities (SPEC.md §6.4).
            "type", "copy", "move", "del", "xcopy", "robocopy", "findstr",
        };

    /// <summary>
    /// Cmdlets whose first non-flag positional is a script block / dynamic
    /// content rather than a path — they have no path positionals
    /// (SPEC.POWERSHELL.md §7.2).
    /// </summary>
    internal static readonly HashSet<string> ScriptBlockVerbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ForEach-Object", "Where-Object",
        };

    /// <summary>
    /// Control-flow, definition, block, and statement keywords v0.2.0 does
    /// not parse. SPEC.POWERSHELL.md §6.4 / §11: one at statement/verb
    /// position sets <c>ParsedCommand.IsUnparseable = true</c>.
    /// </summary>
    internal static readonly HashSet<string> ControlFlowKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "if", "elseif", "else", "switch", "foreach", "for", "while",
            "do", "until", "function", "filter", "workflow", "configuration",
            "class", "enum", "param", "begin", "process", "end",
            "dynamicparam", "trap", "data", "try", "catch", "finally",
            "return", "throw", "break", "continue", "exit", "using", "hidden",
        };

    /// <summary>
    /// Statement keywords that lead a statement and mark it unparseable
    /// (SPEC.POWERSHELL.md §11 item 5). A subset of
    /// <see cref="ControlFlowKeywords"/> kept separate for diagnostic text.
    /// </summary>
    internal static readonly HashSet<string> StatementKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "return", "throw", "break", "continue", "exit", "using", "hidden",
        };

    /// <summary>
    /// The PowerShell host executables whose <c>-Command</c> /
    /// <c>-EncodedCommand</c> the parser recurses into (SPEC.POWERSHELL.md
    /// §10). Matched case-insensitively.
    /// </summary>
    internal static readonly HashSet<string> PwshHostVerbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "pwsh", "powershell", "pwsh.exe", "powershell.exe",
        };

    /// <summary>True when <paramref name="verb"/> is a PowerShell host
    /// executable (<c>pwsh</c> / <c>powershell</c>).</summary>
    internal static bool IsPwshHost(string? verb) =>
        !string.IsNullOrEmpty(verb) && PwshHostVerbs.Contains(verb!);

    /// <summary>
    /// SPEC.POWERSHELL.md §6.2: the native greedy-walk verb-like predicate,
    /// the bash predicate unchanged — length [1, 64], first char ASCII
    /// lowercase, remaining chars <c>[a-z0-9._-]</c>. Deliberately
    /// <em>case-sensitive</em>: the leading-lowercase rule is the only
    /// signal that stops the greedy walk at a capitalized identifier
    /// (<c>dotnet ef migrations add InitialCreate</c> stops at
    /// <c>InitialCreate</c>).
    /// </summary>
    internal static bool IsNativeVerbLikeToken(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        var first = value[0];
        if (!(first >= 'a' && first <= 'z'))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            var ok =
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '-' || c == '.' || c == '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
