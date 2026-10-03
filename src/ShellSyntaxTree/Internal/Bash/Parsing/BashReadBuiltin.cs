// -----------------------------------------------------------------------
// <copyright file="BashReadBuiltin.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// The bounded form of the Bash <c>read</c> builtin (#212). <c>read</c> is
/// part of the shell. It assigns each named variable from its input, so the
/// parser binds each name to an unknown value. It does not run other code.
/// </summary>
/// <remarks>
/// The accepted options are <c>-r</c> and <c>-s</c>, and <c>-d</c>,
/// <c>-n</c>, <c>-N</c>, <c>-t</c>, and <c>-u</c> with a static value.
/// <c>-a</c> (an array), <c>-e</c> and <c>-i</c> (readline), and <c>-p</c>
/// fail closed. Each name must be a static word that passes the
/// command-environment name gate. A <c>read</c> with no name assigns the
/// shell-owned <c>REPLY</c>, which later reads cannot use.
/// </remarks>
internal static class BashReadBuiltin
{
    private const string ValueOptions = "dnNtu";
    private const string FlagOptions = "rs";

    /// <summary>
    /// True when the clause is a direct <c>read</c> in the bounded form.
    /// <paramref name="names"/> receives each assigned name and the index of
    /// its clause element.
    /// </summary>
    internal static bool TryGetNames(
        Clause clause,
        out IReadOnlyList<(string Name, int ElementIndex)> names)
    {
        names = Array.Empty<(string, int)>();
        if (clause.Verb.IsDynamic ||
            clause.Verb.Tokens.Count == 0 ||
            !string.Equals(clause.Verb.Tokens[0], "read", StringComparison.Ordinal) ||
            clause.Elements.Count == 0 ||
            clause.Elements[0].Role != ClauseElementRole.Verb ||
            !IsStatic(clause.Elements[0], "read"))
        {
            return false;
        }

        var found = new List<(string, int)>();
        var optionsEnded = false;
        var pendingValue = false;
        for (var index = 1; index < clause.Elements.Count; index++)
        {
            var element = clause.Elements[index];
            if (element.Role == ClauseElementRole.Redirect)
            {
                continue;
            }

            if (!IsStatic(element, element.Value))
            {
                return false;
            }

            var value = element.Value;
            if (pendingValue)
            {
                pendingValue = false;
                continue;
            }

            if (!optionsEnded && found.Count == 0 && value.Length > 1 && value[0] == '-')
            {
                if (value == "--")
                {
                    optionsEnded = true;
                    continue;
                }

                if (!TryReadOptionCluster(value, out pendingValue))
                {
                    return false;
                }

                continue;
            }

            if (!BashVariableAssignmentGrammar.IsEligibleCommandEnvironmentName(value))
            {
                return false;
            }

            found.Add((value, index));
        }

        if (pendingValue)
        {
            return false;
        }

        names = found;
        return true;
    }

    internal static bool IsBoundedRead(Clause clause) => TryGetNames(clause, out _);

    private static bool TryReadOptionCluster(string value, out bool takesNextWord)
    {
        takesNextWord = false;
        for (var index = 1; index < value.Length; index++)
        {
            var option = value[index];
            if (FlagOptions.IndexOf(option) >= 0)
            {
                continue;
            }

            if (ValueOptions.IndexOf(option) >= 0)
            {
                // The rest of the word is the value, or the next word is.
                takesNextWord = index == value.Length - 1;
                return true;
            }

            return false;
        }

        return true;
    }

    // A static word has the same authored and decoded spelling and no
    // expansion, so it cannot become an option or another name.
    private static bool IsStatic(ClauseElement element, string expected) =>
        element.Kind == ArgKind.Literal &&
        string.Equals(element.Raw, element.Value, StringComparison.Ordinal) &&
        string.Equals(element.Value, expected, StringComparison.Ordinal);
}

/// <summary>
/// The bounded <c>set --</c> form (#221). It replaces the positional
/// parameters and changes no option or variable. The parser does not track
/// positional values, so a later <c>$1</c> stays unknown.
/// </summary>
internal static class BashSetPositionalBuiltin
{
    internal static bool IsBounded(Clause clause)
    {
        if (clause.Verb.IsDynamic ||
            clause.Verb.Tokens.Count == 0 ||
            !string.Equals(clause.Verb.Tokens[0], "set", StringComparison.Ordinal) ||
            clause.Elements.Count < 2 ||
            clause.Elements[0].Role != ClauseElementRole.Verb ||
            !string.Equals(clause.Elements[0].Raw, "set", StringComparison.Ordinal))
        {
            return false;
        }

        var first = clause.Elements[1];
        if (first.Role == ClauseElementRole.Redirect ||
            first.Kind != ArgKind.Literal ||
            !string.Equals(first.Raw, "--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var element in clause.Elements)
        {
            if (element.Role == ClauseElementRole.Redirect)
            {
                return false;
            }
        }

        return true;
    }
}
