// -----------------------------------------------------------------------
// <copyright file="NativeArgumentFragmentClassifier.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Parsing;

/// <summary>
/// Aggregates one native argument from a parser-owned prefix and its adjacent
/// shell fragments. Shell adapters define fragment membership and fallback
/// provenance; this type owns only source-contiguous aggregation.
/// </summary>
internal static class NativeArgumentFragmentClassifier
{
    internal static bool TryClassify<TToken, TAdapter>(
        string source,
        int argumentSourceStart,
        int argumentPrefixSourceEnd,
        int valueSourceStart,
        string decodedArgumentPrefix,
        ShellValue decodedValuePrefix,
        IReadOnlyList<TToken> tokens,
        int firstFragmentIndex,
        TAdapter adapter,
        out NativeArgumentFragmentClassification classification)
        where TAdapter : struct, INativeArgumentFragmentAdapter<TToken>
    {
        classification = default;
        if (firstFragmentIndex < 0
            || firstFragmentIndex >= tokens.Count
            || !adapter.CanStart(tokens[firstFragmentIndex]))
        {
            return false;
        }

        var resolverValues = new List<ShellValue> { decodedValuePrefix };
        var previousSourceEnd = argumentPrefixSourceEnd;
        var nextTokenIndex = firstFragmentIndex;
        while (nextTokenIndex < tokens.Count
            && adapter.TryAdapt(tokens[nextTokenIndex], out var fragment)
            && fragment.SourceStart == previousSourceEnd)
        {
            resolverValues.Add(fragment.Value);
            previousSourceEnd = fragment.SourceStart + fragment.SourceLength;
            nextTokenIndex++;
        }

        if (nextTokenIndex == firstFragmentIndex)
        {
            return false;
        }

        var resolverValue = ShellValue.Concat(resolverValues);
        classification = new NativeArgumentFragmentClassification(
            Raw: source.Substring(
                argumentSourceStart,
                previousSourceEnd - argumentSourceStart),
            ValueRaw: source.Substring(
                valueSourceStart,
                previousSourceEnd - valueSourceStart),
            DecodedArgument: decodedArgumentPrefix + resolverValue.Decoded,
            ResolverValue: resolverValue,
            SourceStart: argumentSourceStart,
            SourceLength: previousSourceEnd - argumentSourceStart,
            NextTokenIndex: nextTokenIndex);
        return true;
    }
}

internal interface INativeArgumentFragmentAdapter<TToken>
{
    bool CanStart(TToken token);

    bool TryAdapt(TToken token, out NativeArgumentFragment fragment);
}

internal readonly record struct NativeArgumentFragment(
    ShellValue Value,
    int SourceStart,
    int SourceLength);

internal readonly record struct NativeArgumentFragmentClassification(
    string Raw,
    string ValueRaw,
    string DecodedArgument,
    ShellValue ResolverValue,
    int SourceStart,
    int SourceLength,
    int NextTokenIndex)
{
    internal string DecodedValue => ResolverValue.Decoded;

    internal bool HasOpaqueFragment => ResolverValue.HasOpaqueFragment;

    internal bool HasExpansionFragment
    {
        get
        {
            foreach (var fragment in ResolverValue.Fragments)
            {
                if (fragment.Kind == ShellValueFragmentKind.Expansion)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal bool HasOpaqueOrComputedFragment =>
        HasOpaqueFragment || HasExpansionFragment;

    internal bool AllValueFragmentsLiteral
    {
        get
        {
            foreach (var fragment in ResolverValue.Fragments)
            {
                if (fragment.Kind != ShellValueFragmentKind.Literal)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
