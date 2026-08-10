// -----------------------------------------------------------------------
// <copyright file="BashRedirectAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Builds occurrence-level redirect facts from parser-owned compatibility
/// leaves and their authored element provenance.
/// </summary>
internal static class BashRedirectAnalysis
{
    internal static IReadOnlyList<RedirectAnalysisFacts> Analyze(Clause clause)
        => AnalyzeCore(clause, source: null, sourceTokens: null);

    internal static IReadOnlyList<RedirectAnalysisFacts> Analyze(
        Clause clause,
        string source,
        IReadOnlyList<BashToken> sourceTokens)
        => AnalyzeCore(clause, source, sourceTokens);

    private static IReadOnlyList<RedirectAnalysisFacts> AnalyzeCore(
        Clause clause,
        string? source,
        IReadOnlyList<BashToken>? sourceTokens)
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
                ? Analyze(
                    index,
                    clause.Redirects[index],
                    elements[index],
                    source,
                    sourceTokens)
                : Incomplete(index);
        }

        return result;
    }

    private static RedirectAnalysisFacts Analyze(
        int redirectIndex,
        Redirect compatibility,
        ClauseElement element,
        string? authoredSource,
        IReadOnlyList<BashToken>? sourceTokens)
    {
        if (!TryReadOperator(
                element.Raw,
                out var redirectSource,
                out var operation,
                out var length))
        {
            return Incomplete(redirectIndex);
        }

        var authoredTarget = element.Raw.Substring(length).TrimStart();
        if (operation == RedirectOperation.HereDocument)
        {
            return AnalyzeHereDocument(
                redirectIndex,
                authoredSource,
                sourceTokens,
                element,
                redirectSource,
                stripLeadingTabs: element.Raw.AsSpan(0, length)
                    .EndsWith("<<-", StringComparison.Ordinal));
        }

        if (operation == RedirectOperation.HereString)
        {
            return AnalyzeHereString(
                redirectIndex,
                compatibility,
                element,
                redirectSource);
        }

        if (operation is RedirectOperation.FileInput or
            RedirectOperation.FileOutput or
            RedirectOperation.FileAppend &&
            authoredTarget.Length > 0 && authoredTarget[0] == '&')
        {
            return AnalyzeDescriptorTarget(
                redirectIndex,
                redirectSource,
                authoredTarget,
                element.Value);
        }

        if (operation == RedirectOperation.Unknown)
        {
            return Incomplete(redirectIndex);
        }

        var isComplete = !compatibility.IsDynamicSkip;
        return new RedirectAnalysisFacts
        {
            RedirectIndex = redirectIndex,
            Source = redirectSource,
            Operation = operation,
            Target = isComplete
                ? new ShellValueDomainFacts
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { compatibility.Target },
                }
                : ShellValueDomainFacts.Unknown,
            IsPathRelevant = true,
            IsComplete = isComplete,
        };
    }

    private static RedirectAnalysisFacts AnalyzeHereString(
        int redirectIndex,
        Redirect compatibility,
        ClauseElement element,
        RedirectSourceFacts source)
    {
        var target = compatibility.IsDynamicSkip
            ? ShellValueDomainFacts.Unknown
            : new ShellValueDomainFacts
            {
                Kind = ShellValueDomainKind.Exact,
                Values = new[] { (element.Resolved ?? element.Value) + "\n" },
            };
        return new RedirectAnalysisFacts
        {
            RedirectIndex = redirectIndex,
            Source = source,
            Operation = RedirectOperation.HereString,
            Target = target,
            IsPathRelevant = false,
            IsComplete = source.Kind != RedirectSourceKind.Unknown,
        };
    }

    internal static ShellValue NormalizeHereStringOperand(ShellValue value)
    {
        var fragments = new ShellValueFragment[value.Fragments.Count];
        var changed = false;
        for (var index = 0; index < fragments.Length; index++)
        {
            var fragment = value.Fragments[index];
            if (fragment.Expansion is { Kind: ShellExpansionKind.Glob })
            {
                changed = true;
                fragments[index] = fragment with
                {
                    Kind = ShellValueFragmentKind.Literal,
                    AllowedTransforms = ShellLexicalTransform.None,
                    Expansion = null,
                    Cardinality = ShellValueCardinality.ExactlyOne,
                };
                continue;
            }

            var transforms = fragment.AllowedTransforms & ~ShellLexicalTransform.FieldSplit;
            changed |= transforms != fragment.AllowedTransforms;
            fragments[index] = fragment with { AllowedTransforms = transforms };
        }

        return changed ? new ShellValue(value.Decoded, fragments) : value;
    }

    private static RedirectAnalysisFacts AnalyzeHereDocument(
        int redirectIndex,
        string? source,
        IReadOnlyList<BashToken>? sourceTokens,
        ClauseElement element,
        RedirectSourceFacts redirectSource,
        bool stripLeadingTabs)
    {
        if (source is null || sourceTokens is null ||
            element.SourceStart is null || element.SourceLength is null)
        {
            return Incomplete(redirectIndex);
        }

        var elementEnd = element.SourceStart.Value + element.SourceLength.Value;
        foreach (var token in sourceTokens)
        {
            if (token.HeredocBodyValue is null ||
                token.HeredocBodyStart is null ||
                token.HeredocBodyLength is null ||
                token.SourceStart + token.SourceLength != elementEnd)
            {
                continue;
            }

            var bodyStart = token.HeredocBodyStart.Value;
            var bodyLength = token.HeredocBodyLength.Value;
            if (token.SourceStart < element.SourceStart.Value ||
                token.SourceStart + token.SourceLength > source.Length ||
                bodyStart < 0 || bodyLength < 0 ||
                bodyStart + bodyLength > source.Length)
            {
                return Incomplete(redirectIndex);
            }

            var hereDocument = new HereDocumentAnalysis
            {
                Delimiter = new ShellSourceFragment
                {
                    Raw = source.Substring(token.SourceStart, token.SourceLength),
                    SourceStart = token.SourceStart,
                    SourceLength = token.SourceLength,
                },
                Body = new ShellSourceFragment
                {
                    Raw = source.Substring(bodyStart, bodyLength),
                    SourceStart = bodyStart,
                    SourceLength = bodyLength,
                },
                ExpansionMode = token.IsHeredocDelimiterQuoted
                    ? HereDocumentExpansionMode.Literal
                    : HereDocumentExpansionMode.Expand,
                StripLeadingTabs = stripLeadingTabs,
                IsComplete = true,
            };
            return new RedirectAnalysisFacts
            {
                RedirectIndex = redirectIndex,
                Source = redirectSource,
                Operation = RedirectOperation.HereDocument,
                HereDocument = hereDocument,
                IsComplete = redirectSource.Kind != RedirectSourceKind.Unknown,
            };
        }

        return Incomplete(redirectIndex);
    }

    private static RedirectAnalysisFacts AnalyzeDescriptorTarget(
        int redirectIndex,
        RedirectSourceFacts source,
        string authoredTarget,
        string decodedTarget)
    {
        if (string.Equals(authoredTarget, decodedTarget, StringComparison.Ordinal))
        {
            if (string.Equals(authoredTarget, "&-", StringComparison.Ordinal))
            {
                return new RedirectAnalysisFacts
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
                return new RedirectAnalysisFacts
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

        // A computed target does not fit any closed descriptor alternative:
        // duplicate and move records require a proved target descriptor.
        return Incomplete(redirectIndex);
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
            if (int.TryParse(
                    descriptorText.ToString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var descriptor) &&
                descriptor >= 0)
            {
                source = new RedirectSourceFacts
                {
                    Kind = RedirectSourceKind.Descriptor,
                    Descriptor = descriptor,
                };
            }
            else
            {
                source = new RedirectSourceFacts();
                if (raw.AsSpan(operatorStart).StartsWith("<<<", StringComparison.Ordinal))
                {
                    operation = RedirectOperation.HereString;
                    length = operatorStart + 3;
                }
                else if (raw.AsSpan(operatorStart).StartsWith("<<-", StringComparison.Ordinal))
                {
                    operation = RedirectOperation.HereDocument;
                    length = operatorStart + 3;
                }
                else if (raw.AsSpan(operatorStart).StartsWith("<<", StringComparison.Ordinal))
                {
                    operation = RedirectOperation.HereDocument;
                    length = operatorStart + 2;
                }

                return true;
            }
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

        if (raw.AsSpan(operatorStart).StartsWith("<<<", StringComparison.Ordinal))
        {
            operation = RedirectOperation.HereString;
            length = operatorStart + 3;
            return true;
        }

        if (raw.AsSpan(operatorStart).StartsWith("<<-", StringComparison.Ordinal))
        {
            operation = RedirectOperation.HereDocument;
            length = operatorStart + 3;
            return true;
        }

        if (raw.AsSpan(operatorStart).StartsWith("<<", StringComparison.Ordinal))
        {
            operation = RedirectOperation.HereDocument;
            length = operatorStart + 2;
            return true;
        }

        if (operatorStart < raw.Length && raw[operatorStart] == '<')
        {
            operation = RedirectOperation.FileInput;
            length = operatorStart + 1;
            return true;
        }

        source = new RedirectSourceFacts();
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

    private static RedirectAnalysisFacts Incomplete(int redirectIndex) => new()
    {
        RedirectIndex = redirectIndex,
    };
}
