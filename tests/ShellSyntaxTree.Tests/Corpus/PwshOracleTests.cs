// -----------------------------------------------------------------------
// <copyright file="PwshOracleTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShellSyntaxTree.Internal.Pwsh.Verbs;
using ShellSyntaxTree.Internal.Pwsh.Parsing;
using ShellSyntaxTree.Tools.PwshCorpus;
using Xunit;
using Xunit.Sdk;

namespace ShellSyntaxTree.Tests.Corpus;

/// <summary>
/// The dialect-selected PowerShell validation gate (SPEC.POWERSHELL.md §13).
/// Feeds every PowerShell corpus <c>input</c> to the matching real parser and
/// enforces the §13 oracle matrix:
/// <list type="bullet">
///   <item><c>isUnparseable=false</c> → real PowerShell reports 0 parse
///         errors (the input is valid PowerShell).</item>
///   <item><c>isUnparseable=true</c> + <c>SyntaxError</c> → ≥1 parse error.</item>
///   <item><c>isUnparseable=true</c> + <c>OutOfScope</c> → 0 parse errors
///         (valid PowerShell the parser declines to model).</item>
/// </list>
/// A developer without the selected executable on PATH sees that dialect's
/// gate skip; CI verifies both supported executables where they are required.
/// </summary>
public class PwshOracleTests
{
    [Fact]
    public void PowerShell_corpus_inputs_are_consistent_with_selected_dialect_oracle()
    {
        var entries = LoadPowershellCorpus();
        if (entries.Count == 0)
        {
            return; // CorpusRunnerTests asserts the corpus is present.
        }

        foreach (var dialect in new[]
                 {
                     PwshDialect.PowerShell7,
                     PwshDialect.WindowsPowerShell51,
                 })
        {
            ValidateDialectCorpus(
                dialect,
                entries.Where(entry =>
                    (entry.Entry.PowerShellDialect ?? PwshDialect.PowerShell7) == dialect)
                    .ToList());
        }
    }

    private static void ValidateDialectCorpus(
        PwshDialect dialect,
        IReadOnlyList<(string File, CorpusEntry Entry)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (!PwshOracle.IsAvailable(dialect))
        {
            Console.WriteLine(
                $"{dialect} compatible oracle is unavailable — its §13 gate is skipped locally.");
            return;
        }

        var inputs = entries.Select(e => e.Entry.Input).ToList();
        var counts = PwshOracle.CountParseErrors(inputs, dialect);
        Assert.NotNull(counts);

        var failures = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var (file, entry) = entries[i];
            var errors = counts![i];
            var expected = entry.Expected!;

            if (!expected.IsUnparseable)
            {
                if (errors != 0)
                {
                    failures.Add($"{file}: corpus marks it parseable, but {dialect} reports {errors} parse error(s).");
                }
            }
            else if (entry.OracleExpectation == OracleExpectation.SyntaxError)
            {
                if (errors == 0)
                {
                    failures.Add($"{file}: corpus marks it SyntaxError, but {dialect} accepts it (0 errors) — use oracleExpectation 'OutOfScope'.");
                }
            }
            else // OutOfScope
            {
                if (errors != 0)
                {
                    failures.Add($"{file}: corpus marks it OutOfScope, but {dialect} reports {errors} parse error(s) — it is a genuine SyntaxError.");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new XunitException(
                $"The {dialect} oracle gate found corpus entries inconsistent with real PowerShell:\n"
                + string.Join("\n", failures.Select(f => "  - " + f)));
        }
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void PwshAliases_table_covers_every_live_alias(PwshDialect dialect)
    {
        if (!PwshOracle.IsAvailable(dialect))
        {
            Console.WriteLine(
                $"{dialect} compatible oracle is unavailable — its §6.3 alias gate is skipped locally.");
            return;
        }

        var live = PwshOracle.GetAliasDefinitions(dialect);
        Assert.NotNull(live);

        var gaps = live!
            .Where(alias => dialect != PwshDialect.PowerShell7 ||
                            !PwshAliases.PowerShell7NativeCollisions.Contains(alias.Name))
            .Where(alias => PwshAliases.Resolve(alias.Name, dialect) is null)
            .OrderBy(alias => alias.Name, StringComparer.Ordinal)
            .Select(alias => alias.Name)
            .ToArray();

        if (gaps.Length > 0)
        {
            throw new XunitException(
                $"PwshAliases is missing {dialect} Get-Alias entries " +
                "(SPEC.POWERSHELL.md §6.3):\n  "
                + string.Join(", ", gaps));
        }

        var mismatches = live
            .Where(alias => dialect != PwshDialect.PowerShell7 ||
                            !PwshAliases.PowerShell7NativeCollisions.Contains(alias.Name))
            .Select(alias => new
            {
                alias.Name,
                Live = NormalizeAliasDefinition(alias.Definition),
                Parsed = PwshAliases.Resolve(alias.Name, dialect),
            })
            .Where(alias => alias.Parsed is not null &&
                            !string.Equals(alias.Live, alias.Parsed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(alias => alias.Name, StringComparer.Ordinal)
            .Select(alias => $"{alias.Name}: live={alias.Live}, parser={alias.Parsed}")
            .ToArray();

        if (mismatches.Length > 0)
        {
            throw new XunitException(
                $"PwshAliases has incorrect {dialect} canonical definitions " +
                "(SPEC.POWERSHELL.md §6.3):\n  "
                + string.Join("\n  ", mismatches));
        }
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void Foreach_binding_boundary_covers_every_fresh_host_variable(
        PwshDialect dialect)
    {
        if (!PwshOracle.IsAvailable(dialect))
        {
            Console.WriteLine(
                $"{dialect} compatible oracle is unavailable — its foreach binding gate is skipped locally.");
            return;
        }

        var live = PwshOracle.GetVariableNames(dialect);
        Assert.NotNull(live);

        var gaps = live!
            .Where(IsSimpleLoopBindingName)
            .Where(PwshForEachValueAnalysis.IsEligibleBindingName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.True(
            gaps.Length == 0,
            $"The isolated {dialect} foreach binding boundary is missing fresh-host variables: "
            + string.Join(", ", gaps));
    }

    private static bool IsSimpleLoopBindingName(string name)
    {
        if (name.Length == 0 || name[0] != '_' && !char.IsLetter(name[0]))
        {
            return false;
        }

        return name.Skip(1).All(character => character == '_' || char.IsLetterOrDigit(character));
    }

    private static string NormalizeAliasDefinition(string definition)
    {
        var separator = definition.LastIndexOf('\\');
        var unqualified = separator >= 0 ? definition.Substring(separator + 1) : definition;
        if (string.Equals(unqualified, "help", StringComparison.OrdinalIgnoreCase))
        {
            return "Get-Help";
        }

        if (string.Equals(unqualified, "mkdir", StringComparison.OrdinalIgnoreCase))
        {
            return "New-Item";
        }

        return unqualified;
    }

    private static List<(string File, CorpusEntry Entry)> LoadPowershellCorpus()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Corpus", "powershell");
        var result = new List<(string, CorpusEntry)>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
        {
            var entry = JsonSerializer.Deserialize<CorpusEntry>(
                File.ReadAllText(file), CorpusRunnerTests.JsonOptions);
            if (entry?.Expected is not null)
            {
                result.Add((Path.GetFileName(file), entry));
            }
        }

        return result;
    }
}
