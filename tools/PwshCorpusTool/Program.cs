// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using ShellSyntaxTree;
using ShellSyntaxTree.Tools.PwshCorpus;

// PwshCorpusTool — the PowerShell corpus authoring aid (SPEC.POWERSHELL.md
// §13). Two modes:
//
//   generate [outputDir]   Regenerate every Corpus/powershell/NNN_slug.json
//                          from the curated CorpusManifest.
//   check "<command>"      Print the parser's expected-AST JSON block for a
//                          command beside the real-pwsh oracle verdict.
//   check-bash "<command>" Print Bash parser expectations using the same
//                          resolver settings as the executable corpus.

// The corpus runner pins these resolver knobs; generation must match.
var options = new PwshParserOptions
{
    HomeDirectory = "C:/Users/user",
    WorkingDirectory = "C:/work",
};
var parser = new PwshParser(options);
var bashParser = new BashParser(new BashParserOptions
{
    HomeDirectory = "/home/test",
    WorkingDirectory = "/work",
    InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
});

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

switch (args[0].ToLowerInvariant())
{
    case "generate":
        return Generate(args.Length > 1 ? args[1] : DefaultCorpusDir());
    case "check":
        return Check(string.Join(' ', args.Skip(1)));
    case "check-bash":
        return CheckBash(string.Join(' ', args.Skip(1)));
    default:
        PrintUsage();
        return 1;
}

int Generate(string outputDir)
{
    Directory.CreateDirectory(outputDir);

    // Regenerate fully — drop stale entries so a removed manifest line does
    // not leave an orphan corpus file behind.
    foreach (var stale in Directory.GetFiles(outputDir, "*.json"))
    {
        File.Delete(stale);
    }

    var entries = CorpusManifest.All();
    var index = 1;
    foreach (var entry in entries)
    {
        var input = entry.ResolveInput();
        var parsed = parser.Parse(input);
        var json = CorpusJson.BuildEntry(
            entry.Name,
            input,
            parsed,
            entry.Notes,
            entry.OutOfScope,
            entry.IncludeElements,
            entry.IncludeStructure,
            entry.IncludeOptionalAssertions,
            includeV03Assertions: false);
        var fileName = $"{index:D3}_{entry.Slug}.json";
        File.WriteAllText(Path.Combine(outputDir, fileName), json);
        index++;
    }

    Console.WriteLine($"Generated {entries.Count} PowerShell corpus entries into {outputDir}");
    return 0;
}

int Check(string command)
{
    if (string.IsNullOrEmpty(command))
    {
        Console.Error.WriteLine("check: supply a command string.");
        return 1;
    }

    var parsed = parser.Parse(command);
    Console.WriteLine("---- parser expected AST ----");
    Console.WriteLine(CorpusJson.BuildEntry(
        "check",
        command,
        parsed,
        "ad-hoc check",
        parsed.IsUnparseable,
        includeElements: true,
        includeStructure: true,
        includeOptionalAssertions: true,
        includeV03Assertions: false));

    Console.WriteLine("---- real pwsh oracle ----");
    var counts = PwshOracle.CountParseErrors(new[] { command });
    if (counts is null)
    {
        Console.WriteLine("pwsh not available — oracle skipped.");
    }
    else
    {
        var errors = counts[0];
        Console.WriteLine(errors == 0
            ? "pwsh: 0 parse errors (valid PowerShell)."
            : $"pwsh: {errors} parse error(s) (malformed PowerShell).");
    }

    Console.WriteLine($"parser: IsUnparseable={parsed.IsUnparseable}"
        + (parsed.UnparseableReason is null ? "" : $" — {parsed.UnparseableReason}"));
    return 0;
}

int CheckBash(string command)
{
    if (string.IsNullOrEmpty(command))
    {
        Console.Error.WriteLine("check-bash: supply a command string.");
        return 1;
    }

    var parsed = bashParser.Parse(command);
    Console.WriteLine(CorpusJson.BuildEntry(
        "check",
        command,
        parsed,
        "ad-hoc check",
        outOfScope: false,
        includeElements: false,
        includeStructure: true,
        includeOptionalAssertions: true,
        includeV03Assertions: true));
    return 0;
}

static string DefaultCorpusDir() => Path.Combine(
    "tests", "ShellSyntaxTree.Tests", "Corpus", "powershell");

static void PrintUsage()
{
    Console.WriteLine("PwshCorpusTool — PowerShell corpus authoring aid");
    Console.WriteLine();
    Console.WriteLine("  generate [outputDir]   Regenerate the Corpus/powershell/ entries.");
    Console.WriteLine("  check \"<command>\"      Show the parser AST + the real-pwsh verdict.");
    Console.WriteLine("  check-bash \"<command>\" Show the Bash parser AST for corpus authoring.");
}
