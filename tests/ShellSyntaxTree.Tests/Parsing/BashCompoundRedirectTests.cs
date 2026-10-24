// -----------------------------------------------------------------------
// <copyright file="BashCompoundRedirectTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>Pins opt-in Bash redirects on supported compound commands.</summary>
public sealed class BashCompoundRedirectTests
{
    [Fact]
    public void Default_mode_keeps_compound_redirects_unparseable()
    {
        const string source = "for item in one two; do cat \"$item\"; done < input.txt";

        var parsed = CreateParser().Parse(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [InlineData("for item in one two; do cat \"$item\"; done < input.txt")]
    [InlineData("while true; do cat input.txt; done < input.txt")]
    [InlineData("until false; do cat input.txt; done < input.txt")]
    [InlineData("if true; then cat input.txt; fi < input.txt")]
    [InlineData("(cat input.txt) < input.txt")]
    [InlineData("for item in one; do VALUE=body; done < input.txt")]
    [InlineData("VALUE=/entry; while VALUE=/condition; do :; done < \"$VALUE/input.txt\"")]
    [InlineData("cd /safe && if cd /other; then :; fi < input.txt")]
    [InlineData("while false; do :; done < \"$(cat secret)\"")]
    [InlineData("bash -c 'while false; do :; done < input.txt'")]
    [InlineData("for item in \"$(while false; do :; done < input.txt)\"; do :; done")]
    [InlineData("for item in one; do :; done <<EOF\n$(sudo cat secret)\nEOF")]
    [InlineData("while false; do :; done <<< \"$(sudo cat secret)\"")]
    [BashTheory]
    public void Bash_accepts_supported_compound_input_redirects_syntax_only(
        string source)
    {
        BashOracle.AssertSyntaxOnly(source);
    }

    [Fact]
    public void Opted_in_loop_redirect_has_exact_owner_and_stays_off_child_occurrence()
    {
        const string source = "for item in one two; do cat \"$item\"; done < input.txt";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var wrapper = AssertSingleCompoundWrapper(parsed);
        Assert.Equal(0, wrapper.SourceStart);
        Assert.Equal(source.Length, wrapper.SourceLength);
        Assert.Equal("/work", Assert.IsType<ShellValueDomain.Exact>(wrapper.WorkingDirectory).Value);
        var loop = Assert.IsType<ForEachSyntax>(wrapper.Command);
        Assert.Single(loop.Body.Statements);
        var redirect = AssertCompoundRedirect(wrapper);
        Assert.Equal(source.IndexOf("< input.txt", StringComparison.Ordinal),
            redirect.SourceStart);
        Assert.Equal("< input.txt".Length, redirect.SourceLength);
        Assert.Equal(FileRedirectMode.Input,
            Assert.IsType<FileRedirectAnalysis>(redirect.Analysis).Mode);
        Assert.Equal("/work/input.txt", Assert.IsType<ShellValueDomain.Exact>(
            Assert.IsType<FileRedirectAnalysis>(redirect.Analysis).Target).Value);
        var child = Assert.Single(parsed.Commands);
        Assert.Empty(child.Redirects);
        Assert.True(child.IsComplete);
        Assert.Contains(child.Ancestry, frame => ReferenceEquals(frame.Ancestor, loop));
    }

    [Theory]
    [InlineData("for item in one two; do cat \"$item\"; done < input.txt > output.txt 2>&1")]
    [InlineData("(cat input.txt) < input.txt > output.txt 2>&1")]
    public void Opted_in_compound_redirects_keep_source_order_and_analysis(
        string source)
    {
        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var wrapper = AssertSingleCompoundWrapper(parsed);
        var redirects = wrapper.Redirects;
        Assert.Equal(3, redirects.Count);
        Assert.Equal(
            new object[] { FileRedirectMode.Input, FileRedirectMode.Output, "DescriptorDuplicate" },
            redirects.Select(redirect => redirect.Analysis switch
            {
                FileRedirectAnalysis file => (object)file.Mode,
                DescriptorDuplicateRedirectAnalysis => "DescriptorDuplicate",
                var other => other.GetType().Name,
            }));
        Assert.Equal(
            new[] { "< input.txt", "> output.txt", "2>&1" },
            redirects.Select(redirect => SourceSlice(
                source,
                redirect.SourceStart,
                redirect.SourceLength)));
        Assert.All(parsed.Commands, command =>
        {
            Assert.True(command.IsComplete);
            Assert.Empty(command.Redirects);
        });
    }

    [Theory]
    [InlineData(
        "for item in one two; do cat \"$item\"; done 2> errors.txt < input.txt",
        FileRedirectMode.Output,
        FileRedirectMode.Input,
        "2> errors.txt",
        "< input.txt")]
    [InlineData(
        "for item in one two; do cat \"$item\"; done 3<input.txt 2> errors.txt",
        FileRedirectMode.Input,
        FileRedirectMode.Output,
        "3<input.txt",
        "2> errors.txt")]
    public void Descriptor_prefixed_redirects_keep_order_and_source_spans(
        string source,
        FileRedirectMode firstMode,
        FileRedirectMode secondMode,
        string firstSpan,
        string secondSpan)
    {
        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var redirects = AssertSingleCompoundWrapper(parsed).Redirects;
        Assert.Equal(2, redirects.Count);
        Assert.Equal(new[] { firstSpan, secondSpan },
            redirects.Select(redirect => SourceSlice(source, redirect.SourceStart, redirect.SourceLength)));
        Assert.Equal(firstMode,
            Assert.IsType<FileRedirectAnalysis>(redirects[0].Analysis).Mode);
        Assert.Equal(secondMode,
            Assert.IsType<FileRedirectAnalysis>(redirects[1].Analysis).Mode);
    }

    [Fact]
    public void Descriptor_only_compound_redirect_is_preserved()
    {
        const string source = "for item in one; do :; done 2>&1";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var redirect = AssertCompoundRedirect(AssertSingleCompoundWrapper(parsed));
        Assert.IsType<DescriptorDuplicateRedirectAnalysis>(redirect.Analysis);
        Assert.Equal("2>&1", SourceSlice(source, redirect.SourceStart, redirect.SourceLength));
    }

    [Fact]
    public void Compound_redirect_target_does_not_use_body_assignment()
    {
        const string source = "VALUE=/entry; for item in one; do VALUE=/body; done < \"$VALUE/input.txt\"";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var wrapper = FindCompoundWrapper(parsed.Syntax);
        var redirect = AssertCompoundRedirect(wrapper);
        var analysis = Assert.IsType<FileRedirectAnalysis>(redirect.Analysis);
        Assert.IsType<ShellValueDomain.Unknown>(analysis.Target);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void Compound_redirect_target_uses_existing_launch_home_fact()
    {
        const string source = "for item in one; do :; done < \"$HOME/input.txt\"";
        var launch = new ShellLaunchEnvironment(
            new Dictionary<string, string> { ["HOME"] = "/launch" },
            Array.Empty<string>());

        var parsed = ParseWithCompoundRedirects(source, launch);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var redirect = AssertCompoundRedirect(AssertSingleCompoundWrapper(parsed));
        var analysis = Assert.IsType<FileRedirectAnalysis>(redirect.Analysis);
        Assert.Equal("/launch/input.txt", Assert.IsType<ShellValueDomain.Exact>(analysis.Target).Value);
    }

    [Fact]
    public void Compound_redirect_target_does_not_use_condition_assignment()
    {
        const string source = "VALUE=/entry; while VALUE=/condition; do :; done < \"$VALUE/input.txt\"";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var redirect = AssertCompoundRedirect(FindCompoundWrapper(parsed.Syntax));
        var analysis = Assert.IsType<FileRedirectAnalysis>(redirect.Analysis);
        Assert.IsType<ShellValueDomain.Unknown>(analysis.Target);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void Compound_redirect_working_directory_uses_entry_state_before_condition_cd()
    {
        const string source = "cd /safe && if cd /other; then :; fi < input.txt";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var wrapper = FindCompoundWrapper(parsed.Syntax);
        Assert.Equal("/safe", Assert.IsType<ShellValueDomain.Exact>(
            wrapper.WorkingDirectory).Value);
        var redirect = AssertCompoundRedirect(wrapper);
        Assert.Equal("/safe/input.txt", Assert.IsType<ShellValueDomain.Exact>(
            Assert.IsType<FileRedirectAnalysis>(redirect.Analysis).Target).Value);
    }

    [Fact]
    public void Compound_redirect_for_loop_target_uses_entry_directory_before_body_cd()
    {
        const string source = "cd /safe && for x in one; do cd /other; done < input.txt";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var wrapper = FindCompoundWrapper(parsed.Syntax);
        Assert.Equal("/safe", Assert.IsType<ShellValueDomain.Exact>(
            wrapper.WorkingDirectory).Value);
        var redirect = AssertCompoundRedirect(wrapper);
        Assert.Equal("/safe/input.txt", Assert.IsType<ShellValueDomain.Exact>(
            Assert.IsType<FileRedirectAnalysis>(redirect.Analysis).Target).Value);
    }

    [Fact]
    public void Compound_redirect_failure_keeps_input_state_for_following_sequence()
    {
        var parsed = ParseWithCompoundRedirects(
            "VALUE=/entry; for item in one; do VALUE=/body; done < input.txt; cat \"$VALUE\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        AssertDomain(Assert.Single(parsed.Commands[^1].EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet, "/body", "/entry");
    }

    [Fact]
    public void Compound_redirect_failure_reaches_or_fallback_and_not_and_command()
    {
        var parsed = ParseWithCompoundRedirects(
            "VALUE=/entry; for item in one; do VALUE=/body; done < input.txt || cat \"$VALUE\"; " +
            "for item in one; do VALUE=/body; done < input.txt && cat \"$VALUE\"");

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(2, parsed.Commands.Count);
        AssertDomain(Assert.Single(parsed.Commands[0].EffectiveArguments).Value,
            ShellValueDomainKind.Exact, "/entry");
        AssertDomain(Assert.Single(parsed.Commands[1].EffectiveArguments).Value,
            ShellValueDomainKind.Exact, "/body");
    }

    [Fact]
    public void Compound_redirect_inside_if_condition_keeps_condition_role()
    {
        const string source = "if while false; do :; done < input.txt; then :; fi";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var conditionCommand = parsed.Commands.Single(command =>
            command.ImmediateRole == CommandOccurrenceRole.Condition);
        Assert.Contains(conditionCommand.Ancestry,
            frame => frame.Region == CommandAncestryRegion.CompoundCommand);
    }

    [Fact]
    public void Compound_redirect_inside_iterator_keeps_iterator_ancestry()
    {
        const string source = "for item in \"$(while false; do :; done < input.txt)\"; do :; done";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Contains(parsed.Commands, command =>
            command.Ancestry.Any(frame => frame.Region == CommandAncestryRegion.Iterator) &&
            command.Ancestry.Any(frame => frame.Region == CommandAncestryRegion.CompoundCommand));
    }

    [Fact]
    public void Decoded_bash_c_compound_redirect_stays_unparseable()
    {
        const string source = "bash -c 'while false; do :; done < input.txt'";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [Fact]
    public void Assignment_only_loop_keeps_compound_redirect_without_command_occurrences()
    {
        const string source = "for item in one; do VALUE=body; done < input.txt";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Empty(parsed.Commands);
        var redirect = AssertCompoundRedirect(AssertSingleCompoundWrapper(parsed));
        Assert.IsType<FileRedirectAnalysis>(redirect.Analysis);
    }

    [Fact]
    public void Unknown_compound_redirect_target_stays_unknown()
    {
        const string source = "for item in one; do cat \"$item\"; done < \"$INPUT\"";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var redirect = AssertCompoundRedirect(AssertSingleCompoundWrapper(parsed));
        var analysis = Assert.IsType<FileRedirectAnalysis>(redirect.Analysis);
        Assert.IsType<ShellValueDomain.Unknown>(analysis.Target);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void Named_compound_redirect_target_needs_variable_attribute_facts()
    {
        var parsed = new BashParser(new BashParserOptions
        {
            WorkingDirectory = "/work",
            ParseCompoundRedirects = true,
        }).Parse("while false; do :; done < \"$INPUT\"");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [Theory]
    [InlineData("while false; do :; done < \"$(cat secret)\"")]
    [InlineData("while false; do :; done > \"$(sudo cat secret)\"")]
    public void Compound_redirect_command_substitution_stays_unparseable(
        string source)
    {
        var parsed = ParseWithCompoundRedirects(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [Theory]
    [InlineData("for item in one; do :; done <<EOF\n$(sudo cat secret)\nEOF")]
    [InlineData("while false; do :; done <<< \"$(sudo cat secret)\"")]
    public void Compound_here_document_and_here_string_stay_unparseable(string source)
    {
        var parsed = ParseWithCompoundRedirects(source);

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    [Fact]
    public void Nested_compound_redirects_keep_distinct_owners_and_empty_body_commands()
    {
        const string source = "for outer in one; do while true; do :; done < inner.txt; done < outer.txt";

        var parsed = ParseWithCompoundRedirects(source);

        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var outer = AssertSingleCompoundWrapper(parsed);
        var outerRedirect = Assert.IsType<FileRedirectAnalysis>(AssertCompoundRedirect(outer).Analysis);
        Assert.Equal("/work/outer.txt", outerRedirect.Authored.Target);
        var loop = Assert.IsType<ForEachSyntax>(outer.Command);
        var inner = Assert.Single(loop.Body.Statements);
        var innerRedirected = Assert.IsType<RedirectedCompoundSyntax>(inner);
        var condition = Assert.IsType<ConditionLoopSyntax>(innerRedirected.Command);
        Assert.Single(condition.Body.Statements);
        Assert.Single(innerRedirected.Redirects);
        Assert.Collection(
            parsed.Commands,
            conditionOccurrence =>
            {
                Assert.Equal(CommandOccurrenceRole.Condition, conditionOccurrence.ImmediateRole);
                Assert.Equal("true", string.Join(" ",
                    Assert.IsType<ShellCommandWords.Known>(conditionOccurrence.CommandWords).Words));
            },
            bodyOccurrence =>
            {
                Assert.Equal(CommandOccurrenceRole.LoopBody, bodyOccurrence.ImmediateRole);
                Assert.Equal(":", string.Join(" ",
                    Assert.IsType<ShellCommandWords.Known>(bodyOccurrence.CommandWords).Words));
            });
    }

    [Fact]
    public void Brace_group_redirect_stays_unparseable_when_option_is_enabled()
    {
        var parsed = ParseWithCompoundRedirects("{ cat input.txt; } < input.txt");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(parsed.Commands);
        Assert.Empty(parsed.Clauses);
    }

    private static BashParser CreateParser() => new(new BashParserOptions
    {
        WorkingDirectory = "/work",
        InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
    });

    private static ParsedCommand ParseWithCompoundRedirects(
        string source,
        ShellLaunchEnvironment? launchEnvironment = null)
    {
        var options = new BashParserOptions
        {
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.FreshNonInteractiveNoStartup,
            LaunchEnvironment = launchEnvironment,
        };
        options = options with { ParseCompoundRedirects = true };
        return new BashParser(options).Parse(source);
    }

    private static RedirectedCompoundSyntax AssertSingleCompoundWrapper(ParsedCommand parsed)
        => Assert.IsType<RedirectedCompoundSyntax>(Assert.Single(parsed.Syntax.Statements));

    private static RedirectedCompoundSyntax FindCompoundWrapper(ShellSyntaxNode node)
    {
        var found = TryFindCompoundWrapper(node);
        Assert.NotNull(found);
        return found!;
    }

    private static RedirectedCompoundSyntax? TryFindCompoundWrapper(ShellSyntaxNode node)
    {
        if (node is RedirectedCompoundSyntax redirected)
            return redirected;

        foreach (var child in node.GetChildren())
        {
            var found = TryFindCompoundWrapper(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static CompoundRedirect AssertCompoundRedirect(RedirectedCompoundSyntax wrapper)
        => Assert.Single(wrapper.Redirects);

    private static string SourceSlice(string source, int start, int length)
        => source.Substring(start, length);

    private static void AssertDomain(
        ShellValueDomain actual,
        ShellValueDomainKind kind,
        params string[] values)
    {
        Assert.Equal(kind, actual.Kind);
        var actualValues = actual switch
        {
            ShellValueDomain.Exact exact => new[] { exact.Value },
            ShellValueDomain.FiniteSet finite => finite.Values.ToArray(),
            _ => Array.Empty<string>(),
        };
        Assert.Equal(values, actualValues);
    }
}
