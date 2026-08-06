// -----------------------------------------------------------------------
// <copyright file="PwshResolver.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.IO;
using System.Text;

namespace ShellSyntaxTree.Internal.Resolving;

/// <summary>
/// Path-token resolver for the PowerShell parser (SPEC.POWERSHELL.md §8).
/// Parallels <see cref="BashResolver"/>: the resolution doctrine — the
/// single-quote bypass, <c>DynamicSkip</c> for anything not statically
/// knowable, the <c>(ArgKind, Resolved, IsPath)</c> result — is the same.
/// PowerShell adds <c>$env:USERPROFILE</c> home expansion, provider-qualifier
/// stripping, drive-qualified and non-FileSystem-PSDrive handling.
/// </summary>
internal static class PwshResolver
{
    private static readonly string[] ProviderQualifiers =
    {
        "Microsoft.PowerShell.Core\\FileSystem::",
        "FileSystem::",
    };

    /// <summary>
    /// Classify and (where possible) resolve a token in a path-arg slot.
    /// </summary>
    /// <param name="raw">Verbatim token slice, quote delimiters stripped.</param>
    /// <param name="treatAsPath">True when the §7 rule classifies the slot
    /// as a path.</param>
    /// <param name="options">Resolver knobs (home / working directory).</param>
    /// <param name="workingDirectoryUnknown">True when a dynamic
    /// <c>Set-Location</c> made the cwd statically unknown (§9).</param>
    /// <param name="isLiteralBytes">True when the token came from a
    /// single-quoted string or an <c>@'...'@</c> here-string — its bytes
    /// are literal (SPEC.POWERSHELL.md §8 step 0).</param>
    internal static (ArgKind Kind, string? Resolved, bool IsPath) Resolve(
        string raw,
        bool treatAsPath,
        ShellParserOptions options,
        bool workingDirectoryUnknown,
        bool isLiteralBytes)
    {
        if (raw is null)
        {
            return (ArgKind.Literal, null, false);
        }

        // Step 0: single-quote / literal here-string bypass.
        if (isLiteralBytes)
        {
            if (!treatAsPath)
            {
                return (ArgKind.Literal, null, false);
            }

            var literalResolved = TryResolveAbsolutePath(raw, options, workingDirectoryUnknown);
            return literalResolved is null
                ? (ArgKind.DynamicSkip, null, false)
                : (ArgKind.Literal, literalResolved, true);
        }

        var working = raw;
        var hadHomeish = false;

        // Step 1: ~ expansion.
        if (working.Length > 0 && working[0] == '~')
        {
            if (working.Length > 1 && working[1] != '/' && working[1] != '\\')
            {
                // ~user — unsupported.
                return treatAsPath
                    ? (ArgKind.DynamicSkip, null, false)
                    : (ArgKind.Tilde, null, false);
            }

            var home = GetHomeDirectory(options);
            working = working.Length == 1
                ? home
                : ShellPathNormalization.Join(home, working.Substring(2));
            hadHomeish = true;
        }

        // Step 2: home-variable substitution + other-variable detection.
        working = SubstituteHomeVariables(working, options, out var hadHomeVar);
        if (hadHomeVar)
        {
            hadHomeish = true;
        }

        if (ContainsDynamicReference(working))
        {
            // $PSScriptRoot, $var, $env:NAME, ${name} — not statically
            // knowable. Path slot → DynamicSkip; non-path slot → EnvVar.
            return treatAsPath
                ? (ArgKind.DynamicSkip, null, false)
                : (ArgKind.EnvVar, null, false);
        }

        // Step 3: provider-qualifier stripping.
        foreach (var qualifier in ProviderQualifiers)
        {
            if (working.StartsWith(qualifier, StringComparison.OrdinalIgnoreCase))
            {
                working = working.Substring(qualifier.Length);
                break;
            }
        }

        // Step 4: drive-qualified paths and non-FileSystem PSDrives. A
        // single-ASCII-letter drive (C:\, d:/foo) is a rooted filesystem
        // path. Any qualifier longer than one letter — HKLM:, Env:, Cert:,
        // a custom PSDrive — is not a filesystem path (SPEC.POWERSHELL.md
        // §8 step 4): classify Literal, IsPath=false.
        var colon = working.IndexOf(':');
        if (colon > 1 && IsDriveQualifier(working, colon))
        {
            return (ArgKind.Literal, null, false);
        }

        // Step 6: glob detection.
        if (ContainsGlobMetacharacters(working))
        {
            return (ArgKind.Glob, null, treatAsPath);
        }

        // Steps 5 + 7: literal path resolution (drive-qualified, UNC,
        // relative) or a plain literal.
        if (!treatAsPath)
        {
            return (hadHomeish ? ArgKind.Tilde : ArgKind.Literal, null, false);
        }

        var resolved = TryResolveAbsolutePath(working, options, workingDirectoryUnknown);
        if (resolved is null)
        {
            return (ArgKind.DynamicSkip, null, false);
        }

        return (hadHomeish ? ArgKind.Tilde : ArgKind.Literal, resolved, true);
    }

