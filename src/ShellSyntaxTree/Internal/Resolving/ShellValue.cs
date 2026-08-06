// -----------------------------------------------------------------------
// <copyright file="ShellValue.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;

namespace ShellSyntaxTree.Internal.Resolving;

internal enum ShellValueFragmentKind
{
    Literal,
    Expansion,
    Opaque,
}

[Flags]
internal enum ShellLexicalTransform
{
    None = 0,
    Variable = 1,
    Tilde = 2,
    Glob = 4,
    FieldSplit = 8,
}

internal enum ShellExpansionKind
{
    Variable,
    SpecialParameter,
    PositionalParameter,
    Tilde,
    Glob,
    ArraySeparator,
}

internal enum ShellValueCardinality
{
    ExactlyOne,
    ZeroOrOne,
    ZeroOrMore,
    Unknown,
}

internal enum ShellOpaqueCause
{
    None,
    CommandSubstitution,
    PowerShellSubexpression,
    PowerShellExpressionSuffix,
    Splat,
    Unsupported,
}

internal enum ShellResolutionConsumer
{
    BashArgument,
    BashRedirect,
    PowerShellNativeArgument,
    PowerShellCmdletPath,
    PowerShellCmdletLiteralPath,
    PowerShellRedirect,
}

internal readonly record struct ShellExpansionReference(
    ShellExpansionKind Kind,
    string? Name);

internal readonly record struct ShellValueFragment(
    string Value,
    ShellValueFragmentKind Kind,
    ShellLexicalTransform AllowedTransforms,
    ShellExpansionReference? Expansion,
    ShellValueCardinality Cardinality,
    ShellOpaqueCause OpaqueCause,
    int? SourceStart,
    int? SourceLength);

/// <summary>
/// One decoded shell value together with the shell-owned facts needed to
/// decide which authored regions may still transform.
/// </summary>
internal sealed class ShellValue
{
    private readonly ShellValueFragment[] _fragments;

    internal ShellValue(string decoded, ShellValueFragment[] fragments)
    {
        Decoded = decoded;
        _fragments = fragments;
    }

    internal string Decoded { get; }

    internal IReadOnlyList<ShellValueFragment> Fragments => _fragments;

