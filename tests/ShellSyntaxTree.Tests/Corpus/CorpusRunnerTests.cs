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
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
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
        AssertClauseElementInvariants(actual, $"{shell}/{fileName}");
        AssertAuthoredTokenCoverage(shell, actual, $"{shell}/{fileName}");
    }

    private static void AssertAuthoredTokenCoverage(
        string shell, ParsedCommand parsed, string context)
    {
        if (parsed.IsUnparseable)
        {
            return;
        }

        var elements = parsed.Clauses
            .SelectMany(clause => clause.Elements)
            .Where(element => element.SourceStart.HasValue)
            .ToArray();

        if (shell == "bash")
        {
            var tokens = BashLexer.Tokenize(parsed.Source);
            var directSegments = DirectBashSegments(parsed, tokens);
            var segment = 0;
            var redirectTargetPending = false;
            foreach (var token in tokens)
            {
                if (RequiresBashElement(token))
                {
                    var isRedirectOperator = IsBashRedirectOperator(token);
                    var isRedirectTarget = redirectTargetPending && !isRedirectOperator;
                    AssertTokenCovered(
                        token.SourceStart,
                        token.SourceLength,
                        elements,
                        context,
                        token.Value,
                        !parsed.Clauses.Any(clause => clause.IsCommandStringWrapped)
                        || directSegments.Contains(segment)
                        || isRedirectOperator
                        || isRedirectTarget);
                    redirectTargetPending = isRedirectOperator;
                }

                if (IsBashSegmentSeparator(token))
                {
                    segment++;
                }
            }

            return;
        }

        var pwshTokens = PwshLexer.Tokenize(parsed.Source);
        var pwshDirectSegments = DirectPwshSegments(parsed, pwshTokens);
        var pwshSegment = 0;
        var pwshRedirectTargetPending = false;
        foreach (var token in pwshTokens)
        {
            if (RequiresPwshElement(token))
            {
                var isRedirectOperator = IsPwshRedirectOperator(token);
                var isRedirectTarget = pwshRedirectTargetPending && !isRedirectOperator;
                AssertTokenCovered(
                    token.SourceStart,
                    token.SourceLength,
                    elements,
                    context,
                    token.Value,
                    !parsed.Clauses.Any(clause => clause.IsCommandStringWrapped)
                    || pwshDirectSegments.Contains(pwshSegment)
                    || isRedirectOperator
                    || isRedirectTarget);
                pwshRedirectTargetPending = isRedirectOperator
                    && token.OperatorText is not null
                    && token.OperatorText.IndexOf(">&", StringComparison.Ordinal) < 0;
            }

            if (IsPwshSegmentSeparator(token))
            {
                pwshSegment++;
            }
        }
    }

    private static HashSet<int> DirectBashSegments(
        ParsedCommand parsed, IReadOnlyList<BashToken> tokens)
    {
        var segments = new HashSet<int>();
        foreach (var element in parsed.Clauses
            .Where(clause => !clause.IsCommandStringWrapped)
            .SelectMany(clause => clause.Elements)
            .Where(element => element.SourceStart.HasValue))
        {
            segments.Add(BashSegmentAt(tokens, element.SourceStart!.Value));
        }

        return segments;
    }

    private static int BashSegmentAt(IReadOnlyList<BashToken> tokens, int sourceStart)
    {
        var segment = 0;
        foreach (var token in tokens)
        {
            if (token.SourceStart >= sourceStart)
            {
                break;
            }

            if (IsBashSegmentSeparator(token))
            {
                segment++;
            }
        }

        return segment;
    }

    private static HashSet<int> DirectPwshSegments(
        ParsedCommand parsed, IReadOnlyList<PwshToken> tokens)
    {
        var segments = new HashSet<int>();
        foreach (var element in parsed.Clauses
            .Where(clause => !clause.IsCommandStringWrapped)
            .SelectMany(clause => clause.Elements)
            .Where(element => element.SourceStart.HasValue))
        {
            segments.Add(PwshSegmentAt(tokens, element.SourceStart!.Value));
        }

        return segments;
    }

    private static int PwshSegmentAt(IReadOnlyList<PwshToken> tokens, int sourceStart)
    {
        var segment = 0;
        foreach (var token in tokens)
        {
            if (token.SourceStart >= sourceStart)
            {
                break;
            }

            if (IsPwshSegmentSeparator(token))
            {
                segment++;
            }
        }

        return segment;
    }

    private static bool RequiresBashElement(BashToken token)
    {
        if (token.Kind is BashTokenKind.Whitespace
            or BashTokenKind.Continuation
            or BashTokenKind.Comment
            or BashTokenKind.UnparseableSentinel)
        {
            return false;
        }

        return token.Kind != BashTokenKind.Operator
            || token.OperatorText is ">" or ">>" or "<" or "2>" or "2>>" or "<<" or "<<-";
    }

    private static bool IsBashRedirectOperator(BashToken token) =>
        token.Kind == BashTokenKind.Operator
        && token.OperatorText is ">" or ">>" or "<" or "2>" or "2>>" or "<<" or "<<-";

    private static bool IsBashSegmentSeparator(BashToken token) =>
        (token.Kind == BashTokenKind.Operator
            && token.OperatorText is "&&" or "||" or ";" or "|")
        || (token.Kind == BashTokenKind.Whitespace && token.IsStatementSeparator);

    private static bool RequiresPwshElement(PwshToken token)
    {
        if (token.Kind is PwshTokenKind.Whitespace
            or PwshTokenKind.Continuation
            or PwshTokenKind.Comment
            or PwshTokenKind.UnparseableSentinel)
        {
            return false;
        }

        return token.Kind != PwshTokenKind.Operator
            || token.OperatorText == "<"
            || (token.OperatorText?.IndexOf('>') ?? -1) >= 0;
    }

    private static bool IsPwshRedirectOperator(PwshToken token) =>
        token.Kind == PwshTokenKind.Operator
        && (token.OperatorText == "<" || (token.OperatorText?.IndexOf('>') ?? -1) >= 0);

    private static bool IsPwshSegmentSeparator(PwshToken token) =>
        (token.Kind == PwshTokenKind.Operator
            && token.OperatorText is "&&" or "||" or ";" or "|")
        || (token.Kind == PwshTokenKind.Whitespace && token.IsStatementSeparator);

    private static void AssertTokenCovered(
        int sourceStart,
        int sourceLength,
        IReadOnlyList<ClauseElement> elements,
        string context,
        string tokenValue,
        bool coverageRequired)
    {
        var sourceEnd = sourceStart + sourceLength;
        var coveringElements = elements.Count(element =>
                element.SourceStart!.Value <= sourceStart
                && element.SourceStart.Value + element.SourceLength!.Value >= sourceEnd);
        Assert.True(
            coveringElements == 1 || (!coverageRequired && coveringElements == 0),
            $"{context}: authored token '{tokenValue}' at {sourceStart}:{sourceLength} "
            + $"is covered by {coveringElements} clause elements; expected exactly one.");
    }

    private static void AssertClauseElementInvariants(ParsedCommand parsed, string context)
    {
        foreach (var clause in parsed.Clauses)
        {
            var precedingVerbs = 0;
            var redirectCount = 0;
            var previousSourceStart = -1;

            foreach (var element in clause.Elements)
            {
                Assert.Equal(precedingVerbs, element.PrecedingVerbElementCount);

                if (element.Role == ClauseElementRole.Verb)
                {
                    Assert.True(
                        precedingVerbs < clause.Verb.Tokens.Count,
                        $"{context}: verb element has no matching Verb.Tokens entry.");
                    Assert.Equal(clause.Verb.Tokens[precedingVerbs], element.Value);
                    precedingVerbs++;
                }
                else if (element.Role == ClauseElementRole.Redirect)
                {
                    redirectCount++;
                }

                Assert.Equal(element.SourceStart.HasValue, element.SourceLength.HasValue);
                if (element.SourceStart.HasValue)
                {
                    var sourceStart = element.SourceStart.Value;
                    var sourceLength = element.SourceLength!.Value;
                    Assert.True(sourceStart >= previousSourceStart, $"{context}: element order regressed.");
                    Assert.InRange(sourceStart, 0, parsed.Source.Length);
                    Assert.InRange(sourceLength, 0, parsed.Source.Length - sourceStart);
                    Assert.Equal(element.Raw, parsed.Source.Substring(sourceStart, sourceLength));
                    previousSourceStart = sourceStart;
                }
                else
                {
                    Assert.True(
                        clause.IsCommandStringWrapped,
                        $"{context}: only wrapped clauses may omit outer source spans.");
                }
            }

            Assert.Equal(clause.Verb.Tokens.Count, precedingVerbs);
            Assert.Equal(clause.Redirects.Count, redirectCount);
        }
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

    /// <summary>
    /// Optional issue #62 provenance expectation. Existing corpus entries may
    /// omit it; entries that exercise ordered elements compare the full list.
    /// </summary>
    public List<ExpectedClauseElement>? Elements { get; init; }

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

public sealed record ExpectedClauseElement
{
    public string Raw { get; init; } = "";

    public string Value { get; init; } = "";

    public ClauseElementRole Role { get; init; }

    public int? SourceStart { get; init; }

    public int? SourceLength { get; init; }

    public int PrecedingVerbElementCount { get; init; }

    public ArgKind Kind { get; init; }

    public bool IsFlag { get; init; }

    public bool IsPath { get; init; }

    public string? Resolved { get; init; }
}
