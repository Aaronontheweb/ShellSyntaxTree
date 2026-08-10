// -----------------------------------------------------------------------
// <copyright file="CorpusJson.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ShellSyntaxTree;

namespace ShellSyntaxTree.Tools.PwshCorpus;

/// <summary>
/// Serializes a live <see cref="ParsedCommand"/> into the corpus JSON
/// schema consumed by <c>CorpusRunnerTests</c> (SPEC §13 and
/// SPEC.POWERSHELL.md §13).
/// Default-valued fields are omitted to keep entries readable.
/// </summary>
internal static class CorpusJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    internal static string BuildEntry(
        string name,
        string input,
        ParsedCommand parsed,
        string notes,
        bool outOfScope,
        bool includeElements,
        bool includeStructure,
        bool includeOptionalAssertions,
        bool includeV03Assertions,
        PwshInitialStateMode? powerShellInitialStateMode = null,
        PwshDialect? powerShellDialect = null)
    {
        var obj = new JsonObject
        {
            ["name"] = name,
            ["input"] = input,
        };

        if (powerShellInitialStateMode is PwshInitialStateMode initialStateMode)
        {
            obj["powerShellInitialStateMode"] = initialStateMode.ToString();
        }

        if (powerShellDialect is PwshDialect dialect)
        {
            obj["powerShellDialect"] = dialect.ToString();
        }

        obj["expected"] = BuildExpected(
            parsed,
            includeElements,
            includeStructure,
            includeOptionalAssertions,
            includeV03Assertions);
        obj["notes"] = notes;

        if (parsed.IsUnparseable && outOfScope)
        {
            obj["oracleExpectation"] = "OutOfScope";
        }

        return obj.ToJsonString(WriteOptions) + "\n";
    }

    private static JsonObject BuildExpected(
        ParsedCommand parsed,
        bool includeElements,
        bool includeStructure,
        bool includeOptionalAssertions,
        bool includeV03Assertions)
    {
        var expected = new JsonObject { ["isUnparseable"] = parsed.IsUnparseable };
        if (parsed.IsUnparseable)
        {
            expected["unparseableReasonContains"] = StableReasonSubstring(parsed.UnparseableReason);
            return expected;
        }

        var clauses = new JsonArray();
        foreach (var clause in parsed.Clauses)
        {
            clauses.Add(BuildClause(clause, includeElements, includeOptionalAssertions));
        }

        expected["clauses"] = clauses;
        if (includeStructure)
        {
            expected["syntax"] = BuildSyntax(parsed, includeV03Assertions);
            expected["commands"] = BuildCommands(parsed, includeV03Assertions);
        }

        return expected;
    }

    private static JsonArray BuildSyntax(
        ParsedCommand parsed,
        bool includeV03Assertions)
    {
        var nodes = new JsonArray();
        AppendSyntax(
            parsed.Syntax,
            parentIndex: null,
            CommandAncestryRegion.Unknown,
            childIndex: null,
            listOperator: null,
            parsed,
            nodes,
            includeV03Assertions,
            isRootBlock: true);
        return nodes;
    }

    private static void AppendSyntax(
        ShellSyntaxNode node,
        int? parentIndex,
        CommandAncestryRegion region,
        int? childIndex,
        CompoundOperator? listOperator,
        ParsedCommand parsed,
        JsonArray nodes,
        bool includeV03Assertions,
        bool isRootBlock = false)
    {
        if (node.Kind == ShellSyntaxKind.Unknown)
        {
            throw new InvalidOperationException(
                "Cannot generate corpus expectations for an unknown syntax node");
        }

        var currentIndex = nodes.Count;
        var clause = (node as SimpleCommandSyntax)?.Clause;
        var forEachNode = node as ForEachSyntax;
        var executionRegionNode = node as ExecutionRegionSyntax;
        var clauseIndex = clause is null ? (int?)null : FindClauseIndex(parsed, clause);
        var syntax = new JsonObject
        {
            ["kind"] = node.Kind.ToString(),
            ["parentIndex"] = JsonValue.Create(parentIndex),
            ["region"] = region.ToString(),
            ["childIndex"] = JsonValue.Create(childIndex),
            ["sourceStart"] = JsonValue.Create(node.SourceStart),
            ["sourceLength"] = JsonValue.Create(node.SourceLength),
            ["clauseIndex"] = JsonValue.Create(clauseIndex),
            ["groupKind"] = (node as GroupSyntax)?.GroupKind.ToString(),
            ["listOperator"] = listOperator?.ToString(),
        };
        if (includeV03Assertions && forEachNode is not null)
        {
            syntax["bindingName"] = forEachNode.Binding.Name;
            syntax["bindingRaw"] = forEachNode.Binding.Source.Raw;
            syntax["bindingSourceStart"] = forEachNode.Binding.Source.SourceStart;
            syntax["bindingSourceLength"] = forEachNode.Binding.Source.SourceLength;
            syntax["iterableRaw"] = forEachNode.Iterable.Raw;
            syntax["iterableSourceStart"] = forEachNode.Iterable.SourceStart;
            syntax["iterableSourceLength"] = forEachNode.Iterable.SourceLength;
        }
        if (executionRegionNode is not null)
        {
            syntax["executionOrigin"] = executionRegionNode.Origin.ToString();
            syntax["hostClauseElementIndex"] =
                JsonValue.Create(executionRegionNode.HostClauseElementIndex);
            syntax["executionPhase"] = executionRegionNode.Phase.ToString();
            syntax["executionTiming"] = executionRegionNode.Timing.ToString();
            syntax["executionCardinality"] =
                executionRegionNode.Cardinality.ToString();
        }

        nodes.Add(syntax);

        switch (node)
        {
            case ShellBlockSyntax block:
                var statementRegion = isRootBlock
                    ? CommandAncestryRegion.Root
                    : CommandAncestryRegion.Statement;
                for (var index = 0; index < block.Statements.Count; index++)
                {
                    AppendSyntax(
                        block.Statements[index],
                        currentIndex,
                        statementRegion,
                        index,
                        listOperator: null,
                        parsed,
                        nodes,
                        includeV03Assertions);
                }

                break;
            case SimpleCommandSyntax simple:
                for (var index = 0; index < simple.Substitutions.Count; index++)
                {
                    AppendSyntax(
                        simple.Substitutions[index],
                        currentIndex,
                        CommandAncestryRegion.Substitution,
                        index,
                        listOperator: null,
                        parsed,
                        nodes,
                        includeV03Assertions);
                }

                for (var index = 0; index < simple.ExecutionRegions.Count; index++)
                {
                    AppendSyntax(
                        simple.ExecutionRegions[index],
                        currentIndex,
                        CommandAncestryRegion.ExecutionRegion,
                        index,
                        listOperator: null,
                        parsed,
                        nodes,
                        includeV03Assertions);
                }

                break;
            case PipelineSyntax pipeline:
                for (var index = 0; index < pipeline.Stages.Count; index++)
                {
                    AppendSyntax(
                        pipeline.Stages[index],
                        currentIndex,
                        CommandAncestryRegion.PipelineStage,
                        index,
                        listOperator: null,
                        parsed,
                        nodes,
                        includeV03Assertions);
                }

                break;
            case CommandListSyntax list:
                for (var index = 0; index < list.Items.Count; index++)
                {
                    AppendSyntax(
                        list.Items[index].Command,
                        currentIndex,
                        CommandAncestryRegion.Statement,
                        index,
                        list.Items[index].Operator,
                        parsed,
                        nodes,
                        includeV03Assertions);
                }

                break;
            case GroupSyntax group:
                AppendSyntax(
                    group.Body,
                    currentIndex,
                    CommandAncestryRegion.GroupBody,
                    childIndex: null,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                break;
            case ForEachSyntax forEach:
                AppendSyntax(
                    forEach.IteratorCommands,
                    currentIndex,
                    CommandAncestryRegion.Iterator,
                    childIndex: null,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                AppendSyntax(
                    forEach.Body,
                    currentIndex,
                    CommandAncestryRegion.LoopBody,
                    childIndex: null,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                break;
            case ConditionLoopSyntax loop:
                AppendSyntax(
                    loop.Condition,
                    currentIndex,
                    CommandAncestryRegion.Condition,
                    childIndex: null,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                AppendSyntax(
                    loop.Body,
                    currentIndex,
                    CommandAncestryRegion.LoopBody,
                    childIndex: null,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                break;
            case ConditionalSyntax conditional:
                for (var index = 0; index < conditional.Branches.Count; index++)
                {
                    AppendSyntax(
                        conditional.Branches[index],
                        currentIndex,
                        CommandAncestryRegion.Branch,
                        index,
                        listOperator: null,
                        parsed,
                        nodes,
                        includeV03Assertions);
                }

                if (conditional.Else is not null)
                {
                    AppendSyntax(
                        conditional.Else,
                        currentIndex,
                        CommandAncestryRegion.Branch,
                        conditional.Branches.Count,
                        listOperator: null,
                        parsed,
                        nodes,
                        includeV03Assertions);
                }

                break;
            case ConditionalBranchSyntax branch:
                AppendSyntax(
                    branch.Condition,
                    currentIndex,
                    CommandAncestryRegion.Condition,
                    childIndex: null,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                AppendSyntax(
                    branch.Body,
                    currentIndex,
                    CommandAncestryRegion.Branch,
                    childIndex: null,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                break;
            case CommandSubstitutionSyntax substitution:
                AppendSyntax(
                    substitution.Body,
                    currentIndex,
                    CommandAncestryRegion.Substitution,
                    childIndex,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                break;
            case ExecutionRegionSyntax executionRegion:
                AppendSyntax(
                    executionRegion.Body,
                    currentIndex,
                    CommandAncestryRegion.ExecutionRegion,
                    childIndex: 0,
                    listOperator: null,
                    parsed,
                    nodes,
                    includeV03Assertions);
                break;
            default:
                throw new InvalidOperationException(
                    $"Cannot generate corpus expectations for syntax type {node.GetType().FullName}");
        }
    }

    private static JsonArray BuildCommands(
        ParsedCommand parsed,
        bool includeV03Assertions)
    {
        var commands = new JsonArray();
        foreach (var command in parsed.Commands)
        {
            if (command.ImmediateRole == CommandOccurrenceRole.Unknown)
            {
                throw new InvalidOperationException(
                    "Cannot generate corpus expectations for an unknown command role");
            }

            var ancestry = new JsonArray();
            foreach (var frame in command.Ancestry)
            {
                if (frame.AncestorKind == ShellSyntaxKind.Unknown ||
                    frame.Region == CommandAncestryRegion.Unknown)
                {
                    throw new InvalidOperationException(
                        "Cannot generate corpus expectations for unknown command ancestry");
                }

                ancestry.Add(new JsonObject
                {
                    ["ancestorKind"] = frame.AncestorKind.ToString(),
                    ["region"] = frame.Region.ToString(),
                    ["childIndex"] = JsonValue.Create(frame.ChildIndex),
                    ["sourceStart"] = JsonValue.Create(frame.SourceStart),
                    ["sourceLength"] = JsonValue.Create(frame.SourceLength),
                });
            }

            var commandJson = new JsonObject
            {
                ["clauseIndex"] = FindClauseIndex(parsed, command.Clause),
                ["immediateRole"] = command.ImmediateRole.ToString(),
                ["isComplete"] = command.IsComplete,
                ["ancestry"] = ancestry,
            };
            if (includeV03Assertions)
            {
                var effectiveArguments = new JsonArray();
                foreach (var effective in command.EffectiveArguments)
                {
                    effectiveArguments.Add(new JsonObject
                    {
                        ["clauseElementIndex"] = effective.ClauseElementIndex,
                        ["value"] = BuildValueDomain(effective.Value),
                    });
                }

                commandJson["effectiveArguments"] = effectiveArguments;
                commandJson["workingDirectory"] = BuildValueDomain(command.WorkingDirectory);

                if (command.Redirects.Count > 0)
                {
                    var redirects = new JsonArray();
                    foreach (var redirect in command.Redirects)
                    {
                        redirects.Add(new JsonObject
                        {
                            ["redirectIndex"] = redirect.RedirectIndex,
                            ["sourceKind"] = redirect.Source.Kind.ToString(),
                            ["sourceDescriptor"] = JsonValue.Create(
                                redirect.Source.Descriptor),
                            ["operation"] = redirect.Operation.ToString(),
                            ["targetDescriptor"] = JsonValue.Create(
                                redirect.TargetDescriptor),
                            ["target"] = BuildValueDomain(redirect.Target),
                            ["isPathRelevant"] = redirect.IsPathRelevant,
                            ["isComplete"] = redirect.IsComplete,
                        });
                    }

                    commandJson["redirects"] = redirects;
                }
            }

            commands.Add(commandJson);
        }

        return commands;
    }

    private static JsonObject BuildValueDomain(ShellValueDomain domain)
    {
        var values = new JsonArray();
        foreach (var value in domain.Values)
        {
            values.Add(value);
        }

        return new JsonObject
        {
            ["kind"] = domain.Kind.ToString(),
            ["values"] = values,
            ["pattern"] = domain.Pattern,
            ["coveringDirectory"] = domain.CoveringDirectory,
        };
    }

    private static int FindClauseIndex(ParsedCommand parsed, Clause clause)
    {
        for (var index = 0; index < parsed.Clauses.Count; index++)
        {
            if (object.ReferenceEquals(parsed.Clauses[index], clause))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            "Structural corpus generation found a Clause outside ParsedCommand.Clauses");
    }

    private static JsonObject BuildClause(
        Clause clause,
        bool includeElements,
        bool includeOptionalAssertions)
    {
        var verb = new JsonArray();
        foreach (var token in clause.Verb.Tokens)
        {
            verb.Add(token);
        }

        var obj = new JsonObject
        {
            ["operator"] = clause.Operator.ToString(),
            ["verb"] = verb,
        };

        if (clause.Verb.CanonicalVerb is not null)
        {
            obj["canonicalVerb"] = clause.Verb.CanonicalVerb;
        }

        if (clause.Verb.IsDynamic)
        {
            obj["isDynamic"] = true;
        }

        var args = new JsonArray();
        foreach (var arg in clause.Args)
        {
            args.Add(BuildArg(arg, includeOptionalAssertions));
        }

        obj["args"] = args;

        var redirects = new JsonArray();
        foreach (var redirect in clause.Redirects)
        {
            redirects.Add(BuildRedirect(redirect, includeOptionalAssertions));
        }

        obj["redirects"] = redirects;

        if (includeElements)
        {
            var elements = new JsonArray();
            foreach (var element in clause.Elements)
            {
                elements.Add(BuildElement(element));
            }

            obj["elements"] = elements;
        }

        if (clause.IsSubshell)
        {
            obj["isSubshell"] = true;
        }

        if (clause.IsCommandStringWrapped)
        {
            obj["isCommandStringWrapped"] = true;
        }

        return obj;
    }

    private static JsonObject BuildArg(Arg arg, bool includeOptionalAssertions)
    {
        var obj = new JsonObject
        {
            ["raw"] = arg.Raw,
            ["kind"] = arg.Kind.ToString(),
            ["isPath"] = arg.IsPath,
        };

        if (arg.Resolved is not null)
        {
            obj["resolved"] = arg.Resolved;
        }
        else if (includeOptionalAssertions)
        {
            obj["resolved"] = "__NULL__";
        }

        if (includeOptionalAssertions)
        {
            obj["isFlag"] = arg.IsFlag;
        }

        if (arg.IsCwdAttribution)
        {
            obj["isCwdAttribution"] = true;
        }

        return obj;
    }

    private static JsonObject BuildRedirect(
        Redirect redirect,
        bool includeOptionalAssertions)
    {
        var obj = new JsonObject
        {
            ["direction"] = redirect.Direction.ToString(),
            ["target"] = redirect.Target,
        };

        if (redirect.IsDynamicSkip)
        {
            obj["isDynamicSkip"] = true;
        }
        else if (includeOptionalAssertions)
        {
            obj["isDynamicSkip"] = false;
        }

        return obj;
    }

    private static JsonObject BuildElement(ClauseElement element)
    {
        var obj = new JsonObject
        {
            ["raw"] = element.Raw,
            ["value"] = element.Value,
            ["role"] = element.Role.ToString(),
            ["sourceStart"] = element.SourceStart,
            ["sourceLength"] = element.SourceLength,
            ["precedingVerbElementCount"] = element.PrecedingVerbElementCount,
            ["kind"] = element.Kind.ToString(),
            ["isFlag"] = element.IsFlag,
            ["isPath"] = element.IsPath,
        };

        if (element.Resolved is not null)
        {
            obj["resolved"] = element.Resolved;
        }

        return obj;
    }

    /// <summary>
    /// A stable substring of an <c>UnparseableReason</c> for the corpus's
    /// <c>unparseableReasonContains</c> assertion — trims the variable
    /// "at position N" / "(N chars)" tails so the assertion does not pin a
    /// position that could shift.
    /// </summary>
    private static string StableReasonSubstring(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        var trimmed = reason!;
        var atPosition = trimmed.IndexOf(" at position", StringComparison.Ordinal);
        if (atPosition > 0)
        {
            trimmed = trimmed.Substring(0, atPosition);
        }

        var paren = trimmed.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
        {
            trimmed = trimmed.Substring(0, paren);
        }

        return trimmed;
    }
}
