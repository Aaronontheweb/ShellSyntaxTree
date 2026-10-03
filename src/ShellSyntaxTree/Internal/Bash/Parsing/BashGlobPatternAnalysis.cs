// -----------------------------------------------------------------------
// <copyright file="BashGlobPatternAnalysis.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Builds lexical pathname-expansion facts for one Bash word (#206). The
/// analysis is lexical. It never reads the file system, and it never says
/// which entries exist.
/// </summary>
/// <remarks>
/// The facts depend on the shell options that control pathname expansion.
/// <see cref="BashInitialStateMode.FreshNonInteractiveNoStartup"/> proves
/// them: the launcher removes <c>BASHOPTS</c>, <c>SHELLOPTS</c>, and
/// <c>GLOBIGNORE</c>, passes no option flag, and runs GNU Bash 5.2 or 5.3, so
/// <c>globskipdots</c> is on and the other glob options are off. The source
/// cannot change them: <c>set</c>, a mutating <c>shopt</c>, <c>source</c>,
/// and <c>eval</c> fail the parse. A decoded <c>bash -c</c> child gets no
/// launch facts, so it gets no pattern facts. The caller opts in with live
/// launch facts, and the facts follow the same liveness rules as the launch
/// values.
/// </remarks>
internal static class BashGlobPatternAnalysis
{
    /// <summary>
    /// True when the shell options that control pathname expansion are
    /// proved for this clause.
    /// </summary>
    internal static bool AreShellOptionsProved(BashParserOptions options) =>
        options.InitialStateMode == BashInitialStateMode.FreshNonInteractiveNoStartup &&
        ShellLaunchFacts.IsActive(options) &&
        options.LaunchEnvironment!.HasLiveFacts;

    /// <summary>
    /// Builds the pattern facts for one authored word.
    /// </summary>
    /// <param name="value">The decoded shell value of the word.</param>
    /// <param name="options">The clause options with the live launch facts.</param>
    /// <param name="workingDirectory">
    /// The proved directory of the command, or null when it is not proved. A
    /// relative pattern needs it.
    /// </param>
    /// <param name="pattern">The pattern domain.</param>
    internal static bool TryAnalyze(
        ShellValue value,
        BashParserOptions options,
        string? workingDirectory,
        out ShellValueDomainFacts pattern)
    {
        pattern = ShellValueDomainFacts.Unknown;
        if (!AreShellOptionsProved(options) ||
            !TryExpand(value, options, out var text, out var isWildcard))
        {
            return false;
        }

        // A word that starts with `-` is option-shaped. The program reads it
        // as an option, so it is not a path pattern.
        if (text.Length == 0 || text[0] == '-')
        {
            return false;
        }

        string root;
        int start;
        if (text[0] == '/')
        {
            root = "/";
            start = 1;
        }
        else if (workingDirectory is not null &&
                 workingDirectory.Length > 0 &&
                 workingDirectory[0] == '/')
        {
            root = workingDirectory;
            start = 0;
        }
        else
        {
            return false;
        }

        var segments = SplitSegments(text, isWildcard, start);
        var firstPattern = segments.FindIndex(segment => segment.IsPattern);
        if (firstPattern < 0)
        {
            return false;
        }

        var directory = new List<string>();
        if (!TryAppendDirectory(root, directory))
        {
            return false;
        }

        for (var index = 0; index < firstPattern; index++)
        {
            var segment = segments[index].Text;
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // Bash resolves `..` through the physical directory. A
                // lexical covering directory can then name the wrong tree.
                return false;
            }

            directory.Add(segment);
        }

        var hasTrailingSlash = segments[segments.Count - 1].Text.Length == 0;
        var last = hasTrailingSlash ? segments.Count - 1 : segments.Count;
        var published = new List<ShellGlobSegment>(last - firstPattern);
        for (var index = firstPattern; index < last; index++)
        {
            var segment = segments[index];
            if (segment.Text.Length == 0 ||
                segment.Text == "." ||
                segment.Text == "..")
            {
                return false;
            }

            published.Add(new ShellGlobSegment(
                segment.Text,
                segment.IsPattern,
                segment.Text[0] == '.' || segment.StartsWithWildcard && segment.Text[0] == '['));
        }

        var covering = directory.Count == 0 ? "/" : "/" + string.Join("/", directory);
        var builder = new StringBuilder(covering);
        foreach (var segment in published)
        {
            if (builder.Length > 1)
            {
                builder.Append('/');
            }

            builder.Append(segment.Text);
        }

