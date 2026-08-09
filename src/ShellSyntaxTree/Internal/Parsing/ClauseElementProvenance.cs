// -----------------------------------------------------------------------
// <copyright file="ClauseElementProvenance.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ShellSyntaxTree.Internal.Parsing;

/// <summary>
/// Shared provenance operations used when shell command-string wrappers lift
/// inner clauses into an outer <see cref="ParsedCommand"/>.
/// </summary>
internal static class ClauseElementProvenance
{
    private static readonly ConditionalWeakTable<ClauseElement, RedirectOwner> RedirectOwners =
        new();

    private static readonly ConditionalWeakTable<ClauseElement, ArgumentBindingCandidate>
        ArgumentBindingCandidates = new();

    /// <summary>
    /// Remove inner-source offsets that cannot be mapped exactly through
    /// quoting, escaping, script-block stripping, or encoded-command decoding.
    /// </summary>
    internal static IReadOnlyList<ClauseElement> WithoutOuterSourceSpans(
        IReadOnlyList<ClauseElement> elements,
        bool preserveArgumentBindingCandidate = false)
    {
        if (elements.Count == 0)
        {
            return Array.Empty<ClauseElement>();
        }

        var withoutSpans = new List<ClauseElement>(elements.Count);
        foreach (var element in elements)
        {
            var clone = element with
            {
                SourceStart = null,
                SourceLength = null,
            };
            CopyRedirectOwner(element, clone);
            if (preserveArgumentBindingCandidate)
            {
                CopyArgumentBindingCandidate(element, clone);
            }

            withoutSpans.Add(clone);
        }

        return withoutSpans;
    }

    internal static ClauseElement WithRedirectInvocationScopeDepth(
        ClauseElement element,
        int depth)
    {
        if (depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        RedirectOwners.Add(element, new RedirectOwner(depth));
        return element;
    }

    internal static int RedirectInvocationScopeDepth(ClauseElement element) =>
        RedirectOwners.TryGetValue(element, out var owner) ? owner.Depth : 0;

    internal static void SetArgumentBindingCandidate(
        ClauseElement element,
        bool? usesNativeArgumentBinding) =>
        ArgumentBindingCandidates.Add(
            element,
            new ArgumentBindingCandidate(usesNativeArgumentBinding));

    internal static bool TryGetArgumentBindingCandidate(
        ClauseElement element,
        out bool? usesNativeArgumentBinding)
    {
        if (ArgumentBindingCandidates.TryGetValue(element, out var candidate))
        {
            usesNativeArgumentBinding = candidate.UsesNativeArgumentBinding;
            return true;
        }

        usesNativeArgumentBinding = null;
        return false;
    }

    private static void CopyRedirectOwner(ClauseElement source, ClauseElement target)
    {
        if (RedirectOwners.TryGetValue(source, out var owner))
        {
            RedirectOwners.Add(target, owner);
        }
    }

    private static void CopyArgumentBindingCandidate(
        ClauseElement source,
        ClauseElement target)
    {
        if (ArgumentBindingCandidates.TryGetValue(source, out var candidate))
        {
            ArgumentBindingCandidates.Add(target, candidate);
        }
    }

    private sealed record RedirectOwner(int Depth);

    private sealed record ArgumentBindingCandidate(bool? UsesNativeArgumentBinding);
}
