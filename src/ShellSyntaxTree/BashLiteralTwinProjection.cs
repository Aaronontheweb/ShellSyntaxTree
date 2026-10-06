// -----------------------------------------------------------------------
// <copyright file="BashLiteralTwinProjection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal;
using ShellSyntaxTree.Internal.Parsing;

namespace ShellSyntaxTree;

/// <summary>
/// The literal twins of the Bash commands whose changeable argument words
/// have a proved finite set of values. A changeable word is a word that the
/// shell can change before the program runs, such as an expansion or a tilde.
/// The proof supplies syntax facts and does not grant execution authority.
/// </summary>
public sealed record BashLiteralTwinProjection
{
    private IReadOnlyList<BashLiteralTwinCommand> _commands =
        Array.Empty<BashLiteralTwinCommand>();

    internal BashLiteralTwinProjection()
    {
    }

    /// <summary>Gets the full authored parse that owns each source occurrence.</summary>
    public ParsedCommand Parsed { get; internal init; } = null!;

    /// <summary>
    /// Gets one record for each occurrence that has literal twins, in the
    /// order of <see cref="ParsedCommand.Commands"/>. An occurrence that is
    /// not in this list has no twins.
    /// </summary>
    public IReadOnlyList<BashLiteralTwinCommand> Commands
    {
        get => _commands;
        internal init => _commands = PublicCollection.Copy(value);
    }
}

/// <summary>One authored command and its literal twins.</summary>
public sealed record BashLiteralTwinCommand
{
    private IReadOnlyList<BashLiteralTwin> _twins = Array.Empty<BashLiteralTwin>();

    internal BashLiteralTwinCommand()
    {
    }

    /// <summary>Gets the occurrence in the full authored parse.</summary>
    public CommandOccurrence SourceOccurrence { get; internal init; } = null!;

    /// <summary>
    /// Gets the index of <see cref="SourceOccurrence"/> in
    /// <see cref="ParsedCommand.Commands"/>. A parse of the source of a twin
    /// has its occurrence at the same index.
    /// </summary>
    public int SourceOccurrenceIndex { get; internal init; }

    /// <summary>
    /// Gets one twin for each combination of the proved word values. The
    /// twins follow the authored word order, then the order of each value set.
    /// </summary>
    public IReadOnlyList<BashLiteralTwin> Twins
    {
        get => _twins;
        internal init => _twins = PublicCollection.Copy(value);
    }
}

/// <summary>
/// One literal twin: the full source with each changeable argument word of one
/// occurrence written as one literal value, and the occurrence parsed from it.
/// </summary>
public sealed record BashLiteralTwin
{
    private IReadOnlyList<BashLiteralTwinWord> _words = Array.Empty<BashLiteralTwinWord>();

    internal BashLiteralTwin()
    {
    }

    /// <summary>
    /// Gets the full source of the twin. Only the changeable argument words of
    /// the source occurrence change. Each such word is one literal word with no
    /// expansion. All other text stays as authored.
    /// </summary>
    public string Source { get; internal init; } = "";

    /// <summary>
    /// Gets the occurrence parsed from <see cref="Source"/> with the parser
    /// options of the projection. It has its own normal facts.
    /// </summary>
    public CommandOccurrence Occurrence { get; internal init; } = null!;

    /// <summary>Gets each replaced word and its literal value.</summary>
    public IReadOnlyList<BashLiteralTwinWord> Words
    {
        get => _words;
        internal init => _words = PublicCollection.Copy(value);
    }
}

/// <summary>One changeable word of a source occurrence and its value in a twin.</summary>
public sealed record BashLiteralTwinWord
{
    internal BashLiteralTwinWord()
    {
    }

    /// <summary>Gets the index of the word in <see cref="Clause.Elements"/>.</summary>
    public int ClauseElementIndex { get; internal init; }

    /// <summary>Gets the literal value of the word in this twin.</summary>
    public string Value { get; internal init; } = "";
}

