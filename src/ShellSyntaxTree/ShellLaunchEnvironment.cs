// -----------------------------------------------------------------------
// <copyright file="ShellLaunchEnvironment.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ShellSyntaxTree;

/// <summary>
/// Environment facts that the process launcher proves for a new shell
/// process before the shell reads the source (#200).
/// </summary>
/// <remarks>
/// <para>
/// Each entry in <see cref="ExportedVariables"/> states that the variable is
/// set, exported, and a scalar with exactly that value when the shell starts.
/// Each name in <see cref="UnsetVariables"/> states that the variable is not
/// set when the shell starts. The caller is responsible for these facts. The
/// parser does not read the environment of the current process.
/// </para>
/// <para>
/// The parser uses the facts only under an initial-state mode that excludes
/// startup content, because startup content can change any variable. It
/// stops trusting a value after a statement that can change the variable.
/// A name that the caller does not supply behaves as before. These facts do
/// not grant authority.
/// </para>
/// </remarks>
public sealed class ShellLaunchEnvironment
{
    private readonly HashSet<string> _revokedNames;
    private readonly bool _allRevoked;

    /// <summary>Creates launcher-proved environment facts.</summary>
    /// <param name="exportedVariables">
    /// Variables that are set, exported, and scalar, with their exact values.
    /// </param>
    /// <param name="unsetVariables">Variables that are not set.</param>
    /// <exception cref="ArgumentNullException">An argument, a name, or a value is null.</exception>
    /// <exception cref="ArgumentException">
    /// A name is not an ASCII shell identifier, a value contains a NUL
    /// character, a name occurs more than once, or a name is both set and
    /// unset.
    /// </exception>
    public ShellLaunchEnvironment(
        IEnumerable<KeyValuePair<string, string>> exportedVariables,
        IEnumerable<string> unsetVariables)
    {
        if (exportedVariables is null)
        {
            throw new ArgumentNullException(nameof(exportedVariables));
        }

        if (unsetVariables is null)
        {
            throw new ArgumentNullException(nameof(unsetVariables));
        }

        var exported = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in exportedVariables)
        {
            ValidateName(entry.Key, nameof(exportedVariables));
            if (entry.Value is null)
            {
                throw new ArgumentNullException(
                    nameof(exportedVariables),
                    $"The value of launch variable '{entry.Key}' is null.");
            }

            if (entry.Value.IndexOf('\0') >= 0)
            {
                throw new ArgumentException(
                    $"The value of launch variable '{entry.Key}' contains a NUL character.",
                    nameof(exportedVariables));
            }

            if (exported.ContainsKey(entry.Key))
            {
                throw new ArgumentException(
                    $"Launch variable '{entry.Key}' occurs more than once.",
                    nameof(exportedVariables));
            }

            exported.Add(entry.Key, entry.Value);
        }

        var unset = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in unsetVariables)
        {
            ValidateName(name, nameof(unsetVariables));
            if (exported.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"Launch variable '{name}' cannot be both set and unset.",
                    nameof(unsetVariables));
            }

            if (!unset.Add(name))
            {
                throw new ArgumentException(
                    $"Unset launch variable '{name}' occurs more than once.",
                    nameof(unsetVariables));
            }
        }

        ExportedVariables = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(exported, StringComparer.Ordinal));
        UnsetVariables = new ReadOnlyCollection<string>(new List<string>(unset));
        _revokedNames = new HashSet<string>(StringComparer.Ordinal);
    }

    private ShellLaunchEnvironment(
        IReadOnlyDictionary<string, string> exportedVariables,
        IReadOnlyList<string> unsetVariables,
        HashSet<string> revokedNames,
        bool allRevoked)
    {
        ExportedVariables = exportedVariables;
        UnsetVariables = unsetVariables;
        _revokedNames = revokedNames;
        _allRevoked = allRevoked;
    }

    /// <summary>
    /// Gets the variables that are set, exported, and scalar when the shell
    /// starts, with their exact values. Names use ordinal comparison.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExportedVariables { get; }

    /// <summary>Gets the variables that are not set when the shell starts.</summary>
    public IReadOnlyList<string> UnsetVariables { get; }

    /// <summary>
    /// Gets the exact value of a supplied variable that no earlier statement
    /// can have changed.
    /// </summary>
    internal bool TryGetLiveValue(string? name, out string value)
    {
        if (name is not null && !_allRevoked && !_revokedNames.Contains(name) &&
            ExportedVariables.TryGetValue(name, out var found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// True when the caller proved that the variable is unset and no earlier
    /// statement can have set it.
    /// </summary>
    internal bool IsLiveUnset(string name)
    {
        if (_allRevoked || _revokedNames.Contains(name))
        {
            return false;
        }

        foreach (var unset in UnsetVariables)
        {
            if (string.Equals(unset, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the caller supplied a fact for the name, but an earlier
    /// statement can have changed the variable.
    /// </summary>
    internal bool IsRevoked(string name) =>
        (_allRevoked || _revokedNames.Contains(name)) &&
        (ExportedVariables.ContainsKey(name) || ContainsUnset(name));

    /// <summary>True when at least one supplied fact is still live.</summary>
    internal bool HasLiveFacts
    {
        get
        {
            if (_allRevoked)
            {
                return false;
            }

            foreach (var name in ExportedVariables.Keys)
            {
                if (!_revokedNames.Contains(name))
                {
                    return true;
                }
            }

            foreach (var name in UnsetVariables)
            {
                if (!_revokedNames.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Returns facts in which one name is no longer trusted. The other facts
    /// stay live.
    /// </summary>
    internal ShellLaunchEnvironment Revoke(string name)
    {
        if (_allRevoked || _revokedNames.Contains(name) ||
            !ExportedVariables.ContainsKey(name) && !ContainsUnset(name))
        {
            return this;
        }

        var revoked = new HashSet<string>(_revokedNames, StringComparer.Ordinal) { name };
        return new ShellLaunchEnvironment(ExportedVariables, UnsetVariables, revoked, false);
    }

    /// <summary>Returns facts in which no name is trusted.</summary>
    internal ShellLaunchEnvironment RevokeAll() =>
        _allRevoked
            ? this
            : new ShellLaunchEnvironment(ExportedVariables, UnsetVariables, _revokedNames, true);

    private bool ContainsUnset(string name)
    {
        foreach (var unset in UnsetVariables)
        {
            if (string.Equals(unset, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateName(string? name, string parameterName)
    {
        if (name is null)
        {
            throw new ArgumentNullException(parameterName, "A launch variable name is null.");
        }

        if (!IsAsciiIdentifier(name))
        {
            throw new ArgumentException(
                $"Launch variable name '{name}' is not an ASCII shell identifier.",
                parameterName);
        }
    }

    internal static bool IsAsciiIdentifier(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsIdentifierStart(value[index]) && value[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char value) =>
        value == '_' || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
