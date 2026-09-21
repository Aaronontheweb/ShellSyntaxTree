// -----------------------------------------------------------------------
// <copyright file="PwshVariableAssignmentGrammar.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Pwsh.Lexing;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

internal static class PwshVariableAssignmentGrammar
{
    internal static bool TryRead(
        IReadOnlyList<PwshToken> tokens,
        int index,
        out string name,
        out string value,
        out int tokenCount)
    {
        name = string.Empty;
        value = string.Empty;
        tokenCount = 0;
        if (index < 0 || index + 1 >= tokens.Count)
        {
            return false;
        }

        var target = tokens[index];
        var right = tokens[index + 1];
        if (target.Kind != PwshTokenKind.Word ||
            target.SourceStart + target.SourceLength != right.SourceStart ||
            right.Kind != PwshTokenKind.QuotedString ||
            !right.IsSingleQuoted || right.IsHereString || right.HasInterpolation ||
            !TryReadTarget(target, out name) ||
            !PwshForEachValueAnalysis.IsEligibleBindingName(name))
        {
            return false;
        }

        value = right.Value;
        tokenCount = 2;
        return true;
    }

    internal static bool TryReadTarget(PwshToken token, out string name)
    {
        var value = token.Value;
        name = string.Empty;
        if (token.Kind != PwshTokenKind.Word ||
            value.Length < 3 || value[0] != '$' || value[value.Length - 1] != '=' ||
            !IsVariableNameStart(value[1]))
        {
            return false;
        }

        for (var index = 2; index < value.Length - 1; index++)
        {
            if (!IsVariableNamePart(value[index]))
            {
                return false;
            }
        }

        name = value.Substring(1, value.Length - 2);
        return true;
    }

    private static bool IsVariableNameStart(char value) =>
        value == '_' || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsVariableNamePart(char value) =>
        IsVariableNameStart(value) || value is >= '0' and <= '9';
}