        if (hasTrailingSlash)
        {
            builder.Append('/');
        }

        // Only a relative word with no directory part can expand to a name
        // that starts with `-`. Any other word starts with its directory.
        var mayStartWithDash = start == 0 && firstPattern == 0 && segments[0].StartsWithWildcard;
        pattern = new ShellValueDomainFacts
        {
            Kind = ShellValueDomainKind.Pattern,
            Pattern = builder.ToString(),
            CoveringDirectory = covering,
            Glob = new ShellGlobExpansion(published, mayStartWithDash),
        };
        return true;
    }

    /// <summary>
    /// Expands the word to its pattern text. It accepts literal text, active
    /// wildcards, and one leading tilde that expands from the live launch
    /// <c>HOME</c>. Each wildcard position is marked in
    /// <paramref name="isWildcard"/>.
    /// </summary>
    private static bool TryExpand(
        ShellValue value,
        BashParserOptions options,
        out string text,
        out List<bool> isWildcard)
    {
        var builder = new StringBuilder(value.Decoded.Length);
        isWildcard = new List<bool>(value.Decoded.Length);
        text = string.Empty;
        var hasWildcard = false;
        for (var index = 0; index < value.Fragments.Count; index++)
        {
            var fragment = value.Fragments[index];
            if (fragment.Kind == ShellValueFragmentKind.Literal &&
                fragment.Cardinality == ShellValueCardinality.ExactlyOne &&
                fragment.Expansion is null)
            {
                // A quoted or escaped glob character is literal text, but the
                // published segment text cannot show the difference. A brace
                // can start brace expansion. A backslash is an escape in
                // pattern syntax. All fail closed.
                if (fragment.Value.IndexOfAny(UnsupportedLiteralCharacters) >= 0)
                {
                    return false;
                }

                Append(builder, isWildcard, fragment.Value, wildcard: false);
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is not { } expansion)
            {
                return false;
            }

            if (expansion.Kind == ShellExpansionKind.Glob &&
                (fragment.AllowedTransforms & ShellLexicalTransform.Glob) != 0 &&
                fragment.Value.Length == 1)
            {
                Append(builder, isWildcard, fragment.Value, wildcard: true);
                hasWildcard = true;
                continue;
            }

            if (expansion.Kind == ShellExpansionKind.Tilde &&
                index == 0 &&
                BashResolver.ClassifyTildeExpansion(value, index) == BashTildeExpansionKind.Home &&
                ShellLaunchFacts.TryGetValue(options, "HOME", out var home) &&
                home.Length > 0 &&
                home[0] == '/' &&
                home.IndexOfAny(UnsupportedHomeCharacters) < 0)
            {
                Append(builder, isWildcard, home, wildcard: false);
                continue;
            }

            return false;
        }

        if (!hasWildcard)
        {
            return false;
        }

        text = builder.ToString();
        return true;
    }

    private static readonly char[] UnsupportedLiteralCharacters =
        { '*', '?', '[', '\\', '{', '}' };

    private static readonly char[] UnsupportedHomeCharacters =
        { '*', '?', '[', '\\', '{', '}', '\0' };

    private static void Append(
        StringBuilder builder,
        List<bool> isWildcard,
        string value,
        bool wildcard)
    {
        builder.Append(value);
        for (var index = 0; index < value.Length; index++)
        {
            isWildcard.Add(wildcard);
        }
    }

    private static bool TryAppendDirectory(string root, List<string> directory)
    {
        foreach (var part in root.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return false;
            }

            directory.Add(part);
        }

        return true;
    }

    private static List<Segment> SplitSegments(string text, List<bool> isWildcard, int start)
    {
        var segments = new List<Segment>();
        var segmentStart = start;
        for (var index = start; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] != '/')
            {
                continue;
            }

            var isPattern = false;
            for (var position = segmentStart; position < index; position++)
            {
                isPattern |= isWildcard[position];
            }

            segments.Add(new Segment(
                text.Substring(segmentStart, index - segmentStart),
                isPattern,
                segmentStart < index && isWildcard[segmentStart]));
            segmentStart = index + 1;
        }

        return segments;
    }

    private readonly record struct Segment(string Text, bool IsPattern, bool StartsWithWildcard);
}
