// -----------------------------------------------------------------------
// <copyright file="PwshAliases.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Pwsh.Verbs;

/// <summary>
/// The default PowerShell built-in alias tables — case-insensitive maps
/// from a typed alias to its canonical command (SPEC.POWERSHELL.md §6.3).
/// Alias resolution is unconditional within the selected dialect.
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
/// The <c>PwshOracleTests</c> alias gate diffs this table against live
/// <c>Get-Alias</c>, including canonical definitions, and fails on any gap;
/// extra Windows-only entries are expected and allowed for PowerShell 7.
/// </para>
/// <para>
/// PowerShell 7 treats <c>curl</c>, <c>wget</c>, <c>sc</c>, <c>set</c>,
/// <c>start</c>, and <c>where</c> as native commands. Windows PowerShell 5.1
/// defines aliases for those spellings, so its dialect table restores their
/// canonical cmdlets. <c>md</c> / <c>mkdir</c> map to
/// <c>New-Item</c> — the effective cmdlet — rather than the thin
/// <c>mkdir</c> function PowerShell's own <c>Get-Alias</c> reports.
/// </para>
/// </remarks>
internal static class PwshAliases
{
    /// <summary>
    /// Aliases §6.3 collision rule 3 forbids resolving in PowerShell 7 —
    /// their cmdlet vs. native-tool meaning is edition-dependent. They are
    /// absent from <see cref="Map"/> and restored only by the Windows
    /// PowerShell 5.1 table.
    /// </summary>
    internal static readonly HashSet<string> PowerShell7NativeCollisions =
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
    /// Aliases present in Windows PowerShell 5.1 but absent from the
    /// PowerShell 7 compatibility table. This includes removed Windows-only
    /// commands as well as spellings that became native-command collisions.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> WindowsPowerShell51Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["curl"] = "Invoke-WebRequest",
            ["wget"] = "Invoke-WebRequest",
            ["sc"] = "Set-Content",
            ["set"] = "Set-Variable",
            ["start"] = "Start-Process",
            ["where"] = "Where-Object",
            ["asnp"] = "Add-PSSnapIn",
            ["epsn"] = "Export-PSSession",
            ["gsnp"] = "Get-PSSnapIn",
            ["gwmi"] = "Get-WmiObject",
            ["ipsn"] = "Import-PSSession",
            ["ise"] = "powershell_ise.exe",
            ["iwmi"] = "Invoke-WmiMethod",
            ["npssc"] = "New-PSSessionConfigurationFile",
            ["rsnp"] = "Remove-PSSnapIn",
            ["rwmi"] = "Remove-WmiObject",
            ["swmi"] = "Set-WmiInstance",
            ["trcm"] = "Trace-Command",
        };

    /// <summary>
    /// Compatibility-table entries that Windows PowerShell 5.1 must not
    /// inherit. <c>gerr</c> was added with <c>Get-Error</c> in PowerShell 7;
    /// <c>chy</c> is a retained v0.2 compatibility spelling rather than a
    /// Windows PowerShell 5.1 default alias.
    /// </summary>
    private static readonly HashSet<string> WindowsPowerShell51ExcludedAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "gerr", "chy",
        };

    private static readonly HashSet<string> PowerShell7CanonicalCommands =
        new(Map.Values, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> WindowsPowerShell51CanonicalCommands =
        BuildWindowsPowerShell51CanonicalCommands();

    /// <summary>
    /// Resolve <paramref name="token"/> to its canonical cmdlet. Returns
    /// null when the token is not a known alias in the selected dialect.
    /// Case-insensitive.
    /// </summary>
    internal static string? Resolve(
        string token,
        PwshDialect dialect)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (dialect == PwshDialect.WindowsPowerShell51)
        {
            if (WindowsPowerShell51Map.TryGetValue(token, out var windowsCanonical))
            {
                return windowsCanonical;
            }

            if (WindowsPowerShell51ExcludedAliases.Contains(token))
            {
                return null;
            }
        }
        else if (dialect != PwshDialect.PowerShell7)
        {
            return null;
        }

        if (PowerShell7NativeCollisions.Contains(token))
        {
            return null;
        }

        return Map.TryGetValue(token, out var canonical) ? canonical : null;
    }

    internal static bool IsKnownCanonical(
        string token,
        PwshDialect dialect)
        => dialect switch
        {
            PwshDialect.PowerShell7 => PowerShell7CanonicalCommands.Contains(token),
            PwshDialect.WindowsPowerShell51 =>
                WindowsPowerShell51CanonicalCommands.Contains(token),
            _ => false,
        };

    private static HashSet<string> BuildWindowsPowerShell51CanonicalCommands()
    {
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in Map)
        {
            if (!WindowsPowerShell51ExcludedAliases.Contains(alias.Key))
            {
                commands.Add(alias.Value);
            }
        }

        foreach (var canonical in WindowsPowerShell51Map.Values)
        {
            commands.Add(canonical);
        }

        return commands;
    }
}
