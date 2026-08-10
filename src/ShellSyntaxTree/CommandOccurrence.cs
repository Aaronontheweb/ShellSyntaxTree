// -----------------------------------------------------------------------
// <copyright file="CommandOccurrence.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal;

namespace ShellSyntaxTree;

/// <summary>One authored simple command that may execute.</summary>
public sealed record CommandOccurrence
{
    private IReadOnlyList<CommandAncestryFrame> _ancestry =
        Array.Empty<CommandAncestryFrame>();
    private IReadOnlyList<AnalyzedArgument> _arguments = Array.Empty<AnalyzedArgument>();
    private IReadOnlyList<RedirectAnalysis> _redirects = Array.Empty<RedirectAnalysis>();

    internal CommandOccurrence()
    {
    }

    /// <summary>Gets the shared compatibility leaf.</summary>
    public Clause Clause { get; internal init; } = new();

    /// <summary>Gets the nearest structural execution role.</summary>
    public CommandOccurrenceRole ImmediateRole { get; internal init; }

    /// <summary>Gets ancestry ordered from outermost to innermost.</summary>
    public IReadOnlyList<CommandAncestryFrame> Ancestry
    {
        get => _ancestry;
        internal init => _ancestry = PublicCollection.Copy(value);
    }

    /// <summary>Gets one analyzed value for every authored, non-cwd argument.</summary>
    public IReadOnlyList<AnalyzedArgument> Arguments
    {
        get => _arguments;
        internal init => _arguments = PublicCollection.Copy(value);
    }

    internal IReadOnlyList<EffectiveArgument> EffectiveArguments
    {
        get
        {
            var values = new List<EffectiveArgument>();
            for (var index = 0; index < _arguments.Count; index++)
            {
                if (!_arguments[index].HasEffectiveValue)
                {
                    continue;
                }

                values.Add(new EffectiveArgument
                {
                    ClauseElementIndex = FindElementIndex(_arguments[index].Element),
                    Value = _arguments[index].Value,
                });
            }

            return values.ToArray();
        }
    }

    /// <summary>Gets the effective working-directory proof.</summary>
    public ShellValueDomain WorkingDirectory { get; internal init; } =
        new ShellValueDomain.Unknown();

    /// <summary>Gets explicit redirect analysis in compatibility redirect order.</summary>
    public IReadOnlyList<RedirectAnalysis> Redirects
    {
        get => _redirects;
        internal init => _redirects = PublicCollection.Copy(value);
    }

    /// <summary>Gets whether parser-owned shell analysis is structurally complete.</summary>
    public bool IsComplete { get; internal init; }

