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
/// Drives the JSON corpus under <c>tests/ShellSyntaxTree.Tests/Corpus/</c>.
/// The corpus is <em>directory-routed by shell</em> (SPEC.POWERSHELL.md
/// §13): an entry under <c>Corpus/bash/</c> is parsed with
/// <see cref="BashParser"/>, an entry under <c>Corpus/powershell/</c> with
/// <see cref="PwshParser"/>. Each entry pairs an <c>input</c> string with an
/// <c>expected</c> <see cref="ParsedCommand"/> shape; <see cref="AstAssert.Equal"/>
/// drives the field-by-field comparison.
/// </summary>
public class CorpusRunnerTests
{
    [Theory]
    [MemberData(nameof(CorpusEntries))]
    public void Corpus_entry_parses_to_expected_ast(string shell, string fileName, CorpusEntry entry)
    {
        Assert.NotNull(entry);
        Assert.False(string.IsNullOrEmpty(entry.Name), $"Corpus entry {fileName} has no name.");
        Assert.NotNull(entry.Expected);

        var actual = CreateParser(shell).Parse(entry.Input);
        AstAssert.Equal(entry.Expected!, actual, $"{shell}/{fileName}");
    }

    /// <summary>
    /// The parser for a corpus shell directory. HomeDirectory and
    /// WorkingDirectory are pinned so entries with relative-path resolution
    /// have stable expected values across hosts (Linux CI, Windows CI, dev
    /// machines).
    /// </summary>
    internal static IShellParser CreateParser(string shell) => shell switch
    {
        "bash" => new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
        }),
        "powershell" => new PwshParser(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/user",
            WorkingDirectory = "C:/work",
        }),
        _ => throw new InvalidOperationException(
            $"No parser is registered for corpus shell directory '{shell}'."),
    };

    public static IEnumerable<object[]> CorpusEntries()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Corpus");
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var shellDir in Directory.GetDirectories(root).OrderBy(d => d))
        {
            var shell = Path.GetFileName(shellDir);
            foreach (var file in Directory.GetFiles(shellDir, "*.json").OrderBy(f => f))
            {
                CorpusEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<CorpusEntry>(
                        File.ReadAllText(file), JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to deserialize corpus entry {shell}/{Path.GetFileName(file)}: {ex.Message}", ex);
                }

                if (entry is null)
                {
                    throw new InvalidOperationException(
                        $"Corpus entry {shell}/{Path.GetFileName(file)} deserialized to null.");
                }

                yield return new object[] { shell, Path.GetFileName(file), entry };
            }
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
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

    /// <summary>
    /// Ground-truth expectation for the real-<c>pwsh</c> validation gate
    /// (SPEC.POWERSHELL.md §13). Meaningful only when
    /// <c>expected.isUnparseable</c> is true; defaults to
    /// <see cref="OracleExpectation.SyntaxError"/>.
    /// </summary>
    public OracleExpectation OracleExpectation { get; init; } = OracleExpectation.SyntaxError;
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

    /// <summary>
    /// Expected <see cref="VerbChain.CanonicalVerb"/>. Omit (leave null) to
    /// assert the parser produced <c>null</c> — every bash clause and every
    /// PowerShell clause whose verb is a canonical cmdlet or unknown command.
    /// Provide the canonical cmdlet to assert an alias was resolved. See
    /// SPEC.POWERSHELL.md §13.
    /// </summary>
    public string? CanonicalVerb { get; init; }

    /// <summary>
    /// Expected <see cref="VerbChain.IsDynamic"/>. Omit to assert
    /// <c>false</c>; set true for a dynamic command name (<c>&amp; $exe</c>).
    /// </summary>
    public bool IsDynamic { get; init; }

    public List<ExpectedArg>? Args { get; init; }

    public List<ExpectedRedirect>? Redirects { get; init; }

    public bool IsSubshell { get; init; }

    public bool IsCommandStringWrapped { get; init; }
}

/// <summary>
/// Ground-truth expectation for the real-<c>pwsh</c> validation gate
/// (SPEC.POWERSHELL.md §13). Meaningful only when <c>isUnparseable</c> is
/// true.
/// </summary>
public enum OracleExpectation
{
    /// <summary>Genuinely malformed PowerShell — real <c>pwsh</c> must also
    /// reject it.</summary>
    SyntaxError,

    /// <summary>Valid PowerShell the parser deliberately does not model —
    /// real <c>pwsh</c> must accept it. Also covers an over-cap input, an
    /// <c>-EncodedCommand</c> decode failure, or a recursion overflow.</summary>
    OutOfScope,
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
