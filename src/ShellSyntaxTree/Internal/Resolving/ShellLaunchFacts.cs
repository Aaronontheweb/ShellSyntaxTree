// -----------------------------------------------------------------------
// <copyright file="ShellLaunchFacts.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Bash.Parsing;

namespace ShellSyntaxTree.Internal.Resolving;

/// <summary>
/// One policy point for launcher-proved environment facts (#200). Every read
/// goes through this gate, so a fact is never used under an initial-state
/// mode that allows startup content.
/// </summary>
internal static class ShellLaunchFacts
{
    /// <summary>
    /// Bash reads these lookup names but does not assign them at startup when
    /// the environment supplies them. Every other shell-owned name stays
    /// rejected, because Bash can set or reset it before the source runs.
    /// </summary>
    private static readonly HashSet<string> BashLookupNames = new(StringComparer.Ordinal)
    {
        "HOME",
        "TMPDIR",
    };

    internal static bool IsActive(BashParserOptions options) =>
        options.LaunchEnvironment is not null &&
        options.InitialStateMode is BashInitialStateMode.FreshNonInteractiveNoStartup
            or BashInitialStateMode.IsolatedNonInteractive;

    internal static bool IsActive(PwshParserOptions options) =>
        options.LaunchEnvironment is not null &&
        options.InitialStateMode == PwshInitialStateMode.IsolatedNonInteractiveNoProfile;

    internal static bool TryGetValue(BashParserOptions options, string? name, out string value)
    {
        if (IsActive(options))
        {
            return options.LaunchEnvironment!.TryGetLiveValue(name, out value);
        }

        value = string.Empty;
        return false;
    }

