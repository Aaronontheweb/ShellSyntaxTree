// -----------------------------------------------------------------------
// <copyright file="BashVerbs.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Bash.Lexing;

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
    /// <para>
    /// CWD verbs are also FILE verbs (their target is a path), included
    /// here for closure so a single membership check suffices.
    /// </para>
    /// <para>
    /// Issue #27: this set ALSO acts as the "stop at 1-token verb chain"
    /// carveout in <c>BashCommandParser.ParseClauseSegment</c>. The
    /// greedy verb-chain heuristic would otherwise over-extract bare-name
    /// targets (<c>cat hello</c>, <c>bash myscript</c>, <c>ln src dst</c>)
    /// into the verb chain and lose the per-verb path-arg classification
    /// downstream consumers depend on for zone-gate evaluation.
    /// </para>
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
    /// Issue #27 / SPEC §6.1: returns <c>true</c> when <paramref name="token"/>
    /// has the shape of a CLI subcommand verb — a bare lowercase identifier
    /// containing only ASCII letters, digits, hyphens, dots, and underscores.
    /// Used to terminate the greedy verb-chain walk at the first token that
    /// looks like a value rather than another subcommand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape predicate is intentionally strict: leading ASCII lowercase
    /// letter, then only <c>[a-z0-9._-]</c>. This rejects flags (start with
    /// <c>-</c>), env-var refs (<c>$</c>), path-shapes (<c>/</c>, <c>\</c>,
    /// <c>~</c>), URLs (<c>:</c>), glob metachars (<c>*</c>, <c>?</c>,
    /// <c>[</c>), uppercase-starting tokens (user-named identifiers like
    /// migration names), and tokens that begin with a digit (numeric
    /// modes / version literals). It tolerates real subcommand shapes
    /// (<c>my-pod</c>, <c>s3</c>, <c>apt-get</c>, <c>python3.11</c>).
    /// </para>
    /// <para>
    /// Quoted strings are not verb-like even when their inner value would
    /// pass — quoting signals the user wanted the bytes as a literal
    /// value. The walk also rejects empty tokens and tokens longer than
    /// 64 characters as a defensive bound against pathological inputs.
    /// </para>
    /// </remarks>
    internal static bool IsVerbLikeToken(in BashToken token)
    {
        if (token.Kind != BashTokenKind.Word)
        {
            return false;
        }

        var v = token.Value;
        if (v.Length == 0 || v.Length > 64)
        {
            return false;
        }

        var first = v[0];
        if (!(first >= 'a' && first <= 'z'))
        {
            return false;
        }

        for (var i = 1; i < v.Length; i++)
        {
            var c = v[i];
            var ok =
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '-' || c == '.' || c == '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
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
