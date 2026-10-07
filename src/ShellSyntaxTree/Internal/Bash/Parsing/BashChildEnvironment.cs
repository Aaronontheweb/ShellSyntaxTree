// -----------------------------------------------------------------------
// <copyright file="BashChildEnvironment.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Abstract-state facts about the environment of a child process: the names
/// that an <c>export</c> marked on at least one path, and whether the state is
/// inside a decoded child shell. Paths join by union, so a name that one
/// branch exports stays exported after the branches meet.
/// </summary>
/// <remarks>Owner: <see cref="BashAbstractStateAnalyzer"/>, call-local.</remarks>
internal sealed class BashChildEnvironment
{
    internal static readonly BashChildEnvironment TopLevel =
        new(Array.Empty<string>(), isChildProcess: false);

    // Names that Bash itself gives the export attribute, also when they are
    // not in its environment (GNU Bash 5.2): it sets PWD, OLDPWD, and SHLVL
    // at startup, and `_` holds the path of each program that it runs. An
    // assignment to one of them reaches a child process.
    private static readonly HashSet<string> BashExportedNames = new(StringComparer.Ordinal)
    {
        "PWD",
        "OLDPWD",
        "SHLVL",
        "_",
    };

    private readonly IReadOnlyCollection<string> _mayBeExported;

    private BashChildEnvironment(IReadOnlyCollection<string> mayBeExported, bool isChildProcess)
    {
        _mayBeExported = mayBeExported;
        IsChildProcess = isChildProcess;
    }

    /// <summary>True inside a decoded <c>bash -c</c> or <c>sh -c</c> child.</summary>
    internal bool IsChildProcess { get; }

    internal static bool IsExportedByBash(string name) => BashExportedNames.Contains(name);

    internal bool MayBeExported(string name)
    {
        foreach (var candidate in _mayBeExported)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal BashChildEnvironment WithExportedNames(IReadOnlyCollection<string> names)
    {
        var union = new HashSet<string>(_mayBeExported, StringComparer.Ordinal);
        union.UnionWith(names);
        return union.Count == _mayBeExported.Count
            ? this
            : new BashChildEnvironment(union, IsChildProcess);
    }

    internal BashChildEnvironment ForChildProcess() =>
        IsChildProcess ? this : new BashChildEnvironment(_mayBeExported, isChildProcess: true);

    internal static BashChildEnvironment Join(BashChildEnvironment left, BashChildEnvironment right)
    {
        if (left.StateEquals(right))
        {
            return left;
        }

        var union = new HashSet<string>(left._mayBeExported, StringComparer.Ordinal);
        union.UnionWith(right._mayBeExported);
        return new BashChildEnvironment(union, left.IsChildProcess || right.IsChildProcess);
    }

    internal bool StateEquals(BashChildEnvironment other)
    {
        if (IsChildProcess != other.IsChildProcess ||
            _mayBeExported.Count != other._mayBeExported.Count)
        {
            return false;
        }

        foreach (var name in _mayBeExported)
        {
            if (!other.MayBeExported(name))
            {
                return false;
            }
        }

        return true;
    }
}
