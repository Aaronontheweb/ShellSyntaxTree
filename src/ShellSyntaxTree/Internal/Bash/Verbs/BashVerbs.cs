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
    /// shape is identical. Native option spelling is case-sensitive even
    /// when the host shell is PowerShell; the outer command lookup remains
    /// case-insensitive for the existing verb-table contract.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, HashSet<string>>
        FlagsWithValue = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "-c", "-C", "--git-dir", "--work-tree",
            },
            ["curl"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "-o", "--output", "-d", "--data",
            },
            ["wget"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "-O", "--output-document",
            },
            ["docker"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "-v", "--volume", "-f", "--file",
            },
            ["tar"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "-f", "--file", "-C", "--directory",
            },
        };

    /// <summary>
    /// SPEC §6.1: returns <c>true</c> when <paramref name="token"/> has the
    /// shape of a CLI subcommand verb — a bare lowercase identifier
    /// containing only ASCII letters, digits, hyphens, dots, and underscores.
    /// Used to terminate the greedy verb-chain walk at the first token that
    /// looks like a value rather than another subcommand.
    /// </summary>
    /// <remarks>
    /// Strict allow-list (leading <c>[a-z]</c>, body <c>[a-z0-9._-]</c>)
    /// remains independent from path classification. The caller applies
    /// <c>BashResolver.LooksLikePath</c> first so a token such as
    /// <c>readme.md</c> remains an argument. Quoted strings are excluded so
    /// the user's intent to treat bytes literally is preserved. The
    /// 64-char bound is a defensive cap against pathological inputs.
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
