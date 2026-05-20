// -----------------------------------------------------------------------
// <copyright file="AstAssert.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit.Sdk;

namespace ShellSyntaxTree.Tests.Corpus;

/// <summary>
/// Structural-equality helper for the corpus runner. Compares an
/// <see cref="ExpectedParsedCommand"/> (the JSON-deserialized expectation
/// shape) against a live <see cref="ParsedCommand"/> and throws an
/// <see cref="XunitException"/> with a diff-friendly message naming the
/// first differing field on mismatch.
/// </summary>
/// <remarks>
/// <para>
/// SPEC §13's <c>AstAssert.Equal</c> is the canonical entry point. The
/// implementation prioritizes legibility of the CI failure log over a
/// machine-readable diff — the goal is that a human reviewing a failing
/// corpus assertion can identify the differing path
/// (e.g. <c>clauses[1].args[2].kind</c>) and the specific value mismatch
/// without re-reading the entire JSON.
/// </para>
/// <para>
/// Comparison rules (see also <see cref="ExpectedParsedCommand"/> and
/// peers for which fields are required vs. opt-in):
/// </para>
/// <list type="bullet">
///   <item>
///     <c>IsUnparseable</c> is compared first. When true on the expected
///     side, only <c>UnparseableReasonContains</c> (substring match, when
///     supplied) is verified — the clauses payload is not inspected.
///   </item>
///   <item>
///     <c>Clauses.Count</c> mismatch dumps both expected and actual clause
///     summaries so the author can eyeball which clause was added/dropped.
///   </item>
///   <item>
///     Per clause: <c>Operator</c>, <c>Verb.Tokens</c> (sequence equality),
///     <c>Args.Count</c>, per-arg fields, <c>Redirects.Count</c>, per-
///     redirect fields, <c>IsSubshell</c>, <c>IsCommandStringWrapped</c>.
///   </item>
///   <item>
///     Per arg: <c>Raw</c>, <c>Kind</c>, <c>IsPath</c>, <c>IsCwdAttribution</c>
///     always asserted. <c>IsFlag</c> opt-in (computed property; corpus may
///     pin it for documentation but it's not required). <c>Resolved</c>
///     opt-in via the JSON sentinel <c>"__NULL__"</c> for null or a literal
///     string for value equality; omitting the field skips the comparison.
///   </item>
/// </list>
/// </remarks>
internal static class AstAssert
{
    /// <summary>
    /// Assert that <paramref name="actual"/> matches <paramref name="expected"/>.
    /// Throws <see cref="XunitException"/> with a path-prefixed message on
    /// first mismatch; never throws on equality.
    /// </summary>
    /// <param name="expected">The expectation shape from a corpus JSON.</param>
    /// <param name="actual">The live <see cref="ParsedCommand"/> from the parser.</param>
    /// <param name="contextLabel">
    /// Optional human-readable label (typically the corpus file name) used
    /// as a prefix on every failure message. When null, no prefix is added.
    /// </param>
    internal static void Equal(
        ExpectedParsedCommand expected,
        ParsedCommand actual,
        string? contextLabel = null)
    {
        var prefix = contextLabel is null ? string.Empty : $"[{contextLabel}] ";

        if (expected.IsUnparseable != actual.IsUnparseable)
        {
            throw new XunitException(
                prefix + $"isUnparseable: expected={expected.IsUnparseable}, actual={actual.IsUnparseable}; reason={actual.UnparseableReason}");
        }

        if (expected.IsUnparseable)
        {
            if (!string.IsNullOrEmpty(expected.UnparseableReasonContains))
            {
                if (actual.UnparseableReason is null
                    || !actual.UnparseableReason.Contains(expected.UnparseableReasonContains!, System.StringComparison.Ordinal))
                {
                    throw new XunitException(
                        prefix + $"unparseableReason: expected contains '{expected.UnparseableReasonContains}', actual='{actual.UnparseableReason}'");
                }
            }

            return;
        }

        var expectedClauses = expected.Clauses ?? new List<ExpectedClause>();
        if (expectedClauses.Count != actual.Clauses.Count)
        {
            throw new XunitException(
                prefix + $"clauses.count: expected={expectedClauses.Count}, actual={actual.Clauses.Count}\n"
                + "  expected: " + SummarizeExpectedClauses(expectedClauses) + "\n"
                + "  actual:   " + SummarizeActualClauses(actual.Clauses));
        }

        for (var i = 0; i < expectedClauses.Count; i++)
        {
            AssertClauseEqual(expectedClauses[i], actual.Clauses[i], $"{prefix}clauses[{i}]");
        }
    }

