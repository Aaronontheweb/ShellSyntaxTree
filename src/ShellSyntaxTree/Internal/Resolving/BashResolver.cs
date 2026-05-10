// -----------------------------------------------------------------------
// <copyright file="BashResolver.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.IO;
using System.Text;

namespace ShellSyntaxTree.Internal.Resolving;

/// <summary>
/// Path-token resolver for the bash parser. Implements SPEC §8 — tilde
/// expansion, the lone <c>$HOME</c> expansion, <c>filesystem::</c> prefix
/// stripping, glob detection, env-var detection (DynamicSkip), and
/// absolute-path normalization against
/// <see cref="BashParserOptions.WorkingDirectory"/>.
/// </summary>
/// <remarks>
/// <para>
/// The resolver is intentionally split from <c>BashPerVerbRules</c> — the
/// per-verb rule decides <em>whether</em> the slot is a path; the resolver
/// decides <em>what value</em> to attribute to it. Callers pass the
/// already-decided <c>treatAsPath</c> bit and get back a tuple the parser
/// can fold straight into an <see cref="Arg"/>.
/// </para>
/// <para>
/// Failure modes bias toward DynamicSkip per the SPEC §8 step 6 safety
/// rule: when in doubt, surface "unknown value" rather than a misclassified
/// literal.
/// </para>
/// </remarks>
internal static class BashResolver
{
    /// <summary>
    /// Curated set of file-extension suffixes that mark a token as
    /// path-shaped even without a slash. SPEC §8 LooksLikePath heuristic.
    /// Lowercase; check via <see cref="string.EndsWith(string, StringComparison)"/>.
    /// </summary>
    private static readonly string[] PathExtensions =
    {
        // Single-segment extensions (curated set; SPEC §8).
        ".json", ".md", ".txt", ".conf", ".yml", ".yaml", ".toml",
        ".xml", ".ini", ".log",
        ".sh", ".py", ".rb", ".js", ".ts", ".cs", ".go", ".rs",
        ".java", ".html", ".css",

        // Multi-segment archive suffixes (handled via lowercased EndsWith).
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".zip",
    };

    /// <summary>
    /// Classify and (where possible) resolve a token in a path-arg slot.
    /// </summary>
    /// <param name="raw">
    /// Verbatim token slice with outer quote delimiters already stripped by
    /// the caller. Multi-byte values are passed through unchanged.
    /// </param>
    /// <param name="treatAsPath">
    /// True when the per-verb rule (or caller-side discovery) classifies this
    /// slot as a path. Drives the difference between Glob-with-IsPath and
    /// EnvVar/Literal-without-IsPath.
    /// </param>
    /// <param name="options">Parser options for HomeDirectory + WorkingDirectory.</param>
    /// <returns>
    /// A (Kind, Resolved, IsPath) tuple the caller drops directly into
    /// <see cref="Arg.Kind"/>, <see cref="Arg.Resolved"/>, and
    /// <see cref="Arg.IsPath"/>.
    /// </returns>
    internal static (ArgKind Kind, string? Resolved, bool IsPath) Resolve(
        string raw, bool treatAsPath, BashParserOptions options)
    {
        if (raw is null)
        {
            // Defensive — public surface guarantees Arg.Raw is non-null.
            // Treat as a literal empty token.
            return (ArgKind.Literal, null, false);
        }

        // Step 1: filesystem::/path prefix stripping. Some agent tools emit
        // `filesystem::/path/to/file` (e.g. MCP filesystem servers). Strip
        // the prefix and continue with the remainder. We *do not* set a
        // separate Kind for this — it's purely a normalization step.
        var working = raw;
        if (working.StartsWith("filesystem::", StringComparison.Ordinal))
        {
            working = working.Substring("filesystem::".Length);
        }

        // Step 2: tilde expansion. `~` alone, `~/path`, and `~user/path` all
        // begin with '~'. The tilde itself must be the leading char (bash
        // semantics): `foo~/bar` is *not* a tilde expansion.
        var startsWithTilde = working.Length > 0 && working[0] == '~';
        var hadTilde = false;

        if (startsWithTilde)
        {
            // ~user (other-user expansion). Two flavors: `~bob` and `~bob/x`.
            // v0.1 doesn't support these.
            if (working.Length > 1 && working[1] != '/' && working[1] != '\\')
            {
                // `~bob` or `~bob/...` — unsupported. SPEC §8 step 1.
                if (treatAsPath)
                {
                    return (ArgKind.DynamicSkip, null, false);
                }

                return (ArgKind.Tilde, null, false);
            }

            // `~` alone or `~/path` — expand.
            var home = GetHomeDirectory(options);
            if (working.Length == 1)
            {
                working = home;
            }
            else
            {
                // Drop the leading `~` and join the rest (which starts with
                // '/' or '\') to home.
                var rest = working.Substring(2); // skip "~/" or "~\\"
                working = JoinPath(home, rest);
            }

            hadTilde = true;
        }

        // Step 3: $HOME / ${HOME} substitution — the *only* env var we
        // expand. Replace literal occurrences anywhere in the token. We
        // expand first so the resulting working string carries the literal
        // home path; subsequent env-var detection sees no $HOME because
        // it's gone. Note: SPEC §8 step 2 says $HOME is the only exception.
        working = SubstituteHome(working, options, out var hadHome);
        if (hadHome)
        {
            hadTilde = true; // Reuse the "expanded a home-ish token" branch — same Kind=Tilde semantics.
        }

        // Step 4: other env-var detection ($VAR or ${VAR} that isn't HOME).
        // Locked interpretation #3 / SPEC §12 `rm $UNRESOLVED/foo` example.
        if (ContainsEnvVarReference(working))
        {
            if (treatAsPath)
            {
                // Path slot: cannot safely resolve — DynamicSkip with no path
                // signal so the consumer doesn't iterate it.
                return (ArgKind.DynamicSkip, null, false);
            }

            // Non-path slot: EnvVar Kind, IsPath=false. The token carries
            // information the consumer may want to surface, just not a path.
            return (ArgKind.EnvVar, null, false);
        }

        // Step 5: glob detection. SPEC §8 step 4: tokens containing '*',
        // '?', or '[' get Kind=Glob. The covering-directory heuristic from
        // locked interpretation #3 puts IsPath=true in a path slot so the
        // consumer keeps the signal; non-path slots stay IsPath=false.
        if (ContainsGlobMetacharacters(working))
        {
            return (ArgKind.Glob, null, treatAsPath);
        }

        // Step 6 + 7: literal path resolution (if treatAsPath), else literal.
        if (!treatAsPath)
        {
            // Non-path slot: literal token, no resolution. If we expanded a
            // tilde the Kind is Tilde (so consumers can detect the expansion
            // happened); otherwise plain Literal. Both with IsPath=false.
            return (hadTilde ? ArgKind.Tilde : ArgKind.Literal, null, false);
        }

        // Path slot: try to normalize to an absolute path.
        var resolved = TryResolveAbsolutePath(working, options);
        if (resolved is null)
        {
            // Resolution failed (IOException / ArgumentException / format) —
            // SPEC §8 step 6 says emit DynamicSkip rather than guess.
            return (ArgKind.DynamicSkip, null, false);
        }

        return (hadTilde ? ArgKind.Tilde : ArgKind.Literal, resolved, true);
    }

