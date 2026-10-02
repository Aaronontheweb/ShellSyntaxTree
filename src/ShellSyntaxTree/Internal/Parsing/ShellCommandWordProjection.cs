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
/// <para>
/// The security goal: the result must never be shorter than the words the
/// program really receives. A consumer keys approval grants on the words,
/// so a word that hides from the result could widen a grant.
/// </para>
/// <para>
/// Each word after the program word gets exactly one class:
/// <list type="bullet">
///   <item>option: the word starts with <c>-</c>;</item>
///   <item>path: a path, a directory reference, or a glob that contains
///   <c>/</c>;</item>
///   <item>dynamic: the shell expands the word before the program runs, so
///   the parser cannot prove the program's words. A bare glob is dynamic.
///   The result is <see cref="ShellCommandWords.Unknown"/>;</item>
///   <item>text: a static value that is empty or contains whitespace or a
///   quoted glob character;</item>
///   <item>value: a static value that contains an ASCII digit;</item>
///   <item>command word: every other static value, quoted or not.</item>
/// </list>
/// Only command words go into the result. A redirect target is never a word
/// of the command, and assignment prefixes are not clause elements.
/// </para>
/// </remarks>
internal static class ShellCommandWordProjection
{
    /// <summary>
    /// Policy point (owner decision, #194). A plain word directly after an
    /// option stays in the command words. Without the option grammar of the
    /// program, the parser cannot tell an option value
    /// (<c>pgrep -x name</c>) from a subcommand after a valueless switch
    /// (<c>git -p filter-branch</c>). If the word were dropped, a subcommand
    /// could hide behind a switch. If the word is kept, an option value can
    /// only make a grant more specific.
    /// </summary>
    internal static bool KeepsPlainWordAfterOption => true;

    private enum WordClass
    {
        Option,
        Path,
        Dynamic,
        Text,
        Value,
        CommandWord,
    }

    internal static ShellCommandWords Project(
        Clause clause,
        bool occurrenceIsComplete,
        ShellProjectionLanguage language)
    {
        if (!occurrenceIsComplete ||
            language is not (ShellProjectionLanguage.Bash or ShellProjectionLanguage.PowerShell) ||
            clause.Verb.IsDynamic ||
            clause.Verb.Tokens.Count == 0 ||
            clause.Elements.Count == 0)
        {
            return new ShellCommandWords.Unknown();
        }

        // The program word must be the first authored element, must agree
        // with the verb chain, and must be one static value. `"git"` and
        // `\git` are static; an expanded program word is not.
        var program = clause.Elements[0];
        if (program.Role != ClauseElementRole.Verb ||
            !string.Equals(program.Value, clause.Verb.Tokens[0], StringComparison.Ordinal) ||
            program.Value.Length == 0 ||
            CountVerbElements(clause.Elements) != clause.Verb.Tokens.Count ||
            ScanProgramWord(program.Raw, language) != WordShape.Static)
        {
            return new ShellCommandWords.Unknown();
        }

        var words = new List<string> { program.Value };
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

            var wordClass = Classify(element, language);
            if (wordClass == WordClass.Dynamic)
            {
                return new ShellCommandWords.Unknown();
            }

            if (wordClass == WordClass.CommandWord &&
                (!followsOption || KeepsPlainWordAfterOption))
            {
                words.Add(element.Value);
            }

            followsOption = wordClass == WordClass.Option &&
                            !HasInlineOptionValue(element.Value);
        }

