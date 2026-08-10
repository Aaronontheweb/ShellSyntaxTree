// -----------------------------------------------------------------------
// <copyright file="RedirectAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
namespace ShellSyntaxTree;

/// <summary>A closed description of a redirect's source stream.</summary>
public abstract record RedirectSource
{
    private protected RedirectSource()
    {
    }

    private protected abstract object LibraryOwnership { get; }

    internal RedirectSourceKind Kind => this switch
    {
        Unknown => RedirectSourceKind.Unknown,
        Default => RedirectSourceKind.Default,
        Descriptor => RedirectSourceKind.Descriptor,
        PowerShellAllStreams => RedirectSourceKind.PowerShellAllStreams,
        _ => RedirectSourceKind.Unknown,
    };

    internal int? DescriptorValue => (this as Descriptor)?.Value;

    /// <summary>The source stream could not be proved.</summary>
    public sealed record Unknown : RedirectSource
    {
        internal Unknown()
        {
        }

        private protected override object LibraryOwnership => this;
    }

    /// <summary>The shell's operation-specific default stream applies.</summary>
    public sealed record Default : RedirectSource
    {
        internal Default()
        {
        }

        private protected override object LibraryOwnership => this;
    }

    /// <summary>An explicit numeric descriptor applies.</summary>
    public sealed record Descriptor : RedirectSource
    {
        internal Descriptor(int value) => Value = value;

        private protected override object LibraryOwnership => this;

        /// <summary>Gets the numeric descriptor.</summary>
        public int Value { get; }
    }

    /// <summary>PowerShell's all-streams selector applies.</summary>
    public sealed record PowerShellAllStreams : RedirectSource
    {
        internal PowerShellAllStreams()
        {
        }

        private protected override object LibraryOwnership => this;
    }
}

/// <summary>Base record for closed, parser-owned redirect alternatives.</summary>
public abstract record RedirectAnalysis
{
    private protected RedirectAnalysis()
    {
    }

    private protected abstract object LibraryOwnership { get; }

    internal int RedirectIndex { get; init; } = -1;

    internal RedirectOperation Operation => this switch
    {
        FileRedirectAnalysis { Mode: FileRedirectMode.Input } => RedirectOperation.FileInput,
        FileRedirectAnalysis { Mode: FileRedirectMode.Output } => RedirectOperation.FileOutput,
        FileRedirectAnalysis { Mode: FileRedirectMode.Append } => RedirectOperation.FileAppend,
        FileRedirectAnalysis { Mode: FileRedirectMode.CombinedOutput } =>
            RedirectOperation.CombinedOutput,
        FileRedirectAnalysis { Mode: FileRedirectMode.CombinedOutputAppend } =>
            RedirectOperation.CombinedOutputAppend,
        DescriptorDuplicateRedirectAnalysis => RedirectOperation.DescriptorDuplicate,
        DescriptorMoveRedirectAnalysis => RedirectOperation.DescriptorMove,
        DescriptorCloseRedirectAnalysis => RedirectOperation.DescriptorClose,
        HereDocumentRedirectAnalysis => RedirectOperation.HereDocument,
        HereStringRedirectAnalysis => RedirectOperation.HereString,
        _ => RedirectOperation.Unknown,
    };

    internal int? TargetDescriptor => this switch
    {
        DescriptorDuplicateRedirectAnalysis duplicate => duplicate.TargetDescriptor,
        DescriptorMoveRedirectAnalysis move => move.TargetDescriptor,
        _ => null,
    };

    internal ShellValueDomain Target => this switch
    {
        FileRedirectAnalysis file => file.Target,
        HereStringRedirectAnalysis hereString => hereString.Data,
        _ => new ShellValueDomain.Unknown(),
    };

    internal HereDocumentAnalysis? HereDocument =>
        (this as HereDocumentRedirectAnalysis)?.Document;

    internal bool IsPathRelevant => this is FileRedirectAnalysis;

    /// <summary>Gets the exact compatibility redirect represented by this analysis.</summary>
    public Redirect Authored { get; internal init; } = null!;

    /// <summary>Gets the redirect's source stream.</summary>
    public RedirectSource Source { get; internal init; } = null!;

    /// <summary>Gets whether the parser-owned redirect analysis is complete.</summary>
    public bool IsComplete { get; internal init; }
}