    /// <summary>
    /// Resolve a lexer-proved value under an explicit PowerShell consumer.
    /// Provider and PSDrive semantics are deliberately absent from native
    /// arguments and applied only by cmdlet path and redirect consumers.
    /// </summary>
    internal static (ArgKind Kind, string? Resolved, bool IsPath) Resolve(
        ShellValue value,
        bool treatAsPath,
        ShellParserOptions options,
        bool workingDirectoryUnknown,
        ShellResolutionConsumer consumer)
    {
        if (consumer is not ShellResolutionConsumer.PowerShellNativeArgument
            and not ShellResolutionConsumer.PowerShellCmdletPath
            and not ShellResolutionConsumer.PowerShellCmdletLiteralPath
            and not ShellResolutionConsumer.PowerShellRedirect)
        {
            throw new ArgumentOutOfRangeException(nameof(consumer));
        }

        if (treatAsPath && value.Decoded.Length == 0)
        {
            return (ArgKind.DynamicSkip, null, false);
        }

        var composed = new StringBuilder(value.Decoded.Length);
        var hadHomeish = false;
        var hasLexicalGlob = false;
        var expandableStringContextProven = false;
        for (var fragmentIndex = 0; fragmentIndex < value.Fragments.Count; fragmentIndex++)
        {
            var fragment = value.Fragments[fragmentIndex];
            if (fragment.Kind == ShellValueFragmentKind.Literal)
            {
                composed.Append(fragment.Value);
                expandableStringContextProven = true;
                continue;
            }

            if (fragment.Kind == ShellValueFragmentKind.Opaque
                || fragment.Expansion is null)
            {
                if (fragment.Kind == ShellValueFragmentKind.Opaque
                    && fragment.OpaqueCause == ShellOpaqueCause.PowerShellExpressionSuffix
                    && expandableStringContextProven)
                {
                    composed.Append(fragment.Value);
                    continue;
                }

                return (ArgKind.DynamicSkip, null, false);
            }

            var expansion = fragment.Expansion.Value;
            switch (expansion.Kind)
            {
                case ShellExpansionKind.Variable:
                case ShellExpansionKind.SpecialParameter:
                case ShellExpansionKind.PositionalParameter:
                    if (!IsHomeVariable(expansion.Name)
                        || (fragment.AllowedTransforms & ShellLexicalTransform.Variable) == 0)
                    {
                        return treatAsPath
                            ? (ArgKind.DynamicSkip, null, false)
                            : (ArgKind.EnvVar, null, false);
                    }

                    composed.Append(GetHomeDirectory(options));
                    hadHomeish = true;
                    break;

                case ShellExpansionKind.Tilde:
                    if (consumer == ShellResolutionConsumer.PowerShellNativeArgument
                        && fragmentIndex == 0
                        && (fragment.AllowedTransforms & ShellLexicalTransform.Tilde) != 0)
                    {
                        if (value.Decoded.Length > 1
                            && value.Decoded[1] != '/'
                            && value.Decoded[1] != '\\')
                        {
                            return treatAsPath
                                ? (ArgKind.DynamicSkip, null, false)
                                : (ArgKind.Tilde, null, false);
                        }

                        composed.Append(GetHomeDirectory(options).TrimEnd('/', '\\'));
                        hadHomeish = true;
                    }
                    else
                    {
                        composed.Append(fragment.Value);
                    }

                    break;

                case ShellExpansionKind.Glob:
                    composed.Append(fragment.Value);
                    hasLexicalGlob = true;
                    break;

                case ShellExpansionKind.ArraySeparator:
                    return (ArgKind.DynamicSkip, null, false);

                default:
                    return (ArgKind.DynamicSkip, null, false);
            }
        }

        var working = composed.ToString();
        var pathLikeConsumer = consumer is ShellResolutionConsumer.PowerShellCmdletPath
            or ShellResolutionConsumer.PowerShellCmdletLiteralPath
            or ShellResolutionConsumer.PowerShellRedirect;
        if (pathLikeConsumer && working.Length > 0 && working[0] == '~')
        {
            if (working.Length > 1 && working[1] != '/' && working[1] != '\\')
            {
                return (ArgKind.DynamicSkip, null, false);
            }

            var home = GetHomeDirectory(options).TrimEnd('/', '\\');
            working = home + working.Substring(1);
            hadHomeish = true;
        }

        if (pathLikeConsumer)
        {
            var fileSystemProviderProven = false;
            foreach (var qualifier in ProviderQualifiers)
            {
                if (working.StartsWith(qualifier, StringComparison.OrdinalIgnoreCase))
                {
                    working = working.Substring(qualifier.Length);
                    fileSystemProviderProven = true;
                    break;
                }
            }

            var colon = working.IndexOf(':');
            if (colon >= 1 && IsDriveQualifier(working, colon))
            {
                var driveName = working.Substring(0, colon);
                if (driveName.Length == 1)
                {
                    if (!fileSystemProviderProven
                        && !IsProvedFileSystemDrive(driveName, options))
                    {
                        return (ArgKind.DynamicSkip, null, false);
                    }
                }
                else if (IsKnownNonFileSystemDrive(driveName)
                    && consumer != ShellResolutionConsumer.PowerShellRedirect)
                {
                    return (ArgKind.Literal, null, false);
                }
                else if (driveName.Length > 1)
                {
                    return (ArgKind.DynamicSkip, null, false);
                }
            }
        }

        var hasPostFormationGlob = pathLikeConsumer
            && working.IndexOfAny(new[] { '*', '?', '[' }) >= 0;
        if (consumer == ShellResolutionConsumer.PowerShellRedirect
            && (hasLexicalGlob || hasPostFormationGlob))
        {
            return (ArgKind.DynamicSkip, null, false);
        }

        if (consumer == ShellResolutionConsumer.PowerShellCmdletPath
            && (hasLexicalGlob || hasPostFormationGlob))
        {
            return (ArgKind.Glob, null, treatAsPath);
        }

        if (consumer == ShellResolutionConsumer.PowerShellNativeArgument
            && hasLexicalGlob)
        {
            return (ArgKind.Glob, null, treatAsPath);
        }

        if (!treatAsPath)
        {
            return (hadHomeish ? ArgKind.Tilde : ArgKind.Literal, null, false);
        }

        var resolved = TryResolveAbsolutePath(
            working, options, workingDirectoryUnknown);
        return resolved is null
            ? (ArgKind.DynamicSkip, null, false)
            : (hadHomeish ? ArgKind.Tilde : ArgKind.Literal, resolved, true);
    }