    internal static bool TryGetValue(PwshParserOptions options, string? name, out string value)
    {
        if (IsActive(options))
        {
            return options.LaunchEnvironment!.TryGetLiveValue(name, out value);
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads a live launch value for a PowerShell <c>$env:NAME</c> or
    /// <c>${env:NAME}</c> expansion. The scope qualifier ignores case. The
    /// variable name must match the supplied name exactly.
    /// </summary>
    internal static bool TryGetPwshEnvironmentValue(
        ShellParserOptions options,
        string? expansionName,
        out string value)
    {
        const string environmentScope = "env:";
        if (options is PwshParserOptions pwshOptions &&
            expansionName is not null &&
            expansionName.Length > environmentScope.Length &&
            expansionName.StartsWith(environmentScope, StringComparison.OrdinalIgnoreCase))
        {
            return TryGetValue(
                pwshOptions,
                expansionName.Substring(environmentScope.Length),
                out value);
        }

        value = string.Empty;
        return false;
    }

    internal static bool IsUnset(BashParserOptions options, string name) =>
        IsActive(options) && options.LaunchEnvironment!.IsLiveUnset(name);

    /// <summary>
    /// True when the caller supplied a fact for the name, but an earlier
    /// statement can have changed it. The consumer must then treat the value
    /// as unknown and must not fall back to a default.
    /// </summary>
    internal static bool IsRevoked(BashParserOptions options, string name) =>
        IsActive(options) && options.LaunchEnvironment!.IsRevoked(name);

    /// <summary>
    /// True when the value of an expansion stays exactly one word. A quoted
    /// expansion always does. An unquoted expansion is split on whitespace
    /// and globbed, and an empty unquoted expansion gives no word.
    /// </summary>
    internal static bool IsSingleWordValue(string value, bool mayFieldSplit)
    {
        if (!mayFieldSplit)
        {
            return true;
        }

        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '*' or '?' or '[')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Expands one Bash word made only of literal text, live launch
    /// variables, and a leading tilde. The result is the exact single word
    /// that the program receives. A leading <c>~</c> or <c>~/</c> expands
    /// from the live launch <c>HOME</c> (#206). False for any other
    /// expansion, for <c>~user</c>, for a value that can split or glob, for
    /// an empty word, or for a word with no launch value.
    /// </summary>
    internal static bool TryExpandWord(
        ShellValue value,
        BashParserOptions options,
        out string expanded)
    {
        expanded = string.Empty;
        var builder = new System.Text.StringBuilder(value.Decoded.Length);
        var usedLaunchValue = false;
        for (var index = 0; index < value.Fragments.Count; index++)
        {
            var fragment = value.Fragments[index];
            if (fragment.Kind == ShellValueFragmentKind.Literal &&
                fragment.Cardinality == ShellValueCardinality.ExactlyOne)
            {
                builder.Append(fragment.Value);
                continue;
            }

            // Bash expands a leading tilde from HOME. The tilde word of a
            // revoked HOME, of `~user`, or of `~+` is not proved.
            if (fragment.Kind == ShellValueFragmentKind.Expansion &&
                fragment.Expansion is { Kind: ShellExpansionKind.Tilde } &&
                fragment.Cardinality == ShellValueCardinality.ExactlyOne)
            {
                var tildeKind = BashResolver.ClassifyTildeExpansion(value, index);
                if (tildeKind == BashTildeExpansionKind.Literal)
                {
                    builder.Append(fragment.Value);
                    continue;
                }

                if (tildeKind != BashTildeExpansionKind.Home ||
                    !TryGetValue(options, "HOME", out var home) ||
                    home.Length == 0)
                {
                    return false;
                }

                builder.Append(home);
                usedLaunchValue = true;
                continue;
            }

            if (fragment.Kind != ShellValueFragmentKind.Expansion ||
                fragment.Expansion is not { Kind: ShellExpansionKind.Variable } expansion ||
                fragment.Cardinality != ShellValueCardinality.ExactlyOne ||
                (fragment.AllowedTransforms & ShellLexicalTransform.Variable) == 0 ||
                !TryGetValue(options, expansion.Name, out var launchValue) ||
                !IsSingleWordValue(
                    launchValue,
                    (fragment.AllowedTransforms & ShellLexicalTransform.FieldSplit) != 0))
            {
                return false;
            }

            builder.Append(launchValue);
            usedLaunchValue = true;
        }

        if (!usedLaunchValue || builder.Length == 0)
        {
            return false;
        }

        expanded = builder.ToString();
        return true;
    }

    /// <summary>
    /// Rejects launch facts that Bash can change before the source runs, and
    /// a supplied <c>HOME</c> that disagrees with
    /// <see cref="ShellParserOptions.HomeDirectory"/>.
    /// </summary>
    internal static void ValidateForBash(BashParserOptions options)
    {
        var launch = options.LaunchEnvironment;
        if (launch is null)
        {
            return;
        }

        foreach (var name in launch.ExportedVariables.Keys)
        {
            if (!IsBashLaunchName(name))
            {
                throw new ArgumentException(
                    $"Bash owns or can change launch variable '{name}' before the source runs.",
                    nameof(options));
            }
        }

        foreach (var name in launch.UnsetVariables)
        {
            if (!IsBashLaunchName(name) && name != "CDPATH")
            {
                throw new ArgumentException(
                    $"Bash owns or can change unset launch variable '{name}' before the source runs.",
                    nameof(options));
            }
        }

        // The home paths join `~` and `$HOME` with the rest of the word. An
        // empty HOME would make an unquoted `$HOME` vanish, which those
        // paths do not model.
        if (launch.ExportedVariables.TryGetValue("HOME", out var home) && home.Length == 0)
        {
            throw new ArgumentException("The launch variable HOME is empty.", nameof(options));
        }

        if (home is not null &&
            !string.IsNullOrEmpty(options.HomeDirectory) &&
            !string.Equals(home, options.HomeDirectory, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The launch variable HOME disagrees with HomeDirectory.",
                nameof(options));
        }
    }

    /// <summary>
    /// Uses a live launch <c>HOME</c> as the home directory when the caller
    /// set no <see cref="ShellParserOptions.HomeDirectory"/>. Paths that
    /// resolve <c>~</c> without the per-command launch facts then use the
    /// proved value, not the process default.
    /// </summary>
    internal static BashParserOptions NormalizeHome(BashParserOptions options) =>
        string.IsNullOrEmpty(options.HomeDirectory) &&
        TryGetValue(options, "HOME", out var home) &&
        home.Length > 0
            ? options with { HomeDirectory = home }
            : options;

    /// <summary>
    /// Rejects launch facts that PowerShell can change at startup, names
    /// that differ only in case, and the home variable that
    /// <see cref="ShellParserOptions.HomeDirectory"/> already owns.
    /// </summary>
    internal static void ValidateForPwsh(PwshParserOptions options)
    {
        var launch = options.LaunchEnvironment;
        if (launch is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(launch.ExportedVariables.Keys);
        names.AddRange(launch.UnsetVariables);
        foreach (var name in names)
        {
            // Windows environment names ignore case. Two spellings of one
            // name would give two different facts for one variable.
            if (!seen.Add(name))
            {
                throw new ArgumentException(
                    $"PowerShell launch variable '{name}' differs from another name only in case.",
                    nameof(options));
            }

            // PowerShell sets PSModulePath and can change PATH at startup.
            // The parser maps USERPROFILE to HomeDirectory.
            if (name.StartsWith("PS", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("POWERSHELL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "USERPROFILE", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"PowerShell owns or can change launch variable '{name}' before the source runs.",
                    nameof(options));
            }
        }
    }

    private static bool IsBashLaunchName(string name) =>
        BashLookupNames.Contains(name) ||
        BashVariableAssignmentGrammar.IsEligibleCommandEnvironmentName(name);
}
