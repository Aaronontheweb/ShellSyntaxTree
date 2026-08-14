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
        var currentIndex = nodes.Count;
        var clause = (node as SimpleCommandSyntax)?.Clause;
        var forEachNode = node as ForEachSyntax;
        var executionRegionNode = node as ExecutionRegionSyntax;
        var clauseIndex = clause is null ? (int?)null : FindClauseIndex(parsed, clause);
        var syntax = new JsonObject
        {
            ["kind"] = SyntaxKind(node),
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
            syntax["bindingName"] = forEachNode.BindingName;
            syntax["bindingRaw"] = forEachNode.BindingSource.Raw;
            syntax["bindingSourceStart"] = forEachNode.BindingSource.SourceStart;
            syntax["bindingSourceLength"] = forEachNode.BindingSource.SourceLength;
            syntax["iterableRaw"] = forEachNode.Iterable.Raw;
            syntax["iterableSourceStart"] = forEachNode.Iterable.SourceStart;
            syntax["iterableSourceLength"] = forEachNode.Iterable.SourceLength;
        }
        if (executionRegionNode is not null)
        {
            syntax["executionOrigin"] = executionRegionNode.Origin.ToString();
            syntax["hostClauseElementIndex"] =
                JsonValue.Create(FindClauseElementIndex(parsed, executionRegionNode.HostArgument));
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
                    // The body ancestry keeps the owning region's authored
                    // sibling coordinate so multiple host regions stay distinct.
                    childIndex,
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
                if (frame.Region == CommandAncestryRegion.Unknown)
                {
                    throw new InvalidOperationException(
                        "Cannot generate corpus expectations for unknown command ancestry");
                }

                ancestry.Add(new JsonObject
                {
                    ["ancestorKind"] = SyntaxKind(frame.Ancestor),
                    ["region"] = frame.Region.ToString(),
                    ["childIndex"] = JsonValue.Create(frame.ChildIndex),
                    ["sourceStart"] = JsonValue.Create(frame.Ancestor.SourceStart),
                    ["sourceLength"] = JsonValue.Create(frame.Ancestor.SourceLength),
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
                var arguments = new JsonArray();
                foreach (var analyzed in command.Arguments)
                {
                    var argument = new JsonObject
                    {
                        ["clauseArgumentIndex"] = FindClauseArgumentIndex(
                            command.Clause,
                            analyzed.Argument),
                        ["clauseElementIndex"] = FindClauseElementIndex(
                            command.Clause,
                            analyzed.Element),
                        ["value"] = BuildValueDomain(analyzed.Value),
                    };
                    if (analyzed.AuthoredFileSystemValue is not ShellValueDomain.Unknown)
                    {
                        argument["authoredFileSystemValue"] =
                            BuildValueDomain(analyzed.AuthoredFileSystemValue);
                    }

                    if (analyzed.AuthoredNonFileSystemValue is not ShellValueDomain.Unknown)
                    {
                        argument["authoredNonFileSystemValue"] =
                            BuildValueDomain(analyzed.AuthoredNonFileSystemValue);
                    }

                    arguments.Add(argument);
                }

                commandJson["arguments"] = arguments;
                commandJson["workingDirectory"] = BuildValueDomain(command.WorkingDirectory);
                commandJson["workingDirectoryEffect"] =
                    BuildWorkingDirectoryEffect(command.WorkingDirectoryEffect);

                if (command.Redirects.Count > 0)
                {
                    var redirects = new JsonArray();
                    for (var redirectIndex = 0;
                         redirectIndex < command.Redirects.Count;
                         redirectIndex++)
                    {
                        redirects.Add(BuildRedirectAnalysis(
                            command.Redirects[redirectIndex],
                            redirectIndex));
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
        if (domain is ShellValueDomain.Exact exact)
        {
            values.Add(exact.Value);
        }
        else if (domain is ShellValueDomain.FiniteSet finiteSet)
        {
            foreach (var value in finiteSet.Values)
            {
                values.Add(value);
            }
        }

        return new JsonObject
        {
            ["kind"] = domain switch
            {
                ShellValueDomain.Unknown => "Unknown",
                ShellValueDomain.Exact => "Exact",
                ShellValueDomain.FiniteSet => "FiniteSet",
                ShellValueDomain.PathPattern => "Pattern",
                _ => throw new InvalidOperationException(
                    $"Unknown value-domain type {domain.GetType().FullName}"),
            },
            ["values"] = values,
            ["pattern"] = (domain as ShellValueDomain.PathPattern)?.Pattern,
            ["coveringDirectory"] =
                (domain as ShellValueDomain.PathPattern)?.CoveringDirectory,
        };
    }

    private static JsonObject BuildWorkingDirectoryEffect(
        ShellWorkingDirectoryEffect effect) => effect switch
        {
            ShellWorkingDirectoryEffect.Unchanged => new JsonObject
            {
                ["kind"] = nameof(ShellWorkingDirectoryEffect.Unchanged),
            },
            ShellWorkingDirectoryEffect.ChangesOnSuccess changed => new JsonObject
            {
                ["kind"] = nameof(ShellWorkingDirectoryEffect.ChangesOnSuccess),
                ["target"] = BuildValueDomain(changed.Target),
            },
            _ => new JsonObject
            {
                ["kind"] = nameof(ShellWorkingDirectoryEffect.Unknown),
            },
        };

    private static JsonObject BuildRedirectAnalysis(
        RedirectAnalysis analysis,
        int redirectIndex)
    {
        var result = new JsonObject
        {
            ["redirectIndex"] = redirectIndex,
            ["kind"] = analysis.GetType().Name,
            ["sourceKind"] = analysis.Source.GetType().Name,
            ["sourceDescriptor"] = JsonValue.Create(
                (analysis.Source as RedirectSource.Descriptor)?.Value),
            ["isComplete"] = analysis.IsComplete,
        };

        switch (analysis)
        {
            case FileRedirectAnalysis file:
                result["fileMode"] = file.Mode.ToString();
                result["value"] = BuildValueDomain(file.Target);
                break;
            case DescriptorDuplicateRedirectAnalysis duplicate:
                result["targetDescriptor"] = duplicate.TargetDescriptor;
                break;
            case DescriptorMoveRedirectAnalysis move:
                result["targetDescriptor"] = move.TargetDescriptor;
                break;
            case HereStringRedirectAnalysis hereString:
                result["value"] = BuildValueDomain(hereString.Data);
                break;
            case HereDocumentRedirectAnalysis hereDocument:
                result["hereDocument"] = new JsonObject
                {
                    ["delimiter"] = BuildSourceFragment(
                        hereDocument.Document.Delimiter),
                    ["body"] = BuildSourceFragment(hereDocument.Document.Body),
                    ["expansionMode"] =
                        hereDocument.Document.ExpansionMode.ToString(),
                    ["stripLeadingTabs"] = hereDocument.Document.StripLeadingTabs,
                    ["isComplete"] = hereDocument.Document.IsComplete,
                };
                break;
            case UnresolvedRedirectAnalysis or DescriptorCloseRedirectAnalysis:
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown redirect-analysis type {analysis.GetType().FullName}");
        }

        return result;
    }

    private static JsonObject BuildSourceFragment(ShellSourceFragment fragment) => new()
    {
        ["raw"] = fragment.Raw,
        ["sourceStart"] = JsonValue.Create(fragment.SourceStart),
        ["sourceLength"] = JsonValue.Create(fragment.SourceLength),
    };

    private static string SyntaxKind(ShellSyntaxNode node) => node switch
    {
        ShellBlockSyntax => "Block",
        SimpleCommandSyntax => "SimpleCommand",
        PipelineSyntax => "Pipeline",
        CommandListSyntax => "CommandList",
        GroupSyntax => "Group",
        ForEachSyntax => "ForEach",
        CommandSubstitutionSyntax => "CommandSubstitution",
        ExecutionRegionSyntax => "ExecutionRegion",
        _ => throw new InvalidOperationException(
            $"Unknown syntax-node type {node.GetType().FullName}"),
    };

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

    private static int FindClauseArgumentIndex(Clause clause, Arg argument)
    {
        for (var index = 0; index < clause.Args.Count; index++)
        {
            if (ReferenceEquals(clause.Args[index], argument))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            "Analyzed argument was not owned by its command clause");
    }

    private static int? FindClauseElementIndex(
        ParsedCommand parsed,
        ClauseElement? element)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var clause in parsed.Clauses)
        {
            for (var index = 0; index < clause.Elements.Count; index++)
            {
                if (ReferenceEquals(clause.Elements[index], element))
                {
                    return index;
                }
            }
        }

        throw new InvalidOperationException(
            "Execution-region host argument was not owned by a parsed clause");
    }

    private static int FindClauseElementIndex(Clause clause, ClauseElement element)
    {
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            if (ReferenceEquals(clause.Elements[index], element))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            "Analyzed element was not owned by its command clause");
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
