// -----------------------------------------------------------------------
// <copyright file="ShellCommandWordProjection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Parsing;

/// <summary>
/// Projects <see cref="CommandOccurrence.CommandWords"/> from the authored
/// clause elements. The rules use general shell conventions only. They do
/// not know which options of a program take a value, and they do not know
/// the subcommand grammar of any program.
/// </summary>
/// <remarks>
/// Each word after the program word gets exactly one class:
/// <list type="bullet">
///   <item>option: the authored word starts with <c>-</c>;</item>
///   <item>expanded: the shell can change the word before the program runs,
///   or the author quoted or escaped it;</item>
///   <item>path: a path, a directory reference, or a glob pattern;</item>
///   <item>value: the word contains an ASCII digit;</item>
///   <item>command word: every other plain literal word.</item>
/// </list>
/// Only command words go into the result. A redirect target is never a word
/// of the command, and assignment prefixes are not clause elements.
/// </remarks>
internal static class ShellCommandWordProjection
{
    /// <summary>
    /// Policy point. A plain word directly after an option stays in the
    /// command words. Without the option grammar of the program, the parser
    /// cannot tell an option value (<c>pgrep -x name</c>) from a subcommand
    /// after a valueless switch (<c>git -p filter-branch</c>). If the word
    /// were dropped, a subcommand could hide behind a switch and a grant
    /// would become broader. If the word is kept, an option value can only
    /// make a grant more specific.
    /// </summary>
    internal static bool KeepsPlainWordAfterOption => true;

    // Characters that make an unquoted Bash word subject to expansion or
    // that only appear in a quoted, escaped, or expanded word.
    private static readonly char[] BashExpansionCharacters =
    {
        '$', '`', '\\', '\'', '"', '*', '?', '[', ']', '{', '}',
    };

    // PowerShell adds array, subexpression, script-block, and splat syntax.
    private static readonly char[] PowerShellExpansionCharacters =
    {
        '$', '`', '\'', '"', '*', '?', '[', ']', '{', '}', '(', ')', ',', ';', '@',
    };

    private static readonly char[] AnyShellExpansionCharacters =
    {
        '$', '`', '\\', '\'', '"', '*', '?', '[', ']', '{', '}', '(', ')', ',', ';', '@',
    };

    private enum WordClass
    {
        Option,
        Expanded,
        Path,
        Value,
        CommandWord,
    }

    internal static ShellCommandWords Project(
        Clause clause,
        bool occurrenceIsComplete,
        ShellProjectionLanguage language)
    {
        if (!occurrenceIsComplete ||
            clause.Verb.IsDynamic ||
            clause.Verb.Tokens.Count == 0 ||
            clause.Elements.Count == 0)
        {
            return new ShellCommandWords.Unknown();
        }

        // The program word must be the first authored element and must
        // agree with the verb chain. Any other shape means the element list
        // does not account for the command, so no word list is proved.
        var program = clause.Elements[0];
        if (program.Role != ClauseElementRole.Verb ||
            !string.Equals(program.Value, clause.Verb.Tokens[0], StringComparison.Ordinal) ||
            CountVerbElements(clause.Elements) != clause.Verb.Tokens.Count)
        {
            return new ShellCommandWords.Unknown();
        }

        var expansionCharacters = language switch
        {
            ShellProjectionLanguage.Bash => BashExpansionCharacters,
            ShellProjectionLanguage.PowerShell => PowerShellExpansionCharacters,
            _ => AnyShellExpansionCharacters,
        };

        var words = new List<string> { clause.Verb.Tokens[0] };
        var followsOption = false;
        for (var index = 1; index < clause.Elements.Count; index++)
        {
            var element = clause.Elements[index];
            switch (element.Role)
            {
                case ClauseElementRole.Redirect:
                    continue;
                case ClauseElementRole.Verb:
                case ClauseElementRole.Argument:
                    break;
                default:
                    return new ShellCommandWords.Unknown();
            }

            var wordClass = Classify(element, expansionCharacters);
            if (wordClass == WordClass.CommandWord &&
                (!followsOption || KeepsPlainWordAfterOption))
            {
                words.Add(element.Value);
            }

            followsOption = wordClass == WordClass.Option &&
                            !HasInlineOptionValue(element.Raw);
        }

        return new ShellCommandWords.Known(words);
    }

    private static WordClass Classify(ClauseElement element, char[] expansionCharacters)
    {
        var raw = element.Raw;
        if (element.IsFlag || (raw.Length > 0 && raw[0] == '-'))
        {
            return WordClass.Option;
        }

        // Raw differs from Value when the author quoted or escaped the word.
        // A non-literal kind means a variable, substitution, tilde, or glob.
        if (element.Kind != ArgKind.Literal ||
            raw.Length == 0 ||
            !string.Equals(raw, element.Value, StringComparison.Ordinal) ||
            raw.IndexOfAny(expansionCharacters) >= 0 ||
            raw[0] == '~')
        {
            return element.IsPath ? WordClass.Path : WordClass.Expanded;
        }

        if (element.IsPath ||
            BashResolver.LooksLikePathOperand(raw, isGlobPattern: false))
        {
            return WordClass.Path;
        }

        return ContainsAsciiDigit(raw) ? WordClass.Value : WordClass.CommandWord;
    }

    // `--name=value` and PowerShell `-Name:value` carry their own value, so
    // the next word is not that option's value.
    private static bool HasInlineOptionValue(string raw) =>
        raw.IndexOf('=') >= 0 || raw.IndexOf(':') >= 0;

    private static bool ContainsAsciiDigit(string value)
    {
        foreach (var character in value)
        {
            if (character is >= '0' and <= '9')
            {
                return true;
            }
        }

        return false;
    }

    private static int CountVerbElements(IReadOnlyList<ClauseElement> elements)
    {
        var count = 0;
        foreach (var element in elements)
        {
            if (element.Role == ClauseElementRole.Verb)
            {
                count++;
            }
        }

        return count;
    }
}
