// -----------------------------------------------------------------------
// <copyright file="PwshRedirectAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

/// <summary>
/// Builds occurrence-level PowerShell redirect facts without collapsing its
/// six numbered streams or all-streams selector into the v0.2 compatibility
/// direction.
/// </summary>
internal static class PwshRedirectAnalysis
{
    internal static IReadOnlyList<RedirectAnalysisFacts> Analyze(Clause clause)
    {
        if (clause.Redirects.Count == 0)
        {
            return Array.Empty<RedirectAnalysisFacts>();
        }

        var elements = new List<ClauseElement>(clause.Redirects.Count);
        foreach (var element in clause.Elements)
        {
            if (element.Role == ClauseElementRole.Redirect)
            {
                elements.Add(element);
            }
        }

        var result = new RedirectAnalysisFacts[clause.Redirects.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = index < elements.Count
                ? Analyze(index, clause.Redirects[index], elements[index])
                : Incomplete(index);
        }

        return result;
    }

    private static RedirectAnalysisFacts Analyze(
        int redirectIndex,
        Redirect compatibility,
        ClauseElement element)
    {
        if (!TryReadOperator(
                element.Raw,
                out var source,
                out var operation,
                out var operatorLength))
        {
            return Incomplete(redirectIndex);
        }

        if (operation == RedirectOperation.DescriptorDuplicate)
        {
            return new RedirectAnalysisFacts
            {
                RedirectIndex = redirectIndex,
                Source = source,
                Operation = operation,
                TargetDescriptor = 1,
                IsComplete = true,
            };
        }

        var authoredTarget = element.Raw.Substring(operatorLength).TrimStart();
        if (compatibility.IsDynamicSkip &&
            compatibility.Target.Equals("$null", StringComparison.OrdinalIgnoreCase))
        {
            // The public v0.3 operation vocabulary has no discard-sink member.
            // No known source may pair with an unresolved operation.
            return Incomplete(redirectIndex);
        }

        return new RedirectAnalysisFacts
        {
            RedirectIndex = redirectIndex,
            Source = source,
            Operation = operation,
            Target = compatibility.IsDynamicSkip
                ? ShellValueDomainFacts.Unknown
                : new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { compatibility.Target },
                },
            IsPathRelevant = true,
            // Completeness describes the redirect grammar, independently from
            // whether the target value can be narrowed beyond Unknown.
            IsComplete = authoredTarget.Length > 0,
        };
    }

    private static bool TryReadOperator(
        string raw,
        out RedirectSourceFacts source,
        out RedirectOperation operation,
        out int length)
    {
        source = new RedirectSourceFacts { Kind = RedirectSourceKind.Default };
        operation = RedirectOperation.Unknown;
        length = 0;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var operatorStart = 0;
        if (raw[0] == '*')
        {
            source = new RedirectSourceFacts
            {
                Kind = RedirectSourceKind.PowerShellAllStreams,
            };
            operatorStart = 1;
        }
        else if (raw[0] is >= '1' and <= '6')
        {
            source = new RedirectSourceFacts
            {
                Kind = RedirectSourceKind.Descriptor,
                Descriptor = raw[0] - '0',
            };
            operatorStart = 1;
        }

        if (raw.AsSpan(operatorStart).StartsWith(">&1", StringComparison.Ordinal))
        {
            if (source.Kind == RedirectSourceKind.Default || source.Descriptor == 1)
            {
                return false;
            }

            operation = RedirectOperation.DescriptorDuplicate;
            length = operatorStart + 3;
            return true;
        }

        if (raw.AsSpan(operatorStart).StartsWith(">>", StringComparison.Ordinal))
        {
            operation = RedirectOperation.FileAppend;
            length = operatorStart + 2;
            return true;
        }

        if (operatorStart < raw.Length && raw[operatorStart] == '>')
        {
            operation = RedirectOperation.FileOutput;
            length = operatorStart + 1;
            return true;
        }

        source = new RedirectSourceFacts();
        return false;
    }

    private static RedirectAnalysisFacts Incomplete(int redirectIndex) => new()
    {
        RedirectIndex = redirectIndex,
    };
}
