// -----------------------------------------------------------------------
// <copyright file="PwshParserOptions.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>Compatibility option for PowerShell initial host-state analysis.</summary>
public enum PwshInitialStateMode
{
    /// <summary>
    /// No safe assumption is made about ambient PowerShell variable-binding
    /// state. Static authored command completeness is unaffected.
    /// </summary>
    Unknown,

    /// <summary>
    /// The source runs in a new non-interactive PowerShell process with
    /// profiles disabled and no reused or caller-initialized runspace.
    /// </summary>
    IsolatedNonInteractiveNoProfile,
}

/// <summary>
/// Selects the PowerShell language edition and versioned parser metadata.
/// </summary>
public enum PwshDialect
{
    /// <summary>No supported PowerShell dialect has been selected.</summary>
    Unknown,

    /// <summary>PowerShell 7.6 servicing releases from 7.6.4, executed by <c>pwsh</c>.</summary>
    PowerShell7,

    /// <summary>Windows PowerShell 5.1, executed by <c>powershell.exe</c>.</summary>
    WindowsPowerShell51,
}

/// <summary>
/// Configuration knobs for <see cref="PwshParser"/>. The resolver knobs live
/// on the shared <see cref="ShellParserOptions"/> base.
/// </summary>
public sealed record PwshParserOptions : ShellParserOptions
{
    /// <summary>
    /// Gets the caller-proved initial PowerShell runspace-state contract. The
    /// default leaves loop-dependent effective values unproved.
    /// </summary>
    public PwshInitialStateMode InitialStateMode { get; init; }

    /// <summary>
    /// Gets the PowerShell dialect whose grammar and versioned metadata apply.
    /// Existing callers retain the PowerShell 7 behavior shipped before this
    /// option was added.
    /// </summary>
    public PwshDialect Dialect { get; init; } = PwshDialect.PowerShell7;
}
