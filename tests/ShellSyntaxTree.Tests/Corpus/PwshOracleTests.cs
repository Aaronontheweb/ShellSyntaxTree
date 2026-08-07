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
/// The real-<c>pwsh</c> validation gate (SPEC.POWERSHELL.md §13). Feeds
/// every PowerShell corpus <c>input</c> to the real PowerShell parser and
/// enforces the §13 oracle matrix:
/// <list type="bullet">
///   <item><c>isUnparseable=false</c> → real <c>pwsh</c> reports 0 parse
///         errors (the input is valid PowerShell).</item>
///   <item><c>isUnparseable=true</c> + <c>SyntaxError</c> → ≥1 parse error.</item>
///   <item><c>isUnparseable=true</c> + <c>OutOfScope</c> → 0 parse errors
///         (valid PowerShell the parser declines to model).</item>
/// </list>
/// A developer without <c>pwsh</c> on PATH sees the gate skip; CI installs
/// <c>pwsh</c> and a workflow step fails loudly if it is absent.
/// </summary>
public class PwshOracleTests
{
    [Fact]
    public void PowerShell_corpus_inputs_are_consistent_with_real_pwsh()
    {
        var entries = LoadPowershellCorpus();
        if (entries.Count == 0)
        {
            return; // CorpusRunnerTests asserts the corpus is present.
        }

        if (!PwshOracle.IsAvailable())
        {
            Console.WriteLine("pwsh not on PATH — the §13 oracle gate is skipped locally.");
            return;
        }

        var inputs = entries.Select(e => e.Entry.Input).ToList();
        var counts = PwshOracle.CountParseErrors(inputs);
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
                    failures.Add($"{file}: corpus marks it parseable, but real pwsh reports {errors} parse error(s).");
                }
            }
            else if (entry.OracleExpectation == OracleExpectation.SyntaxError)
            {
                if (errors == 0)
                {
                    failures.Add($"{file}: corpus marks it SyntaxError, but real pwsh accepts it (0 errors) — use oracleExpectation 'OutOfScope'.");
                }
            }
            else // OutOfScope
            {
                if (errors != 0)
                {
                    failures.Add($"{file}: corpus marks it OutOfScope, but real pwsh reports {errors} parse error(s) — it is a genuine SyntaxError.");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new XunitException(
                "The pwsh oracle gate found corpus entries inconsistent with real PowerShell:\n"
                + string.Join("\n", failures.Select(f => "  - " + f)));
        }
    }

    [Fact]
    public void PwshAliases_table_covers_every_live_alias()
    {
        if (!PwshOracle.IsAvailable())
        {
            Console.WriteLine("pwsh not on PATH — the §6.3 alias completeness gate is skipped locally.");
            return;
        }

        var live = PwshOracle.GetAliasNames();
        Assert.NotNull(live);

        var gaps = live!
            .Where(name => !PwshAliases.NeverAliased.Contains(name))
            .Where(name => !PwshAliases.Map.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (gaps.Length > 0)
        {
            throw new XunitException(
                "PwshAliases is missing live Get-Alias entries (SPEC.POWERSHELL.md §6.3):\n  "
                + string.Join(", ", gaps));
        }
    }

    [Fact]
    public void Foreach_binding_boundary_covers_every_fresh_host_variable()
    {
        if (!PwshOracle.IsAvailable())
        {
            Console.WriteLine("pwsh not on PATH — the foreach binding gate is skipped locally.");
            return;
        }

        var live = PwshOracle.GetVariableNames();
        Assert.NotNull(live);

        var gaps = live!
            .Where(IsSimpleLoopBindingName)
            .Where(PwshForEachValueAnalysis.IsEligibleBindingName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.True(
            gaps.Length == 0,
            "The isolated foreach binding boundary is missing fresh-host variables: "
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
