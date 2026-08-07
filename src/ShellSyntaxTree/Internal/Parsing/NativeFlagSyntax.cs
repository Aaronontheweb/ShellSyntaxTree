// -----------------------------------------------------------------------
// <copyright file="NativeFlagSyntax.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree.Internal.Parsing;

/// <summary>
/// Option syntax shared by every native command, whichever shell invoked
/// it. <c>SPEC.POWERSHELL.md</c> §7.3 requires the PowerShell native-command
/// path to behave identically to <c>SPEC.md</c> §7, so the rules live here
/// once instead of once per parser — a second copy could drift, and this one
/// decides whether a path reaches the consumer's gate.
/// </summary>
internal static class NativeFlagSyntax
{
    /// <summary>
    /// Split an equals-form option — <c>--output=file.txt</c> — into its
    /// flag and value halves so the value can be path-classified through the
    /// per-verb table.
    /// </summary>
    /// <returns>
    /// <c>false</c> when <paramref name="raw"/> is not a flag, carries no
    /// <c>=</c>, or ends with one (<c>--work-tree=</c>). Those stay a single
    /// opaque arg — there is no value half to classify.
    /// </returns>
    internal static bool TrySplitEqualsFlag(
        string raw, out string flagPart, out string valuePart)
    {
        if (raw.Length < 2 || raw[0] != '-')
        {
            flagPart = "";
            valuePart = "";
            return false;
        }

        var eq = raw.IndexOf('=');
        if (eq <= 0 || eq == raw.Length - 1)
        {
            flagPart = "";
            valuePart = "";
            return false;
        }

        flagPart = raw.Substring(0, eq);
        valuePart = raw.Substring(eq + 1);
        return true;
    }

    /// <summary>
    /// Split an equals-form option while allowing an empty value prefix. This
    /// is used when later adjacent shell fragments complete the same argv
    /// entry.
    /// </summary>
    internal static bool TrySplitEqualsPrefix(
        string raw, out string flagPart, out string valuePrefix)
    {
        if (raw.Length < 2 || raw[0] != '-')
        {
            flagPart = "";
            valuePrefix = "";
            return false;
        }

        var eq = raw.IndexOf('=');
        if (eq <= 0)
        {
            flagPart = "";
            valuePrefix = "";
            return false;
        }

        flagPart = raw.Substring(0, eq);
        valuePrefix = raw.Substring(eq + 1);
        return true;
    }

}