/// <summary>
/// Builds literal twins. A twin replaces each changeable argument word of one
/// occurrence with one proved value, then parses the full source again. The
/// twin occurrence then has the facts of a literal word in the same place.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY: an occurrence gets twins only when the parser proves every value
/// of every changeable word. An occurrence gets no twins when it is
/// incomplete, when a verb word is not static, when a word has no complete
/// finite value set, when an unquoted expansion can split or glob, when a
/// redirect depends on an expansion, when a word has no span in the source, or
/// when the combinations pass the limit. A twin gives no authority. The caller
/// keeps its own behavior for an occurrence without twins.
/// </para>
/// <para>
/// The values of two words combine independently. Two words that read the same
/// loop variable can therefore give a twin with two different values. Such a
/// combination does not run, but each of its values does. The twin set is a
/// superset of the runs, so a check of every twin checks every run.
/// </para>
/// </remarks>
internal static class BashLiteralTwinAnalyzer
{
    /// <summary>The most twin parses for one call, successful or not.</summary>
    internal const int MaximumTwinParses = 128;

    /// <summary>
    /// The most source characters that the twin parses of one call can read.
    /// The cost of a parse grows with the source, so a long source gets fewer
    /// twin parses. A 2 KB source gets at most 16.
    /// </summary>
    internal const int MaximumTwinCharacters = 32 * 1024;

    internal static bool TryProject(
        ParsedCommand parsed,
        BashParser parser,
        BashParserOptions options,
        out BashLiteralTwinProjection? projection) =>
        TryProject(parsed, parser, options, out projection, out _);

    internal static bool TryProject(
        ParsedCommand parsed,
        BashParser parser,
        BashParserOptions options,
        out BashLiteralTwinProjection? projection,
        out int parseCount)
    {
        projection = null;
        parseCount = 0;
        if (HasUnmodeledLineText(parsed.Source))
        {
            return false;
        }

        // The cheap checks run for every occurrence before any twin parse.
        var candidates = new List<(int Index, List<TwinWord> Words, int Count)>();
        for (var index = 0; index < parsed.Commands.Count; index++)
        {
            if (TryGetWordValues(parsed.Source, parsed.Commands[index], options, out var words) &&
                words.Count > 0 &&
                TryCountCombinations(words, out var count))
            {
                candidates.Add((index, words, count));
            }
        }

        var commands = new List<BashLiteralTwinCommand>();
        var characters = 0;
        foreach (var candidate in candidates)
        {
            var sources = RenderAll(parsed.Source, candidate.Words);
            var cost = 0;
            foreach (var (source, _) in sources)
            {
                cost += source.Length;
            }

            // SECURITY (availability): the budget counts every twin parse,
            // also one that fails. An occurrence that does not fit in the
            // rest of the budget gets no twins and costs no parse.
            if (parseCount + sources.Count > MaximumTwinParses ||
                characters + cost > MaximumTwinCharacters)
            {
                continue;
            }

            characters += cost;
            var twins = new List<BashLiteralTwin>(sources.Count);
            if (!TryBuildTwins(parsed, parser, candidate.Index, candidate.Words, sources,
                    twins, ref parseCount))
            {
                continue;
            }

            commands.Add(new BashLiteralTwinCommand
            {
                SourceOccurrence = parsed.Commands[candidate.Index],
                SourceOccurrenceIndex = candidate.Index,
                Twins = twins,
            });
        }

        if (commands.Count == 0)
        {
            return false;
        }

        projection = new BashLiteralTwinProjection
        {
            Parsed = parsed,
            Commands = commands,
        };
        return true;
    }

    /// <summary>
    /// Returns true when the source has line text that the Bash lexer does not
    /// model as Bash does.
    /// </summary>
    /// <remarks>
    /// SECURITY: Bash removes a backslash-newline before it reads a word, also
    /// inside an expansion: <c>"$</c>, a backslash-newline, and <c>(id)"</c>
    /// run <c>id</c>. The lexer reads a carriage return as a command separator,
    /// but Bash reads it as a word character. A value can come from any part of the source, such as a
    /// loop list, so the check reads the full source. A twin would turn such a
    /// wrong value into a literal word, so the source gets no twins.
    /// </remarks>
    private static bool HasUnmodeledLineText(string source) =>
        source.IndexOf('\r') >= 0 ||
        source.IndexOf("\\\n", StringComparison.Ordinal) >= 0;