        return new ShellCommandWords.Known(words);
    }

    private static WordClass Classify(ClauseElement element, ShellProjectionLanguage language)
    {
        var shape = Scan(element.Raw, language);
        var isStatic = shape == WordShape.Static &&
                       element.Kind is ArgKind.Literal or ArgKind.DynamicSkip;

        // A word that can split into more than one word (an unquoted Bash
        // expansion, a PowerShell array or splat) can add a word that no
        // class here accounts for. That is true even inside an option or a
        // path: with r='x push', `git --c=$r log` runs `git --c=x push log`.
        // So such a word always makes the result unknown.
        if (shape == WordShape.MaySplit &&
            !(element.IsPath && element.Resolved is not null))
        {
            return WordClass.Dynamic;
        }

        if (element.IsFlag ||
            StartsWithDash(element.Raw) ||
            (isStatic && StartsWithDash(element.Value)))
        {
            // An option stays one option-shaped word whatever its expansion
            // gives, for example `--repo="$R"`. The program can read the
            // value only as the option's value, so it cannot become a
            // command word. That is why an expansion inside an option is
            // skipped and does not make the result unknown.
            return WordClass.Option;
        }

        if (element.Kind == ArgKind.Glob || shape == WordShape.Glob)
        {
            // Owner decision (Option A, #194). The shell replaces a glob with
            // matching file names. A glob that contains `/` can only give
            // words that contain `/`, so every result is a path and none can
            // be a subcommand: `./*`, `src/*`, `**/x`. A bare glob such as
            // `*` or `p?sh` can give any file name in the directory. If a
            // file named `push` exists, `git *` runs `git push`. The parser
            // cannot prove the words, so the result is unknown.
            return element.Value.IndexOf('/') >= 0 ? WordClass.Path : WordClass.Dynamic;
        }

        if (!isStatic)
        {
            // The same reasoning applies to a path operand such as
            // "$HOME/x": its value is a path, not a command word. Any other
            // expanded word can be a subcommand, so the words are unknown.
            return IsPathShaped(element) ? WordClass.Path : WordClass.Dynamic;
        }

        var value = element.Value;
        if (value.Length == 0 ||
            ContainsWhitespace(value) ||
            value.IndexOfAny(GlobCharacters) >= 0)
        {
            // A quoted or escaped glob such as "*" or \* reaches the program
            // as one literal value. It is data, not a command word.
            return WordClass.Text;
        }

        if (IsPathShaped(element))
        {
            return WordClass.Path;
        }

        return ContainsAsciiDigit(value) ? WordClass.Value : WordClass.CommandWord;
    }

    // Use the lexer value, never the authored text: in Bash, `\filter-branch`
    // has a backslash only as a quote character, and a path test on the
    // authored text would hide the subcommand.
    private static bool IsPathShaped(ClauseElement element) =>
        element.IsPath ||
        BashResolver.LooksLikePathOperand(element.Value, isGlobPattern: false);

    private static readonly char[] GlobCharacters = { '*', '?', '[' };

    private enum WordShape
    {
        /// <summary>The authored word has one static value.</summary>
        Static,

        /// <summary>The word has an expansion that keeps it one word.</summary>
        Expanded,

        /// <summary>The word has an unquoted glob character.</summary>
        Glob,

        /// <summary>The word has an expansion that can give more words.</summary>
        MaySplit,
    }

    private static WordShape Scan(string raw, ShellProjectionLanguage language) =>
        language == ShellProjectionLanguage.Bash
            ? ScanBash(raw)
            : ScanPowerShell(raw, isCommandName: false);

    // Bash expands a glob in the command-name word too, so the program word
    // uses the ordinary scan. PowerShell resolves a command name and does
    // not expand wildcards in it, so `?` (the Where-Object alias) is static.
    private static WordShape ScanProgramWord(string raw, ShellProjectionLanguage language) =>
        language == ShellProjectionLanguage.Bash
            ? ScanBash(raw)
            : ScanPowerShell(raw, isCommandName: true);

    private static bool ExpandsToManyWordsInDoubleQuotes(string raw, int dollar)
    {
        if (dollar + 1 >= raw.Length)
        {
            return false;
        }

        if (raw[dollar + 1] == '@')
        {
            return true;
        }

        if (raw[dollar + 1] != '{')
        {
            return false;
        }

        var close = raw.IndexOf('}', dollar + 2);
        var body = close < 0 ? raw.Substring(dollar + 2) : raw.Substring(dollar + 2, close - dollar - 2);
        return body.IndexOf('@') >= 0;
    }

    /// <summary>
    /// Quote-aware scan of one authored Bash word. It does not decode the
    /// word; the lexer owns the decoded value. It only proves whether the
    /// shell can change the word before the program runs.
    /// </summary>
    private static WordShape ScanBash(string raw)
    {
        var shape = WordShape.Static;
        var inSingle = false;
        var inDouble = false;
        var braceDepth = 0;
        var braceHasList = false;
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (inSingle)
            {
                inSingle = character != '\'';
                continue;
            }

            if (inDouble)
            {
                switch (character)
                {
                    case '\\':
                        index++;
                        break;
                    case '"':
                        inDouble = false;
                        break;
                    case '$' when ExpandsToManyWordsInDoubleQuotes(raw, index):
                        // "$@" and "${array[@]}" give one word per element.
                        return WordShape.MaySplit;
                    case '$':
                    case '`':
                        // Any other quoted expansion stays one word.
                        shape = Max(shape, WordShape.Expanded);
                        break;
                }

                continue;
            }

            switch (character)
            {
                case '\\':
                    index++;
                    break;
                case '\'':
                    inSingle = true;
                    break;
                case '"':
                    inDouble = true;
                    break;
                case '$':
                case '`':
                case '(':
                case ')':
                case '<':
                case '>':
                    // Unquoted parameter, command, arithmetic, or process
                    // substitution: field splitting can add words.
                    return WordShape.MaySplit;
                case '*':
                case '?':
                case '[':
                    shape = Max(shape, WordShape.Glob);
                    break;
                case '~' when index == 0:
                    shape = Max(shape, WordShape.Expanded);
                    break;
                case '{':
                    braceDepth++;
                    break;
                case ',' when braceDepth > 0:
                    braceHasList = true;
                    break;
                case '.' when braceDepth > 0 &&
                              index + 1 < raw.Length && raw[index + 1] == '.':
                    braceHasList = true;
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    if (braceHasList)
                    {
                        // Brace expansion: `{push,log}` becomes two words.
                        return WordShape.MaySplit;
                    }

                    break;
            }
        }

        // An unterminated quote cannot come from a parsed word.
        return inSingle || inDouble ? WordShape.MaySplit : shape;
    }

    /// <summary>
    /// Quote-aware scan of one authored PowerShell argument. PowerShell does
    /// not split a variable into words, but an array, a splat, or a
    /// subexpression can supply more than one argument.
    /// </summary>
    private static WordShape ScanPowerShell(string raw, bool isCommandName)
    {
        var shape = WordShape.Static;
        var inSingle = false;
        var inDouble = false;
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (IsSmartQuote(character))
            {
                // PowerShell treats typographic quotes as quote characters.
                // This scan does not model them, so it fails closed.
                return WordShape.MaySplit;
            }

            if (inSingle)
            {
                if (character == '\'')
                {
                    if (index + 1 < raw.Length && raw[index + 1] == '\'')
                    {
                        index++;
                    }
                    else
                    {
                        inSingle = false;
                    }
                }

                continue;
            }

            if (inDouble)
            {
                switch (character)
                {
                    case '`':
                        index++;
                        break;
                    case '"':
                        if (index + 1 < raw.Length && raw[index + 1] == '"')
                        {
                            index++;
                        }
                        else
                        {
                            inDouble = false;
                        }

                        break;
                    case '$':
                        shape = Max(shape, WordShape.Expanded);
                        break;
                }

                continue;
            }

            switch (character)
            {
                case '`':
                    index++;
                    break;
                case '\'':
                    inSingle = true;
                    break;
                case '"':
                    inDouble = true;
                    break;
                case '$':
                    shape = Max(shape, WordShape.Expanded);
                    break;
                case '@' when index == 0:
                case '(':
                case ')':
                case '{':
                case '}':
                case ',':
                case ';':
                    // Splat, array, subexpression, or script block.
                    return WordShape.MaySplit;
                case '*' when !isCommandName:
                case '?' when !isCommandName:
                case '[' when !isCommandName:
                    shape = Max(shape, WordShape.Glob);
                    break;
                case '~' when index == 0:
                    shape = Max(shape, WordShape.Expanded);
                    break;
            }
        }

        return inSingle || inDouble ? WordShape.MaySplit : shape;
    }

    private static WordShape Max(WordShape left, WordShape right) =>
        left > right ? left : right;

    private static bool IsSmartQuote(char character) =>
        character is '‘' or '’' or '‚' or '‛' or
            '“' or '”' or '„';

    private static bool StartsWithDash(string value) =>
        value.Length > 0 && value[0] == '-';

    // `--name=value` and PowerShell `-Name:value` carry their own value, so
    // the next word is not that option's value.
    private static bool HasInlineOptionValue(string value) =>
        value.IndexOf('=') >= 0 || value.IndexOf(':') >= 0;

    private static bool ContainsWhitespace(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

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
