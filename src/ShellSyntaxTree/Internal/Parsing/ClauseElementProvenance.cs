// -----------------------------------------------------------------------
// <copyright file="ClauseElementProvenance.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Parsing;

/// <summary>
/// Shared provenance operations used when shell command-string wrappers lift
/// inner clauses into an outer <see cref="ParsedCommand"/>.
/// </summary>
internal static class ClauseElementProvenance
{
    /// <summary>
    /// Remove inner-source offsets that cannot be mapped exactly through
    /// quoting, escaping, script-block stripping, or encoded-command decoding.
    /// </summary>
    internal static IReadOnlyList<ClauseElement> WithoutOuterSourceSpans(
        IReadOnlyList<ClauseElement> elements)
    {
        if (elements.Count == 0)
        {
            return Array.Empty<ClauseElement>();
        }

        var withoutSpans = new List<ClauseElement>(elements.Count);
        foreach (var element in elements)
        {
            withoutSpans.Add(element with
            {
                SourceStart = null,
                SourceLength = null,
            });
        }

        return withoutSpans;
    }
}
