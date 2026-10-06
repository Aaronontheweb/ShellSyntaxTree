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

    /// <summary>
    /// Gets the occurrence in the full authored parse. Read its structure
    /// (role, ancestry, assignments) from here, not from a twin.
    /// </summary>
    public CommandOccurrence SourceOccurrence { get; internal init; } = null!;

    /// <summary>
    /// Gets the index of <see cref="SourceOccurrence"/> in
    /// <see cref="ParsedCommand.Commands"/> of <see cref="BashLiteralTwinProjection.Parsed"/>.
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
/// One literal twin: the text of one authored command with each changeable
/// argument word written as one literal value, and the occurrence parsed from
/// that text.
/// </summary>
public sealed record BashLiteralTwin
{
    private IReadOnlyList<BashLiteralTwinWord> _words = Array.Empty<BashLiteralTwinWord>();

    internal BashLiteralTwin()
    {
    }

    /// <summary>
    /// Gets the text of the twin command: the authored text from the first to
    /// the last word of the source occurrence, with each changeable argument
    /// word written as one literal word with no expansion. Bash never runs
    /// this text.
    /// </summary>
    public string Source { get; internal init; } = "";

    /// <summary>
    /// Gets the exact directory of the source occurrence. The parser parses
    /// <see cref="Source"/> in this directory, with the launch facts that are
    /// live at the source occurrence.
    /// </summary>
    public string WorkingDirectory { get; internal init; } = "";

    /// <summary>
    /// Gets the occurrence parsed from <see cref="Source"/>. Its words, values,
    /// path facts, and command words are the facts of the typed literal
    /// command. Its structure is the structure of one top-level command.
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
/// The work of one projection: twin parses and the characters of the twin
/// texts that it built and parsed.
/// </summary>
internal readonly struct BashLiteralTwinCost
{
    internal BashLiteralTwinCost(int parses, long characters)
    {
        Parses = parses;
        Characters = characters;
    }

    internal int Parses { get; }

    internal long Characters { get; }
}

/// <summary>
/// Builds literal twins. A twin replaces each changeable argument word of one
/// occurrence with one proved value. The parser then parses the text of that
/// one command in the exact directory of the occurrence, with the launch facts
/// that are live there. The twin occurrence has the facts of a literal word in
/// the same command.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY: an occurrence gets twins only when the parser proves every value
/// of every changeable word. An occurrence gets no twins when it is
/// incomplete, when its directory is not exact, when it has an assignment
/// prefix, when a verb word is not static, when a word has no complete finite
/// value set, when an unquoted expansion can split or glob, when a redirect
/// depends on an expansion, when a word has no span in the source, or when its
/// parses do not fit in the budget. A twin gives no authority. The caller keeps
/// its own behavior for an occurrence without twins.
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
    /// <summary>The most twin parses for one call.</summary>
    internal const int MaximumTwinParses = 128;

    /// <summary>
    /// The characters that the twin texts of one call can hold, before the
    /// share for each source character.
    /// </summary>
    internal const int CharacterBudgetBase = 2048;

    /// <summary>
    /// The characters of twin text that each source character adds to the
    /// budget. A twin text is one simple command with no loop, so its parse
    /// costs about as much as its length. The twin parses of one call
    /// therefore cost a small constant factor of one <c>Parse</c>.
    /// </summary>
    internal const int CharacterBudgetFactor = 8;

    internal static long CharacterBudget(int sourceLength) =>
        CharacterBudgetBase + (long)CharacterBudgetFactor * sourceLength;

    internal static bool TryProject(
        ParsedCommand parsed,
        BashParserOptions options,
        out BashLiteralTwinProjection? projection) =>
        TryProject(parsed, options, out projection, out _);

