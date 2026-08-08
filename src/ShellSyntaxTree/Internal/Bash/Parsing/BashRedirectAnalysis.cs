// -----------------------------------------------------------------------
// <copyright file="BashRedirectAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Builds occurrence-level redirect facts from parser-owned compatibility
/// leaves and their authored element provenance.
/// </summary>
internal static class BashRedirectAnalysis
{
    internal static IReadOnlyList<RedirectAnalysis> Analyze(Clause clause)
    {
        if (clause.Redirects.Count == 0)
        {
            return Array.Empty<RedirectAnalysis>();
        }

        var elements = new List<ClauseElement>(clause.Redirects.Count);
        foreach (var element in clause.Elements)
        {
            if (element.Role == ClauseElementRole.Redirect)
            {
                elements.Add(element);
            }
        }

        var result = new RedirectAnalysis[clause.Redirects.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = index < elements.Count
                ? Analyze(index, clause.Redirects[index], elements[index])
                : Incomplete(index);
        }

        return result;
    }

    private static RedirectAnalysis Analyze(
        int redirectIndex,
        Redirect compatibility,
        ClauseElement element)
    {
        if (!TryReadOperator(element.Raw, out var source, out var operation, out var length))
        {
            return Incomplete(redirectIndex);
        }

        var authoredTarget = element.Raw.Substring(length).TrimStart();
        if (operation is RedirectOperation.FileInput or
            RedirectOperation.FileOutput or
            RedirectOperation.FileAppend &&
            authoredTarget.Length > 0 && authoredTarget[0] == '&')
        {
            return AnalyzeDescriptorTarget(
                redirectIndex,
                source,
                authoredTarget,
                element.Value);
        }

        if (operation == RedirectOperation.Unknown)
        {
            return new RedirectAnalysis
            {
                RedirectIndex = redirectIndex,
                Source = source,
            };
        }

        var isComplete = !compatibility.IsDynamicSkip;
        return new RedirectAnalysis
        {
            RedirectIndex = redirectIndex,
            Source = source,
            Operation = operation,
            Target = isComplete
                ? new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { compatibility.Target },
                }
                : ShellValueDomain.Unknown,
            IsPathRelevant = true,
            IsComplete = isComplete,
        };
    }

    private static RedirectAnalysis AnalyzeDescriptorTarget(
        int redirectIndex,
        RedirectSource source,
        string authoredTarget,
        string decodedTarget)
    {
        if (string.Equals(authoredTarget, decodedTarget, StringComparison.Ordinal))
        {
            if (string.Equals(authoredTarget, "&-", StringComparison.Ordinal))
            {
                return new RedirectAnalysis
                {
                    RedirectIndex = redirectIndex,
                    Source = source,
                    Operation = RedirectOperation.DescriptorClose,
                    IsComplete = true,
                };
            }

            var descriptorText = authoredTarget.Substring(1);
            var isMove = descriptorText.EndsWith("-", StringComparison.Ordinal);
            if (isMove)
            {
                descriptorText = descriptorText.Substring(0, descriptorText.Length - 1);
            }

            if (int.TryParse(
                    descriptorText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var descriptor) &&
                descriptor >= 0)
            {
                return new RedirectAnalysis
                {
                    RedirectIndex = redirectIndex,
                    Source = source,
                    Operation = isMove
                        ? RedirectOperation.DescriptorMove
                        : RedirectOperation.DescriptorDuplicate,
                    TargetDescriptor = descriptor,
                    IsComplete = true,
                };
            }
        }

        return new RedirectAnalysis
        {
            RedirectIndex = redirectIndex,
            Source = source,
            Operation = RedirectOperation.DescriptorDuplicate,
        };
    }

    private static bool TryReadOperator(
        string raw,
        out RedirectSource source,
        out RedirectOperation operation,
        out int length)
    {
        source = new RedirectSource { Kind = RedirectSourceKind.Default };
        operation = RedirectOperation.Unknown;
        length = 0;

        if (raw.StartsWith("&>>", StringComparison.Ordinal))
        {
            operation = RedirectOperation.CombinedOutputAppend;
            length = 3;
            return true;
        }

        if (raw.StartsWith("&>", StringComparison.Ordinal))
        {
            operation = RedirectOperation.CombinedOutput;
            length = 2;
            return true;
        }

        var descriptorText = new StringBuilder();
        var operatorStart = 0;
        while (operatorStart < raw.Length)
        {
            if (raw[operatorStart] is >= '0' and <= '9')
            {
                descriptorText.Append(raw[operatorStart]);
                operatorStart++;
                continue;
            }

            if (TrySkipLineContinuation(raw, operatorStart, out var afterContinuation))
            {
                operatorStart = afterContinuation;
                continue;
            }

            break;
        }

        if (descriptorText.Length > 0)
        {
            if (!int.TryParse(
                    descriptorText.ToString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var descriptor) ||
                descriptor < 0)
            {
                source = new RedirectSource();
                return true;
            }

            source = new RedirectSource
            {
                Kind = RedirectSourceKind.Descriptor,
                Descriptor = descriptor,
            };
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

        if (raw.AsSpan(operatorStart).StartsWith("<<-", StringComparison.Ordinal))
        {
            length = operatorStart + 3;
            return true;
        }

        if (raw.AsSpan(operatorStart).StartsWith("<<", StringComparison.Ordinal))
        {
            length = operatorStart + 2;
            return true;
        }

        if (operatorStart < raw.Length && raw[operatorStart] == '<')
        {
            operation = RedirectOperation.FileInput;
            length = operatorStart + 1;
            return true;
        }

        source = new RedirectSource();
        return false;
    }

    private static bool TrySkipLineContinuation(
        string source,
        int index,
        out int afterContinuation)
    {
        afterContinuation = index;
        if (index + 1 >= source.Length || source[index] != '\\' ||
            source[index + 1] is not ('\n' or '\r'))
        {
            return false;
        }

        afterContinuation = source[index + 1] == '\r' &&
            index + 2 < source.Length && source[index + 2] == '\n'
            ? index + 3
            : index + 2;
        return true;
    }

    private static RedirectAnalysis Incomplete(int redirectIndex) => new()
    {
        RedirectIndex = redirectIndex,
    };
}
