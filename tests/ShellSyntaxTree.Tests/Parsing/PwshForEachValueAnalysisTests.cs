// -----------------------------------------------------------------------
// <copyright file="PwshForEachValueAnalysisTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using ShellSyntaxTree.Internal.Pwsh.Parsing;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

public class PwshForEachValueAnalysisTests
{
    [Fact]
    public void Documented_preference_variables_are_ineligible_bindings()
    {
        var documented = new[]
        {
            "ConfirmPreference", "DebugPreference", "ErrorActionPreference", "ErrorView",
            "FormatEnumerationLimit", "InformationPreference", "LogCommandHealthEvent",
            "LogCommandLifecycleEvent", "LogEngineHealthEvent", "LogEngineLifecycleEvent",
            "LogProviderHealthEvent", "LogProviderLifecycleEvent", "MaximumHistoryCount",
            "OFS", "OutputEncoding", "ProgressPreference", "PSDefaultParameterValues",
            "PSEmailServer", "PSModuleAutoLoadingPreference",
            "PSNativeCommandArgumentPassing", "PSNativeCommandUseErrorActionPreference",
            "PSSessionApplicationName", "PSSessionConfigurationName", "PSSessionOption",
            "PSStyle", "Transcript", "VerbosePreference", "WarningPreference",
            "WhatIfPreference",
        };

        Assert.All(
            documented,
            name => Assert.False(PwshForEachValueAnalysis.IsEligibleBindingName(name)));
    }

    [Fact]
    public void Literal_array_plan_preserves_authored_order_and_duplicates()
    {
        var plan = Capture("@('a','b','a')", isLiteralExpression: true);

        Assert.Equal(PwshIterationCardinality.OneOrMore, plan.Cardinality);
        Assert.Equal(3, plan.AuthoredVisitCount);
        Assert.False(plan.RequiresFixedPoint);
        Assert.Equal(
            new[] { "a", "b", "a" },
            plan.OrderedCandidates.Select(candidate => Assert.Single(candidate.Values)));
        Assert.Equal(ShellValueDomainKind.FiniteSet, plan.Summary.Kind);
        Assert.Equal(new[] { "a", "b" }, plan.Summary.Values);
    }

    [Fact]
    public void Ordered_visit_overflow_retains_count_without_truncating_sequence()
    {
        var values = string.Join(",", Enumerable.Repeat("'same'", 33));
        var plan = Capture($"@({values})", isLiteralExpression: true);

        Assert.Equal(33, plan.AuthoredVisitCount);
        Assert.True(plan.RequiresFixedPoint);
        Assert.Empty(plan.OrderedCandidates);
        Assert.Equal(ShellValueDomainKind.Exact, plan.Summary.Kind);
        Assert.Equal("same", Assert.Single(plan.Summary.Values));
    }

    [Fact]
    public void Single_command_iterator_has_unknown_zero_or_more_cardinality()
    {
        var plan = Capture("Get-ChildItem", isLiteralExpression: false);

        Assert.Equal(PwshIterationCardinality.ZeroOrMore, plan.Cardinality);
        Assert.Null(plan.AuthoredVisitCount);
        Assert.True(plan.RequiresFixedPoint);
        Assert.Empty(plan.OrderedCandidates);
        Assert.Equal(ShellValueDomainKind.Unknown, plan.Summary.Kind);
    }

    [Fact]
    public void Non_string_literal_retains_one_unknown_authored_visit()
    {
        var plan = Capture("1", isLiteralExpression: true);

        Assert.Equal(PwshIterationCardinality.OneOrMore, plan.Cardinality);
        Assert.Equal(1, plan.AuthoredVisitCount);
        Assert.False(plan.RequiresFixedPoint);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(plan.OrderedCandidates).Kind);
    }

    private static PwshForEachAnalysisPlan Capture(
        string source,
        bool isLiteralExpression) =>
        PwshForEachValueAnalysis.CapturePlan(
            "f",
            PwshLexer.Tokenize(source),
            isLiteralExpression);
}