    private sealed class TwinWord
    {
        internal TwinWord(int elementIndex, int sourceStart, int sourceLength,
            IReadOnlyList<string> values)
        {
            ElementIndex = elementIndex;
            SourceStart = sourceStart;
            SourceLength = sourceLength;
            Values = values;
        }

        internal int ElementIndex { get; }

        internal int SourceStart { get; }

        internal int SourceLength { get; }

        internal IReadOnlyList<string> Values { get; }
    }

    private static bool TryGetWordValues(
        string source,
        CommandOccurrence occurrence,
        BashParserOptions options,
        out List<TwinWord> words)
    {
        words = new List<TwinWord>();
        if (!occurrence.IsComplete || UsesUnprovedHome(source, occurrence))
        {
            return false;
        }

        foreach (var redirect in occurrence.Redirects)
        {
            if (!HasFixedRedirect(redirect))
            {
                return false;
            }
        }

        var elements = occurrence.Clause.Elements;
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (HasAssignmentTilde(element.Raw))
            {
                return false;
            }

            if (element.Role == ClauseElementRole.Redirect)
            {
                continue;
            }

            // A static word has one value that the shell cannot change, so
            // the twin keeps it as authored. A verb word has no analyzed
            // argument, so a program word or a verb word that the shell can
            // change gives no twins.
            if (ShellCommandWordProjection.IsStaticBashWord(element.Raw))
            {
                continue;
            }

            if (!TryGetArgument(occurrence, element, out var argument) ||
                !TryGetProvedValues(argument, options, out var values) ||
                element.SourceStart is not int start ||
                element.SourceLength is not int length ||
                !IsSourceSpan(source, start, length, element.Raw))
            {
                return false;
            }

            words.Add(new TwinWord(index, start, length, values));
        }