    internal static bool TryProject(
        ParsedCommand parsed,
        BashParserOptions options,
        out BashLiteralTwinProjection? projection,
        out BashLiteralTwinCost cost)
    {
        projection = null;
        cost = default;
        var source = parsed.Source;
        if (HasUnmodeledText(source))
        {
            return false;
        }

        // The cheap checks run for every occurrence before any twin is built.
        var mentionsHome = source.IndexOf('~') >= 0 ||
                           source.IndexOf("HOME", StringComparison.Ordinal) >= 0;
        var candidates = new List<Candidate>();
        for (var index = 0; index < parsed.Commands.Count; index++)
        {
            if (TryCreateCandidate(source, index, parsed.Commands[index], options,
                    mentionsHome, out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        // SECURITY (availability): the budget is checked before a twin text is
        // built, with lengths known from the cheap checks. An occurrence whose
        // twins do not fit gets no twins and costs no build and no parse. A
        // charged occurrence keeps its charge when a twin fails. After the
        // budget is used, each later occurrence costs one comparison.
        var budget = CharacterBudget(source.Length);
        var parses = 0;
        var characters = 0L;
        var commands = new List<BashLiteralTwinCommand>();
        foreach (var candidate in candidates)
        {
            if (parses + candidate.Count > MaximumTwinParses ||
                characters + candidate.Characters > budget)
            {
                continue;
            }

            parses += candidate.Count;
            characters += candidate.Characters;
            if (TryBuildTwins(source, options, parsed.Commands[candidate.Index], candidate,
                    out var twins))
            {
                commands.Add(new BashLiteralTwinCommand
                {
                    SourceOccurrence = parsed.Commands[candidate.Index],
                    SourceOccurrenceIndex = candidate.Index,
                    Twins = twins,
                });
            }
        }

        cost = new BashLiteralTwinCost(parses, characters);
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
    /// Returns true when the source has text that the Bash lexer does not
    /// model as Bash does.
    /// </summary>
    /// <remarks>
    /// SECURITY: a value can come from any part of the source, such as a loop
    /// list, so each check reads the full source. A twin would turn a wrong
    /// value into a literal word, so such a source gets no twins.
    /// <list type="bullet">
    ///   <item>Bash removes a backslash-newline before it reads a word, also
    ///         inside an expansion: <c>"$</c>, a backslash-newline, and
    ///         <c>(id)"</c> run <c>id</c>.</item>
    ///   <item>The lexer reads a carriage return as a command separator, but
    ///         Bash reads it as a word character.</item>
    ///   <item>Bash expands a tilde after <c>=</c> and after <c>:</c> in an
    ///         argument or a loop list item that looks like an assignment
    ///         (<c>dd if=~/x</c>, <c>for f in if=~/x</c>, <c>PATH=a:~/bin</c>).
    ///         The parser reads such text as static. The check is lexical and
    ///         stricter than Bash: it also rejects quoted text and
    ///         <c>[[ $x =~ re ]]</c>.</item>
    /// </list>
    /// </remarks>
    private static bool HasUnmodeledText(string source) =>
        source.IndexOf('\r') >= 0 ||
        source.IndexOf("\\\n", StringComparison.Ordinal) >= 0 ||
        source.IndexOf("=~", StringComparison.Ordinal) >= 0 ||
        source.IndexOf(":~", StringComparison.Ordinal) >= 0;

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

    private sealed class Candidate
    {
        internal Candidate(int index, string directory, int sliceStart, int sliceLength,
            List<TwinWord> words, int count, long characters)
        {
            Index = index;
            Directory = directory;
            SliceStart = sliceStart;
            SliceLength = sliceLength;
            Words = words;
            Count = count;
            Characters = characters;
        }

        internal int Index { get; }

        internal string Directory { get; }

        internal int SliceStart { get; }

        internal int SliceLength { get; }

        internal List<TwinWord> Words { get; }

        internal int Count { get; }

        /// <summary>Gets the total length of all twin texts, known before a build.</summary>
        internal long Characters { get; }
    }

    private static bool TryCreateCandidate(
        string source,
        int index,
        CommandOccurrence occurrence,
        BashParserOptions options,
        bool mentionsHome,
        out Candidate candidate)
    {
        candidate = null!;
        if (!occurrence.IsComplete ||
            occurrence.WorkingDirectory is not ShellValueDomain.Exact directory ||
            HasCommandEnvironmentAssignment(occurrence) ||
            mentionsHome && !HasLiveHome(occurrence) ||
            !TryGetSlice(source, occurrence, out var sliceStart, out var sliceLength))
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

        var words = new List<TwinWord>();
        var elements = occurrence.Clause.Elements;
        for (var elementIndex = 0; elementIndex < elements.Count; elementIndex++)
        {
            var element = elements[elementIndex];
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
                !TryGetProvedValues(argument, options, out var values))
            {
                return false;
            }

            words.Add(new TwinWord(elementIndex, element.SourceStart!.Value,
                element.SourceLength!.Value, values));
        }

        if (words.Count == 0 || !TryCountCombinations(words, out var count))
        {
            return false;
        }

        // The length of each twin text is the slice length plus, for each
        // word, the length of the spelled value minus the authored length.
        // Each value of a word occurs in count / values twins.
        var characters = (long)count * sliceLength;
        foreach (var word in words)
        {
            var share = count / word.Values.Count;
            foreach (var value in word.Values)
            {
                characters += (long)share * (Spell(value).Length - word.SourceLength);
            }
        }

        candidate = new Candidate(index, directory.Value, sliceStart, sliceLength,
            words, count, characters);
        return true;
    }

    // An assignment prefix (`X=$n cmd`) is part of the command, but outside
    // its words. A twin of the words alone would lose it.
    private static bool HasCommandEnvironmentAssignment(CommandOccurrence occurrence)
    {
        foreach (var assignment in occurrence.Assignments)
        {
            if (assignment.Scope == ShellVariableAssignmentScope.CommandEnvironment)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the launch facts prove the home directory at the
    /// occurrence.
    /// </summary>
    /// <remarks>
    /// SECURITY: Bash expands <c>~</c> from <c>$HOME</c>. Without a live launch
    /// <c>HOME</c>, the parser takes the home directory from
    /// <see cref="ShellParserOptions.HomeDirectory"/>, a caller assumption. A
    /// twin would show that assumption as a literal path. A value can come
    /// from any part of the source, such as a loop list, so the caller asks
    /// this when the source has <c>~</c> or <c>HOME</c> anywhere.
    /// </remarks>
    private static bool HasLiveHome(CommandOccurrence occurrence) =>
        occurrence.LaunchEnvironment?.TryGetLiveValue("HOME", out _) == true;

    /// <summary>
    /// Finds the source text of the occurrence, from its first word to its
    /// last word. Returns false when a word has no span in the submitted
    /// source, as in a <c>bash -c</c> child.
    /// </summary>
    private static bool TryGetSlice(
        string source,
        CommandOccurrence occurrence,
        out int sliceStart,
        out int sliceLength)
    {
        sliceStart = int.MaxValue;
        var sliceEnd = -1;
        foreach (var element in occurrence.Clause.Elements)
        {
            if (element.SourceStart is not int start ||
                element.SourceLength is not int length ||
                !IsSourceSpan(source, start, length, element.Raw))
            {
                sliceLength = 0;
                return false;
            }

            sliceStart = Math.Min(sliceStart, start);
            sliceEnd = Math.Max(sliceEnd, start + length);
        }

        sliceLength = sliceEnd - sliceStart;
        return sliceEnd > sliceStart;
    }

    private static bool IsSourceSpan(string source, int start, int length, string raw) =>
        start >= 0 && length > 0 && start <= source.Length - length &&
        string.CompareOrdinal(source, start, raw, 0, length) == 0 &&
        raw.Length == length;

    /// <summary>
    /// Returns true when the redirect does not depend on an expansion value
    /// and fits in the twin text: an exact file target, a descriptor
    /// operation, or a here-string with exact data.
    /// </summary>
    /// <remarks>
    /// A twin keeps each redirect as authored. A redirect with more than one
    /// value would keep its expansion, so the occurrence gets no twins. A
    /// heredoc body is outside the words of the command, so the twin text
    /// cannot hold it.
    /// </remarks>
    private static bool HasFixedRedirect(RedirectAnalysis redirect) =>
        redirect.IsComplete && redirect switch
        {
            FileRedirectAnalysis file => file.Target is ShellValueDomain.Exact,
            DescriptorDuplicateRedirectAnalysis => true,
            DescriptorMoveRedirectAnalysis => true,
            DescriptorCloseRedirectAnalysis => true,
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

    private static bool TryBuildTwins(
        string source,
        BashParserOptions options,
        CommandOccurrence occurrence,
        Candidate candidate,
        out List<BashLiteralTwin> twins)
    {
        twins = new List<BashLiteralTwin>(candidate.Count);
        var parser = new BashParser(options with
        {
            WorkingDirectory = candidate.Directory,
            // Only the facts that were live at this command (#200). Without a
            // caller-supplied start directory, the directory is the process
            // default, so a relative cd must stay unknown.
            LaunchEnvironment = options.WorkingDirectory is null
                ? occurrence.LaunchEnvironment?.Revoke("CDPATH")
                : occurrence.LaunchEnvironment,
        });
        var words = candidate.Words;
        var choice = new int[words.Count];
        while (true)
        {
            var text = Render(source, candidate, choice);
            if (!TryMatchTwin(occurrence, parser.Parse(text), candidate, choice,
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
                Source = text,
                WorkingDirectory = candidate.Directory,
                Occurrence = twinOccurrence,
                Words = twinWords,
            });

            var position = words.Count - 1;
            while (position >= 0 && ++choice[position] == words[position].Values.Count)
            {
                choice[position] = 0;
                position--;
            }

            if (position < 0)
            {
                return true;
            }
        }
    }

    private static string Render(string source, Candidate candidate, int[] choice)
    {
        var words = candidate.Words;
        var builder = new StringBuilder(candidate.SliceLength);
        var cursor = candidate.SliceStart;
        // The words are in element order, and the elements are in source order.
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            builder.Append(source, cursor, word.SourceStart - cursor);
            builder.Append(Spell(word.Values[choice[index]]));
            cursor = word.SourceStart + word.SourceLength;
        }

        builder.Append(source, cursor, candidate.SliceStart + candidate.SliceLength - cursor);
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
    /// Proves that the twin text is one complete command in the same
    /// directory and that each replaced word is now one literal word with the
    /// chosen value.
    /// </summary>
    private static bool TryMatchTwin(
        CommandOccurrence sourceOccurrence,
        ParsedCommand twinParse,
        Candidate candidate,
        int[] choice,
        out CommandOccurrence twinOccurrence)
    {
        twinOccurrence = null!;
        if (twinParse.IsUnparseable || twinParse.Commands.Count != 1)
        {
            return false;
        }

        var twin = twinParse.Commands[0];
        var sourceElements = sourceOccurrence.Clause.Elements;
        var twinElements = twin.Clause.Elements;
        if (!twin.IsComplete ||
            twin.WorkingDirectory is not ShellValueDomain.Exact { Value: var directory } ||
            !string.Equals(directory, candidate.Directory, StringComparison.Ordinal) ||
            twinElements.Count != sourceElements.Count)
        {
            return false;
        }

        var words = candidate.Words;
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
                (!TryGetArgument(twin, twinElement, out var argument) ||
                 argument.Value is not ShellValueDomain.Exact exact ||
                 !string.Equals(exact.Value, value, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        twinOccurrence = twin;
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
