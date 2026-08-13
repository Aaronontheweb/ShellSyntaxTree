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
using Xunit.Sdk;

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
        if (shell != "bash")
        {
            Assert.Null(entry.BashInitialStateMode);
            Assert.False(entry.PublishAuthoredSourceFacts);
        }

        if (shell != "powershell")
        {
            Assert.Null(entry.PowerShellInitialStateMode);
            Assert.Null(entry.PowerShellDialect);
        }

        var actual = CreateParser(
            shell,
            entry.BashInitialStateMode,
            entry.PowerShellInitialStateMode,
            entry.PowerShellDialect,
            entry.PublishAuthoredSourceFacts).Parse(entry.Input);
        AstAssert.Equal(entry.Expected!, actual, $"{shell}/{fileName}");
        AssertClauseElementInvariants(actual, $"{shell}/{fileName}");
        AssertAuthoredTokenCoverage(shell, actual, $"{shell}/{fileName}");
    }

    [Fact]
    public void Executable_corpus_shape_preserves_execution_region_facts()
    {
        var hostClause = new Clause
        {
            Verb = new VerbChain { Tokens = new[] { "ForEach-Object" } },
            Elements = new[]
            {
                new ClauseElement { Role = ClauseElementRole.Verb },
                new ClauseElement
                {
                    Role = ClauseElementRole.Argument,
                    Kind = ArgKind.DynamicSkip,
                },
            },
        };
        var bodyClause = new Clause
        {
            Verb = new VerbChain { Tokens = new[] { "Remove-Item" } },
            Elements = new[] { new ClauseElement { Role = ClauseElementRole.Verb } },
        };
        var actual = new ParsedCommand
        {
            Clauses = new[] { hostClause, bodyClause },
            Syntax = new ShellBlockSyntax
            {
                Statements = new ShellSyntaxNode[]
                {
                    new SimpleCommandSyntax
                    {
                        Clause = hostClause,
                        ExecutionRegions = new[]
                        {
                            new ExecutionRegionSyntax
                            {
                                Origin = ExecutionRegionOrigin.CommandArgument,
                                HostArgument = hostClause.Elements[1],
                                HostClauseElementIndex = 1,
                                Phase = ExecutionRegionPhase.Process,
                                Timing = ExecutionRegionTiming.Synchronous,
                                Cardinality = ExecutionRegionCardinality.OncePerInputObject,
                                Body = new ShellBlockSyntax
                                {
                                    Statements = new ShellSyntaxNode[]
                                    {
                                        new SimpleCommandSyntax { Clause = bodyClause },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
        var expected = new ExpectedParsedCommand
        {
            Clauses = new List<ExpectedClause>
            {
                new()
                {
                    Verb = new List<string> { "ForEach-Object" },
                    Elements = new List<ExpectedClauseElement>
                    {
                        new() { Role = ClauseElementRole.Verb },
                        new()
                        {
                            Role = ClauseElementRole.Argument,
                            Kind = ArgKind.DynamicSkip,
                        },
                    },
                },
                new() { Verb = new List<string> { "Remove-Item" } },
            },
            Syntax = new List<ExpectedSyntaxNode>
            {
                new() { Kind = "Block" },
                new()
                {
                    Kind = "SimpleCommand",
                    ParentIndex = 0,
                    Region = CommandAncestryRegion.Root,
                    ChildIndex = 0,
                    ClauseIndex = 0,
                },
                new()
                {
                    Kind = "ExecutionRegion",
                    ParentIndex = 1,
                    Region = CommandAncestryRegion.ExecutionRegion,
                    ChildIndex = 0,
                    ExecutionOrigin = ExecutionRegionOrigin.CommandArgument,
                    HostClauseElementIndex = 1,
                    ExecutionPhase = ExecutionRegionPhase.Process,
                    ExecutionTiming = ExecutionRegionTiming.Synchronous,
                    ExecutionCardinality = ExecutionRegionCardinality.OncePerInputObject,
                },
                new()
                {
                    Kind = "Block",
                    ParentIndex = 2,
                    Region = CommandAncestryRegion.ExecutionRegion,
                    ChildIndex = 0,
                },
                new()
                {
                    Kind = "SimpleCommand",
                    ParentIndex = 3,
                    Region = CommandAncestryRegion.Statement,
                    ChildIndex = 0,
                    ClauseIndex = 1,
                },
            },
        };

        AstAssert.Equal(expected, actual);
    }

    [Fact]
    public void Executable_corpus_rejects_invalid_execution_region_host_coordinates()
    {
        Assert.Throws<XunitException>(() => AstAssert.Equal(
            CreateExpectation(ArgKind.Literal, 1),
            CreateActual(ArgKind.Literal)));
        Assert.Throws<XunitException>(() => AstAssert.Equal(
            CreateExpectation(ArgKind.DynamicSkip, 2),
            CreateActual(ArgKind.DynamicSkip)));
        Assert.Throws<XunitException>(() => AstAssert.Equal(
            CreateExpectation(ArgKind.DynamicSkip, 1, 1),
            CreateActual(ArgKind.DynamicSkip)));

        static ParsedCommand CreateActual(ArgKind hostKind) => new()
        {
            Clauses = new[]
            {
                new Clause
                {
                    Verb = new VerbChain { Tokens = new[] { "host" } },
                    Elements = new[]
                    {
                        new ClauseElement { Role = ClauseElementRole.Verb },
                        new ClauseElement
                        {
                            Role = ClauseElementRole.Argument,
                            Kind = hostKind,
                        },
                    },
                },
            },
        };

        static ExpectedParsedCommand CreateExpectation(
            ArgKind hostKind,
            params int[] coordinates)
        {
            var syntax = new List<ExpectedSyntaxNode>
            {
                new() { Kind = "Block" },
                new()
                {
                    Kind = "SimpleCommand",
                    ParentIndex = 0,
                    Region = CommandAncestryRegion.Root,
                    ChildIndex = 0,
                    ClauseIndex = 0,
                },
            };
            for (var index = 0; index < coordinates.Length; index++)
            {
                syntax.Add(new ExpectedSyntaxNode
                {
                    Kind = "ExecutionRegion",
                    ParentIndex = 1,
                    Region = CommandAncestryRegion.ExecutionRegion,
                    ChildIndex = index,
                    ExecutionOrigin = ExecutionRegionOrigin.CommandArgument,
                    HostClauseElementIndex = coordinates[index],
                    ExecutionPhase = ExecutionRegionPhase.Main,
                    ExecutionTiming = ExecutionRegionTiming.Synchronous,
                    ExecutionCardinality = ExecutionRegionCardinality.Once,
                });
            }

            return new ExpectedParsedCommand
            {
                Clauses = new List<ExpectedClause>
                {
                    new()
                    {
                        Verb = new List<string> { "host" },
                        Elements = new List<ExpectedClauseElement>
                        {
                            new() { Role = ClauseElementRole.Verb },
                            new()
                            {
                                Role = ClauseElementRole.Argument,
                                Kind = hostKind,
                            },
                        },
                    },
                },
                Syntax = syntax,
            };
        }
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
                        (!parsed.Clauses.Any(clause => clause.IsCommandStringWrapped)
                         || directSegments.Contains(segment)
                         || isRedirectOperator
                         || isRedirectTarget) &&
                        !IsBashForEachStructuralToken(parsed.Syntax, token));
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
        var standaloneSubstitutions = StandaloneSubstitutionRegions(parsed.Syntax);
        var directExecutionRegions = DirectExecutionRegions(parsed.Syntax);
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
                    (!parsed.Clauses.Any(clause => clause.IsCommandStringWrapped)
                     || pwshDirectSegments.Contains(pwshSegment)
                     || isRedirectOperator
                     || isRedirectTarget) &&
                    !IsPwshForEachStructuralToken(parsed.Syntax, token) &&
                    !IsDirectExecutionRegionToken(
                        parsed.Source,
                        token,
                        directExecutionRegions) &&
                    !standaloneSubstitutions.Any(region =>
                        region.Start <= token.SourceStart &&
                        region.Start + region.Length >=
                            token.SourceStart + token.SourceLength));
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

    private static bool IsBashForEachStructuralToken(
        ShellSyntaxNode node,
        BashToken token)
    {
        if (node is ForEachSyntax forEach &&
            IsForEachStructuralToken(forEach, token))
        {
            return true;
        }

        return node switch
        {
            ShellBlockSyntax block => block.Statements.Any(
                child => IsBashForEachStructuralToken(child, token)),
            SimpleCommandSyntax simple => simple.Substitutions.Any(
                    child => IsBashForEachStructuralToken(child, token)) ||
                simple.ExecutionRegions.Any(
                    child => IsBashForEachStructuralToken(child, token)),
            PipelineSyntax pipeline => pipeline.Stages.Any(
                child => IsBashForEachStructuralToken(child, token)),
            CommandListSyntax list => list.Items.Any(
                item => IsBashForEachStructuralToken(item.Command, token)),
            GroupSyntax group => IsBashForEachStructuralToken(group.Body, token),
            ForEachSyntax nested =>
                IsBashForEachStructuralToken(nested.IteratorCommands, token) ||
                IsBashForEachStructuralToken(nested.Body, token),
            ConditionLoopSyntax loop =>
                IsBashForEachStructuralToken(loop.Condition, token) ||
                IsBashForEachStructuralToken(loop.Body, token),
            ConditionalSyntax conditional => conditional.Branches.Any(
                    branch => IsBashForEachStructuralToken(branch, token)) ||
                conditional.Else is not null &&
                IsBashForEachStructuralToken(conditional.Else, token),
            ConditionalBranchSyntax branch =>
                IsBashForEachStructuralToken(branch.Condition, token) ||
                IsBashForEachStructuralToken(branch.Body, token),
            CommandSubstitutionSyntax substitution =>
                IsBashForEachStructuralToken(substitution.Body, token),
            ExecutionRegionSyntax executionRegion =>
                IsBashForEachStructuralToken(executionRegion.Body, token),
            _ => false,
        };
    }

    private static bool IsForEachStructuralToken(
        ForEachSyntax forEach,
        BashToken token)
    {
        if (forEach.SourceStart is null || forEach.SourceLength is null ||
            forEach.Binding.Source.SourceStart is null ||
            forEach.Binding.Source.SourceLength is null ||
            forEach.Iterable.SourceStart is null ||
            forEach.Iterable.SourceLength is null ||
            forEach.Body.SourceStart is null || forEach.Body.SourceLength is null)
        {
            return false;
        }

        var tokenEnd = token.SourceStart + token.SourceLength;
        var loopEnd = forEach.SourceStart.Value + forEach.SourceLength.Value;
        var bindingStart = forEach.Binding.Source.SourceStart.Value;
        var bindingEnd = bindingStart + forEach.Binding.Source.SourceLength.Value;
        var iterableStart = forEach.Iterable.SourceStart.Value;
        var iterableEnd = iterableStart + forEach.Iterable.SourceLength.Value;
        var bodyStart = forEach.Body.SourceStart.Value;
        var bodyEnd = bodyStart + forEach.Body.SourceLength.Value;
        if (token.SourceStart >= iterableStart && tokenEnd <= iterableEnd ||
            token.SourceStart == bindingStart && tokenEnd == bindingEnd)
        {
            return true;
        }

        return token.Kind == BashTokenKind.Word &&
            (string.Equals(token.Value, "for", StringComparison.Ordinal) &&
             token.SourceStart == forEach.SourceStart ||
             string.Equals(token.Value, "in", StringComparison.Ordinal) &&
             token.SourceStart >= bindingEnd && tokenEnd <= iterableStart ||
             string.Equals(token.Value, "do", StringComparison.Ordinal) &&
             token.SourceStart >= iterableEnd && tokenEnd == bodyStart ||
             string.Equals(token.Value, "done", StringComparison.Ordinal) &&
             token.SourceStart == bodyEnd && tokenEnd == loopEnd);
    }

    private static bool IsPwshForEachStructuralToken(
        ShellSyntaxNode node,
        PwshToken token)
    {
        if (node is ForEachSyntax forEach &&
            IsPwshForEachStructuralToken(forEach, token))
        {
            return true;
        }

        return node switch
        {
            ShellBlockSyntax block => block.Statements.Any(
                child => IsPwshForEachStructuralToken(child, token)),
            SimpleCommandSyntax simple => simple.Substitutions.Any(
                    child => IsPwshForEachStructuralToken(child, token)) ||
                simple.ExecutionRegions.Any(
                    child => IsPwshForEachStructuralToken(child, token)),
            PipelineSyntax pipeline => pipeline.Stages.Any(
                child => IsPwshForEachStructuralToken(child, token)),
            CommandListSyntax list => list.Items.Any(
                item => IsPwshForEachStructuralToken(item.Command, token)),
            GroupSyntax group => IsPwshForEachStructuralToken(group.Body, token),
            ForEachSyntax nested =>
                IsPwshForEachStructuralToken(nested.IteratorCommands, token) ||
                IsPwshForEachStructuralToken(nested.Body, token),
            ConditionLoopSyntax loop =>
                IsPwshForEachStructuralToken(loop.Condition, token) ||
                IsPwshForEachStructuralToken(loop.Body, token),
            ConditionalSyntax conditional => conditional.Branches.Any(
                    branch => IsPwshForEachStructuralToken(branch, token)) ||
                conditional.Else is not null &&
                IsPwshForEachStructuralToken(conditional.Else, token),
            ConditionalBranchSyntax branch =>
                IsPwshForEachStructuralToken(branch.Condition, token) ||
                IsPwshForEachStructuralToken(branch.Body, token),
            CommandSubstitutionSyntax substitution =>
                IsPwshForEachStructuralToken(substitution.Body, token),
            ExecutionRegionSyntax executionRegion =>
                IsPwshForEachStructuralToken(executionRegion.Body, token),
            _ => false,
        };
    }

    private static bool IsPwshForEachStructuralToken(
        ForEachSyntax forEach,
        PwshToken token)
    {
        if (forEach.SourceStart is null || forEach.SourceLength is null ||
            forEach.Binding.Source.SourceStart is null ||
            forEach.Binding.Source.SourceLength is null ||
            forEach.Iterable.SourceStart is null ||
            forEach.Iterable.SourceLength is null ||
            forEach.Body.SourceStart is null || forEach.Body.SourceLength is null)
        {
            return false;
        }

        var tokenEnd = token.SourceStart + token.SourceLength;
        var bindingStart = forEach.Binding.Source.SourceStart.Value;
        var bindingEnd = bindingStart + forEach.Binding.Source.SourceLength.Value;
        var iterableStart = forEach.Iterable.SourceStart.Value;
        var iterableEnd = iterableStart + forEach.Iterable.SourceLength.Value;
        var bodyStart = forEach.Body.SourceStart.Value;
        var bodyEnd = bodyStart + forEach.Body.SourceLength.Value;
        if (token.SourceStart >= iterableStart && tokenEnd <= iterableEnd ||
            token.SourceStart == bindingStart && tokenEnd == bindingEnd ||
            token.SourceStart == bodyStart - 1 && tokenEnd == bodyEnd + 1)
        {
            return true;
        }

        return token.Kind == PwshTokenKind.Word &&
            (string.Equals(token.Value, "foreach", StringComparison.OrdinalIgnoreCase) &&
             token.SourceStart == forEach.SourceStart ||
             string.Equals(token.Value, "in", StringComparison.OrdinalIgnoreCase) &&
             token.SourceStart >= bindingEnd && tokenEnd <= iterableStart);
    }

    private static IReadOnlyList<SourceRegion> StandaloneSubstitutionRegions(
        ShellSyntaxNode syntax)
    {
        var regions = new List<SourceRegion>();
        CollectStandaloneSubstitutionRegions(syntax, attachedToSimple: false, regions);
        return regions;
    }

    private static void CollectStandaloneSubstitutionRegions(
        ShellSyntaxNode node,
        bool attachedToSimple,
        ICollection<SourceRegion> regions)
    {
        switch (node)
        {
            case CommandSubstitutionSyntax substitution:
                if (!attachedToSimple && substitution.SourceStart.HasValue &&
                    substitution.SourceLength.HasValue)
                {
                    regions.Add(new SourceRegion(
                        substitution.SourceStart.Value,
                        substitution.SourceLength.Value));
                }

                CollectStandaloneSubstitutionRegions(
                    substitution.Body, attachedToSimple: false, regions);
                break;
            case SimpleCommandSyntax simple:
                foreach (var substitution in simple.Substitutions)
                {
                    CollectStandaloneSubstitutionRegions(
                        substitution, attachedToSimple: true, regions);
                }

                foreach (var executionRegion in simple.ExecutionRegions)
                {
                    CollectStandaloneSubstitutionRegions(
                        executionRegion, attachedToSimple: false, regions);
                }

                break;
            case ExecutionRegionSyntax executionRegion:
                CollectStandaloneSubstitutionRegions(
                    executionRegion.Body, attachedToSimple: false, regions);
                break;
            case ShellBlockSyntax block:
                foreach (var statement in block.Statements)
                {
                    CollectStandaloneSubstitutionRegions(
                        statement, attachedToSimple: false, regions);
                }

                break;
            case PipelineSyntax pipeline:
                foreach (var stage in pipeline.Stages)
                {
                    CollectStandaloneSubstitutionRegions(
                        stage, attachedToSimple: false, regions);
                }

                break;
            case CommandListSyntax list:
                foreach (var item in list.Items)
                {
                    CollectStandaloneSubstitutionRegions(
                        item.Command, attachedToSimple: false, regions);
                }

                break;
            case GroupSyntax group:
                CollectStandaloneSubstitutionRegions(
                    group.Body, attachedToSimple: false, regions);
                break;
        }
    }

    private readonly record struct SourceRegion(int Start, int Length);

    private static IReadOnlyList<DirectExecutionRegionSource> DirectExecutionRegions(
        ShellSyntaxNode syntax)
    {
        var regions = new List<DirectExecutionRegionSource>();
        CollectDirectExecutionRegions(syntax, regions);
        return regions;
    }

    private static void CollectDirectExecutionRegions(
        ShellSyntaxNode node,
        ICollection<DirectExecutionRegionSource> regions)
    {
        switch (node)
        {
            case ExecutionRegionSyntax region:
                if ((region.Origin is ExecutionRegionOrigin.DirectCall or
                        ExecutionRegionOrigin.DotSource) &&
                    region.SourceStart.HasValue && region.SourceLength.HasValue)
                {
                    regions.Add(new DirectExecutionRegionSource(
                        region.SourceStart.Value,
                        region.SourceLength.Value,
                        region.Origin));
                }

                CollectDirectExecutionRegions(region.Body, regions);
                break;
            case SimpleCommandSyntax simple:
                foreach (var substitution in simple.Substitutions)
                {
                    CollectDirectExecutionRegions(substitution, regions);
                }

                foreach (var region in simple.ExecutionRegions)
                {
                    CollectDirectExecutionRegions(region, regions);
                }

                break;
            case CommandSubstitutionSyntax substitution:
                CollectDirectExecutionRegions(substitution.Body, regions);
                break;
            case ShellBlockSyntax block:
                foreach (var statement in block.Statements)
                {
                    CollectDirectExecutionRegions(statement, regions);
                }

                break;
            case PipelineSyntax pipeline:
                foreach (var stage in pipeline.Stages)
                {
                    CollectDirectExecutionRegions(stage, regions);
                }

                break;
            case CommandListSyntax list:
                foreach (var item in list.Items)
                {
                    CollectDirectExecutionRegions(item.Command, regions);
                }

                break;
            case GroupSyntax group:
                CollectDirectExecutionRegions(group.Body, regions);
                break;
        }
    }

    private static bool IsDirectExecutionRegionToken(
        string source,
        PwshToken token,
        IReadOnlyList<DirectExecutionRegionSource> regions)
    {
        var tokenEnd = token.SourceStart + token.SourceLength;
        foreach (var region in regions)
        {
            if (token.SourceStart >= region.Start &&
                tokenEnd <= region.Start + region.Length)
            {
                return true;
            }

            if (region.Origin == ExecutionRegionOrigin.DotSource &&
                token.Kind == PwshTokenKind.Word && token.Value == "." &&
                tokenEnd <= region.Start &&
                source.Substring(tokenEnd, region.Start - tokenEnd)
                    .All(char.IsWhiteSpace))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct DirectExecutionRegionSource(
        int Start,
        int Length,
        ExecutionRegionOrigin Origin);

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
    internal static IShellParser CreateParser(
        string shell,
        BashInitialStateMode? bashInitialStateMode = null,
        PwshInitialStateMode? powerShellInitialStateMode = null,
        PwshDialect? powerShellDialect = null,
        bool publishAuthoredSourceFacts = false) => shell switch
        {
            "bash" => new BashParser(new BashParserOptions
            {
                HomeDirectory = "/home/test",
                WorkingDirectory = "/work",
                InitialStateMode = bashInitialStateMode ??
                    BashInitialStateMode.IsolatedNonInteractive,
                PublishAuthoredSourceFacts = publishAuthoredSourceFacts,
            }),
            "powershell" => new PwshParser(new PwshParserOptions
            {
                HomeDirectory = "C:/Users/user",
                WorkingDirectory = "C:/work",
                InitialStateMode = powerShellInitialStateMode ?? PwshInitialStateMode.Unknown,
                Dialect = powerShellDialect ?? PwshDialect.PowerShell7,
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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };
}

// -------- corpus DTOs (JSON shape) --------

public sealed record CorpusEntry
{
    public string Name { get; init; } = "";

    public string Input { get; init; } = "";

    public BashInitialStateMode? BashInitialStateMode { get; init; }

    public PwshInitialStateMode? PowerShellInitialStateMode { get; init; }

    public PwshDialect? PowerShellDialect { get; init; }

    public bool PublishAuthoredSourceFacts { get; init; }

    public ExpectedParsedCommand? Expected { get; init; }

    public string? Notes { get; init; }

    /// <summary>
    /// Ground-truth expectation for the selected PowerShell validation gate
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

    /// <summary>
    /// Optional pre-order structural projection. When present, every syntax
    /// node, relationship, span, and simple-command clause identity is pinned.
    /// </summary>
    public List<ExpectedSyntaxNode>? Syntax { get; init; }

    /// <summary>
    /// Optional executable-accounting projection. When present, command order,
    /// role, ancestry, completeness, and compatibility-clause identity are pinned.
    /// </summary>
    public List<ExpectedCommandOccurrence>? Commands { get; init; }
}

public sealed record ExpectedSyntaxNode
{
    public string Kind { get; init; } = "";

    public int? ParentIndex { get; init; }

    public CommandAncestryRegion Region { get; init; }

    public int? ChildIndex { get; init; }

    public int? SourceStart { get; init; }

    public int? SourceLength { get; init; }

    public int? ClauseIndex { get; init; }

    public ShellGroupKind? GroupKind { get; init; }

    public CompoundOperator? ListOperator { get; init; }

    public string? BindingName { get; init; }

    public string? BindingRaw { get; init; }

    public int? BindingSourceStart { get; init; }

    public int? BindingSourceLength { get; init; }

    public string? IterableRaw { get; init; }

    public int? IterableSourceStart { get; init; }

    public int? IterableSourceLength { get; init; }

    public ExecutionRegionOrigin? ExecutionOrigin { get; init; }

    public int? HostClauseElementIndex { get; init; }

    public ExecutionRegionPhase? ExecutionPhase { get; init; }

    public ExecutionRegionTiming? ExecutionTiming { get; init; }

    public ExecutionRegionCardinality? ExecutionCardinality { get; init; }
}

public sealed record ExpectedCommandOccurrence
{
    public int ClauseIndex { get; init; }

    public CommandOccurrenceRole ImmediateRole { get; init; }

    public bool IsComplete { get; init; }

    public List<ExpectedCommandAncestryFrame>? Ancestry { get; init; }

    public List<ExpectedAnalyzedArgument>? Arguments { get; init; }

    public ExpectedValueDomain? WorkingDirectory { get; init; }

    public List<ExpectedRedirectAnalysis>? Redirects { get; init; }
}

public sealed record ExpectedRedirectAnalysis
{
    public int RedirectIndex { get; init; } = -1;

    public string Kind { get; init; } = "";

    public string SourceKind { get; init; } = "";

    public int? SourceDescriptor { get; init; }

    public FileRedirectMode? FileMode { get; init; }

    public int? TargetDescriptor { get; init; }

    public ExpectedValueDomain? Value { get; init; }

    public ExpectedHereDocumentAnalysis? HereDocument { get; init; }

    public bool IsComplete { get; init; }
}

public sealed record ExpectedHereDocumentAnalysis
{
    public ExpectedSourceFragment Delimiter { get; init; } = new();

    public ExpectedSourceFragment Body { get; init; } = new();

    public HereDocumentExpansionMode ExpansionMode { get; init; }

    public bool StripLeadingTabs { get; init; }

    public bool IsComplete { get; init; }
}

public sealed record ExpectedSourceFragment
{
    public string Raw { get; init; } = "";

    public int? SourceStart { get; init; }

    public int? SourceLength { get; init; }
}

public sealed record ExpectedAnalyzedArgument
{
    public int ClauseArgumentIndex { get; init; } = -1;

    public int ClauseElementIndex { get; init; } = -1;

    public ExpectedValueDomain Value { get; init; } = new();

    public ExpectedValueDomain? AuthoredFileSystemValue { get; init; }
}

public sealed record ExpectedValueDomain
{
    public string Kind { get; init; } = "";

    public List<string>? Values { get; init; }

    public string? Pattern { get; init; }

    public string? CoveringDirectory { get; init; }
}

public sealed record ExpectedCommandAncestryFrame
{
    public string AncestorKind { get; init; } = "";

    public CommandAncestryRegion Region { get; init; }

    public int? ChildIndex { get; init; }

    public int? SourceStart { get; init; }

    public int? SourceLength { get; init; }
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
/// Ground-truth expectation for the selected PowerShell validation gate
/// (SPEC.POWERSHELL.md §13). Meaningful only when <c>isUnparseable</c> is
/// true.
/// </summary>
public enum OracleExpectation
{
    /// <summary>Genuinely malformed PowerShell — the selected real shell must
    /// also reject it.</summary>
    SyntaxError,

    /// <summary>Valid PowerShell the parser deliberately does not model —
    /// the selected real shell must accept it. Also covers an over-cap input, an
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
