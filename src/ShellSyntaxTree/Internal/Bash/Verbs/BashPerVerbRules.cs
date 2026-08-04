// -----------------------------------------------------------------------
// <copyright file="BashPerVerbRules.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Verbs;

/// <summary>
/// Per-verb path-arg classification rules from SPEC §7. The parser asks
/// this class "is the i-th non-flag positional of <c>verb</c> a path?" and
/// gets a boolean back. The rules are a layered fall-through:
/// per-verb override → FileVerb default ("all positionals are paths") →
/// <see cref="BashResolver.LooksLikePath"/> heuristic for non-FileVerb verbs.
/// </summary>
/// <remarks>
/// This file also owns the per-flag value-classification table referenced
/// in SPEC §7's flag-with-value section — e.g. <c>git -C /repo</c> says
/// "/repo is a path", <c>curl -d body</c> says "body is *not* a path".
/// </remarks>
internal static class BashPerVerbRules
{
    /// <summary>
    /// Per-verb override delegate. Returns <c>true</c> when the i-th
    /// (0-based) non-flag positional arg of <paramref name="verb"/> is a path.
    /// </summary>
    private delegate bool PerVerbRule(int positionalIndex);

    /// <summary>
    /// Override table keyed by the first token of the verb chain. The
    /// FileVerb default ("all non-flag positionals are paths") covers
    /// everything not listed here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, PerVerbRule> Overrides =
        new Dictionary<string, PerVerbRule>(StringComparer.OrdinalIgnoreCase)
        {
            // chmod/chown/chgrp: first positional is the mode/user/group,
            // remainder are paths.
            ["chmod"] = i => i >= 1,
            ["chown"] = i => i >= 1,
            ["chgrp"] = i => i >= 1,

            // ln: all positionals are paths (source then target — both
            // file-system locations).
            ["ln"] = _ => true,

            // find: i=0 is the search root path; i>=1 are predicate args
            // (-name, -type values, action specs). Marking predicate args
            // as paths would create false positives on the consumer side.
            ["find"] = i => i == 0,

            // grep / rg / sed / awk: first positional is the
            // pattern/script/program, rest are paths.
            ["grep"] = i => i >= 1,
            ["rg"] = i => i >= 1,
            ["sed"] = i => i >= 1,
            ["awk"] = i => i >= 1,

            // curl / wget: first positional is a URL (not a path). The
            // file-path comes from a flag-with-value (-o / -O), handled
            // separately by ValueOfFlagIsPath.
            ["curl"] = _ => false,
            ["wget"] = _ => false,

            // scp / rsync / sftp: all positionals are paths. Some are
            // remote (user@host:/path); we still mark them IsPath=true with
            // the resolver deciding Kind (Literal if it parses, DynamicSkip
            // otherwise).
            ["scp"] = _ => true,
            ["rsync"] = _ => true,
            ["sftp"] = _ => true,
        };

    /// <summary>
    /// Decide whether the i-th non-flag positional arg (0-based, counted
    /// <em>after</em> the verb chain) of <paramref name="verb"/> should be
    /// classified as a path.
    /// </summary>
    /// <param name="verb">Verb chain; only the first token drives the rule.</param>
    /// <param name="positionalIndex">0-based positional index among non-flag args.</param>
    /// <param name="token">The token text itself (used for the LooksLikePath fallback).</param>
    /// <returns>True when this slot is a path; false otherwise.</returns>
    internal static bool IsPositionalPathArg(VerbChain verb, int positionalIndex, string token)
    {
        if (verb is null || verb.Tokens is null || verb.Tokens.Count == 0)
        {
            // No verb (redirect-only clause). Fall back to the path-shape
            // heuristic on the token itself.
            return BashResolver.LooksLikePath(token ?? "");
        }

        var firstVerb = verb.Tokens[0];

        // Explicit override first (chmod / find / curl / ...).
        if (Overrides.TryGetValue(firstVerb, out var rule))
        {
            return rule(positionalIndex);
        }

        // FileVerb default: every non-flag positional is a path. This
        // covers cd / ls / cat / rm / cp / mv / mkdir / tar (per locked
        // interpretation #8) / etc.
        if (BashVerbs.FileVerbs.Contains(firstVerb))
        {
            return true;
        }

        // Non-FileVerb verb: fall back to the SPEC §8 LooksLikePath
        // heuristic on the token. Keeps `cmd /etc/foo` recognizing the path
        // even when the verb is unknown to our tables.
        return BashResolver.LooksLikePath(token ?? "");
    }

    /// <summary>
    /// Per-verb table of flag-value path classification. For a flag in
    /// <see cref="BashVerbs.FlagsWithValue"/>, the consumed value is a path
    /// only when this table says so. Verbs/flags not listed get the
    /// "value is not a path" default, consistent with the safety bias
    /// (locked interpretation #8).
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Verb, string Flag), bool>
        FlagValueIsPath = new Dictionary<(string Verb, string Flag), bool>(FlagKeyComparer.Instance)
        {
            // Git native options are case-sensitive: -c consumes a config
            // key/value while -C consumes a directory path.
            [("git", "-c")] = false,
            [("git", "-C")] = true,
            [("git", "--git-dir")] = true,
            [("git", "--work-tree")] = true,

            // curl: -o / --output is a file path; -d / --data is body text.
            [("curl", "-o")] = true,
            [("curl", "--output")] = true,
            [("curl", "-d")] = false,
            [("curl", "--data")] = false,

            // wget: -O / --output-document is the saved file path.
            [("wget", "-O")] = true,
            [("wget", "--output-document")] = true,

            // docker: -f / --file is the Dockerfile path. -v / --volume is
            // a colon-joined host:container literal per locked interpretation #8.
            [("docker", "-f")] = true,
            [("docker", "--file")] = true,
            [("docker", "-v")] = false,
            [("docker", "--volume")] = false,

            // tar: -f / --file is the archive path; -C / --directory is a
            // directory path.
            [("tar", "-f")] = true,
            [("tar", "--file")] = true,
            [("tar", "-C")] = true,
            [("tar", "--directory")] = true,
        };

    /// <summary>
    /// Whether the value following <paramref name="flag"/> for
    /// <paramref name="verb"/> should be classified as a path.
    /// </summary>
    internal static bool ValueOfFlagIsPath(string verb, string flag)
    {
        if (string.IsNullOrEmpty(verb) || string.IsNullOrEmpty(flag))
        {
            return false;
        }

        return FlagValueIsPath.TryGetValue((verb, flag), out var isPath) && isPath;
    }

    // ---------------------------------------------------------------- key comparer

    /// <summary>
    /// Verb keys retain the existing case-insensitive lookup; native flag
    /// spelling is ordinal because executables may assign different meanings
    /// to options that differ only by case.
    /// </summary>
    private sealed class FlagKeyComparer : IEqualityComparer<(string Verb, string Flag)>
    {
        internal static readonly FlagKeyComparer Instance = new();

        public bool Equals((string Verb, string Flag) x, (string Verb, string Flag) y) =>
            string.Equals(x.Verb, y.Verb, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Flag, y.Flag, StringComparison.Ordinal);

        public int GetHashCode((string Verb, string Flag) obj)
        {
            // Hash combination follows the mixed verb/flag comparison.
            // Avoid HashCode.Combine for netstandard2.0 parity.
            unchecked
            {
                var h1 = obj.Verb is null
                    ? 0
                    : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Verb);
                var h2 = obj.Flag is null
                    ? 0
                    : StringComparer.Ordinal.GetHashCode(obj.Flag);
                return (h1 * 397) ^ h2;
            }
        }
    }
}
