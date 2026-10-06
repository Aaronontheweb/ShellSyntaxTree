// -----------------------------------------------------------------------
// <copyright file="ShellCommandWordProjection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Bash.Lexing;
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
///   In the verb slot, the result is <see cref="ShellCommandWords.Unknown"/>.
///   After the verb slot, the word is skipped;</item>
///   <item>text: a static value that is empty or contains whitespace or a
///   quoted glob character;</item>
///   <item>value: a static value that contains an ASCII digit;</item>
///   <item>command word: every other static value, quoted or not.</item>
/// </list>
/// Only command words go into the result. A redirect target is never a word
/// of the command, and assignment prefixes are not clause elements.
/// </para>
/// <para>
/// Position rule (#197). The verb slot is the first command word after the
/// program word. Until it is filled, the rules are strict: a dynamic word
/// makes the result unknown, and a plain word after an option is kept,
/// because it can be a subcommand after a valueless switch
/// (<c>git -p filter-branch</c>). After the slot is filled, a dynamic word
/// is skipped as an argument, and a plain word directly after an option is
/// skipped as that option's value (<c>dotnet build -c Release</c>). A plain
/// word that does not follow an option is still kept.
/// </para>
/// <para>
/// Limit: after the verb slot, a sub-subcommand that follows an option is
/// skipped. <c>git remote -v add evil url</c> gives <c>git remote</c>.
/// Without the grammar of the program, a switch and a sub-subcommand look
/// the same as an option and its value. The top-level verb stays protected.
/// </para>
/// </remarks>
internal static class ShellCommandWordProjection
{
    /// <summary>
    /// Policy point (owner decision, #197). The verb slot is filled when the
    /// result holds the program word and one more command word. Strict rules
    /// apply only before this point.
    /// </summary>
    internal static bool IsVerbSlotFilled(int commandWordCount) => commandWordCount > 1;

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
        ShellProjectionLanguage language) =>
        Project(clause, occurrenceIsComplete, language, CommandOccurrenceFacts.EmptyLaunchWordValues);

    /// <param name="clause">The authored clause.</param>
    /// <param name="occurrenceIsComplete">Whether the occurrence is complete.</param>
    /// <param name="language">The shell language.</param>
    /// <param name="launchWordValues">
    /// The exact single word for each element that has only literal text and
    /// live launch variables (#200). Such an element is classified by that
    /// word, the same as a static word.
    /// </param>
    internal static ShellCommandWords Project(
        Clause clause,
        bool occurrenceIsComplete,
        ShellProjectionLanguage language,
        IReadOnlyDictionary<int, string> launchWordValues)
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
        var hasLaunchProgram = launchWordValues.TryGetValue(0, out var launchProgram);
        if (program.Role != ClauseElementRole.Verb ||
            !string.Equals(program.Value, clause.Verb.Tokens[0], StringComparison.Ordinal) ||
            program.Value.Length == 0 ||
            CountVerbElements(clause.Elements) != clause.Verb.Tokens.Count ||
            !hasLaunchProgram && ScanProgramWord(program.Raw, language) != WordShape.Static)
        {
            return new ShellCommandWords.Unknown();
        }

        var words = new List<string> { hasLaunchProgram ? launchProgram! : program.Value };
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

            var hasLaunchWord = launchWordValues.TryGetValue(index, out var launchWord);
            var value = hasLaunchWord ? launchWord! : element.Value;
            var wordClass = hasLaunchWord
                ? ClassifyLaunchWord(element, launchWord!)
                : Classify(element, language);
            var verbSlotFilled = IsVerbSlotFilled(words.Count);
            if (wordClass == WordClass.Dynamic && !verbSlotFilled)
            {
                // A dynamic word in the verb slot could be the subcommand.
                return new ShellCommandWords.Unknown();
            }

            // After the verb slot, a plain word directly after an option is
            // that option's value. Before it, the word can be a subcommand
            // after a valueless switch, so it is kept.
            if (wordClass == WordClass.CommandWord &&
                (!followsOption || !verbSlotFilled))
            {
                words.Add(value);
            }

            followsOption = wordClass == WordClass.Option &&
                            !HasInlineOptionValue(value);
        }

        return new ShellCommandWords.Known(words);
    }

    private static WordClass Classify(ClauseElement element, ShellProjectionLanguage language)
    {
        var shape = Scan(element.Raw, language);
        var isStatic = shape == WordShape.Static &&
                       element.Kind is ArgKind.Literal or ArgKind.DynamicSkip;

        // A word that can split into more than one word (an unquoted Bash
        // expansion, a brace list, a PowerShell array or splat) can add a
        // word that no class here accounts for. That is true even inside an
        // option or a path: with r='x push', `git --c=$r log` runs
        // `git --c=x push log`, and `git {push,a/b}` runs `git push a/b`.
        // The parser can report a brace word as one resolved path, so a
        // path fact never excuses a split word.
        if (shape == WordShape.MaySplit)
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

    /// <summary>
    /// Classifies a word whose exact value comes from live launch variables
    /// (#200). The program receives exactly this one word, so it gets the
    /// static-word rules. A glob character in it is data: an unquoted value
    /// with a glob character never reaches this point.
    /// </summary>
    private static WordClass ClassifyLaunchWord(ClauseElement element, string value)
    {
        if (StartsWithDash(value))
        {
            return WordClass.Option;
        }

        if (value.Length == 0 ||
            ContainsWhitespace(value) ||
            value.IndexOfAny(GlobCharacters) >= 0)
        {
            return WordClass.Text;
        }

        if (element.IsPath || BashResolver.LooksLikePathOperand(value, isGlobPattern: false))
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
        // Bash removes a line continuation before it reads the expansion,
        // so `"$\⏎@"` is `"$@"` (#243).
        var next = BashLineContinuation.Skip(
            raw.AsSpan(), dollar + 1, BashContinuationContext.DoubleQuoted);
        if (next >= raw.Length)
        {
            return false;
        }

        if (raw[next] == '@')
        {
            return true;
        }

        if (raw[next] != '{')
        {
            return false;
        }

        var close = raw.IndexOf('}', next + 1);
        var body = close < 0 ? raw.Substring(next + 1) : raw.Substring(next + 1, close - next - 1);
        return body.IndexOf('@') >= 0;
    }

    /// <summary>
    /// Quote-aware scan of one authored Bash word. It does not decode the
    /// word; the lexer owns the decoded value. It only proves whether the
    /// shell can change the word before the program runs.
    /// </summary>
    private static WordShape ScanBash(string raw)
    {
        // A lone `[` has no closing `]`, so it is not a pattern (#212). Bash
        // removes a line continuation, so `[\⏎` is also a lone `[` (#243).
        if (BashLineContinuation.Remove(raw.AsSpan(), BashContinuationContext.Unquoted) == "[")
        {
            return WordShape.Static;
        }

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
                case '~' when FollowsAssignmentMark(raw, index):
                    // Bash can expand `~` after `=` or `:` of an
                    // assignment-shaped word: `PREFIX=~/x` (#243).
                    shape = Max(shape, WordShape.Expanded);
                    break;
                case '{':
                    braceDepth++;
                    break;
                case ',' when braceDepth > 0:
                    braceHasList = true;
                    break;
                case '.' when braceDepth > 0 && IsNextLogicalDot(raw, index):
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

    // The character before `~`, across line continuations, is `=` or `:`.
    private static bool FollowsAssignmentMark(string raw, int tilde)
    {
        var index = tilde - 1;
        while (index >= 1 && raw[index] == '\n' && raw[index - 1] == '\\')
        {
            index -= 2;
        }

        return index >= 0 && raw[index] is '=' or ':';
    }

    // `{a.\⏎.c}` is the sequence `{a..c}`: a line continuation between the
    // dots does not end the `..` (#243).
    private static bool IsNextLogicalDot(string raw, int index)
    {
        var next = BashLineContinuation.Skip(
            raw.AsSpan(), index + 1, BashContinuationContext.Unquoted);
        return next < raw.Length && raw[next] == '.';
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
