// -----------------------------------------------------------------------
// <copyright file="BashFiniteScopeProjection.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShellSyntaxTree;

/// <summary>
/// A bounded proof of every directory that can reach a simple Bash command.
/// The proof supplies syntax facts and does not grant execution authority.
/// </summary>
public sealed record BashFiniteScopeProjection
{
    private IReadOnlyList<BashScopedCommand> _commands = Array.Empty<BashScopedCommand>();

    internal BashFiniteScopeProjection()
    {
    }

    /// <summary>Gets the full authored parse that owns each source occurrence.</summary>
    public ParsedCommand Parsed { get; internal init; } = null!;

    /// <summary>
    /// Gets one command for each reachable exact directory, in list-item,
    /// ordinal directory, then pipeline-stage order.
    /// </summary>
    public IReadOnlyList<BashScopedCommand> Commands
    {
        get => _commands;
        internal init => _commands = Array.AsReadOnly(value.ToArray());
    }
}

/// <summary>One authored command parsed again under one reachable directory.</summary>
public sealed record BashScopedCommand
{
    internal BashScopedCommand()
    {
    }

    /// <summary>Gets the occurrence in the full authored command.</summary>
    public CommandOccurrence SourceOccurrence { get; internal init; } = null!;

    /// <summary>Gets the occurrence with exact directory and path facts.</summary>
    public CommandOccurrence ScopedOccurrence { get; internal init; } = null!;

    /// <summary>Gets the exact authored slice that produced the scoped facts.</summary>
    public string Source { get; internal init; } = "";

    /// <summary>Gets the slice offset in the full authored source.</summary>
    public int SourceStart { get; internal init; }

    /// <summary>Gets the exact directory where this occurrence can execute.</summary>
    public string WorkingDirectory { get; internal init; } = "";
}

internal static class BashFiniteScopeAnalyzer
{
    private const int MaximumDirectories = 32;
    private const int MaximumCommands = 128;

    internal static bool TryProject(
        ParsedCommand parsed,
        BashParserOptions options,
        out BashFiniteScopeProjection? projection)
    {
        projection = null;
        if (parsed.IsUnparseable || parsed.Commands.Count < 2 ||
            parsed.Syntax.Statements.Count != 1 ||
            parsed.Syntax.Statements[0] is not CommandListSyntax list ||
            list.Items.Count < 2 ||
            parsed.Commands[0].WorkingDirectory is not ShellValueDomain.Exact initial ||
            !IsPosixAbsolute(initial.Value))
        {
            return false;
        }

        foreach (var occurrence in parsed.Commands)
        {
            foreach (var assignment in occurrence.Assignments)
            {
                if (assignment.Scope == ShellVariableAssignmentScope.ShellState)
                {
                    return false;
                }
            }
        }

        var scoped = new List<BashScopedCommand>();
        var visited = new List<Clause>();
        var success = new HashSet<string>(StringComparer.Ordinal);
        var failure = new HashSet<string>(StringComparer.Ordinal);

        for (var itemIndex = 0; itemIndex < list.Items.Count; itemIndex++)
        {
            var item = list.Items[itemIndex];
            if (!TryGetInputDirectories(item.Operator, itemIndex, initial.Value,
                    success, failure, out var input) ||
                input.Count == 0 || input.Count > MaximumDirectories ||
                !TryGetSimpleCommands(item.Command, out var commands) ||
                !ValidateOccurrences(parsed, commands, item.Command, list, itemIndex, visited))
            {
                return false;
            }

            var itemSuccess = new HashSet<string>(StringComparer.Ordinal);
            var itemFailure = new HashSet<string>(StringComparer.Ordinal);
            foreach (var directory in input.OrderBy(value => value, StringComparer.Ordinal))
            {
                ShellWorkingDirectoryEffect? effect = null;
                foreach (var simple in commands)
                {
                    if (scoped.Count >= MaximumCommands ||
                        !TryParseSlice(parsed, options, simple, directory, out var slice))
                    {
                        return false;
                    }

                    scoped.Add(slice);
                    effect = slice.ScopedOccurrence.WorkingDirectoryEffect;
                    if (item.Command is PipelineSyntax &&
                        effect is not ShellWorkingDirectoryEffect.Unchanged)
                    {
                        return false;
                    }
                }

                if (item.Command is PipelineSyntax ||
                    effect is ShellWorkingDirectoryEffect.Unchanged)
                {
                    itemSuccess.Add(directory);
                    itemFailure.Add(directory);
                }
                else if (effect is ShellWorkingDirectoryEffect.ChangesOnSuccess
                         { Target: ShellValueDomain.Exact target } &&
                         IsPosixAbsolute(target.Value))
                {
                    itemSuccess.Add(target.Value);
                    itemFailure.Add(directory);
                }
                else
                {
                    return false;
                }
            }

            var nextSuccess = item.Operator == CompoundOperator.OrIf
                ? Union(success, itemSuccess)
                : itemSuccess;
            var nextFailure = item.Operator == CompoundOperator.AndIf
                ? Union(failure, itemFailure)
                : itemFailure;
            if (Union(nextSuccess, nextFailure).Count > MaximumDirectories)
            {
                return false;
            }

            success = nextSuccess;
            failure = nextFailure;
        }

        if (visited.Count != parsed.Commands.Count || scoped.Count == 0)
        {
            return false;
        }

        projection = new BashFiniteScopeProjection
        {
            Parsed = parsed,
            Commands = scoped,
        };
        return true;
    }

