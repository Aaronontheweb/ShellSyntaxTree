// -----------------------------------------------------------------------
// <copyright file="BashVerbs.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Bash.Verbs;

/// <summary>
/// Static, case-insensitive verb data per SPEC §6 / §7. These are *data*,
/// not logic — the parser probes them and applies the rules described in
/// the SPEC. Centralizing the tables here keeps the parser readable and
/// keeps reviewers' eyes on a single file when curating verb knowledge.
/// </summary>
internal static class BashVerbs
{
    /// <summary>
    /// How many tokens form the verb chain for known commands. Defaults to
    /// 1 when not in the table. SPEC §6.1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per the SPEC §6.1 implementation note, the parser must look up
    /// multi-token verbs by joining the first 1, 2, then 3 tokens and
    /// probing the table from <em>longest to shortest</em>. So
    /// <c>docker compose up nginx</c> first probes <c>docker compose up</c>
    /// (not in table), then <c>docker compose</c> (arity 3 — match). The
    /// matched key's value <em>is</em> the verb chain length, so e.g.
    /// <c>docker compose</c>'s value of 3 means "consume three source
    /// tokens as the verb chain."
    /// </para>
    /// <para>
    /// The table is non-exhaustive. Verbs not listed default to a 1-token
    /// chain. Add entries as the corpus surfaces real commands.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, int> BashArity =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Two-token verbs.
            ["git"] = 2,
            ["dotnet"] = 2,
            ["npm"] = 2,
            ["yarn"] = 2,
            ["pnpm"] = 2,
            ["cargo"] = 2,
            ["go"] = 2,
            ["kubectl"] = 2,
            ["helm"] = 2,
            ["systemctl"] = 2,
            ["service"] = 2,
            ["pip"] = 2,
            ["pip3"] = 2,
            ["brew"] = 2,
            ["apt"] = 2,
            ["apt-get"] = 2,
            ["yum"] = 2,
            ["dnf"] = 2,
            ["pacman"] = 2,
            ["aws"] = 2,
            ["gcloud"] = 2,
            ["az"] = 2,
            ["docker"] = 2,
            ["docker-compose"] = 2,
            ["bun"] = 2,
            ["nuget"] = 2,

            // Three-token verbs.
            ["docker compose"] = 3,
            ["bun run"] = 3,
        };

    /// <summary>
    /// Verbs whose first non-flag positional arg becomes the cwd for
    /// subsequent clauses in the same compound. SPEC §6.2.
    /// </summary>
    /// <remarks>
    /// <c>push-location</c> / <c>set-location</c> are PowerShell idioms
    /// listed for forward-compat; v0.1 only emits attribution for
    /// <c>cd</c> / <c>chdir</c> per locked interpretation #5.
    /// </remarks>
    internal static readonly HashSet<string> CwdVerbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cd",
            "chdir",
            "popd",
            "pushd",
            "push-location",
            "set-location",
        };

    /// <summary>
    /// Verbs whose positional args are paths. The default extraction rule
    /// is "all non-flag positional args after the verb chain are paths,"
    /// modulo per-verb overrides in SPEC §7. SPEC §6.3.
    /// </summary>
    /// <remarks>
    /// CWD verbs are also FILE verbs (their target is a path), included
    /// here for closure so a single membership check suffices.
    /// </remarks>
    internal static readonly HashSet<string> FileVerbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // CWD verbs (also file verbs).
            "cd", "chdir", "popd", "pushd", "push-location", "set-location",

            // File mutation.
            "rm", "cp", "mv", "mkdir", "rmdir", "touch", "ln",
            "chmod", "chown", "chgrp", "stat", "test",

            // Read.
            "cat", "less", "more", "head", "tail", "grep", "rg",
            "find", "fd", "locate", "wc", "file",

            // Editors / text tools.
            "sed", "awk", "vi", "vim", "nano", "emacs", "ed",

            // Compression.
            "tar", "zip", "unzip", "gzip", "gunzip", "bzip2", "xz",

            // Network with file targets.
            "curl", "wget", "scp", "rsync", "sftp",

            // Shell / interpreter loaders.
            "bash", "sh", "zsh", "fish",
            "python", "python3", "node", "ruby", "perl", "php",

            // Diff / patch.
            "diff", "patch", "cmp",

            // Listing.
            "ls", "dir", "tree",
        };

    /// <summary>
    /// Per-verb table of flags whose <em>next</em> token is consumed as the
    /// flag's value. Per SPEC §7, the equals form
    /// (<c>--output=file</c>) is the same logical case but realized
    /// differently in the lexer; the parser splits on <c>=</c> and emits
    /// two args.
    /// </summary>
    /// <remarks>
    /// Value type is <see cref="HashSet{T}"/> rather than
    /// <c>IReadOnlySet&lt;string&gt;</c> for netstandard2.0 parity —
    /// <c>IReadOnlySet&lt;T&gt;</c> ships in net5+ only. Internally the
    /// shape is identical (case-insensitive set lookup).
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, HashSet<string>>
        FlagsWithValue = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "-C", "--git-dir", "--work-tree",
            },
            ["curl"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "-o", "--output", "-d", "--data",
            },
            ["wget"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "-O", "--output-document",
            },
            ["docker"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "-v", "--volume", "-f", "--file",
            },
            ["tar"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "-f", "--file", "-C", "--directory",
            },
        };

    /// <summary>
    /// Resolve the verb-chain length for a token sequence at <paramref name="start"/>.
    /// Implements the longest-prefix probe from SPEC §6.1: try the first 3
    /// tokens, then 2, then 1, returning the matching arity.
    /// </summary>
    /// <param name="tokens">Source-order list of verb-candidate tokens.</param>
    /// <param name="start">Index into <paramref name="tokens"/> where the verb chain begins.</param>
    /// <returns>
    /// 1, 2, or 3 — the number of tokens that form the verb chain.
    /// Defaults to 1 when no match is found (per SPEC §6.1) or when fewer
    /// than the probed-prefix length tokens remain.
    /// </returns>
    internal static int ProbeArity(IReadOnlyList<string> tokens, int start)
    {
        var available = tokens.Count - start;
        if (available <= 0)
        {
            return 0;
        }

        // Three-token probe.
        if (available >= 3)
        {
            var key3 = tokens[start] + " " + tokens[start + 1] + " " + tokens[start + 2];
            if (BashArity.TryGetValue(key3, out var arity3) && arity3 == 3)
            {
                return 3;
            }
        }

        // Two-token probe.
        if (available >= 2)
        {
            var key2 = tokens[start] + " " + tokens[start + 1];
            if (BashArity.TryGetValue(key2, out var arity2))
            {
                return Math.Min(arity2, available);
            }
        }

        // Single-token probe (default arity 1).
        if (BashArity.TryGetValue(tokens[start], out var arity1))
        {
            return Math.Min(arity1, available);
        }

        return 1;
    }

    /// <summary>
    /// Control-flow keywords that v0.1 explicitly does not parse. Per SPEC
    /// §11, encountering one as a clause's verb sets
    /// <c>ParsedCommand.IsUnparseable = true</c>.
    /// </summary>
    internal static readonly HashSet<string> ControlFlowKeywords =
        new(StringComparer.Ordinal)
        {
            "for", "while", "until", "do", "done",
            "if", "then", "elif", "else", "fi",
            "case", "esac", "select", "in",
            "function",
        };
}