    private static void AssertClauseEqual(ExpectedClause expected, Clause actual, string path)
    {
        if (expected.Operator != actual.Operator)
        {
            throw new XunitException(
                $"{path}.operator: expected={expected.Operator}, actual={actual.Operator}");
        }

        var expectedVerb = expected.Verb ?? new List<string>();
        if (!expectedVerb.SequenceEqual(actual.Verb.Tokens))
        {
            throw new XunitException(
                $"{path}.verb: expected=[{string.Join(",", expectedVerb)}], actual=[{string.Join(",", actual.Verb.Tokens)}]");
        }

        if (expected.CanonicalVerb != actual.Verb.CanonicalVerb)
        {
            throw new XunitException(
                $"{path}.canonicalVerb: expected={Quote(expected.CanonicalVerb)}, actual={Quote(actual.Verb.CanonicalVerb)}");
        }

        if (expected.IsDynamic != actual.Verb.IsDynamic)
        {
            throw new XunitException(
                $"{path}.isDynamic: expected={expected.IsDynamic}, actual={actual.Verb.IsDynamic}");
        }

        var expectedArgs = expected.Args ?? new List<ExpectedArg>();
        if (expectedArgs.Count != actual.Args.Count)
        {
            throw new XunitException(
                $"{path}.args.count: expected={expectedArgs.Count}, actual={actual.Args.Count}\n"
                + "  actual args: " + string.Join(", ", actual.Args.Select(a => $"{{raw={a.Raw}, kind={a.Kind}, isPath={a.IsPath}, isCwdAttribution={a.IsCwdAttribution}}}")));
        }

        for (var i = 0; i < expectedArgs.Count; i++)
        {
            AssertArgEqual(expectedArgs[i], actual.Args[i], $"{path}.args[{i}]");
        }

        var expectedRedirects = expected.Redirects ?? new List<ExpectedRedirect>();
        if (expectedRedirects.Count != actual.Redirects.Count)
        {
            throw new XunitException(
                $"{path}.redirects.count: expected={expectedRedirects.Count}, actual={actual.Redirects.Count}\n"
                + "  actual redirects: " + string.Join(", ", actual.Redirects.Select(r => $"{{direction={r.Direction}, target={r.Target}, dynamicSkip={r.IsDynamicSkip}}}")));
        }

        for (var i = 0; i < expectedRedirects.Count; i++)
        {
            AssertRedirectEqual(expectedRedirects[i], actual.Redirects[i], $"{path}.redirects[{i}]");
        }

        if (expected.IsSubshell != actual.IsSubshell)
        {
            throw new XunitException(
                $"{path}.isSubshell: expected={expected.IsSubshell}, actual={actual.IsSubshell}");
        }

        if (expected.IsCommandStringWrapped != actual.IsCommandStringWrapped)
        {
            throw new XunitException(
                $"{path}.isCommandStringWrapped: expected={expected.IsCommandStringWrapped}, actual={actual.IsCommandStringWrapped}");
        }
    }

    private static string Quote(string? value) => value is null ? "null" : $"'{value}'";

    private static void AssertArgEqual(ExpectedArg expected, Arg actual, string path)
    {
        if (expected.Raw != actual.Raw)
        {
            throw new XunitException(
                $"{path}.raw: expected='{expected.Raw}', actual='{actual.Raw}'");
        }

        if (expected.Kind != actual.Kind)
        {
            throw new XunitException(
                $"{path}.kind: expected={expected.Kind}, actual={actual.Kind}");
        }

        if (expected.IsPath != actual.IsPath)
        {
            throw new XunitException(
                $"{path}.isPath: expected={expected.IsPath}, actual={actual.IsPath}");
        }

        if (expected.IsCwdAttribution != actual.IsCwdAttribution)
        {
            throw new XunitException(
                $"{path}.isCwdAttribution: expected={expected.IsCwdAttribution}, actual={actual.IsCwdAttribution}");
        }

        if (expected.IsFlag.HasValue && expected.IsFlag.Value != actual.IsFlag)
        {
            throw new XunitException(
                $"{path}.isFlag: expected={expected.IsFlag}, actual={actual.IsFlag}");
        }

        if (expected.Resolved is not null)
        {
            if (expected.Resolved == "__NULL__")
            {
                if (actual.Resolved is not null)
                {
                    throw new XunitException(
                        $"{path}.resolved: expected=null, actual='{actual.Resolved}'");
                }
            }
            else if (expected.Resolved != actual.Resolved)
            {
                throw new XunitException(
                    $"{path}.resolved: expected='{expected.Resolved}', actual='{actual.Resolved}'");
            }
        }
    }

    private static void AssertRedirectEqual(ExpectedRedirect expected, Redirect actual, string path)
    {
        if (expected.Direction != actual.Direction)
        {
            throw new XunitException(
                $"{path}.direction: expected={expected.Direction}, actual={actual.Direction}");
        }

        if (expected.Target != actual.Target)
        {
            throw new XunitException(
                $"{path}.target: expected='{expected.Target}', actual='{actual.Target}'");
        }

        if (expected.IsDynamicSkip.HasValue && expected.IsDynamicSkip.Value != actual.IsDynamicSkip)
        {
            throw new XunitException(
                $"{path}.isDynamicSkip: expected={expected.IsDynamicSkip}, actual={actual.IsDynamicSkip}");
        }
    }

    private static string SummarizeExpectedClauses(IReadOnlyList<ExpectedClause> clauses)
    {
        if (clauses.Count == 0)
        {
            return "[]";
        }

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < clauses.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var c = clauses[i];
            var verbStr = c.Verb is null || c.Verb.Count == 0 ? "(redirect-only)" : string.Join(" ", c.Verb);
            sb.Append($"{c.Operator}:'{verbStr}'");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string SummarizeActualClauses(IReadOnlyList<Clause> clauses)
    {
        if (clauses.Count == 0)
        {
            return "[]";
        }

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < clauses.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var c = clauses[i];
            var verbStr = c.Verb.Tokens.Count == 0 ? "(redirect-only)" : c.Verb.Joined;
            sb.Append($"{c.Operator}:'{verbStr}'");
        }
        sb.Append(']');
        return sb.ToString();
    }
}
