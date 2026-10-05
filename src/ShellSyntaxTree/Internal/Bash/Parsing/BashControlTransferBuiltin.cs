// -----------------------------------------------------------------------
// <copyright file="BashControlTransferBuiltin.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

internal enum BashLoopTransferKind
{
    Break,
    Continue,
}

/// <summary>
/// The bounded forms of the Bash control-transfer builtins <c>break</c>,
/// <c>continue</c>, <c>exit</c>, and <c>return</c>. They change no variable
/// and no working directory. They only change which statement runs next.
/// </summary>
/// <remarks>
/// <para>
/// The bounded form has no operand or one static decimal operand. A
/// <c>break</c> or <c>continue</c> level must be 1 or more. Bash ends the
/// shell for a non-numeric level and for too many operands, and it treats a
/// level of 0 or less as an error that leaves the loop. Those forms keep the
/// conservative rule.
/// </para>
/// <para>
/// The state pass does not stop the flow at these builtins. It analyzes the
/// next statements as if they can run, which includes more states than Bash
/// can reach. It also joins the state at a <c>break</c> into the end of its
/// loop, and the state at a <c>continue</c> into the head of its loop.
/// </para>
/// </remarks>
internal static class BashControlTransferBuiltin
{
    /// <summary>True when the clause is a bounded control-transfer builtin.</summary>
    internal static bool IsBounded(Clause clause) =>
        TryClassify(clause, out _, out _);

    /// <summary>
    /// True when the clause is a bounded <c>break</c> or <c>continue</c>.
    /// <paramref name="level"/> receives the number of enclosing loops that
    /// the builtin leaves, 1 or more.
    /// </summary>
    internal static bool TryGetLoopTransfer(
        Clause clause,
        out BashLoopTransferKind kind,
        out int level)
    {
        kind = default;
        if (!TryClassify(clause, out var loopKind, out level) || loopKind is null)
        {
            return false;
        }

        kind = loopKind.Value;
        return true;
    }

    private static bool TryClassify(
        Clause clause,
        out BashLoopTransferKind? loopKind,
        out int level)
    {
        loopKind = null;
        level = 1;
        if (clause.Verb.IsDynamic ||
            clause.Verb.Tokens.Count != 1 ||
            clause.Elements.Count == 0 ||
            !IsStatic(clause.Elements[0], ClauseElementRole.Verb))
        {
            return false;
        }

        var verb = clause.Elements[0].Value;
        var isLoopTransfer = verb is "break" or "continue";
        if (!isLoopTransfer && verb is not ("exit" or "return"))
        {
            return false;
        }

        string? operand = null;
        for (var index = 1; index < clause.Elements.Count; index++)
        {
            var element = clause.Elements[index];
            if (element.Role == ClauseElementRole.Redirect)
            {
                continue;
            }

            if (operand is not null ||
                !IsStatic(element, ClauseElementRole.Argument) ||
                !IsDecimal(element.Value))
            {
                return false;
            }

            operand = element.Value;
        }

        if (isLoopTransfer)
        {
            if (operand is not null && !TryParseLevel(operand, out level))
            {
                return false;
            }

            loopKind = verb == "break" ? BashLoopTransferKind.Break : BashLoopTransferKind.Continue;
        }

        return true;
    }

    private static bool TryParseLevel(string operand, out int level)
    {
        // A level beyond the loop depth leaves every loop, so a large level
        // is the same as the outermost loop.
        level = 0;
        foreach (var digit in operand)
        {
            level = Math.Min(level * 10 + (digit - '0'), ShellAnalysisLimits.MaxStructuralNesting + 1);
        }

        return level >= 1;
    }

    private static bool IsStatic(ClauseElement element, ClauseElementRole role) =>
        element.Role == role &&
        element.Kind == ArgKind.Literal &&
        string.Equals(element.Raw, element.Value, StringComparison.Ordinal);

    private static bool IsDecimal(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
