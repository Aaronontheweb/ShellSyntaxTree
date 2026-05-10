// -----------------------------------------------------------------------
// <copyright file="CorpusRunnerTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ShellSyntaxTree.Tests.Corpus;

/// <summary>
/// Drives the JSON corpus under <c>tests/ShellSyntaxTree.Tests/Corpus/bash/</c>.
/// Each entry pairs an <c>input</c> string with an <c>expected</c>
/// <see cref="ParsedCommand"/> shape; the runner parses the input and
/// compares the result field-by-field. PR 3 ships a basic comparison
/// helper; PR 6 will polish to <c>AstAssert.Equal</c> with rich diffs.
/// </summary>
public class CorpusRunnerTests
{
    [Theory]
    [MemberData(nameof(CorpusEntries))]
    public void Corpus_entry_parses_to_expected_ast(string fileName, CorpusEntry entry)
    {
        Assert.NotNull(entry);
        Assert.False(string.IsNullOrEmpty(entry.Name), $"Corpus entry {fileName} has no name.");
        Assert.NotNull(entry.Expected);

        // Pin HomeDirectory and WorkingDirectory so corpus entries with
        // relative-path resolution have stable expected values across
        // hosts (Linux CI, Windows CI, dev machines). The values mirror
        // the BashCommandParserTests harness.
        var parser = new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
        });
        var actual = parser.Parse(entry.Input);

        AssertParsedCommandEqual(entry.Expected!, actual, fileName);
    }

    public static IEnumerable<object[]> CorpusEntries()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Corpus", "bash");
        if (!Directory.Exists(dir))
        {
            // Empty corpus is a valid state until entries arrive.
            yield break;
        }

        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToArray();
        foreach (var file in files)
        {
            CorpusEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<CorpusEntry>(
                    File.ReadAllText(file),
                    JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to deserialize corpus entry {Path.GetFileName(file)}: {ex.Message}", ex);
            }

            if (entry is null)
            {
                throw new InvalidOperationException(
                    $"Corpus entry {Path.GetFileName(file)} deserialized to null.");
            }

            yield return new object[] { Path.GetFileName(file), entry };
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // -------- structural assertion (rough; PR 6 polishes) --------

    private static void AssertParsedCommandEqual(
        ExpectedParsedCommand expected, ParsedCommand actual, string fileName)
    {
        var diffPrefix = $"[{fileName}] ";

        Assert.True(
            expected.IsUnparseable == actual.IsUnparseable,
            diffPrefix + $"IsUnparseable mismatch. expected={expected.IsUnparseable}, actual={actual.IsUnparseable}; reason={actual.UnparseableReason}");

        if (expected.IsUnparseable)
        {
            // PR 3: don't pin the exact reason text — it's an implementation
            // detail. The corpus author can include `unparseableReasonContains`
            // for a substring contract.
            if (!string.IsNullOrEmpty(expected.UnparseableReasonContains))
            {
                Assert.True(
                    actual.UnparseableReason is not null
                    && actual.UnparseableReason.Contains(expected.UnparseableReasonContains, StringComparison.Ordinal),
                    diffPrefix + $"UnparseableReason should contain '{expected.UnparseableReasonContains}', got: '{actual.UnparseableReason}'");
            }

            return;
        }

        var expectedClauses = expected.Clauses ?? new List<ExpectedClause>();
        Assert.True(
            expectedClauses.Count == actual.Clauses.Count,
            diffPrefix + $"Clause count mismatch. expected={expectedClauses.Count}, actual={actual.Clauses.Count}\n"
            + DumpActual(actual));

        for (var i = 0; i < expectedClauses.Count; i++)
        {
            AssertClauseEqual(expectedClauses[i], actual.Clauses[i], diffPrefix + $"clause[{i}]: ");
        }
    }

    private static void AssertClauseEqual(ExpectedClause expected, Clause actual, string diffPrefix)
    {
        Assert.True(
            expected.Operator == actual.Operator,
            diffPrefix + $"Operator mismatch. expected={expected.Operator}, actual={actual.Operator}");

        var expectedVerb = expected.Verb ?? new List<string>();
        Assert.True(
            expectedVerb.SequenceEqual(actual.Verb.Tokens),
            diffPrefix + $"Verb mismatch. expected=[{string.Join(",", expectedVerb)}], actual=[{string.Join(",", actual.Verb.Tokens)}]");

        var expectedArgs = expected.Args ?? new List<ExpectedArg>();
        Assert.True(
            expectedArgs.Count == actual.Args.Count,
            diffPrefix + $"Args count mismatch. expected={expectedArgs.Count}, actual={actual.Args.Count}\n"
            + " actual args: " + string.Join(", ", actual.Args.Select(a => $"({a.Raw}, kind={a.Kind})")));

        for (var i = 0; i < expectedArgs.Count; i++)
        {
            AssertArgEqual(expectedArgs[i], actual.Args[i], diffPrefix + $"arg[{i}]: ");
        }

        var expectedRedirects = expected.Redirects ?? new List<ExpectedRedirect>();
        Assert.True(
            expectedRedirects.Count == actual.Redirects.Count,
            diffPrefix + $"Redirects count mismatch. expected={expectedRedirects.Count}, actual={actual.Redirects.Count}");

        for (var i = 0; i < expectedRedirects.Count; i++)
        {
            AssertRedirectEqual(expectedRedirects[i], actual.Redirects[i], diffPrefix + $"redirect[{i}]: ");
        }

        Assert.True(
            expected.IsSubshell == actual.IsSubshell,
            diffPrefix + $"IsSubshell mismatch. expected={expected.IsSubshell}, actual={actual.IsSubshell}");
        Assert.True(
            expected.IsBashCWrapped == actual.IsBashCWrapped,
            diffPrefix + $"IsBashCWrapped mismatch. expected={expected.IsBashCWrapped}, actual={actual.IsBashCWrapped}");
    }

    private static void AssertArgEqual(ExpectedArg expected, Arg actual, string diffPrefix)
    {
        Assert.True(expected.Raw == actual.Raw, diffPrefix + $"Raw mismatch. expected='{expected.Raw}', actual='{actual.Raw}'");
        Assert.True(expected.Kind == actual.Kind, diffPrefix + $"Kind mismatch. expected={expected.Kind}, actual={actual.Kind}");
        Assert.True(expected.IsPath == actual.IsPath, diffPrefix + $"IsPath mismatch. expected={expected.IsPath}, actual={actual.IsPath}");
        // isFlag is computed; assert when present so corpus can document it.
        if (expected.IsFlag.HasValue)
        {
            Assert.True(expected.IsFlag.Value == actual.IsFlag, diffPrefix + $"IsFlag mismatch. expected={expected.IsFlag}, actual={actual.IsFlag}");
        }

        // Resolved comparison: opt-in via the corpus author. Use the
        // sentinel "__NULL__" to assert that Resolved is null; omit the
        // field entirely (default null) to skip the check.
        if (expected.Resolved is not null)
        {
            if (expected.Resolved == "__NULL__")
            {
                Assert.True(actual.Resolved is null, diffPrefix + $"Resolved expected null, actual='{actual.Resolved}'");
            }
            else
            {
                Assert.True(expected.Resolved == actual.Resolved, diffPrefix + $"Resolved mismatch. expected='{expected.Resolved}', actual='{actual.Resolved}'");
            }
        }
    }

    private static void AssertRedirectEqual(ExpectedRedirect expected, Redirect actual, string diffPrefix)
    {
        Assert.True(expected.Direction == actual.Direction, diffPrefix + $"Direction mismatch. expected={expected.Direction}, actual={actual.Direction}");
        Assert.True(expected.Target == actual.Target, diffPrefix + $"Target mismatch. expected='{expected.Target}', actual='{actual.Target}'");
        if (expected.IsDynamicSkip.HasValue)
        {
            Assert.True(expected.IsDynamicSkip.Value == actual.IsDynamicSkip, diffPrefix + $"IsDynamicSkip mismatch. expected={expected.IsDynamicSkip}, actual={actual.IsDynamicSkip}");
        }
    }

    private static string DumpActual(ParsedCommand actual) =>
        JsonSerializer.Serialize(new
        {
            actual.Source,
            actual.IsUnparseable,
            actual.UnparseableReason,
            Clauses = actual.Clauses.Select(c => new
            {
                Operator = c.Operator.ToString(),
                Verb = c.Verb.Tokens,
                Args = c.Args.Select(a => new { a.Raw, Kind = a.Kind.ToString(), a.IsPath, a.IsFlag }),
                Redirects = c.Redirects.Select(r => new { Direction = r.Direction.ToString(), r.Target, r.IsDynamicSkip }),
                c.IsSubshell,
                c.IsBashCWrapped,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
}

// -------- corpus DTOs (JSON shape; PR 6 may move into a shared file) --------

public sealed record CorpusEntry
{
    public string Name { get; init; } = "";

    public string Input { get; init; } = "";

    public ExpectedParsedCommand? Expected { get; init; }

    public string? Notes { get; init; }
}

public sealed record ExpectedParsedCommand
{
    public bool IsUnparseable { get; init; }

    public string? UnparseableReasonContains { get; init; }

    public List<ExpectedClause>? Clauses { get; init; }
}

public sealed record ExpectedClause
{
    public CompoundOperator Operator { get; init; }

    public List<string>? Verb { get; init; }

    public List<ExpectedArg>? Args { get; init; }

    public List<ExpectedRedirect>? Redirects { get; init; }

    public bool IsSubshell { get; init; }

    public bool IsBashCWrapped { get; init; }
}

public sealed record ExpectedArg
{
    public string Raw { get; init; } = "";

    public ArgKind Kind { get; init; } = ArgKind.Literal;

    public bool IsPath { get; init; }

    public bool? IsFlag { get; init; }

    /// <summary>
    /// Expected <see cref="Arg.Resolved"/> value. Omit (leave null) to skip
    /// the comparison; provide explicitly (including empty string) to pin
    /// a literal value. The corpus author may use the special sentinel
    /// <c>"__NULL__"</c> to assert that Resolved is null.
    /// </summary>
    public string? Resolved { get; init; }
}

public sealed record ExpectedRedirect
{
    public RedirectDirection Direction { get; init; }

    public string Target { get; init; } = "";

    public bool? IsDynamicSkip { get; init; }
}
