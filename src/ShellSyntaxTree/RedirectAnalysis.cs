// -----------------------------------------------------------------------
// <copyright file="RedirectAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>Explicit analysis for one compatibility redirect.</summary>
public sealed record RedirectAnalysis
{
    /// <summary>Gets the index into <see cref="Clause.Redirects"/>.</summary>
    public int RedirectIndex { get; init; } = -1;

    /// <summary>Gets the redirect's source stream.</summary>
    public RedirectSource Source { get; init; } = new();

    /// <summary>Gets the redirect operation.</summary>
    public RedirectOperation Operation { get; init; }

    /// <summary>Gets a static target descriptor, when proved.</summary>
    public int? TargetDescriptor { get; init; }

    /// <summary>Gets the target path or data proof.</summary>
    public ShellValueDomain Target { get; init; } = ShellValueDomain.Unknown;

    /// <summary>Gets heredoc-specific facts for a heredoc operation.</summary>
    public HereDocumentAnalysis? HereDocument { get; init; }

    /// <summary>Gets whether the target is a filesystem policy input.</summary>
    public bool IsPathRelevant { get; init; }

    /// <summary>Gets whether the parser-owned redirect analysis is complete.</summary>
    public bool IsComplete { get; init; }
}

/// <summary>Authored heredoc delimiter, body, and expansion facts.</summary>
public sealed record HereDocumentAnalysis
{
    /// <summary>Gets the authored delimiter.</summary>
    public ShellSourceFragment Delimiter { get; init; } = new();

    /// <summary>Gets the authored body.</summary>
    public ShellSourceFragment Body { get; init; } = new();

    /// <summary>Gets whether the body is literal or expanding.</summary>
    public HereDocumentExpansionMode ExpansionMode { get; init; }

    /// <summary>Gets whether leading body tabs are stripped.</summary>
    public bool StripLeadingTabs { get; init; }

    /// <summary>Gets whether delimiter, body, and substitutions are complete.</summary>
    public bool IsComplete { get; init; }
}

/// <summary>Identifies heredoc body expansion behavior.</summary>
public enum HereDocumentExpansionMode
{
    /// <summary>The expansion behavior is unknown.</summary>
    Unknown,
    /// <summary>The body is literal data.</summary>
    Literal,
    /// <summary>The body performs shell expansion.</summary>
    Expand,
}

/// <summary>A shell redirect's source stream.</summary>
public sealed record RedirectSource
{
    /// <summary>Gets the source-stream kind.</summary>
    public RedirectSourceKind Kind { get; init; }

    /// <summary>Gets the numeric descriptor for a descriptor source.</summary>
    public int? Descriptor { get; init; }
}

/// <summary>Identifies the source side of a redirect.</summary>
public enum RedirectSourceKind
{
    /// <summary>The source is unknown.</summary>
    Unknown,
    /// <summary>The shell's default source stream applies.</summary>
    Default,
    /// <summary>An explicit numeric descriptor applies.</summary>
    Descriptor,
    /// <summary>PowerShell's all-streams selector applies.</summary>
    PowerShellAllStreams,
}

/// <summary>Identifies a redirect's shell operation.</summary>
public enum RedirectOperation
{
    /// <summary>The operation is unknown.</summary>
    Unknown,
    /// <summary>Read input from a file.</summary>
    FileInput,
    /// <summary>Write output to a file.</summary>
    FileOutput,
    /// <summary>Append output to a file.</summary>
    FileAppend,
    /// <summary>Duplicate one descriptor to another.</summary>
    DescriptorDuplicate,
    /// <summary>Close a descriptor.</summary>
    DescriptorClose,
    /// <summary>Move and close a descriptor.</summary>
    DescriptorMove,
    /// <summary>Write standard output and error to one file.</summary>
    CombinedOutput,
    /// <summary>Append standard output and error to one file.</summary>
    CombinedOutputAppend,
    /// <summary>Supply heredoc data.</summary>
    HereDocument,
    /// <summary>Supply Bash here-string data.</summary>
    HereString,
}
