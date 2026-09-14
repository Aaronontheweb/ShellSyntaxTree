// -----------------------------------------------------------------------
// <copyright file="PwshApprovalFatigueRegressionTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Resolving;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class PwshApprovalFatigueRegressionTests
{
    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void Sanitized_recursive_source_query_has_exact_bounded_operands(
        PwshDialect dialect)
    {
        const string source =
            "Get-ChildItem -Path \"C:\\WORK\\PROJECT\" -Recurse " +
            "-Include *.cs,*.conf,*.json | " +
            "Select-Object -ExpandProperty FullName";

        var result = Parse(source, dialect);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);

        var getChildItem = result.Commands[0];
        var path = Argument(getChildItem, "\"C:\\WORK\\PROJECT\"");
        Assert.True(path.Argument.IsPath);
        Assert.Equal("C:/WORK/PROJECT", path.Argument.Resolved);
        Assert.Equal(
            "C:/WORK/PROJECT",
            Assert.IsType<ShellValueDomain.Exact>(path.AuthoredFileSystemValue).Value);

        var include = Argument(getChildItem, "*.cs,*.conf,*.json");
        Assert.False(include.Argument.IsPath);
        Assert.Null(include.Argument.Resolved);
        Assert.IsType<ShellValueDomain.Unknown>(include.AuthoredValue);
        Assert.Equal(
            new[] { "*.cs", "*.conf", "*.json" },
            Assert.IsType<ShellValueDomain.OrderedList>(
                include.AuthoredNonFileSystemValue).Values);

        var recurse = Argument(getChildItem, "-Recurse");
        Assert.False(recurse.Argument.IsPath);
        Assert.IsType<ShellValueDomain.Unknown>(recurse.AuthoredFileSystemValue);

        var select = result.Commands[1];
        var expandedProperty = Argument(select, "FullName");
        Assert.Equal(
            "FullName",
            Assert.IsType<ShellValueDomain.Exact>(
                expandedProperty.AuthoredNonFileSystemValue).Value);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void Sanitized_content_range_query_uses_the_integer_range_domain(
        PwshDialect dialect)
    {
        const string source =
            "Get-Content \"C:\\WORK\\PROJECT\\SourceFile.cs\" | " +
            "Select-Object -Index (113..145)";

        var result = Parse(source, dialect);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);

        var path = Argument(result.Commands[0], "\"C:\\WORK\\PROJECT\\SourceFile.cs\"");
        Assert.True(path.Argument.IsPath);
        Assert.Equal("C:/WORK/PROJECT/SourceFile.cs", path.Argument.Resolved);
        Assert.Equal(
            "C:/WORK/PROJECT/SourceFile.cs",
            Assert.IsType<ShellValueDomain.Exact>(path.AuthoredFileSystemValue).Value);

        var index = Argument(result.Commands[1], "(113..145)");
        Assert.Equal(ArgKind.DynamicSkip, index.Argument.Kind);
        var effective = Assert.IsType<ShellValueDomain.IntegerRange>(index.Value);
        Assert.Equal(113, effective.MinimumInclusive);
        Assert.Equal(145, effective.MaximumInclusive);
        var authored = Assert.IsType<ShellValueDomain.IntegerRange>(index.AuthoredValue);
        Assert.Equal(113, authored.MinimumInclusive);
        Assert.Equal(145, authored.MaximumInclusive);
    }

    [Theory]
    [InlineData("Select-Object Id,ProcessName,StartTime")]
    [InlineData("Select-Object -Property Id,ProcessName,StartTime")]
    [InlineData("Get-ChildItem -Include *.cs,*.conf,*.json")]
    public void Audited_static_nonfilesystem_groups_publish_ordered_lists(string source)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var group = Assert.Single(
            Assert.Single(result.Commands).Arguments,
            argument => argument.Argument.Raw.Contains(','));
        Assert.IsType<ShellValueDomain.Unknown>(group.Value);
        Assert.IsType<ShellValueDomain.Unknown>(group.AuthoredValue);
        var members = Assert.IsType<ShellValueDomain.OrderedList>(
            group.AuthoredNonFileSystemValue);
        Assert.InRange(members.Values.Count, 2, ShellAnalysisLimits.MaxValueCandidates);
        Assert.IsType<ShellValueDomain.Unknown>(group.AuthoredFileSystemValue);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void Positional_SelectObject_property_lists_require_a_flag_free_shape(
        PwshDialect dialect)
    {
        var plain = Parse("Select-Object Name,Id", dialect);
        var plainList = Assert.IsType<ShellValueDomain.OrderedList>(
            Assert.Single(Assert.Single(plain.Commands).Arguments)
                .AuthoredNonFileSystemValue);
        Assert.Equal(new[] { "Name", "Id" }, plainList.Values);

        var parameterized = Parse("Select-Object -Path C:\\WORK Name,Id", dialect);
        var group = Assert.Single(
            Assert.Single(parameterized.Commands).Arguments,
            argument => argument.Argument.Raw == "Name,Id");
        Assert.IsType<ShellValueDomain.Unknown>(group.AuthoredNonFileSystemValue);
    }

    [Theory]
    [InlineData("Select-Object \"Id,ProcessName\"", "Id,ProcessName")]
    [InlineData("Select-Object Id`,ProcessName", "Id,ProcessName")]
    [InlineData("Get-ChildItem -Include \"*.cs,*.json\"", "*.cs,*.json")]
    public void Quoted_or_escaped_comma_is_one_scalar_value(
        string source,
        string expected)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var value = Assert.Single(
            Assert.Single(result.Commands).Arguments,
            argument => !argument.Argument.IsFlag);
        Assert.Equal(expected, Assert.IsType<ShellValueDomain.Exact>(value.AuthoredValue).Value);
        Assert.Equal(
            expected,
            Assert.IsType<ShellValueDomain.Exact>(
                value.AuthoredNonFileSystemValue).Value);
    }

    [Theory]
    [InlineData("Select-Object Id,$name")]
    [InlineData("Select-Object Id,$(Get-Date)")]
    [InlineData("Select-Object Id,{ $_.Name }")]
    [InlineData("Select-Object Name,@{Name='Display';Expression={$_.Name}}")]
    [InlineData("Select-Object Id,")]
    [InlineData("Select-Object Id,,Name")]
    [InlineData("Select-Object -Prop Id,Name")]
    [InlineData("Get-ChildItem -Inc *.cs,*.json")]
    [InlineData("Get-Process -N process-a,process-b")]
    public void Dynamic_partial_calculated_or_abbreviated_groups_stay_unknown(string source)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        Assert.All(
            result.Commands.SelectMany(command => command.Arguments)
                .Where(argument =>
                    argument.Argument.Kind == ArgKind.DynamicSkip ||
                    argument.Argument.Raw.Contains(',')),
            argument =>
                Assert.IsType<ShellValueDomain.Unknown>(
                    argument.AuthoredNonFileSystemValue));
    }

    [Fact]
    public void GetProcess_exact_name_parameter_publishes_an_ordered_list()
    {
        var result = Parse(
            "Get-Process -Name process-a,process-b -ErrorAction SilentlyContinue",
            PwshDialect.PowerShell7);

        var group = Argument(Assert.Single(result.Commands), "process-a,process-b");
        Assert.Equal(
            new[] { "process-a", "process-b" },
            Assert.IsType<ShellValueDomain.OrderedList>(
                group.AuthoredNonFileSystemValue).Values);
        Assert.IsType<ShellValueDomain.Unknown>(group.Value);
        Assert.IsType<ShellValueDomain.Unknown>(group.AuthoredFileSystemValue);
    }

    [Fact]
    public void Thirty_three_group_members_exceed_the_fixed_cap()
    {
        var values = string.Join(",", Enumerable.Range(1, 33).Select(index => $"P{index}"));

        var result = Parse($"Select-Object {values}", PwshDialect.PowerShell7);

        var group = Assert.Single(Assert.Single(result.Commands).Arguments);
        Assert.IsType<ShellValueDomain.Unknown>(group.AuthoredValue);
        Assert.IsType<ShellValueDomain.Unknown>(group.AuthoredNonFileSystemValue);
    }

    [Theory]
    [InlineData("Select-Object Name,Id,Name")]
    [InlineData("Get-ChildItem -Include Name,Id,Name")]
    [InlineData("Get-Process -Name Name,Id,Name")]
    public void Ordered_lists_preserve_source_order_and_duplicates(string source)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        var group = Assert.Single(
            Assert.Single(result.Commands).Arguments,
            argument => argument.Argument.Raw.Contains(','));
        Assert.Equal(
            new[] { "Name", "Id", "Name" },
            Assert.IsType<ShellValueDomain.OrderedList>(
                group.AuthoredNonFileSystemValue).Values);
        Assert.IsType<ShellValueDomain.Unknown>(group.Value);
        Assert.IsType<ShellValueDomain.Unknown>(group.AuthoredValue);
    }

    [Theory]
    [InlineData("Get-ChildItem -Path C:\\one,C:\\two")]
    [InlineData("Get-ChildItem C:\\one,C:\\two")]
    [InlineData("Select-String -Path C:\\one,C:\\two needle")]
    public void Path_arrays_never_publish_a_single_or_group_filesystem_value(string source)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        Assert.All(
            Assert.Single(result.Commands).Arguments,
            argument => Assert.IsType<ShellValueDomain.Unknown>(
                argument.AuthoredFileSystemValue));
    }

    [Theory]
    [InlineData("Get-ChildItem -Path C:\\WORK\\PROJECT", "C:/WORK/PROJECT")]
    [InlineData("Get-ChildItem -LiteralPath C:\\WORK\\PROJECT", "C:/WORK/PROJECT")]
    [InlineData("Get-ChildItem C:\\WORK\\PROJECT", "C:/WORK/PROJECT")]
    [InlineData("Get-Content -Path C:\\WORK\\PROJECT\\a.cs", "C:/WORK/PROJECT/a.cs")]
    [InlineData("Get-Content C:\\WORK\\PROJECT\\a.cs", "C:/WORK/PROJECT/a.cs")]
    [InlineData("Select-String -Path C:\\WORK\\PROJECT\\a.cs needle", "C:/WORK/PROJECT/a.cs")]
    public void Audited_path_bindings_publish_exact_local_files(string source, string expected)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        var path = Assert.Single(
            Assert.Single(result.Commands).Arguments,
            argument => argument.Argument.Resolved == expected);
        Assert.Equal(
            expected,
            Assert.IsType<ShellValueDomain.Exact>(path.AuthoredFileSystemValue).Value);
        Assert.IsType<ShellValueDomain.Unknown>(path.AuthoredNonFileSystemValue);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "Get-ChildItem -Property x C:\\WORK\\PROJECT")]
    [InlineData(PwshDialect.PowerShell7, "Get-ChildItem -ErrorA Stop C:\\WORK\\PROJECT")]
    [InlineData(PwshDialect.PowerShell7, "Get-ChildItem C:\\WORK\\PROJECT extra")]
    [InlineData(PwshDialect.PowerShell7, "Get-ChildItem -ErrorAction -Force C:\\WORK\\PROJECT")]
    [InlineData(PwshDialect.PowerShell7, "Get-Content -Property x C:\\WORK\\PROJECT\\a.cs")]
    [InlineData(PwshDialect.PowerShell7, "Get-Content -ErrorA Stop C:\\WORK\\PROJECT\\a.cs")]
    [InlineData(PwshDialect.PowerShell7, "Get-Content C:\\WORK\\PROJECT\\a.cs extra")]
    [InlineData(PwshDialect.PowerShell7, "Get-Content -ErrorAction -Force C:\\WORK\\PROJECT\\a.cs")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-ChildItem -Property x C:\\WORK\\PROJECT")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-ChildItem -ErrorA Stop C:\\WORK\\PROJECT")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-ChildItem C:\\WORK\\PROJECT extra")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-ChildItem -ErrorAction -Force C:\\WORK\\PROJECT")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-Content -Property x C:\\WORK\\PROJECT\\a.cs")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-Content -ErrorA Stop C:\\WORK\\PROJECT\\a.cs")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-Content C:\\WORK\\PROJECT\\a.cs extra")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-Content -ErrorAction -Force C:\\WORK\\PROJECT\\a.cs")]
    public void Positional_path_facts_require_an_exact_cmdlet_parameter_binding(
        PwshDialect dialect,
        string source)
    {
        var result = Parse(source, dialect);

        Assert.All(
            Assert.Single(result.Commands).Arguments,
            argument => Assert.IsType<ShellValueDomain.Unknown>(
                argument.AuthoredFileSystemValue));
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "Get-ChildItem -ErrorAction Stop C:\\WORK\\PROJECT")]
    [InlineData(PwshDialect.PowerShell7, "Get-Content -ErrorAction Stop C:\\WORK\\PROJECT\\a.cs")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-ChildItem -ErrorAction Stop C:\\WORK\\PROJECT")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Get-Content -ErrorAction Stop C:\\WORK\\PROJECT\\a.cs")]
    public void Exact_supported_parameters_preserve_positional_path_binding(
        PwshDialect dialect,
        string source)
    {
        var result = Parse(source, dialect);

        Assert.Contains(
            Assert.Single(result.Commands).Arguments,
            argument => argument.AuthoredFileSystemValue is ShellValueDomain.Exact);
    }

    [Theory]
    [InlineData("Get-ChildItem -Path C:\\WORK\\PROJECT\\*.cs")]
    [InlineData("Get-ChildItem C:\\WORK\\*\\a.cs")]
    [InlineData("Select-String -Path C:\\WORK\\PROJECT\\*.cs needle")]
    [InlineData("Select-String needle C:\\WORK\\PROJECT\\a.cs")]
    public void Wildcard_or_unaudited_positional_paths_stay_strict(string source)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        Assert.Contains(
            Assert.Single(result.Commands).Arguments,
            argument => argument.Argument.Kind == ArgKind.Glob || argument.Argument.IsPath);
        Assert.All(
            Assert.Single(result.Commands).Arguments,
            argument => Assert.IsType<ShellValueDomain.Unknown>(
                argument.AuthoredFileSystemValue));
    }

    [Theory]
    [InlineData("Select-Object -Index (113..$end)")]
    [InlineData("Select-Object -Index (113..$(Get-Date))")]
    [InlineData("Select-Object -Index (113+1..145)")]
    [InlineData("Select-Object -Index ((113..145))")]
    [InlineData("Select-Object -Index (113..145; netclaw daemon stop)")]
    public void Index_range_never_swallows_dynamic_arithmetic_or_nested_commands(string source)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Theory]
    [InlineData("Select-Object -Index (-2..3)", -2, 3)]
    [InlineData("Select-Object -Index (145..113)", 113, 145)]
    public void Fixed_signed_and_descending_ranges_publish_set_bounds(
        string source,
        long expectedMinimum,
        long expectedMaximum)
    {
        var result = Parse(source, PwshDialect.PowerShell7);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var range = Assert.IsType<ShellValueDomain.IntegerRange>(
            Assert.Single(
                Assert.Single(result.Commands).Arguments,
                argument => !argument.Argument.IsFlag).Value);
        Assert.Equal(expectedMinimum, range.MinimumInclusive);
        Assert.Equal(expectedMaximum, range.MaximumInclusive);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "Select-Object -Index (113`..145)")]
    [InlineData(PwshDialect.PowerShell7, "Select-Object -Index (1`13..145)")]
    [InlineData(PwshDialect.PowerShell7, "Select-Object -Index (113..1`45)")]
    [InlineData(PwshDialect.PowerShell7, "Select-Object -Index '(113..145)'")]
    [InlineData(PwshDialect.PowerShell7, "Select-Object -Index:(113..145)")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Select-Object -Index (113`..145)")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Select-Object -Index (1`13..145)")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Select-Object -Index (113..1`45)")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Select-Object -Index '(113..145)'")]
    [InlineData(PwshDialect.WindowsPowerShell51, "Select-Object -Index:(113..145)")]
    public void Noncanonical_range_spellings_never_publish_integer_bounds(
        PwshDialect dialect,
        string source)
    {
        var result = Parse(source, dialect);

        Assert.DoesNotContain(
            result.Commands.SelectMany(command => command.Arguments),
            argument => argument.Value is ShellValueDomain.IntegerRange ||
                argument.AuthoredValue is ShellValueDomain.IntegerRange);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "pwsh")]
    [InlineData(PwshDialect.WindowsPowerShell51, "powershell.exe")]
    public void Static_wrappers_never_promote_escaped_ranges_to_integer_bounds(
        PwshDialect dialect,
        string host)
    {
        var escaped = Parse(
            $"{host} -Command 'Select-Object -Index (113`..145)'",
            dialect);

        Assert.DoesNotContain(
            escaped.Commands.SelectMany(command => command.Arguments),
            argument => argument.Value is ShellValueDomain.IntegerRange ||
                argument.AuthoredValue is ShellValueDomain.IntegerRange);
    }

    [Fact]
    public void Ordered_lists_are_rejected_from_scalar_domain_slots()
    {
        var parsed = Parse("Select-Object Name,Id", PwshDialect.PowerShell7);
        var argument = Assert.Single(Assert.Single(parsed.Commands).Arguments);
        var list = Assert.IsType<ShellValueDomain.OrderedList>(
            argument.AuthoredNonFileSystemValue);

        Assert.False(AuthoredOperandSemanticsProjection.HasValidDomains(new[]
        {
            argument with { Value = list },
        }));
        Assert.False(AuthoredOperandSemanticsProjection.HasValidDomains(new[]
        {
            argument with { AuthoredValue = list },
        }));
        Assert.False(AuthoredOperandSemanticsProjection.HasValidDomains(new[]
        {
            argument with
            {
                AuthoredFileSystemValue = list,
                AuthoredNonFileSystemValue = new ShellValueDomain.Unknown(),
            },
        }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(33)]
    public void Ordered_list_domain_rejects_out_of_range_cardinality(int count)
    {
        var parsed = Parse("Select-Object Name,Id", PwshDialect.PowerShell7);
        var argument = Assert.Single(Assert.Single(parsed.Commands).Arguments);
        var values = Enumerable.Range(0, count).Select(index => $"P{index}").ToArray();

        Assert.False(AuthoredOperandSemanticsProjection.HasValidDomains(new[]
        {
            argument with
            {
                AuthoredNonFileSystemValue = new ShellValueDomain.OrderedList(values),
            },
        }));
    }

    [Fact]
    public void Ordered_list_domain_rejects_null_members()
    {
        var parsed = Parse("Select-Object Name,Id", PwshDialect.PowerShell7);
        var argument = Assert.Single(Assert.Single(parsed.Commands).Arguments);

        Assert.False(AuthoredOperandSemanticsProjection.HasValidDomains(new[]
        {
            argument with
            {
                AuthoredNonFileSystemValue = new ShellValueDomain.OrderedList(
                    new[] { "Name", null! }),
            },
        }));
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7)]
    [InlineData(PwshDialect.WindowsPowerShell51)]
    public void Nested_escaped_ranges_never_publish_integer_bounds(PwshDialect dialect)
    {
        var result = Parse(
            "ForEach-Object -Process { Select-Object -Index (113`..145) }",
            dialect);

        Assert.DoesNotContain(
            result.Commands.SelectMany(command => command.Arguments),
            argument => argument.Value is ShellValueDomain.IntegerRange ||
                argument.AuthoredValue is ShellValueDomain.IntegerRange);
    }

    [Fact]
    public void Range_closing_parenthesis_does_not_swallow_a_following_statement()
    {
        var result = Parse(
            "Select-Object -Index (113..145); netclaw daemon stop",
            PwshDialect.PowerShell7);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(
            new[] { "Select-Object", "netclaw daemon stop" },
            result.Commands.Select(command => command.Clause.Verb.Joined));
    }

    [Fact]
    public void Decoded_wrappers_do_not_publish_static_group_membership()
    {
        var result = Parse(
            "Invoke-Expression 'Select-Object Id,Name'",
            PwshDialect.PowerShell7);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.All(
            Assert.Single(result.Commands).Arguments,
            argument => Assert.IsType<ShellValueDomain.Unknown>(
                argument.AuthoredNonFileSystemValue));
    }

    private static AnalyzedArgument Argument(CommandOccurrence command, string raw) =>
        Assert.Single(command.Arguments, argument => argument.Argument.Raw == raw);

    private static ParsedCommand Parse(string source, PwshDialect dialect) =>
        new PwshParser(new PwshParserOptions
        {
            HomeDirectory = "C:/Users/test",
            WorkingDirectory = "C:/WORK",
            InitialStateMode = PwshInitialStateMode.IsolatedNonInteractiveNoProfile,
            Dialect = dialect,
        }).Parse(source);
}