    internal bool HasOpaqueFragment
    {
        get
        {
            foreach (var fragment in _fragments)
            {
                if (fragment.Kind == ShellValueFragmentKind.Opaque)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal bool HasOpaqueFragmentOtherThan(ShellOpaqueCause allowedCause)
    {
        foreach (var fragment in _fragments)
        {
            if (fragment.Kind == ShellValueFragmentKind.Opaque
                && fragment.OpaqueCause != allowedCause)
            {
                return true;
            }
        }

        return false;
    }

    internal static ShellValue Literal(
        string value,
        int? sourceStart = null,
        int? sourceLength = null) =>
        Create(
            value,
            ShellValueFragmentKind.Literal,
            ShellLexicalTransform.None,
            null,
            ShellValueCardinality.ExactlyOne,
            ShellOpaqueCause.None,
            sourceStart,
            sourceLength);

    internal static ShellValue Opaque(
        string value,
        ShellOpaqueCause cause,
        int? sourceStart = null,
        int? sourceLength = null) =>
        Create(
            value,
            ShellValueFragmentKind.Opaque,
            ShellLexicalTransform.None,
            null,
            ShellValueCardinality.Unknown,
            cause,
            sourceStart,
            sourceLength);

    internal static ShellValue Expansion(
        string value,
        ShellLexicalTransform allowedTransforms,
        ShellExpansionReference expansion,
        ShellValueCardinality cardinality,
        int? sourceStart = null,
        int? sourceLength = null) =>
        Create(
            value,
            ShellValueFragmentKind.Expansion,
            allowedTransforms,
            expansion,
            cardinality,
            ShellOpaqueCause.None,
            sourceStart,
            sourceLength);

    internal static ShellValue Concat(IEnumerable<ShellValue> values)
    {
        var builder = new ShellValueBuilder();
        foreach (var value in values)
        {
            builder.Append(value);
        }

        return builder.Build();
    }

    internal ShellValue Slice(int start) => Slice(start, Decoded.Length - start, false);

    internal ShellValue Slice(int start, int length) => Slice(start, length, false);

    internal ShellValue Slice(
        int start,
        int length,
        bool preserveLiteralPrefixBoundary)
    {
        if (start < 0 || length < 0 || start + length > Decoded.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var builder = new ShellValueBuilder();
        if (preserveLiteralPrefixBoundary
            && start > 0
            && TryGetLiteralBoundary(start, out var boundarySourceStart))
        {
            builder.AppendBoundary(boundarySourceStart);
        }

        var sliceEnd = start + length;
        var decodedOffset = 0;
        foreach (var fragment in _fragments)
        {
            if (decodedOffset > sliceEnd
                || (decodedOffset == sliceEnd && fragment.Value.Length > 0))
            {
                break;
            }

            var fragmentEnd = decodedOffset + fragment.Value.Length;
            if (fragment.Value.Length == 0
                && decodedOffset >= start
                && decodedOffset <= sliceEnd)
            {
                builder.Append(fragment);
            }

            var overlapStart = Math.Max(start, decodedOffset);
            var overlapEnd = Math.Min(sliceEnd, fragmentEnd);
            if (overlapStart < overlapEnd)
            {
                var localStart = overlapStart - decodedOffset;
                var localLength = overlapEnd - overlapStart;
                var fragmentStart = fragment.SourceStart;
                var fragmentLength = fragment.SourceLength;
                if (localStart != 0 || localLength != fragment.Value.Length)
                {
                    if (fragmentStart is not null
                        && fragmentLength == fragment.Value.Length)
                    {
                        fragmentStart += localStart;
                        fragmentLength = localLength;
                    }
                    else
                    {
                        fragmentStart = null;
                        fragmentLength = null;
                    }
                }

                builder.Append(fragment with
                {
                    Value = fragment.Value.Substring(localStart, localLength),
                    SourceStart = fragmentStart,
                    SourceLength = fragmentLength,
                });
            }

            decodedOffset = fragmentEnd;
        }

        return builder.Build();
    }

    private bool TryGetLiteralBoundary(int decodedBoundary, out int? sourceStart)
    {
        var decodedOffset = 0;
        foreach (var fragment in _fragments)
        {
            var fragmentEnd = decodedOffset + fragment.Value.Length;
            if (decodedBoundary > decodedOffset
                && decodedBoundary <= fragmentEnd
                && fragment.Kind == ShellValueFragmentKind.Literal)
            {
                sourceStart = fragment.SourceStart is not null
                    && fragment.SourceLength == fragment.Value.Length
                        ? fragment.SourceStart + decodedBoundary - decodedOffset
                        : null;
                return true;
            }

            if (fragmentEnd >= decodedBoundary)
            {
                break;
            }

            decodedOffset = fragmentEnd;
        }

        sourceStart = null;
        return false;
    }

    private static ShellValue Create(
        string value,
        ShellValueFragmentKind kind,
        ShellLexicalTransform allowedTransforms,
        ShellExpansionReference? expansion,
        ShellValueCardinality cardinality,
        ShellOpaqueCause opaqueCause,
        int? sourceStart,
        int? sourceLength)
    {
        if (value.Length == 0 && sourceStart is null && sourceLength is null)
        {
            return new ShellValue(string.Empty, Array.Empty<ShellValueFragment>());
        }

        return new ShellValue(
            value,
            new[]
            {
                new ShellValueFragment(
                    value,
                    kind,
                    allowedTransforms,
                    expansion,
                    cardinality,
                    opaqueCause,
                    sourceStart,
                    sourceLength),
            });
    }
}

internal sealed class ShellValueBuilder
{
    private readonly List<ShellValueFragment> _fragments = new();

    internal void AppendBoundary(int? sourceStart) =>
        Append(new ShellValueFragment(
            string.Empty,
            ShellValueFragmentKind.Literal,
            ShellLexicalTransform.None,
            null,
            ShellValueCardinality.ExactlyOne,
            ShellOpaqueCause.None,
            sourceStart,
            0));

    internal void AppendLiteral(
        char value,
        int? sourceStart,
        int? sourceLength) =>
        AppendLiteral(value.ToString(), sourceStart, sourceLength);

    internal void AppendLiteral(
        string value,
        int? sourceStart,
        int? sourceLength) =>
        Append(new ShellValueFragment(
            value,
            ShellValueFragmentKind.Literal,
            ShellLexicalTransform.None,
            null,
            ShellValueCardinality.ExactlyOne,
            ShellOpaqueCause.None,
            sourceStart,
            sourceLength));

    internal void AppendExpansion(
        string value,
        ShellLexicalTransform allowedTransforms,
        ShellExpansionReference expansion,
        ShellValueCardinality cardinality,
        int? sourceStart,
        int? sourceLength) =>
        Append(new ShellValueFragment(
            value,
            ShellValueFragmentKind.Expansion,
            allowedTransforms,
            expansion,
            cardinality,
            ShellOpaqueCause.None,
            sourceStart,
            sourceLength));

    internal void AppendOpaque(
        string value,
        ShellOpaqueCause cause,
        int? sourceStart,
        int? sourceLength) =>
        Append(new ShellValueFragment(
            value,
            ShellValueFragmentKind.Opaque,
            ShellLexicalTransform.None,
            null,
            ShellValueCardinality.Unknown,
            cause,
            sourceStart,
            sourceLength));

    internal void Append(ShellValue value)
    {
        foreach (var fragment in value.Fragments)
        {
            Append(fragment);
        }
    }

    internal void Append(ShellValueFragment fragment)
    {
        if (_fragments.Count > 0)
        {
            var previous = _fragments[_fragments.Count - 1];
            var sourceIsContiguous = previous.SourceStart is not null
                && previous.SourceLength is not null
                && fragment.SourceStart == previous.SourceStart + previous.SourceLength;
            var bothUnmapped = previous.SourceStart is null && fragment.SourceStart is null;
            if (previous.Value.Length > 0
                && fragment.Value.Length > 0
                && previous.Kind == ShellValueFragmentKind.Literal
                && fragment.Kind == ShellValueFragmentKind.Literal
                && previous.AllowedTransforms == fragment.AllowedTransforms
                && previous.Expansion == fragment.Expansion
                && previous.Cardinality == fragment.Cardinality
                && previous.OpaqueCause == fragment.OpaqueCause
                && (sourceIsContiguous || bothUnmapped))
            {
                _fragments[_fragments.Count - 1] = previous with
                {
                    Value = previous.Value + fragment.Value,
                    SourceLength = sourceIsContiguous
                        ? previous.SourceLength + fragment.SourceLength
                        : null,
                };
                return;
            }
        }

        _fragments.Add(fragment);
    }

    internal ShellValue Build()
    {
        if (_fragments.Count == 0)
        {
            return ShellValue.Literal(string.Empty);
        }

        var decoded = new StringBuilder();
        foreach (var fragment in _fragments)
        {
            decoded.Append(fragment.Value);
        }

        return new ShellValue(decoded.ToString(), _fragments.ToArray());
    }
}
