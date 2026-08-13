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
    private static readonly HashSet<string> UnsupportedReservedExecutionSyntax =
        new(StringComparer.Ordinal)
        {
            "!",
            "coproc",
            "time",
            "{",
            "}",
        };

    private static readonly HashSet<string> ExecutionBearingBuiltins =
        new(StringComparer.Ordinal)
        {
            ".",
            "declare",
            "eval",
            "exec",
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
        if (ClassifyExecutionBoundary(clause) == BashExecutionBoundaryKind.Query)
        {
            return BashDispatchKind.Query;
        }

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
        if (target.Value != "cd")
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
        if (words.Count > 0)
        {
            var firstWord = clause.Elements[words[0]];
            if (firstWord.Raw == firstWord.Value &&
                UnsupportedReservedExecutionSyntax.Contains(firstWord.Value))
            {
                return BashExecutionBoundaryKind.UnsupportedReservedExecutionSyntax;
            }
        }

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

        var commandResolutionBoundary = ClassifyCommandResolutionBoundary(
            clause,
            words,
            targetWordIndex,
            target.Value);
        if (commandResolutionBoundary != BashExecutionBoundaryKind.Allowed)
        {
            return commandResolutionBoundary;
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

    private static BashExecutionBoundaryKind ClassifyCommandResolutionBoundary(
        Clause clause,
        IReadOnlyList<int> words,
        int targetWordIndex,
        string target)
    {
        return target switch
        {
            "hash" => IsStaticHashQuery(clause, words, targetWordIndex + 1)
                ? BashExecutionBoundaryKind.Query
                : BashExecutionBoundaryKind.CommandResolutionMutation,
            "alias" => IsStaticAliasQuery(clause, words, targetWordIndex + 1)
                ? BashExecutionBoundaryKind.Query
                : BashExecutionBoundaryKind.CommandResolutionMutation,
            "shopt" => IsStaticShoptQuery(clause, words, targetWordIndex + 1)
                ? BashExecutionBoundaryKind.Query
                : BashExecutionBoundaryKind.CommandResolutionMutation,
            "enable" => IsStaticEnableQuery(clause, words, targetWordIndex + 1)
                ? BashExecutionBoundaryKind.Query
                : BashExecutionBoundaryKind.CommandResolutionMutation,
            "unalias" => BashExecutionBoundaryKind.CommandResolutionMutation,
            _ => BashExecutionBoundaryKind.Allowed,
        };
    }

    private static bool IsStaticHashQuery(
        Clause clause,
        IReadOnlyList<int> words,
        int argumentWordIndex)
    {
        if (argumentWordIndex == words.Count)
        {
            return true;
        }

        var printLocations = false;
        var reusableListing = false;
        var operandCount = 0;
        for (var index = argumentWordIndex; index < words.Count; index++)
        {
            var argument = clause.Elements[words[index]];
            if (!IsStaticWord(argument))
            {
                return false;
            }

            var value = argument.Value;
            if (operandCount > 0 && value.Length > 0 && value[0] == '-')
            {
                return false;
            }

            if (operandCount == 0 && value.Length > 1 && value[0] == '-')
            {
                for (var optionIndex = 1; optionIndex < value.Length; optionIndex++)
                {
                    switch (value[optionIndex])
                    {
                        case 'l':
                            reusableListing = true;
                            break;
                        case 't':
                            printLocations = true;
                            break;
                        default:
                            return false;
                    }
                }

                continue;
            }

            operandCount++;
        }

        if (printLocations)
        {
            return operandCount > 0;
        }

        return reusableListing && operandCount == 0;
    }

    private static bool IsStaticAliasQuery(
        Clause clause,
        IReadOnlyList<int> words,
        int argumentWordIndex)
    {
        var allowPrintOption = true;
        for (var index = argumentWordIndex; index < words.Count; index++)
        {
            var argument = clause.Elements[words[index]];
            if (!IsStaticWord(argument))
            {
                return false;
            }

            if (allowPrintOption && argument.Value == "-p")
            {
                allowPrintOption = false;
                continue;
            }

            allowPrintOption = false;
            if ((argument.Value.Length > 0 && argument.Value[0] == '-') ||
                argument.Value.IndexOf('=') >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStaticShoptQuery(
        Clause clause,
        IReadOnlyList<int> words,
        int argumentWordIndex)
    {
        var parsingOptions = true;
        for (var index = argumentWordIndex; index < words.Count; index++)
        {
            var argument = clause.Elements[words[index]];
            if (!IsStaticWord(argument))
            {
                return false;
            }

            var value = argument.Value;
            if (parsingOptions && value.Length > 1 && value[0] == '-')
            {
                for (var optionIndex = 1; optionIndex < value.Length; optionIndex++)
                {
                    if (value[optionIndex] is 's' or 'u' ||
                        value[optionIndex] is not ('p' or 'q' or 'o'))
                    {
                        return false;
                    }
                }

                continue;
            }

            parsingOptions = false;
            if (value.Length > 0 && value[0] == '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStaticEnableQuery(
        Clause clause,
        IReadOnlyList<int> words,
        int argumentWordIndex)
    {
        for (var index = argumentWordIndex; index < words.Count; index++)
        {
            var argument = clause.Elements[words[index]];
            if (!IsStaticWord(argument) ||
                argument.Value.Length <= 1 ||
                argument.Value[0] != '-')
            {
                return false;
            }

            for (var optionIndex = 1; optionIndex < argument.Value.Length; optionIndex++)
            {
                if (argument.Value[optionIndex] is not ('a' or 'n' or 'p' or 's'))
                {
                    return false;
                }
            }
        }

        return true;
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
    CommandResolutionMutation,
    UnsupportedReservedExecutionSyntax,
    UnsupportedDispatch,
}