        return true;
    }

    /// <summary>
    /// Returns true when a value of the occurrence can come from a home
    /// directory that the launch facts do not prove.
    /// </summary>
    /// <remarks>
    /// SECURITY: Bash expands <c>~</c> from <c>$HOME</c>. Without a live launch
    /// <c>HOME</c>, the parser takes the home directory from
    /// <see cref="ShellParserOptions.HomeDirectory"/>, a caller assumption. A
    /// twin would show that assumption as a literal path. The value can come
    /// from any part of the source, such as a loop list, so the check reads the
    /// full source. It is lexical and stricter than Bash.
    /// </remarks>
    private static bool UsesUnprovedHome(string source, CommandOccurrence occurrence) =>
        (source.IndexOf('~') >= 0 || source.IndexOf("HOME", StringComparison.Ordinal) >= 0) &&
        occurrence.LaunchEnvironment?.TryGetLiveValue("HOME", out _) != true;

    /// <summary>
    /// Returns true when a word has <c>=~</c> or <c>:~</c>.
    /// </summary>
    /// <remarks>
    /// SECURITY: Bash expands a tilde after <c>=</c> and after <c>:</c> in an
    /// argument that looks like an assignment (<c>dd if=~/x</c>,
    /// <c>PATH=a:~/bin</c>). The parser reads such a word as static text, so a
    /// twin would keep a wrong value. The check is lexical and stricter than
    /// Bash: it also rejects quoted text.
    /// </remarks>
    private static bool HasAssignmentTilde(string raw) =>
        raw.IndexOf("=~", StringComparison.Ordinal) >= 0 ||
        raw.IndexOf(":~", StringComparison.Ordinal) >= 0;

    // A word of a `bash -c` child has no span in the submitted source, so it
    // cannot be rewritten in place.
    private static bool IsSourceSpan(string source, int start, int length, string raw) =>
        start >= 0 && length > 0 && start <= source.Length - length &&
        string.CompareOrdinal(source, start, raw, 0, length) == 0 &&
        raw.Length == length;

    /// <summary>
    /// Returns true when the redirect does not depend on an expansion value:
    /// an exact file target, a descriptor operation, a heredoc with a literal
    /// body, or a here-string with exact data.
    /// </summary>
    /// <remarks>
    /// A twin keeps each redirect as authored. A redirect with more than one
    /// value would keep its expansion, so the occurrence gets no twins.
    /// </remarks>
    private static bool HasFixedRedirect(RedirectAnalysis redirect) =>
        redirect.IsComplete && redirect switch
        {
            FileRedirectAnalysis file => file.Target is ShellValueDomain.Exact,
            DescriptorDuplicateRedirectAnalysis => true,
            DescriptorMoveRedirectAnalysis => true,
            DescriptorCloseRedirectAnalysis => true,
            HereDocumentRedirectAnalysis heredoc =>
                heredoc.Document.IsComplete &&
                heredoc.Document.ExpansionMode == HereDocumentExpansionMode.Literal,
            HereStringRedirectAnalysis hereString => hereString.Data is ShellValueDomain.Exact,
            _ => false,
        };

    private static bool TryGetArgument(
        CommandOccurrence occurrence,
        ClauseElement element,
        out AnalyzedArgument argument)
    {
        argument = null!;
        foreach (var candidate in occurrence.Arguments)
        {
            if (!ReferenceEquals(candidate.Element, element))
            {
                continue;
            }

            // An element with two arguments (an inline option pair such as
            // `--opt=$n`) has no single word value.
            if (argument is null)
            {
                argument = candidate;
            }
            else
            {
                return false;
            }
        }

        return argument is not null;
    }

    /// <summary>
    /// Returns the proved values of one argument word, or false.
    /// </summary>
    /// <remarks>
    /// SECURITY: an unquoted expansion can split or glob. The effective value
    /// stays unknown for such a word. The word has its authored values only
    /// when the caller proves the fresh-process contract: that contract removes
    /// an inherited <c>IFS</c>, and the parser rejects source that changes
    /// <c>IFS</c> or a glob option. Bash then splits only on space, tab, and
    /// newline. It globs on <c>*</c>, <c>?</c>, and <c>[</c>, and with
    /// <c>extglob</c> on, a Bash build default, also on a pattern such as
    /// <c>@(a)</c> or <c>+(a)</c>. A value without these characters, without a
    /// parenthesis, and without a backslash stays one unchanged word. An empty
    /// value can remove the word, so it gives no twin.
    /// </remarks>
    private static bool TryGetProvedValues(
        AnalyzedArgument argument,
        BashParserOptions options,
        out IReadOnlyList<string> values)
    {
        var domain = argument.Value;
        var mayChange = argument.MayFieldSplit || argument.MayPathnameExpand;
        if (mayChange && domain is ShellValueDomain.Unknown)
        {
            domain = argument.AuthoredValue;
        }

        values = domain switch
        {
            ShellValueDomain.Exact exact => new[] { exact.Value },
            ShellValueDomain.FiniteSet finite => finite.Values,
            _ => Array.Empty<string>(),
        };
        if (values.Count == 0)
        {
            return false;
        }

        if (!mayChange)
        {
            return true;
        }

        if (options.InitialStateMode != BashInitialStateMode.FreshNonInteractiveNoStartup)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value.Length == 0 || value.IndexOfAny(SplitOrGlobCharacters) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static readonly char[] SplitOrGlobCharacters =
        { ' ', '\t', '\n', '*', '?', '[', '\\', '(', ')' };

    private static bool TryCountCombinations(List<TwinWord> words, out int count)
    {
        count = 1;
        foreach (var word in words)
        {
            count *= word.Values.Count;
            if (count > ShellAnalysisLimits.MaxValueCandidates)
            {
                return false;
            }
        }

        return true;
    }

    private static List<(string Source, int[] Choice)> RenderAll(
        string source,
        List<TwinWord> words)
    {
        var sources = new List<(string, int[])>();
        var choice = new int[words.Count];
        while (true)
        {
            sources.Add((Render(source, words, choice), (int[])choice.Clone()));
            var position = words.Count - 1;
            while (position >= 0 && ++choice[position] == words[position].Values.Count)
            {
                choice[position] = 0;
                position--;
            }

            if (position < 0)
            {
                return sources;
            }
        }
    }

    private static bool TryBuildTwins(
        ParsedCommand parsed,
        BashParser parser,
        int occurrenceIndex,
        List<TwinWord> words,
        List<(string Source, int[] Choice)> sources,
        List<BashLiteralTwin> twins,
        ref int parseCount)
    {
        foreach (var (source, choice) in sources)
        {
            parseCount++;
            var twinParse = parser.Parse(source);
            if (!TryMatchTwin(parsed, twinParse, occurrenceIndex, words, choice,
                    out var twinOccurrence))
            {
                return false;
            }

            var twinWords = new BashLiteralTwinWord[words.Count];
            for (var index = 0; index < words.Count; index++)
            {
                twinWords[index] = new BashLiteralTwinWord
                {
                    ClauseElementIndex = words[index].ElementIndex,
                    Value = words[index].Values[choice[index]],
                };
            }

            twins.Add(new BashLiteralTwin
            {
                Source = source,
                Occurrence = twinOccurrence,
                Words = twinWords,
            });
        }

        return true;
    }

    private static string Render(string source, List<TwinWord> words, int[] choice)
    {
        var builder = new StringBuilder(source.Length);
        var cursor = 0;
        var order = new List<int>(words.Count);
        for (var index = 0; index < words.Count; index++)
        {
            order.Add(index);
        }

        order.Sort((left, right) => words[left].SourceStart.CompareTo(words[right].SourceStart));
        foreach (var index in order)
        {
            var word = words[index];
            builder.Append(source, cursor, word.SourceStart - cursor);
            builder.Append(Spell(word.Values[choice[index]]));
            cursor = word.SourceStart + word.SourceLength;
        }

        builder.Append(source, cursor, source.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// Spells one value as one Bash word with no expansion. A plain value
    /// keeps its plain spelling, so it gets the facts of the word as a person
    /// types it. Other values use single quotes.
    /// </summary>
    private static string Spell(string value)
    {
        if (value.Length > 0 && IsPlain(value))
        {
            return value;
        }

        return "'" + value.Replace("'", "'\\''") + "'";
    }

    private static bool IsPlain(string value)
    {
        foreach (var character in value)
        {
            var plain = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '.' or '_' or '/' or ':' or '@' or '%' or
                '+' or '=' or ',' or '-';
            if (!plain)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Proves that the twin parse has the same command structure and that each
    /// replaced word is now one literal word with the chosen value.
    /// </summary>
    private static bool TryMatchTwin(
        ParsedCommand parsed,
        ParsedCommand twinParse,
        int occurrenceIndex,
        List<TwinWord> words,
        int[] choice,
        out CommandOccurrence twinOccurrence)
    {
        twinOccurrence = null!;
        if (twinParse.IsUnparseable ||
            twinParse.Commands.Count != parsed.Commands.Count)
        {
            return false;
        }

        var sourceOccurrence = parsed.Commands[occurrenceIndex];
        var candidate = twinParse.Commands[occurrenceIndex];
        var sourceElements = sourceOccurrence.Clause.Elements;
        var twinElements = candidate.Clause.Elements;
        if (!candidate.IsComplete ||
            twinElements.Count != sourceElements.Count)
        {
            return false;
        }

        for (var index = 0; index < sourceElements.Count; index++)
        {
            var wordIndex = words.FindIndex(word => word.ElementIndex == index);
            var twinElement = twinElements[index];
            if (!HasSameRoleClass(twinElement.Role, sourceElements[index].Role))
            {
                return false;
            }

            if (wordIndex < 0)
            {
                if (!string.Equals(twinElement.Raw, sourceElements[index].Raw,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                continue;
            }

            var value = words[wordIndex].Values[choice[wordIndex]];
            if (twinElement.Kind != ArgKind.Literal ||
                !string.Equals(twinElement.Value, value, StringComparison.Ordinal))
            {
                return false;
            }

            if (twinElement.Role == ClauseElementRole.Argument &&
                (!TryGetArgument(candidate, twinElement, out var argument) ||
                 argument.Value is not ShellValueDomain.Exact exact ||
                 !string.Equals(exact.Value, value, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        twinOccurrence = candidate;
        return true;
    }

    // The parser folds a plain word after the program into the verb words
    // (`echo b`, `git push`). A literal value can therefore move a word between
    // the verb and argument roles, as the typed literal does. A redirect word
    // keeps its role.
    private static bool HasSameRoleClass(ClauseElementRole twin, ClauseElementRole source) =>
        twin == source ||
        twin is ClauseElementRole.Verb or ClauseElementRole.Argument &&
        source is ClauseElementRole.Verb or ClauseElementRole.Argument;
}
