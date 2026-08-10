// -----------------------------------------------------------------------
// <copyright file="ParserLanguageBoundaryTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree.Internal.Pwsh.Verbs;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class ParserLanguageBoundaryTests
{
    [Fact]
    public void Bash_does_not_parse_a_pwsh_command_payload()
    {
        var parsed = new BashParser().Parse(
            "pwsh -NoProfile -Command 'Get-Content input.txt | Set-Content output.txt'");

        var occurrence = Assert.Single(parsed.Commands);
        Assert.Equal("pwsh", occurrence.Clause.Verb.Joined);
        Assert.Contains(
            occurrence.Clause.Args,
            argument => argument.Raw == "'Get-Content input.txt | Set-Content output.txt'");
        Assert.DoesNotContain(parsed.Commands, candidate =>
            candidate.Clause.Verb.Joined is "Get-Content" or "Set-Content");
    }

    [Fact]
    public void PowerShell_does_not_parse_a_bash_command_payload()
    {
        var parsed = new PwshParser().Parse("bash -c 'rm target.txt'");

        var occurrence = Assert.Single(parsed.Commands);
        Assert.Equal("bash", occurrence.Clause.Verb.Joined);
        Assert.Contains(occurrence.Clause.Args, argument => argument.Raw == "'rm target.txt'");
        Assert.DoesNotContain(parsed.Commands, candidate => candidate.Clause.Verb.Joined == "rm");
    }

    [Fact]
    public void PowerShell7_remains_the_default_dialect()
    {
        var parsed = new PwshParser().Parse("Get-Item a && Get-Item b");

        Assert.False(parsed.IsUnparseable);
        Assert.Equal(2, parsed.Commands.Count);
    }

    [Theory]
    [InlineData("Get-Item a && Get-Item b")]
    [InlineData("Get-Item a || Get-Item b")]
    public void WindowsPowerShell51_rejects_pipeline_chain_operators(string source)
    {
        var parsed = Parse(PwshDialect.WindowsPowerShell51, source);

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("PowerShell 7 dialect", parsed.UnparseableReason);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [Theory]
    [InlineData("foreach ($x in 1) { Get-Item a && Get-Item b }")]
    [InlineData("& { Get-Item a && Get-Item b }")]
    [InlineData("ForEach-Object -Process { Get-Item a && Get-Item b }")]
    [InlineData("$(Get-Item a && Get-Item b)")]
    [InlineData("$(Get-Item a || Get-Item b)")]
    [InlineData("Invoke-Expression 'Get-Item a && Get-Item b'")]
    [InlineData("powershell.exe -Command 'Get-Item a && Get-Item b'")]
    public void WindowsPowerShell51_rejects_pipeline_chains_in_every_nested_parse_path(
        string source)
    {
        var parsed = Parse(PwshDialect.WindowsPowerShell51, source);

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("PowerShell 7 dialect", parsed.UnparseableReason);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [Fact]
    public void PowerShell7_retains_pipeline_chains_inside_nested_regions()
    {
        var parsed = Parse(
            PwshDialect.PowerShell7,
            "& { Get-Item a && Get-Item b }");

        Assert.False(parsed.IsUnparseable);
        Assert.Equal(2, parsed.Commands.Count);
    }

    [Theory]
    [InlineData(PwshDialect.Unknown)]
    [InlineData((PwshDialect)999)]
    public void Unsupported_dialect_values_fail_closed(PwshDialect dialect)
    {
        var parsed = Parse(dialect, "Get-Date");

        Assert.True(parsed.IsUnparseable);
        Assert.Contains("unsupported PowerShell dialect", parsed.UnparseableReason);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [Fact]
    public void WindowsPowerShell51_uses_its_curl_alias()
    {
        var parsed = Parse(PwshDialect.WindowsPowerShell51, "curl example.test");

        var occurrence = Assert.Single(parsed.Commands);
        Assert.Equal("curl", occurrence.Clause.Verb.Joined);
        Assert.Equal("Invoke-WebRequest", occurrence.Clause.Verb.CanonicalVerb);
    }

    [Fact]
    public void PowerShell7_keeps_curl_native()
    {
        var parsed = Parse(PwshDialect.PowerShell7, "curl example.test");

        var occurrence = Assert.Single(parsed.Commands);
        Assert.Null(occurrence.Clause.Verb.CanonicalVerb);
    }

    [Theory]
    [InlineData("asnp", "Add-PSSnapIn")]
    [InlineData("gwmi", "Get-WmiObject")]
    [InlineData("ise", "powershell_ise.exe")]
    [InlineData("trcm", "Trace-Command")]
    public void WindowsPowerShell51_uses_edition_specific_aliases(
        string alias,
        string canonical)
    {
        var parsed = Parse(PwshDialect.WindowsPowerShell51, alias);

        var occurrence = Assert.Single(parsed.Commands);
        Assert.Equal(alias, occurrence.Clause.Verb.Joined);
        Assert.Equal(canonical, occurrence.Clause.Verb.CanonicalVerb);
    }

    [Theory]
    [InlineData("gerr")]
    [InlineData("chy")]
    public void WindowsPowerShell51_does_not_borrow_non_51_aliases(string command)
    {
        var parsed = Parse(PwshDialect.WindowsPowerShell51, command);

        var occurrence = Assert.Single(parsed.Commands);
        Assert.Null(occurrence.Clause.Verb.CanonicalVerb);
    }

    [Fact]
    public void Canonical_alias_targets_are_also_dialect_local()
    {
        Assert.True(PwshAliases.IsKnownCanonical(
            "Get-WmiObject", PwshDialect.WindowsPowerShell51));
        Assert.False(PwshAliases.IsKnownCanonical(
            "Get-WmiObject", PwshDialect.PowerShell7));
        Assert.True(PwshAliases.IsKnownCanonical(
            "Get-Error", PwshDialect.PowerShell7));
        Assert.False(PwshAliases.IsKnownCanonical(
            "Get-Error", PwshDialect.WindowsPowerShell51));
    }

    [Fact]
    public void WindowsPowerShell51_dialect_survives_nested_current_scope_parsing()
    {
        var parsed = Parse(PwshDialect.WindowsPowerShell51, "$(curl example.test)");

        var occurrence = Assert.Single(parsed.Commands);
        Assert.Equal("Invoke-WebRequest", occurrence.Clause.Verb.CanonicalVerb);
    }

    [Fact]
    public void Static_pwsh_child_switches_to_PowerShell7()
    {
        var parsed = Parse(
            PwshDialect.WindowsPowerShell51,
            "pwsh -Command 'Get-Item a && Get-Item b'");

        Assert.False(parsed.IsUnparseable);
        Assert.Equal(2, parsed.Commands.Count);
        Assert.All(parsed.Commands, occurrence => Assert.True(occurrence.Clause.IsCommandStringWrapped));
    }

    [Fact]
    public void Static_powershell_child_switches_to_WindowsPowerShell51()
    {
        var parsed = Parse(
            PwshDialect.PowerShell7,
            "powershell.exe -Command 'Get-Item a && Get-Item b'");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [Fact]
    public void WindowsPowerShell51_does_not_borrow_parallel_receiver_metadata()
    {
        var parsed = Parse(
            PwshDialect.WindowsPowerShell51,
            "ForEach-Object -Parallel { Get-Date }");

        Assert.False(parsed.IsUnparseable);
        Assert.Contains(parsed.Commands, occurrence =>
            occurrence.Clause.Verb.Joined == "ForEach-Object" && !occurrence.IsComplete);
        Assert.Contains(parsed.Commands, occurrence =>
            occurrence.Clause.Verb.Joined == "Get-Date" && !occurrence.IsComplete);
    }

    private static ParsedCommand Parse(PwshDialect dialect, string source) =>
        new PwshParser(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/user",
            WorkingDirectory = "C:/work",
            Dialect = dialect,
        }).Parse(source);
}
