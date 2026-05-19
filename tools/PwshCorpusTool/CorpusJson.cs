// -----------------------------------------------------------------------
// <copyright file="CorpusJson.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ShellSyntaxTree;

namespace ShellSyntaxTree.Tools.PwshCorpus;

/// <summary>
/// Serializes a live <see cref="ParsedCommand"/> into the corpus JSON
/// schema consumed by <c>CorpusRunnerTests</c> (SPEC.POWERSHELL.md §13).
/// Default-valued fields are omitted to keep entries readable.
/// </summary>
internal static class CorpusJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    internal static string BuildEntry(
        string name, string input, ParsedCommand parsed, string notes, bool outOfScope)
    {
        var obj = new JsonObject
        {
            ["name"] = name,
            ["input"] = input,
            ["expected"] = BuildExpected(parsed),
            ["notes"] = notes,
        };

        if (parsed.IsUnparseable && outOfScope)
        {
            obj["oracleExpectation"] = "OutOfScope";
        }

        return obj.ToJsonString(WriteOptions) + "\n";
    }

    private static JsonObject BuildExpected(ParsedCommand parsed)
    {
        var expected = new JsonObject { ["isUnparseable"] = parsed.IsUnparseable };
        if (parsed.IsUnparseable)
        {
            expected["unparseableReasonContains"] = StableReasonSubstring(parsed.UnparseableReason);
            return expected;
        }

        var clauses = new JsonArray();
        foreach (var clause in parsed.Clauses)
        {
            clauses.Add(BuildClause(clause));
        }

        expected["clauses"] = clauses;
        return expected;
    }

    private static JsonObject BuildClause(Clause clause)
    {
        var verb = new JsonArray();
        foreach (var token in clause.Verb.Tokens)
        {
            verb.Add(token);
        }

        var obj = new JsonObject
        {
            ["operator"] = clause.Operator.ToString(),
            ["verb"] = verb,
        };

        if (clause.Verb.CanonicalVerb is not null)
        {
            obj["canonicalVerb"] = clause.Verb.CanonicalVerb;
        }

        if (clause.Verb.IsDynamic)
        {
            obj["isDynamic"] = true;
        }

        var args = new JsonArray();
        foreach (var arg in clause.Args)
        {
            args.Add(BuildArg(arg));
        }

        obj["args"] = args;

        var redirects = new JsonArray();
        foreach (var redirect in clause.Redirects)
        {
            redirects.Add(BuildRedirect(redirect));
        }

        obj["redirects"] = redirects;

        if (clause.IsSubshell)
        {
            obj["isSubshell"] = true;
        }

        if (clause.IsCommandStringWrapped)
        {
            obj["isCommandStringWrapped"] = true;
        }

        return obj;
    }

    private static JsonObject BuildArg(Arg arg)
    {
        var obj = new JsonObject
        {
            ["raw"] = arg.Raw,
            ["kind"] = arg.Kind.ToString(),
            ["isPath"] = arg.IsPath,
        };

        if (arg.Resolved is not null)
        {
            obj["resolved"] = arg.Resolved;
        }

        if (arg.IsCwdAttribution)
        {
            obj["isCwdAttribution"] = true;
        }

        return obj;
    }

    private static JsonObject BuildRedirect(Redirect redirect)
    {
        var obj = new JsonObject
        {
            ["direction"] = redirect.Direction.ToString(),
            ["target"] = redirect.Target,
        };

        if (redirect.IsDynamicSkip)
        {
            obj["isDynamicSkip"] = true;
        }

        return obj;
    }

    /// <summary>
    /// A stable substring of an <c>UnparseableReason</c> for the corpus's
    /// <c>unparseableReasonContains</c> assertion — trims the variable
    /// "at position N" / "(N chars)" tails so the assertion does not pin a
    /// position that could shift.
    /// </summary>
    private static string StableReasonSubstring(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        var trimmed = reason!;
        var atPosition = trimmed.IndexOf(" at position", StringComparison.Ordinal);
        if (atPosition > 0)
        {
            trimmed = trimmed.Substring(0, atPosition);
        }

        var paren = trimmed.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
        {
            trimmed = trimmed.Substring(0, paren);
        }

        return trimmed;
    }
}
