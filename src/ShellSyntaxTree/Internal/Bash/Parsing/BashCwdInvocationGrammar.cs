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
    internal static BashDispatchKind Classify(
        Clause clause,
        out IReadOnlyList<int> cwdArgumentElementIndices)
    {
        cwdArgumentElementIndices = Array.Empty<int>();
        var words = new List<int>();
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            if (clause.Elements[index].Role != ClauseElementRole.Redirect)
            {
                words.Add(index);
            }
        }

        var wordIndex = 0;
        while (wordIndex < words.Count)
        {
            var element = clause.Elements[words[wordIndex]];
            if (!IsStaticWord(element))
            {
                return BashDispatchKind.None;
            }

            if (element.Value == "command")
            {
                wordIndex++;
                var optionsEnded = false;
                while (wordIndex < words.Count && !optionsEnded)
                {
                    var option = clause.Elements[words[wordIndex]];
                    if (!IsStaticWord(option))
                    {
                        return BashDispatchKind.None;
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
                                return BashDispatchKind.None;
                        }
                    }

                    wordIndex++;
                    if (query)
                    {
                        return BashDispatchKind.Query;
                    }
                }

                continue;
            }

            if (element.Value == "builtin")
            {
                wordIndex++;
                if (wordIndex < words.Count)
                {
                    var option = clause.Elements[words[wordIndex]];
                    if (!IsStaticWord(option))
                    {
                        return BashDispatchKind.None;
                    }

                    if (option.Value == "--")
                    {
                        wordIndex++;
                    }
                    else if (option.Value.Length > 1 && option.Value[0] == '-')
                    {
                        return BashDispatchKind.None;
                    }
                }

                continue;
            }

            if (element.Value is not ("cd" or "chdir"))
            {
                return BashDispatchKind.None;
            }

            var arguments = new int[words.Count - wordIndex - 1];
            for (var argumentIndex = 0;
                 argumentIndex < arguments.Length;
                 argumentIndex++)
            {
                arguments[argumentIndex] = words[wordIndex + argumentIndex + 1];
            }

            cwdArgumentElementIndices = arguments;
            return BashDispatchKind.CwdTransfer;
        }

        return BashDispatchKind.None;
    }

    private static bool IsStaticWord(ClauseElement element) =>
        element.Kind == ArgKind.Literal;
}

internal enum BashDispatchKind
{
    None,
    Query,
    CwdTransfer,
}
