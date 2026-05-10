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
/// <see cref="ParsedCommand"/> shape; <see cref="AstAssert.Equal"/> drives
/// the field-by-field comparison and emits diff-friendly failure messages
/// pointing at the first differing path.
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

        AstAssert.Equal(entry.Expected!, actual, fileName);
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
}

// -------- corpus DTOs (JSON shape) --------

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
    /// the comparison; provide explicitly to pin a literal value. The
    /// corpus author may use the special sentinel <c>"__NULL__"</c> to
    /// assert that Resolved is null.
    /// </summary>
    public string? Resolved { get; init; }

    /// <summary>
    /// Expected <see cref="Arg.IsCwdAttribution"/>. Omit to assert
    /// <c>false</c> (the default for normal user-emitted args); set to
    /// <c>true</c> to assert the synthetic cd-attribution arg that PR 5
    /// appends to subsequent clauses (SPEC §9 / locked interpretation #6).
    /// </summary>
    public bool IsCwdAttribution { get; init; }
}

public sealed record ExpectedRedirect
{
    public RedirectDirection Direction { get; init; }

    public string Target { get; init; } = "";

    public bool? IsDynamicSkip { get; init; }
}