/// <summary>A redirect to or from a filesystem path.</summary>
public sealed record FileRedirectAnalysis : RedirectAnalysis
{
    internal FileRedirectAnalysis(FileRedirectMode mode) => Mode = mode;

    private protected override object LibraryOwnership => this;

    /// <summary>Gets the file operation.</summary>
    public FileRedirectMode Mode { get; }

    /// <summary>Gets the target path proof.</summary>
    public new ShellValueDomain Target { get; internal init; } = null!;
}

/// <summary>A completely delimited redirect whose semantics remain unproved.</summary>
public sealed record UnresolvedRedirectAnalysis : RedirectAnalysis
{
    internal UnresolvedRedirectAnalysis()
    {
    }

    private protected override object LibraryOwnership => this;
}

/// <summary>Identifies a file redirect operation.</summary>
public enum FileRedirectMode
{
    Input,
    Output,
    Append,
    CombinedOutput,
    CombinedOutputAppend,
}

/// <summary>Duplicates a source descriptor to a target descriptor.</summary>
public sealed record DescriptorDuplicateRedirectAnalysis : RedirectAnalysis
{
    internal DescriptorDuplicateRedirectAnalysis()
    {
    }

    private protected override object LibraryOwnership => this;

    public new int TargetDescriptor { get; internal init; }
}

/// <summary>Moves a source descriptor to a target descriptor.</summary>
public sealed record DescriptorMoveRedirectAnalysis : RedirectAnalysis
{
    internal DescriptorMoveRedirectAnalysis()
    {
    }

    private protected override object LibraryOwnership => this;

    public new int TargetDescriptor { get; internal init; }
}

/// <summary>Closes a source descriptor.</summary>
public sealed record DescriptorCloseRedirectAnalysis : RedirectAnalysis
{
    internal DescriptorCloseRedirectAnalysis()
    {
    }

    private protected override object LibraryOwnership => this;
}

/// <summary>Supplies standard input from a Bash heredoc.</summary>
public sealed record HereDocumentRedirectAnalysis : RedirectAnalysis
{
    internal HereDocumentRedirectAnalysis()
    {
    }

    private protected override object LibraryOwnership => this;

    public HereDocumentAnalysis Document { get; internal init; } = null!;
}

/// <summary>Supplies standard input from a Bash here string.</summary>
public sealed record HereStringRedirectAnalysis : RedirectAnalysis
{
    internal HereStringRedirectAnalysis()
    {
    }

    private protected override object LibraryOwnership => this;

    public ShellValueDomain Data { get; internal init; } = null!;
}

/// <summary>Authored heredoc delimiter, body, and expansion facts.</summary>
public sealed record HereDocumentAnalysis
{
    internal HereDocumentAnalysis()
    {
    }

    public ShellSourceFragment Delimiter { get; internal init; } = null!;

    public ShellSourceFragment Body { get; internal init; } = null!;

    public HereDocumentExpansionMode ExpansionMode { get; internal init; }

    public bool StripLeadingTabs { get; internal init; }

    public bool IsComplete { get; internal init; }
}

public enum HereDocumentExpansionMode
{
    Unknown,
    Literal,
    Expand,
}

/// <summary>Internal mutable-by-copy redirect facts produced by shell analyzers.</summary>
internal sealed record RedirectAnalysisFacts
{
    internal int RedirectIndex { get; init; } = -1;

    internal RedirectSourceFacts Source { get; init; } = new();

    internal RedirectOperation Operation { get; init; }

    internal int? TargetDescriptor { get; init; }

    internal ShellValueDomainFacts Target { get; init; } = ShellValueDomainFacts.Unknown;

    internal HereDocumentAnalysis? HereDocument { get; init; }

    internal bool IsPathRelevant { get; init; }

    internal bool IsComplete { get; init; }
}

internal sealed record RedirectSourceFacts
{
    internal RedirectSourceKind Kind { get; init; }

    internal int? Descriptor { get; init; }
}

internal enum RedirectSourceKind
{
    Unknown,
    Default,
    Descriptor,
    PowerShellAllStreams,
}

internal enum RedirectOperation
{
    Unknown,
    FileInput,
    FileOutput,
    FileAppend,
    DescriptorDuplicate,
    DescriptorClose,
    DescriptorMove,
    CombinedOutput,
    CombinedOutputAppend,
    HereDocument,
    HereString,
}
