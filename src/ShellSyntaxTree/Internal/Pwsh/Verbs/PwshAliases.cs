// -----------------------------------------------------------------------
// <copyright file="PwshAliases.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Pwsh.Verbs;

/// <summary>
/// The default PowerShell built-in alias table — a case-insensitive map
/// from a typed alias to its canonical cmdlet (SPEC.POWERSHELL.md §6.3).
/// Alias resolution is unconditional: the most security-relevant
/// normalization the parser performs.
/// </summary>
/// <remarks>
/// <para>
/// This table is intentionally a <em>superset</em> of any single platform's
/// live <c>Get-Alias</c> output. PowerShell 7 omits the Unix-collision
/// aliases (<c>rm</c>, <c>ls</c>, <c>cat</c>, <c>cp</c>, <c>mv</c>,
/// <c>ps</c>, <c>kill</c>, <c>sleep</c>, ...) on non-Windows hosts so the
/// native tool wins. A security parser cannot know which host a command
/// string targets, so the table carries the Windows superset: recognizing
/// <c>rm</c> as <c>Remove-Item</c> on Linux is safe (the worst case is a
/// canonical identity for a token that would have run native <c>rm</c>
/// anyway), whereas <em>missing</em> it loses file-verb path classification
/// — a false-negative-shaped failure (§6.3).
/// </para>
/// <para>
/// The <c>PwshAliasCompletenessTests</c> [Fact] diffs this table against
/// live <c>Get-Alias</c> and fails on any gap (a live alias absent here);
/// extra Windows-only entries are expected and allowed.
/// </para>
/// <para>
/// <c>curl</c>, <c>wget</c>, <c>sc</c>, <c>set</c>, <c>start</c>, and
/// <c>where</c> are deliberately absent: §6.3 collision rule 3 treats them
/// as native commands, never aliased. <c>md</c> / <c>mkdir</c> map to
/// <c>New-Item</c> — the effective cmdlet — rather than the thin
/// <c>mkdir</c> function PowerShell's own <c>Get-Alias</c> reports.
/// </para>
/// </remarks>
internal static class PwshAliases
{
    /// <summary>
    /// Aliases §6.3 collision rule 3 forbids resolving — their cmdlet vs.
    /// native-tool meaning is version-dependent. Absent from
    /// <see cref="Map"/>; the parser treats them as native commands.
    /// </summary>
    internal static readonly HashSet<string> NeverAliased =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "curl", "wget", "sc", "set", "start", "where",
        };

    /// <summary>Alias → canonical cmdlet, matched case-insensitively.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ---- file / path / cwd verbs (security-relevant excerpt) ----
            ["rm"] = "Remove-Item",
            ["del"] = "Remove-Item",
            ["erase"] = "Remove-Item",
            ["rd"] = "Remove-Item",
            ["rmdir"] = "Remove-Item",
            ["ri"] = "Remove-Item",
            ["ls"] = "Get-ChildItem",
            ["dir"] = "Get-ChildItem",
            ["gci"] = "Get-ChildItem",
            ["cd"] = "Set-Location",
            ["chdir"] = "Set-Location",
            ["sl"] = "Set-Location",
            ["pushd"] = "Push-Location",
            ["popd"] = "Pop-Location",
            ["cat"] = "Get-Content",
            ["gc"] = "Get-Content",
            ["type"] = "Get-Content",
            ["cp"] = "Copy-Item",
            ["copy"] = "Copy-Item",
            ["cpi"] = "Copy-Item",
            ["mv"] = "Move-Item",
            ["move"] = "Move-Item",
            ["mi"] = "Move-Item",
            ["ni"] = "New-Item",
            ["mkdir"] = "New-Item",
            ["md"] = "New-Item",
            ["ren"] = "Rename-Item",
            ["rni"] = "Rename-Item",
            ["ac"] = "Add-Content",
            ["sls"] = "Select-String",
            ["ipcsv"] = "Import-Csv",
            ["epcsv"] = "Export-Csv",
            ["gp"] = "Get-ItemProperty",
            ["sp"] = "Set-ItemProperty",
            ["rp"] = "Remove-ItemProperty",
            ["gpv"] = "Get-ItemPropertyValue",
            ["cpp"] = "Copy-ItemProperty",
            ["mp"] = "Move-ItemProperty",
            ["rnp"] = "Rename-ItemProperty",
            ["clc"] = "Clear-Content",
            ["cli"] = "Clear-Item",
            ["clp"] = "Clear-ItemProperty",
            ["rvpa"] = "Resolve-Path",
            ["cvpa"] = "Convert-Path",
            ["gi"] = "Get-Item",
            ["ii"] = "Invoke-Item",
            ["si"] = "Set-Item",
            ["gl"] = "Get-Location",
            ["pwd"] = "Get-Location",
            ["gdr"] = "Get-PSDrive",
            ["ndr"] = "New-PSDrive",
            ["rdr"] = "Remove-PSDrive",
            ["mount"] = "New-PSDrive",

            // ---- code execution / pipeline ----
            ["iex"] = "Invoke-Expression",
            ["icm"] = "Invoke-Command",
            ["%"] = "ForEach-Object",
            ["foreach"] = "ForEach-Object",
            ["?"] = "Where-Object",
            ["echo"] = "Write-Output",
            ["write"] = "Write-Output",
            ["irm"] = "Invoke-RestMethod",
            ["iwr"] = "Invoke-WebRequest",
            ["saps"] = "Start-Process",
            ["spps"] = "Stop-Process",
            ["gps"] = "Get-Process",
            ["ps"] = "Get-Process",
            ["kill"] = "Stop-Process",
            ["sleep"] = "Start-Sleep",
            ["spsv"] = "Stop-Service",
            ["gsv"] = "Get-Service",
            ["sasv"] = "Start-Service",

            // ---- object pipeline / formatting ----
            ["select"] = "Select-Object",
            ["sort"] = "Sort-Object",
            ["measure"] = "Measure-Object",
            ["group"] = "Group-Object",
            ["compare"] = "Compare-Object",
            ["diff"] = "Compare-Object",
            ["tee"] = "Tee-Object",
            ["gu"] = "Get-Unique",
            ["gm"] = "Get-Member",
            ["fl"] = "Format-List",
            ["ft"] = "Format-Table",
            ["fw"] = "Format-Wide",
            ["fc"] = "Format-Custom",
            ["fhx"] = "Format-Hex",
            ["oh"] = "Out-Host",
            ["ogv"] = "Out-GridView",
            ["lp"] = "Out-Printer",
            ["man"] = "Get-Help",
            ["clear"] = "Clear-Host",
            ["cls"] = "Clear-Host",
            ["gcb"] = "Get-Clipboard",
            ["scb"] = "Set-Clipboard",

            // ---- variables / aliases / modules ----
            ["gv"] = "Get-Variable",
            ["sv"] = "Set-Variable",
            ["nv"] = "New-Variable",
            ["rv"] = "Remove-Variable",
            ["clv"] = "Clear-Variable",
            ["gal"] = "Get-Alias",
            ["sal"] = "Set-Alias",
            ["nal"] = "New-Alias",
            ["epal"] = "Export-Alias",
            ["ipal"] = "Import-Alias",
            ["gmo"] = "Get-Module",
            ["ipmo"] = "Import-Module",
            ["nmo"] = "New-Module",
            ["rmo"] = "Remove-Module",
            ["gcm"] = "Get-Command",
            ["gerr"] = "Get-Error",

            // ---- history / sessions / jobs / breakpoints ----
            ["h"] = "Get-History",
            ["history"] = "Get-History",
            ["ghy"] = "Get-History",
            ["chy"] = "Clear-History",
            ["clhy"] = "Clear-History",
            ["ihy"] = "Invoke-History",
            ["r"] = "Invoke-History",
            ["etsn"] = "Enter-PSSession",
            ["exsn"] = "Exit-PSSession",
            ["nsn"] = "New-PSSession",
            ["gsn"] = "Get-PSSession",
            ["rsn"] = "Remove-PSSession",
            ["cnsn"] = "Connect-PSSession",
            ["dnsn"] = "Disconnect-PSSession",
            ["rcsn"] = "Receive-PSSession",
            ["sajb"] = "Start-Job",
            ["gjb"] = "Get-Job",
            ["rjb"] = "Remove-Job",
            ["spjb"] = "Stop-Job",
            ["wjb"] = "Wait-Job",
            ["rcjb"] = "Receive-Job",
            ["rujb"] = "Resume-Job",
            ["sujb"] = "Suspend-Job",
            ["gbp"] = "Get-PSBreakpoint",
            ["sbp"] = "Set-PSBreakpoint",
            ["rbp"] = "Remove-PSBreakpoint",
            ["dbp"] = "Disable-PSBreakpoint",
            ["ebp"] = "Enable-PSBreakpoint",
            ["gcs"] = "Get-PSCallStack",

            // ---- Windows-only host integrations ----
            ["shcm"] = "Show-Command",
        };

    /// <summary>
    /// Resolve <paramref name="token"/> to its canonical cmdlet. Returns
    /// null when the token is not a known alias or is one of the
    /// <see cref="NeverAliased"/> commands. Case-insensitive.
    /// </summary>
    internal static string? Resolve(string token)
    {
        if (string.IsNullOrEmpty(token) || NeverAliased.Contains(token))
        {
            return null;
        }

        return Map.TryGetValue(token, out var canonical) ? canonical : null;
    }
}