    /// <summary>
    /// SPEC §8 LooksLikePath heuristic. Used to fall back when no per-verb
    /// rule applies. Conservative — when a token "looks like a path" we run
    /// it through the resolver; when it doesn't, we leave it as a plain
    /// Literal.
    /// </summary>
    internal static bool LooksLikePath(string token)
    {
        if (token is null || token.Length == 0)
        {
            return false;
        }

        // Unix absolute or root.
        if (token[0] == '/')
        {
            return true;
        }

        // Windows UNC (\\server\share) or rooted path with backslash.
        if (token[0] == '\\')
        {
            return true;
        }

        // Windows drive letter (X:\foo or X:foo).
        if (token.Length >= 2 && IsAsciiLetter(token[0]) && token[1] == ':')
        {
            return true;
        }

        // Unix-relative shorthands.
        if (token.StartsWith("./", StringComparison.Ordinal)
            || token.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        // Tilde — bash home reference.
        if (token[0] == '~')
        {
            return true;
        }

        // Any directory separator.
        if (token.IndexOf('/') >= 0 || token.IndexOf('\\') >= 0)
        {
            return true;
        }

        // File-extension suffix match (case-insensitive).
        var lower = token.ToLowerInvariant();
        foreach (var ext in PathExtensions)
        {
            if (lower.EndsWith(ext, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- helpers

    private static string GetHomeDirectory(BashParserOptions options)
    {
        if (!string.IsNullOrEmpty(options.HomeDirectory))
        {
            return options.HomeDirectory!;
        }

        // Lazy fallback. SPEC §2 / §8: defaults to UserProfile. May be the
        // empty string in pathological environments — callers tolerate that
        // because JoinPath / Path.GetFullPath fall back accordingly.
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string GetWorkingDirectory(BashParserOptions options)
    {
        if (!string.IsNullOrEmpty(options.WorkingDirectory))
        {
            return options.WorkingDirectory!;
        }

        return Environment.CurrentDirectory;
    }

    /// <summary>
    /// Replace literal <c>$HOME</c> and <c>${HOME}</c> occurrences in
    /// <paramref name="input"/> with the configured home directory. Sets
    /// <paramref name="hadHome"/> when at least one replacement occurred.
    /// </summary>
    private static string SubstituteHome(string input, BashParserOptions options, out bool hadHome)
    {
        hadHome = false;
        if (input.Length == 0 || input.IndexOf('$') < 0)
        {
            return input;
        }

        string? home = null;
        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (c == '$' && i + 1 < input.Length)
            {
                // ${HOME}
                if (input[i + 1] == '{')
                {
                    var close = input.IndexOf('}', i + 2);
                    if (close > 0)
                    {
                        var name = input.Substring(i + 2, close - (i + 2));
                        if (string.Equals(name, "HOME", StringComparison.Ordinal))
                        {
                            home ??= GetHomeDirectory(options);
                            sb.Append(home);
                            hadHome = true;
                            i = close + 1;
                            continue;
                        }
                    }
                }
                else if (TryReadIdentifier(input, i + 1, out var end, out var name))
                {
                    if (string.Equals(name, "HOME", StringComparison.Ordinal))
                    {
                        home ??= GetHomeDirectory(options);
                        sb.Append(home);
                        hadHome = true;
                        i = end;
                        continue;
                    }
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Detect any remaining <c>$VAR</c> or <c>${VAR}</c> reference. Caller
    /// should have already substituted <c>$HOME</c>; any survivor here is
    /// an unresolved-and-not-HOME env var.
    /// </summary>
    private static bool ContainsEnvVarReference(string input)
    {
        if (input.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c != '$' || i + 1 >= input.Length)
            {
                continue;
            }

            var next = input[i + 1];
            if (next == '{')
            {
                // ${...} — any non-empty body counts. (Empty ${} is a bash
                // error, but we don't validate that here.)
                if (i + 2 < input.Length && input[i + 2] != '}')
                {
                    return true;
                }
            }
            else if (IsIdentifierStart(next))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsGlobMetacharacters(string input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '*' || c == '?' || c == '[')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadIdentifier(string input, int start, out int end, out string name)
    {
        if (start >= input.Length || !IsIdentifierStart(input[start]))
        {
            end = start;
            name = "";
            return false;
        }

        var j = start;
        while (j < input.Length && IsIdentifierContinuation(input[j]))
        {
            j++;
        }

        end = j;
        name = input.Substring(start, j - start);
        return true;
    }

    private static bool IsIdentifierStart(char c) =>
        c == '_' || IsAsciiLetter(c);

    private static bool IsIdentifierContinuation(char c) =>
        c == '_' || IsAsciiLetter(c) || (c >= '0' && c <= '9');

    private static bool IsAsciiLetter(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>
    /// Combine a base directory with a relative or rooted sub-path. Cross-
    /// platform-safe: uses <see cref="Path.Combine(string, string)"/> and
    /// strips a single leading separator from the sub-path so the combine
    /// doesn't treat the sub-path as rooted on Windows.
    /// </summary>
    private static string JoinPath(string baseDir, string sub)
    {
        if (string.IsNullOrEmpty(sub))
        {
            return baseDir;
        }

        // Path.Combine will discard baseDir if `sub` looks rooted. For the
        // tilde-expansion case the sub came from `~/rest` so it starts with
        // `/` or `\` — strip exactly one leading separator before combining.
        var s = sub;
        if (s.Length > 0 && (s[0] == '/' || s[0] == '\\'))
        {
            s = s.Substring(1);
        }

        return Path.Combine(baseDir, s);
    }

    /// <summary>
    /// Resolve <paramref name="token"/> to an absolute path against the
    /// supplied options. Returns null on resolution failure (SPEC §8 step 6).
    /// </summary>
    private static string? TryResolveAbsolutePath(string token, BashParserOptions options)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            if (IsRootedPath(token))
            {
                // Path.GetFullPath normalizes redundant separators / dot-segments
                // without touching the filesystem.
                return Path.GetFullPath(token);
            }

            var wd = GetWorkingDirectory(options);
            if (string.IsNullOrEmpty(wd))
            {
                // No working directory available — surface as DynamicSkip
                // rather than guess.
                return null;
            }

            var combined = Path.Combine(wd, token);
            return Path.GetFullPath(combined);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            // Path.GetFullPath throws this for invalid path characters on
            // some runtimes — treat the same as ArgumentException.
            return null;
        }
    }

    /// <summary>
    /// Check whether a path is "rooted" — absolute Unix, Windows UNC, or
    /// Windows drive-letter — without invoking <c>Path.IsPathRooted</c>,
    /// which is platform-aware and would, e.g., treat <c>/foo</c> as
    /// non-rooted on Windows for the purposes of bash path semantics.
    /// </summary>
    private static bool IsRootedPath(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        // Unix absolute.
        if (token[0] == '/')
        {
            return true;
        }

        // Windows UNC.
        if (token.Length >= 2 && token[0] == '\\' && token[1] == '\\')
        {
            return true;
        }

        // Windows drive letter X: (with or without trailing separator).
        if (token.Length >= 2 && IsAsciiLetter(token[0]) && token[1] == ':')
        {
            return true;
        }

        return false;
    }
}
