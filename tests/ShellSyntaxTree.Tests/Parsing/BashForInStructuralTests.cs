// -----------------------------------------------------------------------
// <copyright file="BashForInStructuralTests.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Xunit;

namespace ShellSyntaxTree.Tests.Parsing;

/// <summary>Pins the bounded Bash <c>for name in words</c> vertical slice.</summary>
public class BashForInStructuralTests
{
    [Fact]
    public void Default_initial_state_fails_loop_binding_closed()
    {
        var result = new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
        }).Parse("for f in a; do printf '%s' \"$f\"; done");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("isolated non-interactive initial state", result.UnparseableReason!);
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("RANDOM")]
    [InlineData("LINENO")]
    [InlineData("PATH")]
    [InlineData("CDPATH")]
    [InlineData("IFS")]
    [InlineData("_")]
    [InlineData("auto_resume")]
    [InlineData("histchars")]
    [InlineData("MixedCase")]
    [InlineData("_private")]
    public void Nonordinary_loop_binding_names_fail_atomically(string binding)
    {
        var result = Parse($"for {binding} in value; do printf '%s' \"${binding}\"; done");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("ordinary-scalar boundary", result.UnparseableReason!);
    }

    [Theory]
    [InlineData("f")]
    [InlineData("file")]
    [InlineData("file_2")]
    [InlineData("for")]
    public void Ordinary_scalar_binding_names_remain_eligible(string binding)
    {
        var result = Parse($"for {binding} in value; do printf '%s' \"${binding}\"; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
    }

    [Fact]
    public void Literal_iterable_emits_one_body_occurrence_with_finite_effective_value()
    {
        const string source = "for f in a.txt b.txt; do rm -- \"$f\"; done";

        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var loop = Assert.IsType<ForEachSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.Equal("f", loop.Binding.Name);
        Assert.Equal("f", loop.Binding.Source.Raw);
        Assert.Equal(4, loop.Binding.Source.SourceStart);
        Assert.Equal(1, loop.Binding.Source.SourceLength);
        Assert.Equal("a.txt b.txt", loop.Iterable.Raw);
        Assert.Equal(9, loop.Iterable.SourceStart);
        Assert.Equal(11, loop.Iterable.SourceLength);
        Assert.Empty(loop.IteratorCommands.Statements);
        Assert.Equal(0, loop.SourceStart);
        Assert.Equal(source.Length, loop.SourceLength);

        var simple = Assert.IsType<SimpleCommandSyntax>(Assert.Single(loop.Body.Statements));
        var occurrence = Assert.Single(result.Commands);
        Assert.Same(simple.Clause, occurrence.Clause);
        Assert.Equal(CommandOccurrenceRole.LoopBody, occurrence.ImmediateRole);
        Assert.True(occurrence.IsComplete);
        var effective = Assert.Single(occurrence.EffectiveArguments);
        Assert.Equal("\"$f\"", simple.Clause.Elements[effective.ClauseElementIndex].Raw);
        AssertDomain(effective.Value, ShellValueDomainKind.FiniteSet, "a.txt", "b.txt");
        Assert.Equal(ArgKind.DynamicSkip, simple.Clause.Elements[effective.ClauseElementIndex].Kind);
    }

    [Theory]
    [InlineData("for f in a\"b\"; do echo \"$f\"; done", "ab")]
    [InlineData("for f in \"a\"'b'; do echo \"$f\"; done", "ab")]
    [InlineData("for f in \"\"; do echo \"$f\"; done", "")]
    [InlineData("for f in a a; do echo \"$f\"; done", "a")]
    [InlineData("for for in a; do echo \"$for\"; done", "a")]
    [InlineData("for in in a; do echo \"$in\"; done", "a")]
    [InlineData("for do in a; do echo \"$do\"; done", "a")]
    [InlineData("for done in a; do echo \"$done\"; done", "a")]
    [InlineData("for f in a; do echo \"${f}\"; done", "a")]
    [InlineData("for f in a; do echo \"${f}.txt\"; done", "a.txt")]
    [InlineData("for f in a; do echo \"$f.txt\"; done", "a.txt")]
    public void Bash_word_formation_and_identifier_rules_preserve_exact_values(
        string source,
        string expected)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        AssertDomain(effective.Value, ShellValueDomainKind.Exact, expected);
    }

    [Fact]
    public void Static_path_glob_is_a_pattern_without_filesystem_enumeration()
    {
        var result = Parse("for f in /tmp/*.txt; do rm -- \"$f\"; done");

        var domain = Assert.Single(Assert.Single(result.Commands).EffectiveArguments).Value;
        Assert.Equal(ShellValueDomainKind.Pattern, domain.Kind);
        Assert.Equal("/tmp/*.txt", domain.Pattern);
        Assert.Equal("/tmp", domain.CoveringDirectory);
        Assert.Empty(domain.Values);
    }

    [Fact]
    public void Relative_static_glob_uses_the_original_parser_working_directory()
    {
        var result = Parse("for f in *.txt; do rm -- \"$f\"; done");

        var domain = Assert.Single(Assert.Single(result.Commands).EffectiveArguments).Value;
        Assert.Equal(ShellValueDomainKind.Pattern, domain.Kind);
        Assert.Equal("*.txt", domain.Pattern);
        Assert.Equal("/work", domain.CoveringDirectory);
    }

    [Theory]
    [InlineData("cd /a || cd /b; for f in *.txt; do rm -- \"$f\" rel.txt; done")]
    [InlineData("cd /a | cat; for f in *.txt; do rm -- \"$f\" rel.txt; done")]
    [InlineData("command cd /a; for f in x; do rm -- \"$f\" rel.txt; done")]
    [InlineData("builtin cd /a; for f in x; do rm -- \"$f\" rel.txt; done")]
    [InlineData("eval \"cd /a\"; for f in x; do rm -- \"$f\" rel.txt; done")]
    [InlineData("trap \"f=x\" DEBUG; for f in a b; do echo \"$f\"; done")]
    public void Loop_after_prior_shell_state_mutation_fails_atomically(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("shell-state mutation", result.UnparseableReason!);
    }

    [Theory]
    [InlineData("for f in \"$DIR\"/*.txt; do rm -- \"$f\"; done")]
    [InlineData("for f in ../*.txt; do rm -- \"$f\"; done")]
    [InlineData("for f in /tmp/*/../../etc/passwd; do rm -- \"$f\"; done")]
    [InlineData("for f in */../../etc/passwd; do rm -- \"$f\"; done")]
    [InlineData("for f in /tmp/.[.]/etc/passwd; do rm -- \"$f\"; done")]
    [InlineData("for f in /tmp/.?/etc/passwd; do rm -- \"$f\"; done")]
    [InlineData("for f in /tmp/..*/etc/passwd; do rm -- \"$f\"; done")]
    [InlineData("for f in {a,b}; do rm -- \"$f\"; done")]
    [InlineData("for f in $HOME; do rm -- \"$f\"; done")]
    public void Unbounded_iterables_keep_the_body_value_unknown(string source)
    {
        var result = Parse(source);

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
    }

    [Fact]
    public void Iterator_substitution_precedes_body_and_does_not_see_new_binding()
    {
        var result = Parse("for f in $(printf '%s' \"$f\"); do rm -- \"$f\"; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(new[] { "printf", "rm" }, result.Commands.Select(CommandVerb));
        Assert.Equal(CommandOccurrenceRole.Substitution, result.Commands[0].ImmediateRole);
        Assert.Empty(result.Commands[0].EffectiveArguments);
        Assert.Equal(CommandOccurrenceRole.LoopBody, result.Commands[1].ImmediateRole);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(result.Commands[1].EffectiveArguments).Value.Kind);
        var loop = Assert.IsType<ForEachSyntax>(Assert.Single(result.Syntax.Statements));
        Assert.IsType<CommandSubstitutionSyntax>(Assert.Single(loop.IteratorCommands.Statements));
    }

    [Fact]
    public void Body_substitution_inherits_the_loop_binding()
    {
        var result = Parse(
            "for f in a b; do printf '%s' \"$(echo \"$f\")\"; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(new[] { "echo", "printf" }, result.Commands.Select(CommandVerb));
        AssertDomain(
            Assert.Single(result.Commands[0].EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet,
            "a",
            "b");
        Assert.All(result.Commands, command =>
            AssertDomain(command.WorkingDirectory, ShellValueDomainKind.Exact, "/work"));
    }

    [Fact]
    public void Nested_bindings_cross_product_and_repeated_binding_remains_correlated()
    {
        var result = Parse(
            "for d in a b; do for f in x y; do echo \"$d/$f/$d\"; done; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        AssertDomain(
            effective.Value,
            ShellValueDomainKind.FiniteSet,
            "a/x/a",
            "a/y/a",
            "b/x/b",
            "b/y/b");
    }

    [Fact]
    public void Nested_reuse_of_an_active_binding_name_fails_atomically()
    {
        var result = Parse(
            "for f in a b; do for f in x y; do printf %s \"$f\"; done; echo \"$f\"; done");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("binding reuse", result.UnparseableReason!);
    }

    [Fact]
    public void Unquoted_binding_is_visible_but_unknown_due_to_field_splitting()
    {
        var result = Parse("for f in a b; do rm $f; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var effective = Assert.Single(Assert.Single(result.Commands).EffectiveArguments);
        Assert.Equal(ShellValueDomainKind.Unknown, effective.Value.Kind);
        Assert.Equal(ArgKind.DynamicSkip, EffectiveValueElement(result, effective).Kind);
    }

    [Fact]
    public void Option_like_candidates_are_not_reclassified_by_loop_analysis()
    {
        var result = Parse("for f in -rf /tmp/x; do rm \"$f\"; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var command = Assert.Single(result.Commands);
        var effective = Assert.Single(command.EffectiveArguments);
        AssertDomain(effective.Value, ShellValueDomainKind.FiniteSet, "-rf", "/tmp/x");
        Assert.Equal(ArgKind.DynamicSkip, command.Clause.Elements[effective.ClauseElementIndex].Kind);
    }

    [Fact]
    public void Candidate_cap_is_exact_and_overflow_becomes_unknown()
    {
        var exactValues = Enumerable.Range(1, ShellAnalysisLimits.MaxValueCandidates)
            .Select(index => $"v{index:00}")
            .ToArray();
        var exact = Parse(
            $"for f in {string.Join(" ", exactValues)}; do echo \"$f\"; done");
        var overflow = Parse(
            $"for f in {string.Join(" ", exactValues)} v33; do echo \"$f\"; done");

        AssertDomain(
            Assert.Single(Assert.Single(exact.Commands).EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet,
            exactValues);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(Assert.Single(overflow.Commands).EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Empty_iterable_keeps_the_authored_body_with_unknown_binding()
    {
        var result = Parse("for f in; do echo \"$f\"; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Single(result.Commands);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(Assert.Single(result.Commands).EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Ordered_duplicate_iterations_join_body_facts_and_preserve_final_binding()
    {
        var result = Parse(
            "for f in a b a; do printf '%s' \"$f\"; done; echo \"$f\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        AssertDomain(
            Assert.Single(result.Commands[0].EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet,
            "a",
            "b");
        AssertDomain(
            Assert.Single(result.Commands[1].EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "a");
    }

    [Fact]
    public void Empty_loop_preserves_the_incoming_binding_value()
    {
        var result = Parse(
            "for f in seed; do :; done; for f in; do :; done; echo \"$f\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        AssertDomain(
            Assert.Single(result.Commands[2].EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "seed");
    }

    [Fact]
    public void Sequential_same_name_loop_replaces_the_prior_binding()
    {
        var result = Parse(
            "for f in first; do :; done; for f in second; do :; done; echo \"$f\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        AssertDomain(
            Assert.Single(result.Commands[2].EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "second");
    }

    [Fact]
    public void Empty_loop_failure_continuation_is_unreachable_and_conservative()
    {
        var result = Parse(
            "for f in; do false; done || cat relative.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        var unreachable = result.Commands[1];
        Assert.Equal(ShellValueDomainKind.Unknown, unreachable.WorkingDirectory.Kind);
        var relative = Assert.Single(unreachable.Clause.Args, argument =>
            argument.Raw == "relative.txt");
        Assert.Null(relative.Resolved);
    }

    [Fact]
    public void Empty_loop_success_continuation_keeps_exact_state()
    {
        var result = Parse(
            "for f in; do false; done && cat relative.txt");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(2, result.Commands.Count);
        var reached = result.Commands[1];
        AssertDomain(reached.WorkingDirectory, ShellValueDomainKind.Exact, "/work");
        var relative = Assert.Single(reached.Clause.Args, argument =>
            argument.Raw == "relative.txt");
        Assert.Equal("/work/relative.txt", relative.Resolved);
    }

    [Fact]
    public void Thirty_two_visits_preserve_the_last_value_after_the_loop()
    {
        var values = Enumerable.Range(1, ShellAnalysisLimits.MaxValueCandidates)
            .Select(index => $"v{index:00}")
            .ToArray();
        var result = Parse(
            $"for f in {string.Join(" ", values)}; do :; done; echo \"$f\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        AssertDomain(
            Assert.Single(result.Commands[1].EffectiveArguments).Value,
            ShellValueDomainKind.Exact,
            "v32");
    }

    [Fact]
    public void Thirty_three_duplicate_visits_widen_instead_of_deduplicating_the_plan()
    {
        var values = Enumerable.Repeat("same", ShellAnalysisLimits.MaxValueCandidates + 1);
        var result = Parse(
            $"for f in {string.Join(" ", values)}; do echo \"$f\"; done; printf '%s' \"$f\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.All(result.Commands, command => Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(command.EffectiveArguments).Value.Kind));
    }

    [Fact]
    public void Zero_or_more_fixed_point_widens_recursive_binding_growth()
    {
        var result = Parse(
            "for f in seed; do :; done; " +
            "for d in \"$UNKNOWN\"; do for f in \"${f}x\"; do :; done; done; " +
            "echo \"$f\"");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(
            ShellValueDomainKind.Unknown,
            Assert.Single(result.Commands[2].EffectiveArguments).Value.Kind);
    }

    [Fact]
    public void Nested_concrete_loop_product_exceeding_analysis_budget_fails_atomically()
    {
        var values = string.Join(
            " ",
            Enumerable.Range(1, ShellAnalysisLimits.MaxValueCandidates));
        var result = Parse(
            $"for a in {values}; do for b in {values}; do " +
            $"for c in {values}; do :; done; done; done");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("exceeded limits", result.UnparseableReason!);
    }

    [Fact]
    public void Pipelines_compose_with_loop_ancestry_without_synthetic_operators()
    {
        var result = Parse("for f in a b; do printf '%s' \"$f\" | sort; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(
            new[] { CommandOccurrenceRole.PipelineStage, CommandOccurrenceRole.PipelineStage },
            result.Commands.Select(command => command.ImmediateRole));
        Assert.Equal(
            new[] { CompoundOperator.None, CompoundOperator.Pipe },
            result.Clauses.Select(clause => clause.Operator));
        Assert.Contains(
            result.Commands[0].Ancestry,
            frame => frame.AncestorKind == ShellSyntaxKind.ForEach &&
                frame.Region == CommandAncestryRegion.LoopBody);
    }

    [Theory]
    [InlineData("for f in a; do unset f; done")]
    [InlineData("for f in a; do read f; done")]
    [InlineData("for f in a; do printf -v f x; done")]
    [InlineData("for f in a; do eval 'f=x'; done")]
    [InlineData("for f in a; do source script.sh; done")]
    [InlineData("for f in a; do cd /tmp; done")]
    [InlineData("for f in a b; do trap 'f=x' DEBUG; echo \"$f\"; done")]
    [InlineData("for f in a; do command unset f; echo \"$f\"; done")]
    [InlineData("for f in a; do builtin unset f; echo \"$f\"; done")]
    [InlineData("for f in a; do break; done")]
    [InlineData("for f in a; do continue; done")]
    [InlineData("for f in a; do return; done")]
    [InlineData("for f in a; do exit 0; done")]
    [InlineData("for f in a; do exec echo replaced; done")]
    [InlineData("for f in a; do command exit 0; done")]
    [InlineData("for f in a; do builtin break; done")]
    public void Unsupported_loop_state_transfer_fails_the_whole_parse(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("mutation or control transfer", result.UnparseableReason!);
    }

    [Theory]
    [InlineData("for 1f in a; do echo ok; done")]
    [InlineData("for f-x in a; do echo ok; done")]
    [InlineData("for \\f in a; do echo ok; done")]
    [InlineData("for f a; do echo ok; done")]
    [InlineData("for f in a do echo ok; done")]
    [InlineData("for f in a; echo ok; done")]
    [InlineData("for f in a; do done")]
    [InlineData("for f in a; do echo ok")]
    [InlineData("for f; do echo ok; done")]
    [InlineData("for f in a; do; done")]
    [InlineData("\\for f in a; do echo ok; done")]
    [InlineData("for f \\in a; do echo ok; done")]
    [InlineData("for f i\\n a; do echo ok; done")]
    [InlineData("for f in a; \\do echo ok; done")]
    [InlineData("for f in a; d\\o echo ok; done")]
    [InlineData("for f in a; do echo ok; \\done")]
    public void Malformed_or_unsupported_for_forms_fail_atomically(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Theory]
    [InlineData("do rm -rf /")]
    [InlineData("done")]
    public void Stray_loop_delimiters_fail_atomically(string source)
    {
        var result = Parse(source);

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Hidden_execution_in_parameter_operator_fails_atomically()
    {
        var result = Parse("for f in a; do echo \"${f:-$(evil)}\"; done");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
    }

    [Fact]
    public void Contextual_words_remain_ordinary_arguments_outside_command_position()
    {
        var result = Parse("echo for in do done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(new[] { "for", "in", "do", "done" }, Assert.Single(result.Clauses).Args.Select(a => a.Raw));
    }

    [Fact]
    public void Loop_structural_depth_limit_is_enforced()
    {
        var exact = Parse(NestedLoops(ShellAnalysisLimits.MaxStructuralNesting));
        var overflow = Parse(NestedLoops(ShellAnalysisLimits.MaxStructuralNesting + 1));

        Assert.False(exact.IsUnparseable, exact.UnparseableReason);
        Assert.True(overflow.IsUnparseable);
        Assert.Empty(overflow.Commands);
        Assert.Empty(overflow.Clauses);
    }

    [Fact]
    public void Static_bash_c_does_not_inherit_unexported_loop_binding()
    {
        var result = Parse("for f in a b; do bash -c 'echo \"$f\"'; done");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Empty(Assert.Single(result.Commands).EffectiveArguments);
    }

    [Fact]
    public void Static_bash_c_preserves_decoded_loop_facts_without_outer_source_spans()
    {
        var result = Parse("bash -c 'for f in a b; do rm -- \"$f\"; done'");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        var wrapper = Assert.IsType<GroupSyntax>(Assert.Single(result.Syntax.Statements));
        var loop = Assert.IsType<ForEachSyntax>(Assert.Single(wrapper.Body.Statements));
        Assert.Equal("f", loop.Binding.Name);
        Assert.Equal("f", loop.Binding.Source.Raw);
        Assert.Equal("a b", loop.Iterable.Raw);
        Assert.Null(loop.SourceStart);
        Assert.Null(loop.SourceLength);
        Assert.Null(loop.Binding.Source.SourceStart);
        Assert.Null(loop.Binding.Source.SourceLength);
        Assert.Null(loop.Iterable.SourceStart);
        Assert.Null(loop.Iterable.SourceLength);
        Assert.Null(loop.IteratorCommands.SourceStart);
        Assert.Null(loop.IteratorCommands.SourceLength);
        Assert.Null(loop.Body.SourceStart);
        Assert.Null(loop.Body.SourceLength);

        var simple = Assert.IsType<SimpleCommandSyntax>(Assert.Single(loop.Body.Statements));
        Assert.Null(simple.SourceStart);
        Assert.Null(simple.SourceLength);
        Assert.All(simple.Clause.Elements, element =>
        {
            Assert.Null(element.SourceStart);
            Assert.Null(element.SourceLength);
        });

        var occurrence = Assert.Single(result.Commands);
        Assert.Same(simple.Clause, occurrence.Clause);
        Assert.Same(simple.Clause, Assert.Single(result.Clauses));
        AssertDomain(
            Assert.Single(occurrence.EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet,
            "a",
            "b");
    }

    [Fact]
    public void Static_bash_c_remaps_nested_loop_plans_and_value_provenance()
    {
        var result = Parse(
            "bash -c 'for d in a b; do for f in x y; do echo \"$d/$f/$d\"; done; done'");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        AssertDomain(
            Assert.Single(Assert.Single(result.Commands).EffectiveArguments).Value,
            ShellValueDomainKind.FiniteSet,
            "a/x/a",
            "a/y/a",
            "b/x/b",
            "b/y/b");
    }

    [Fact]
    public void Decoded_loop_fails_after_outer_variable_state_mutation()
    {
        var result = Parse(
            "export f=ambient; bash -c 'for f in a; do printf %s \"$f\"; done'");

        Assert.True(result.IsUnparseable);
        Assert.Empty(result.Commands);
        Assert.Empty(result.Clauses);
        Assert.Contains("isolated non-interactive initial state", result.UnparseableReason!);
    }

    [Fact]
    public void Decoded_nonloop_command_remains_visible_after_outer_export()
    {
        var result = Parse("export f=ambient; bash -c 'printf ok'");

        Assert.False(result.IsUnparseable, result.UnparseableReason);
        Assert.Equal(
            new[] { "export", "printf" },
            result.Commands.Select(command => command.Clause.Verb.Tokens[0]));
    }

    private static ClauseElement EffectiveValueElement(
        ParsedCommand result,
        EffectiveArgument effective) =>
        Assert.Single(result.Commands).Clause.Elements[effective.ClauseElementIndex];

    private static ParsedCommand Parse(string input) =>
        new BashParser(new BashParserOptions
        {
            HomeDirectory = "/home/test",
            WorkingDirectory = "/work",
            InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
        }).Parse(input);

    private static string CommandVerb(CommandOccurrence command) => command.Clause.Verb.Joined;

    private static void AssertDomain(
        ShellValueDomain actual,
        ShellValueDomainKind expectedKind,
        params string[] expectedValues)
    {
        Assert.Equal(expectedKind, actual.Kind);
        Assert.Equal(expectedValues, actual.Values);
    }

    private static string NestedLoops(int depth)
    {
        var source = "echo ok";
        for (var index = 0; index < depth; index++)
        {
            source = $"for f{index} in x; do {source}; done";
        }

        return source;
    }
}