    private static bool IsHomeVariable(string? name) =>
        string.Equals(name, "HOME", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "env:USERPROFILE", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownNonFileSystemDrive(string name) =>
        string.Equals(name, "Alias", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Cert", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Env", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Function", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "HKCU", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "HKLM", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Variable", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "WSMan", StringComparison.OrdinalIgnoreCase);

    private static bool IsProvedFileSystemDrive(
        string driveName, ShellParserOptions options) =>
        HasDrive(options.WorkingDirectory, driveName)
        || HasDrive(options.HomeDirectory, driveName);

    private static bool HasDrive(string? path, string driveName) =>
        !string.IsNullOrEmpty(path)
        && path!.Length >= 2
        && path[1] == ':'
        && string.Equals(path.Substring(0, 1), driveName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDriveRelativePath(string path) =>
        path.Length >= 2
        && IsAsciiLetter(path[0])
        && path[1] == ':'
        && (path.Length == 2 || (path[2] != '/' && path[2] != '\\'));

    /// <summary>
    /// True when <paramref name="token"/> contains an unquoted top-level
    /// comma — PowerShell's array operator (SPEC.POWERSHELL.md §8). A
    /// comma-array path token is marked <c>DynamicSkip</c>; the parser
    /// checks this before calling <see cref="Resolve"/>.
    /// </summary>
    internal static bool LooksLikeCommaArray(string token) =>
        !string.IsNullOrEmpty(token) && token.IndexOf(',') >= 0;

    // ---------------------------------------------------------------- helpers

    private static string GetHomeDirectory(ShellParserOptions options)
    {
        if (!string.IsNullOrEmpty(options.HomeDirectory))
        {
            return options.HomeDirectory!;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string GetWorkingDirectory(ShellParserOptions options)
    {
        if (!string.IsNullOrEmpty(options.WorkingDirectory))
        {
            return options.WorkingDirectory!;
        }

        return Environment.CurrentDirectory;
    }

    /// <summary>
    /// Replace <c>$HOME</c>, <c>${HOME}</c>, <c>$env:USERPROFILE</c>, and
    /// <c>${env:USERPROFILE}</c> with the configured home directory. Only
    /// the home variables are privileged (SPEC.POWERSHELL.md §8 step 2).
    /// </summary>
    private static string SubstituteHomeVariables(
        string input, ShellParserOptions options, out bool hadHome)
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
            if (input[i] == '$' && TryMatchHomeVariable(input, i, out var consumed))
            {
                home ??= GetHomeDirectory(options);
                sb.Append(home);
                hadHome = true;
                i += consumed;
                continue;
            }

            sb.Append(input[i]);
            i++;
        }

        return sb.ToString();
    }

    // Recognized home-variable forms: $HOME / ${HOME} / $env:USERPROFILE /
    // ${env:USERPROFILE}. The braced forms are self-delimiting; the bare
    // forms must not run into an adjacent identifier char.
    private static readonly string[] BracedHomeForms = { "${HOME}", "${env:USERPROFILE}" };

    private static readonly string[] BareHomeForms = { "$env:USERPROFILE", "$HOME" };

    private static bool TryMatchHomeVariable(string input, int i, out int consumed)
    {
        foreach (var form in BracedHomeForms)
        {
            if (Matches(input, i, form))
            {
                consumed = form.Length;
                return true;
            }
        }

        foreach (var form in BareHomeForms)
        {
            if (Matches(input, i, form)
                && !IsIdentifierContinuation(CharAt(input, i + form.Length)))
            {
                consumed = form.Length;
                return true;
            }
        }

        consumed = 0;
        return false;
    }

    private static bool Matches(string input, int i, string form) =>
        i + form.Length <= input.Length
        && string.Compare(input, i, form, 0, form.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static char CharAt(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <summary>
    /// Detect any <c>$var</c> / <c>$env:NAME</c> / <c>${name}</c> /
    /// <c>$PSScriptRoot</c> reference left after home substitution. Every
    /// such reference resolves the same way — not statically knowable.
    /// </summary>
    private static bool ContainsDynamicReference(string input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] != '$' || i + 1 >= input.Length)
            {
                continue;
            }

            var next = input[i + 1];
            if (next == '{')
            {
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

    /// <summary>
    /// True when chars <c>[0, colon)</c> of <paramref name="path"/> form a
    /// PSDrive qualifier — an identifier run (first char a letter, rest
    /// letters/digits) immediately followed by <c>:</c>. Distinguishes
    /// <c>HKLM:\x</c> (a drive qualifier) from <c>/etc/a:b</c> (a colon in a
    /// Unix filename).
    /// </summary>
    private static bool IsDriveQualifier(string path, int colon)
    {
        if (colon <= 0 || !IsAsciiLetter(path[0]))
        {
            return false;
        }

        for (var i = 1; i < colon; i++)
        {
            var c = path[i];
            if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsGlobMetacharacters(string input)
    {
        foreach (var c in input)
        {
            if (c == '*' || c == '?' || c == '[')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierStart(char c) =>
        c == '_' || IsAsciiLetter(c);

    private static bool IsIdentifierContinuation(char c) =>
        c == '_' || IsAsciiLetter(c) || (c >= '0' && c <= '9') || c == ':';

    private static bool IsAsciiLetter(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>
    /// Resolve <paramref name="token"/> to a normalized absolute path.
    /// Returns null on failure (SPEC.POWERSHELL.md §8). Drive-qualified
    /// (<c>C:\</c>) and UNC (<c>\\server\share</c>) paths are rooted; a
    /// relative token is joined to the working directory.
    /// </summary>
    private static string? TryResolveAbsolutePath(
        string token, ShellParserOptions options, bool workingDirectoryUnknown)
    {
        if (string.IsNullOrEmpty(token) || IsDriveRelativePath(token))
        {
            return null;
        }

        try
        {
            string combined;
            if (IsRootedPath(token))
            {
                combined = ShellPathNormalization.NormalizeSeparators(token);
            }
            else if (workingDirectoryUnknown)
            {
                return null;
            }
            else
            {
                var wd = GetWorkingDirectory(options);
                if (string.IsNullOrEmpty(wd))
                {
                    return null;
                }

                combined = ShellPathNormalization.Join(wd, token);
            }

            return NormalizePath(combined);
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
            return null;
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');

        string prefix;
        string rest;
        if (normalized.Length >= 2 && normalized[0] == '/' && normalized[1] == '/')
        {
            prefix = "//";
            rest = normalized.Substring(2);
        }
        else if (normalized.Length > 0 && normalized[0] == '/')
        {
            prefix = "/";
            rest = normalized.Substring(1);
        }
        else if (normalized.Length >= 2 && IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        {
            // Drive-letter prefix — normalize to upper-case drive for output
            // consistency.
            prefix = char.ToUpperInvariant(normalized[0]) + ":";
            if (normalized.Length > 2 && normalized[2] == '/')
            {
                prefix += "/";
                rest = normalized.Substring(3);
            }
            else
            {
                rest = normalized.Substring(2);
            }
        }
        else
        {
            prefix = string.Empty;
            rest = normalized;
        }

        var segments = rest.Split('/');
        var stack = new System.Collections.Generic.List<string>();
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0 && stack[stack.Count - 1] != "..")
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (string.IsNullOrEmpty(prefix))
                {
                    stack.Add("..");
                }

                continue;
            }

            stack.Add(segment);
        }

        var joined = string.Join("/", stack);
        if (prefix.Length == 0)
        {
            return joined.Length == 0 ? "." : joined;
        }

        return prefix + joined;
    }

    private static bool IsRootedPath(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (token[0] == '/' || token[0] == '\\')
        {
            return true;
        }

        if (token.Length >= 3
            && IsAsciiLetter(token[0])
            && token[1] == ':'
            && (token[2] == '/' || token[2] == '\\'))
        {
            return true;
        }

        return false;
    }
}
