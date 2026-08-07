// -----------------------------------------------------------------------
// <copyright file="PwshSetLocationContext.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

/// <summary>
/// Parser-internal mutable state tracking the attributed cwd for the
/// current compound (SPEC.POWERSHELL.md §9). Simpler than the bash
/// <c>CdAttributionContext</c>: PowerShell's <c>( ... )</c> is a grouping
/// operator, not a subshell, so attribution <em>propagates through</em> a
/// group rather than being isolated by it — there is no push/pop stack.
/// </summary>
internal sealed class PwshSetLocationContext
{
    /// <summary>
    /// The resolved working directory inherited from the most recent
    /// literal <c>Set-Location</c> in the current compound. Null when no
    /// attribution is active or the target was dynamic.
    /// </summary>
    public string? ResolvedCwd { get; private set; }

    /// <summary>
    /// True when the most recent <c>Set-Location</c> target was dynamic — a
    /// variable, a non-FileSystem PSDrive, or <c>-</c> / <c>+</c>
    /// (SPEC.POWERSHELL.md §9 rule 2).
    /// </summary>
    public bool IsDynamic { get; private set; }

    /// <summary>True when a <c>Set-Location</c> set attribution.</summary>
    public bool HasAttribution => ResolvedCwd is not null || IsDynamic;

    /// <summary>Record a literal <c>Set-Location</c> attribution.</summary>
    public void SetLiteral(string resolvedTarget)
    {
        ResolvedCwd = resolvedTarget;
        IsDynamic = false;
    }

    /// <summary>Record that the most recent <c>Set-Location</c> target was dynamic.</summary>
    public void SetDynamic()
    {
        ResolvedCwd = null;
        IsDynamic = true;
    }

    internal PwshSetLocationContext Clone()
    {
        var clone = new PwshSetLocationContext();
        if (IsDynamic)
        {
            clone.SetDynamic();
        }
        else if (ResolvedCwd is not null)
        {
            clone.SetLiteral(ResolvedCwd);
        }

        return clone;
    }
}
