// -----------------------------------------------------------------------
// <copyright file="PwshApprovedVerbs.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Pwsh.Verbs;

/// <summary>
/// The closed set of approved PowerShell verbs returned by <c>Get-Verb</c>
/// on the reference PowerShell 7.x build. SPEC.POWERSHELL.md §6.1: a token
/// is cmdlet-shaped only when the segment before its single <c>-</c> is one
/// of these verbs. Gating on the verb table — rather than "any letters
/// before a dash" — keeps hyphenated native tools (<c>docker-compose</c>,
/// <c>apt-get</c>) on the native-command path.
/// </summary>
internal static class PwshApprovedVerbs
{
    /// <summary>The 98 approved verbs, matched case-insensitively.</summary>
    internal static readonly HashSet<string> Verbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Add", "Approve", "Assert", "Backup", "Block", "Build",
            "Checkpoint", "Clear", "Close", "Compare", "Complete", "Compress",
            "Confirm", "Connect", "Convert", "ConvertFrom", "ConvertTo",
            "Copy", "Debug", "Deny", "Deploy", "Disable", "Disconnect",
            "Dismount", "Edit", "Enable", "Enter", "Exit", "Expand", "Export",
            "Find", "Format", "Get", "Grant", "Group", "Hide", "Import",
            "Initialize", "Install", "Invoke", "Join", "Limit", "Lock",
            "Measure", "Merge", "Mount", "Move", "New", "Open", "Optimize",
            "Out", "Ping", "Pop", "Protect", "Publish", "Push", "Read",
            "Receive", "Redo", "Register", "Remove", "Rename", "Repair",
            "Request", "Reset", "Resize", "Resolve", "Restart", "Restore",
            "Resume", "Revoke", "Save", "Search", "Select", "Send", "Set",
            "Show", "Skip", "Split", "Start", "Step", "Stop", "Submit",
            "Suspend", "Switch", "Sync", "Test", "Trace", "Unblock", "Undo",
            "Uninstall", "Unlock", "Unprotect", "Unpublish", "Unregister",
            "Update", "Use", "Wait", "Watch", "Write",
        };

    /// <summary>
    /// SPEC.POWERSHELL.md §6.1: returns true when <paramref name="token"/>
    /// has cmdlet shape — length in [3, 64], exactly one <c>-</c>, the
    /// segment before <c>-</c> is an approved verb, and the segment after
    /// <c>-</c> begins with an ASCII letter and is ASCII letters/digits only.
    /// Case-insensitive throughout.
    /// </summary>
    internal static bool IsCmdletShaped(string token)
    {
        if (token is null || token.Length < 3 || token.Length > 64)
        {
            return false;
        }

        var dash = token.IndexOf('-');
        if (dash <= 0 || dash == token.Length - 1)
        {
            return false;
        }

        // Exactly one dash.
        if (token.IndexOf('-', dash + 1) >= 0)
        {
            return false;
        }

        var verb = token.Substring(0, dash);
        if (!Verbs.Contains(verb))
        {
            return false;
        }

        // Noun: first char ASCII letter, remainder ASCII letters/digits.
        var nounStart = dash + 1;
        var firstNoun = token[nounStart];
        if (!IsAsciiLetter(firstNoun))
        {
            return false;
        }

        for (var i = nounStart + 1; i < token.Length; i++)
        {
            var c = token[i];
            if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}