    private int FindElementIndex(ClauseElement element)
    {
        for (var index = 0; index < Clause.Elements.Count; index++)
        {
            if (ReferenceEquals(Clause.Elements[index], element))
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>Identifies the nearest structural role of a command occurrence.</summary>
public enum CommandOccurrenceRole
{
    Unknown,
    Ordinary,
    PipelineStage,
    Iterator,
    LoopBody,
    Substitution,
    ExecutionRegion,
}

/// <summary>One compositional structural ancestor of a command occurrence.</summary>
public sealed record CommandAncestryFrame
{
    internal CommandAncestryFrame()
    {
    }

    /// <summary>Gets the actual ancestor node.</summary>
    public ShellSyntaxNode Ancestor { get; internal init; } = null!;

    /// <summary>Gets the occurrence's region within the ancestor.</summary>
    public CommandAncestryRegion Region { get; internal init; }

    /// <summary>Gets the child index for repeated regions, when applicable.</summary>
    public int? ChildIndex { get; internal init; }

    internal ShellSyntaxKind AncestorKind => Ancestor.Kind;

    internal int? SourceStart => Ancestor.SourceStart;

    internal int? SourceLength => Ancestor.SourceLength;
}

/// <summary>Identifies an occurrence's region within a structural ancestor.</summary>
public enum CommandAncestryRegion
{
    Unknown,
    Root,
    Statement,
    PipelineStage,
    GroupBody,
    Iterator,
    LoopBody,
    Substitution,
    ExecutionRegion,
}

/// <summary>One parser-owned join between a compatibility argument and its source.</summary>
public sealed record AnalyzedArgument
{
    internal AnalyzedArgument()
    {
    }

    /// <summary>Gets the compatibility argument.</summary>
    public Arg Argument { get; internal init; } = null!;

    /// <summary>Gets the authored element that produced the argument.</summary>
    public ClauseElement Element { get; internal init; } = null!;

    /// <summary>Gets the effective shell-value proof.</summary>
    public ShellValueDomain Value { get; internal init; } = null!;

    internal bool HasEffectiveValue { get; init; }
}

internal sealed record EffectiveArgument
{
    internal int ClauseElementIndex { get; init; } = -1;

    internal ShellValueDomain Value { get; init; } = null!;
}

/// <summary>A bounded, non-executing proof for a shell value.</summary>
public abstract record ShellValueDomain
{
    private protected ShellValueDomain()
    {
    }

    private protected abstract object LibraryOwnership { get; }

    internal abstract ShellValueDomainKind InternalKind { get; }

    internal ShellValueDomainKind Kind => InternalKind;

    internal virtual IReadOnlyList<string> InternalValues => Array.Empty<string>();

    internal IReadOnlyList<string> Values => InternalValues;

    internal virtual string? InternalPattern => null;

    internal string? Pattern => InternalPattern;

    internal virtual string? InternalCoveringDirectory => null;

    internal string? CoveringDirectory => InternalCoveringDirectory;

    /// <summary>No bounded value is proved.</summary>
    public sealed record Unknown : ShellValueDomain
    {
        internal Unknown()
        {
        }

        private protected override object LibraryOwnership => this;

        internal override ShellValueDomainKind InternalKind => ShellValueDomainKind.Unknown;
    }

    /// <summary>Exactly one value is proved.</summary>
    public sealed record Exact : ShellValueDomain
    {
        internal Exact(string value) => Value = value;

        private protected override object LibraryOwnership => this;

        internal override ShellValueDomainKind InternalKind => ShellValueDomainKind.Exact;

        internal override IReadOnlyList<string> InternalValues => new[] { Value };

        /// <summary>Gets the proved value.</summary>
        public string Value { get; }
    }

    /// <summary>Two through 32 distinct values are proved.</summary>
    public sealed record FiniteSet : ShellValueDomain
    {
        internal FiniteSet(IEnumerable<string> values) =>
            Values = PublicCollection.Copy(values);

        private protected override object LibraryOwnership => this;

        internal override ShellValueDomainKind InternalKind =>
            ShellValueDomainKind.FiniteSet;

        internal override IReadOnlyList<string> InternalValues => Values;

        /// <summary>Gets the proved finite values.</summary>
        public new IReadOnlyList<string> Values { get; }
    }

    /// <summary>A bounded path pattern and its conservative covering directory.</summary>
    public sealed record PathPattern : ShellValueDomain
    {
        internal PathPattern(string pattern, string coveringDirectory)
        {
            Pattern = pattern;
            CoveringDirectory = coveringDirectory;
        }

        private protected override object LibraryOwnership => this;

        internal override ShellValueDomainKind InternalKind => ShellValueDomainKind.Pattern;

        internal override string InternalPattern => Pattern;

        internal override string InternalCoveringDirectory => CoveringDirectory;

        /// <summary>Gets the symbolic path pattern.</summary>
        public new string Pattern { get; }

        /// <summary>Gets the conservative covering directory.</summary>
        public new string CoveringDirectory { get; }
    }
}

internal enum ShellValueDomainKind
{
    Unknown,
    Exact,
    FiniteSet,
    Pattern,
}

internal static class ShellValueDomains
{
    internal static ShellValueDomain Unknown { get; } = new ShellValueDomain.Unknown();

    internal static ShellValueDomain Exact(string value) => new ShellValueDomain.Exact(value);

    internal static ShellValueDomain FiniteSet(IEnumerable<string> values) =>
        new ShellValueDomain.FiniteSet(values);

    internal static ShellValueDomain Pattern(string pattern, string coveringDirectory) =>
        new ShellValueDomain.PathPattern(pattern, coveringDirectory);
}