    private static bool TryGetInputDirectories(
        CompoundOperator operation,
        int itemIndex,
        string initialDirectory,
        HashSet<string> success,
        HashSet<string> failure,
        out HashSet<string> input)
    {
        input = itemIndex == 0
            ? new HashSet<string>(new[] { initialDirectory }, StringComparer.Ordinal)
            : operation switch
            {
                CompoundOperator.Sequence => Union(success, failure),
                CompoundOperator.AndIf => new HashSet<string>(success, StringComparer.Ordinal),
                CompoundOperator.OrIf => new HashSet<string>(failure, StringComparer.Ordinal),
                _ => new HashSet<string>(StringComparer.Ordinal),
            };
        return itemIndex == 0
            ? operation == CompoundOperator.None
            : operation is CompoundOperator.Sequence or CompoundOperator.AndIf or
                CompoundOperator.OrIf;
    }

    private static HashSet<string> Union(IReadOnlyCollection<string> first,
        IReadOnlyCollection<string> second)
    {
        var result = new HashSet<string>(first, StringComparer.Ordinal);
        result.UnionWith(second);
        return result;
    }

    private static bool TryGetSimpleCommands(ShellSyntaxNode node,
        out IReadOnlyList<SimpleCommandSyntax> commands)
    {
        commands = node switch
        {
            SimpleCommandSyntax simple => new[] { simple },
            PipelineSyntax pipeline when pipeline.Stages.Count >= 2 &&
                pipeline.Stages.All(stage => stage is SimpleCommandSyntax) =>
                pipeline.Stages.Cast<SimpleCommandSyntax>().ToArray(),
            _ => Array.Empty<SimpleCommandSyntax>(),
        };
        return commands.Count > 0 && commands.All(simple =>
            simple.Substitutions.Count == 0 &&
            simple.ExecutionRegions.Count == 0 &&
            simple.SourceStart >= 0 && simple.SourceLength > 0);
    }

    private static bool ValidateOccurrences(
        ParsedCommand parsed,
        IReadOnlyList<SimpleCommandSyntax> commands,
        ShellSyntaxNode item,
        CommandListSyntax list,
        int itemIndex,
        List<Clause> visited)
    {
        for (var stageIndex = 0; stageIndex < commands.Count; stageIndex++)
        {
            var simple = commands[stageIndex];
            var occurrence = parsed.Commands.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Clause, simple.Clause));
            if (occurrence is null ||
                visited.Any(clause => ReferenceEquals(clause, simple.Clause)) ||
                !occurrence.IsComplete ||
                occurrence.Ancestry.Count != (item is PipelineSyntax ? 3 : 2) ||
                occurrence.Ancestry[0] is not
                    { Ancestor: ShellBlockSyntax, Region: CommandAncestryRegion.Root, ChildIndex: 0 } ||
                occurrence.Ancestry[1] is not
                    { Ancestor: var ancestor, Region: CommandAncestryRegion.Statement,
                        ChildIndex: var childIndex } ||
                !ReferenceEquals(ancestor, list) || childIndex != itemIndex)
            {
                return false;
            }

            if (item is PipelineSyntax pipeline)
            {
                if (occurrence.ImmediateRole != CommandOccurrenceRole.PipelineStage ||
                    occurrence.Ancestry[2] is not
                        { Ancestor: var stageParent, Region: CommandAncestryRegion.PipelineStage,
                            ChildIndex: var childStage } ||
                    !ReferenceEquals(stageParent, pipeline) || childStage != stageIndex)
                {
                    return false;
                }
            }
            else if (occurrence.ImmediateRole != CommandOccurrenceRole.Ordinary)
            {
                return false;
            }

            visited.Add(simple.Clause);
        }

        return true;
    }

    private static bool TryParseSlice(
        ParsedCommand parsed,
        BashParserOptions options,
        SimpleCommandSyntax simple,
        string directory,
        out BashScopedCommand scoped)
    {
        scoped = null!;
        var start = simple.SourceStart!.Value;
        var length = simple.SourceLength!.Value;
        if (start > parsed.Source.Length || length > parsed.Source.Length - start)
        {
            return false;
        }

        var source = parsed.Source.Substring(start, length);
        var slice = new BashParser(options with { WorkingDirectory = directory }).Parse(source);
        if (slice.IsUnparseable || slice.Commands.Count != 1 ||
            !slice.Commands[0].IsComplete ||
            slice.Commands[0].WorkingDirectory is not ShellValueDomain.Exact exact ||
            !string.Equals(exact.Value, directory, StringComparison.Ordinal) ||
            !HasSameAuthoredElements(simple.Clause, slice.Commands[0].Clause))
        {
            return false;
        }

        scoped = new BashScopedCommand
        {
            SourceOccurrence = parsed.Commands.First(occurrence =>
                ReferenceEquals(occurrence.Clause, simple.Clause)),
            ScopedOccurrence = slice.Commands[0],
            Source = source,
            SourceStart = start,
            WorkingDirectory = directory,
        };
        return true;
    }

    private static bool HasSameAuthoredElements(Clause first, Clause second) =>
        first.Elements.Count == second.Elements.Count &&
        first.Elements.Zip(second.Elements, (left, right) =>
            left.Role == right.Role &&
            string.Equals(left.Raw, right.Raw, StringComparison.Ordinal)).All(equal => equal);

    private static bool IsPosixAbsolute(string path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/';
}
