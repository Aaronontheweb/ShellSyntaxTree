// -----------------------------------------------------------------------
// <copyright file="BashCwdInvocationGrammar.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

internal static class BashCwdInvocationGrammar
{
    private static readonly HashSet<string> ExecutionBearingBuiltins =
        new(StringComparer.Ordinal)
        {
            ".",
            "declare",
            "eval",
            "export",
            "getopts",
            "let",
            "local",
            "mapfile",
            "read",
            "readarray",
            "readonly",
            "set",
            "source",
            "trap",
            "typeset",
            "unset",
        };

    internal static BashDispatchKind Classify(
        Clause clause,
        out IReadOnlyList<int> cwdArgumentElementIndices)
    {
        cwdArgumentElementIndices = Array.Empty<int>();
        var words = CollectWords(clause);
        var dispatch = ParseDispatch(clause, words);
        if (dispatch.IsQuery)
        {
            return BashDispatchKind.Query;
        }

        if (!dispatch.IsSupported || dispatch.TargetWordIndex is null)
        {
            return BashDispatchKind.None;
        }

        var targetWordIndex = dispatch.TargetWordIndex.Value;
        var target = clause.Elements[words[targetWordIndex]];
        if (target.Value is not ("cd" or "chdir"))
        {
            return BashDispatchKind.None;
        }

        var arguments = new int[words.Count - targetWordIndex - 1];
        for (var argumentIndex = 0; argumentIndex < arguments.Length; argumentIndex++)
        {
            arguments[argumentIndex] = words[targetWordIndex + argumentIndex + 1];
        }

        cwdArgumentElementIndices = arguments;
        return BashDispatchKind.CwdTransfer;
    }

    internal static BashExecutionBoundaryKind ClassifyExecutionBoundary(Clause clause)
    {
        var words = CollectWords(clause);
        var dispatch = ParseDispatch(clause, words);
        if (!dispatch.IsSupported)
        {
            return dispatch.HasWrapper
                ? BashExecutionBoundaryKind.UnsupportedDispatch
                : BashExecutionBoundaryKind.Allowed;
        }

        if (dispatch.IsQuery)
        {
            return BashExecutionBoundaryKind.Query;
        }

        if (dispatch.TargetWordIndex is not { } targetWordIndex)
        {
            return BashExecutionBoundaryKind.Allowed;
        }

        var target = clause.Elements[words[targetWordIndex]];
        if (ExecutionBearingBuiltins.Contains(target.Value))
        {
            return BashExecutionBoundaryKind.ExecutionBearingBuiltin;
        }

        if (target.Value == "printf" && targetWordIndex + 1 < words.Count)
        {
            var firstArgument = clause.Elements[words[targetWordIndex + 1]];
            if (!IsStaticWord(firstArgument) ||
                firstArgument.Value.StartsWith("-v", StringComparison.Ordinal))
            {
                return BashExecutionBoundaryKind.ExecutionBearingBuiltin;
            }
        }

        return BashExecutionBoundaryKind.Allowed;
    }

    private static IReadOnlyList<int> CollectWords(Clause clause)
    {
        var words = new List<int>();
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            if (clause.Elements[index].Role != ClauseElementRole.Redirect)
            {
                words.Add(index);
            }
        }

        return words;
    }

    private static BashDispatchAnalysis ParseDispatch(
        Clause clause,
        IReadOnlyList<int> words)
    {
        var wordIndex = 0;
        var hasWrapper = false;
        while (wordIndex < words.Count)
        {
            var element = clause.Elements[words[wordIndex]];
            if (!IsStaticWord(element))
            {
                return new BashDispatchAnalysis(false, false, hasWrapper, null);
            }

            if (element.Value == "command")
            {
                hasWrapper = true;
                wordIndex++;
                var optionsEnded = false;
                while (wordIndex < words.Count && !optionsEnded)
                {
                    var option = clause.Elements[words[wordIndex]];
                    if (!IsStaticWord(option))
                    {
                        return new BashDispatchAnalysis(false, false, true, null);
                    }

                    if (option.Value == "--")
                    {
                        optionsEnded = true;
                        wordIndex++;
                        continue;
                    }

                    if (option.Value.Length <= 1 || option.Value[0] != '-')
                    {
                        break;
                    }

                    var query = false;
                    for (var optionIndex = 1;
                         optionIndex < option.Value.Length;
                         optionIndex++)
                    {
                        switch (option.Value[optionIndex])
                        {
                            case 'p':
                                break;
                            case 'v':
                            case 'V':
                                query = true;
                                break;
                            default:
                                return new BashDispatchAnalysis(false, false, true, null);
                        }
                    }

                    wordIndex++;
                    if (query)
                    {
                        return new BashDispatchAnalysis(true, true, true, null);
                    }
                }

                continue;
            }

            if (element.Value == "builtin")
            {
                hasWrapper = true;
                wordIndex++;
                if (wordIndex < words.Count)
                {
                    var option = clause.Elements[words[wordIndex]];
                    if (!IsStaticWord(option))
                    {
                        return new BashDispatchAnalysis(false, false, true, null);
                    }

                    if (option.Value == "--")
                    {
                        wordIndex++;
                    }
                    else if (option.Value.Length > 1 && option.Value[0] == '-')
                    {
                        return new BashDispatchAnalysis(false, false, true, null);
                    }
                }

                continue;
            }

            return new BashDispatchAnalysis(true, false, hasWrapper, wordIndex);
        }

        return new BashDispatchAnalysis(true, false, hasWrapper, null);
    }

    private static bool IsStaticWord(ClauseElement element) =>
        element.Kind == ArgKind.Literal;

    private readonly record struct BashDispatchAnalysis(
        bool IsSupported,
        bool IsQuery,
        bool HasWrapper,
        int? TargetWordIndex);
}

internal enum BashDispatchKind
{
    None,
    Query,
    CwdTransfer,
}

internal enum BashExecutionBoundaryKind
{
    Allowed,
    Query,
    ExecutionBearingBuiltin,
    UnsupportedDispatch,
}
