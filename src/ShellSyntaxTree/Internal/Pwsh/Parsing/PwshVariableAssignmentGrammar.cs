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
        string source,
        IReadOnlyList<PwshToken> tokens,
        int index,
        out string name,
        out string value,
        out int tokenCount)
    {
        name = string.Empty;
        value = string.Empty;
        tokenCount = 0;
        if (index < 0 || index >= tokens.Count)
        {
            return false;
        }

        var target = tokens[index];
        int rightIndex;
        if (TryReadTarget(target, out name))
        {
            rightIndex = index + 1;
            if (!HasOnlyHorizontalWhitespace(
                    source,
                    target.SourceStart + target.SourceLength,
                    tokens,
                    rightIndex))
            {
                name = string.Empty;
                return false;
            }
        }
        else if (TryReadBareTarget(target, out name) && index + 2 < tokens.Count &&
                 IsExactEqualsToken(source, tokens[index + 1]) &&
                 HasOnlyHorizontalWhitespace(
                     source,
                     target.SourceStart + target.SourceLength,
                     tokens,
                     index + 1) &&
                 HasOnlyHorizontalWhitespace(
                     source,
                     tokens[index + 1].SourceStart + tokens[index + 1].SourceLength,
                     tokens,
                     index + 2))
        {
            rightIndex = index + 2;
        }
        else
        {
            name = string.Empty;
            return false;
        }

        if (!HasExactTargetSource(source, target, name, rightIndex == index + 1))
        {
            name = string.Empty;
            return false;
        }

        var right = tokens[rightIndex];
        if (right.Kind != PwshTokenKind.QuotedString ||
            !right.IsSingleQuoted || right.IsHereString || right.HasInterpolation ||
            !PwshForEachValueAnalysis.IsEligibleBindingName(name))
        {
            name = string.Empty;
            return false;
        }

        value = right.Value;
        tokenCount = rightIndex - index + 1;
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

    internal static bool TryReadBareTarget(PwshToken token, out string name)
    {
        var value = token.Value;
        name = string.Empty;
        if (token.Kind != PwshTokenKind.Word ||
            value.Length < 2 || value[0] != '$' ||
            !IsVariableNameStart(value[1]))
        {
            return false;
        }

        for (var index = 2; index < value.Length; index++)
        {
            if (!IsVariableNamePart(value[index]))
            {
                return false;
            }
        }

        name = value.Substring(1);
        return true;
    }

    private static bool IsExactEqualsToken(string source, PwshToken token) =>
        token.Kind == PwshTokenKind.Word && token.Value == "=" &&
        token.SourceStart >= 0 && token.SourceLength == 1 &&
        token.SourceStart < source.Length && source[token.SourceStart] == '=';

    private static bool HasExactTargetSource(
        string source,
        PwshToken target,
        string name,
        bool includesEquals)
    {
        var expected = "$" + name + (includesEquals ? "=" : string.Empty);
        return target.SourceStart >= 0 && target.SourceLength == expected.Length &&
            target.SourceStart + target.SourceLength <= source.Length &&
            string.CompareOrdinal(
                source,
                target.SourceStart,
                expected,
                0,
                expected.Length) == 0;
    }

    private static bool HasOnlyHorizontalWhitespace(
        string source,
        int start,
        IReadOnlyList<PwshToken> tokens,
        int nextTokenIndex)
    {
        if (nextTokenIndex >= tokens.Count || start < 0 || start > source.Length)
        {
            return false;
        }

        var end = tokens[nextTokenIndex].SourceStart;
        if (end < start || end > source.Length)
        {
            return false;
        }

        for (var index = start; index < end; index++)
        {
            var value = source[index];
            if (value is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVariableNameStart(char value) =>
        value == '_' || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsVariableNamePart(char value) =>
        IsVariableNameStart(value) || value is >= '0' and <= '9';
}
