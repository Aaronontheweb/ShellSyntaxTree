// -----------------------------------------------------------------------
// <copyright file="PwshStructuralCoordinator.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Pwsh.Lexing;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

internal static partial class PwshCommandParser
{
    private static ParsedCommand ParseStructured(
        string source,
        IReadOnlyList<PwshToken> tokens,
        PwshParserOptions options,
        int recursionDepth,
        bool markWrapped,
        PwshSetLocationContext? sharedLocation)
    {
        var coordinator = new StructuralCoordinator(
            source,
            tokens,
            options,
            recursionDepth,
            markWrapped,
            sharedLocation);
        if (!coordinator.TryParse(out var syntax, out var error))
        {
            return StructuralFailure(source, error, syntax);
        }

        if (!ShellSyntaxProjection.TryProject(
                syntax,
                simple => new CommandOccurrenceFacts
                {
                    IsComplete = IsStructurallyComplete(simple.Clause),
                },
                out var projection))
        {
            return StructuralFailure(
                source,
                "PowerShell structural syntax exceeded limits or contained invalid parser-owned facts",
                syntax);
        }

        return new ParsedCommand
        {
            Source = source,
            Syntax = syntax,
            Commands = projection.Commands,
            Clauses = projection.Clauses,
        };
    }

    private static ParsedCommand StructuralFailure(
        string source,
        string? reason,
        ShellBlockSyntax? syntax = null) => new()
        {
            Source = source,
            Syntax = syntax ?? new ShellBlockSyntax(),
            Commands = Array.Empty<CommandOccurrence>(),
            Clauses = Array.Empty<Clause>(),
            IsUnparseable = true,
            UnparseableReason = reason,
        };

    private sealed class StructuralCoordinator
    {
        private readonly string _source;
        private readonly IReadOnlyList<PwshToken> _tokens;
        private readonly PwshParserOptions _options;
        private readonly int _recursionDepth;
        private readonly bool _markWrapped;
        private readonly PwshSetLocationContext _attribution;
        private int _position;
        private int _groupDepth;

        internal StructuralCoordinator(
            string source,
            IReadOnlyList<PwshToken> tokens,
            PwshParserOptions options,
            int recursionDepth,
            bool markWrapped,
            PwshSetLocationContext? sharedLocation)
        {
            _source = source;
            _tokens = tokens;
            _options = options;
            _recursionDepth = recursionDepth;
            _markWrapped = markWrapped;
            _attribution = sharedLocation ?? new PwshSetLocationContext();
        }

        internal bool TryParse(out ShellBlockSyntax syntax, out string? error)
        {
            SkipNewlines();
            if (_position == _tokens.Count)
            {
                syntax = new ShellBlockSyntax
                {
                    SourceStart = 0,
                    SourceLength = _source.Length,
                };
                error = null;
                return true;
            }

            if (!TryParseList(CompoundOperator.None, out var command, out error) ||
                command is null)
            {
                syntax = new ShellBlockSyntax();
                return false;
            }

            SkipNewlines();
            if (_position != _tokens.Count)
            {
                syntax = new ShellBlockSyntax();
                error = IsOperator(")")
                    ? $"unbalanced ')' at position {_tokens[_position].SourceStart}"
                    : $"unexpected token at position {_tokens[_position].SourceStart}";
                return false;
            }

            syntax = new ShellBlockSyntax
            {
                Statements = new[] { command },
                SourceStart = 0,
                SourceLength = _source.Length,
            };
            return true;
        }

        private bool TryParseList(
            CompoundOperator firstCompatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            error = null;
            SkipNewlines();
            if (_position == _tokens.Count)
            {
                return true;
            }

            if (!TryParsePipeline(firstCompatibilityOperator, out var first, out error))
            {
                return false;
            }

            var items = new List<CommandListItemSyntax>
            {
                new() { Operator = CompoundOperator.None, Command = first! },
            };

            while (_position < _tokens.Count)
            {
                if (IsOperator(")"))
                {
                    error = $"unbalanced ')' at position {_tokens[_position].SourceStart}";
                    return false;
                }

                if (!TryReadListOperator(out var listOperator))
                {
                    error = $"unexpected token at position {_tokens[_position].SourceStart}";
                    return false;
                }

                SkipNewlines();
                if (_position == _tokens.Count)
                {
                    if (listOperator == CompoundOperator.Sequence)
                    {
                        break;
                    }

                    error = "compound operator is missing a following command";
                    return false;
                }

                if (!TryParsePipeline(listOperator, out var next, out error))
                {
                    return false;
                }

                items.Add(new CommandListItemSyntax
                {
                    Operator = listOperator,
                    Command = next!,
                });
            }

            if (items.Count == 1)
            {
                command = first;
                return true;
            }

            var children = new List<ShellSyntaxNode>(items.Count);
            foreach (var item in items)
            {
                children.Add(item.Command);
            }

            command = new CommandListSyntax
            {
                Items = items,
                SourceStart = CombinedStart(children),
                SourceLength = CombinedLength(children),
            };
            return true;
        }

        private bool TryParsePipeline(
            CompoundOperator firstCompatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            if (!TryParseCommand(firstCompatibilityOperator, out var first, out error))
            {
                return false;
            }

            var stages = new List<ShellSyntaxNode> { first! };
            while (IsOperator("|"))
            {
                _position++;
                SkipNewlines();
                if (_position == _tokens.Count || IsOperator(")"))
                {
                    error = "pipeline operator is missing a following command";
                    return false;
                }

                if (IsOperator("("))
                {
                    error = "a grouped expression is only supported as the first pipeline element";
                    return false;
                }

                if (!TryParseCommand(CompoundOperator.Pipe, out var stage, out error))
                {
                    return false;
                }

                stages.Add(stage!);
            }

            if (stages.Count == 1)
            {
                command = first;
                return true;
            }

            command = new PipelineSyntax
            {
                Stages = stages,
                SourceStart = CombinedStart(stages),
                SourceLength = CombinedLength(stages),
            };
            error = null;
            return true;
        }

        private bool TryParseCommand(
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            error = null;
            if (_position == _tokens.Count)
            {
                error = "expected a command";
                return false;
            }

            if (IsOperator("("))
            {
                return TryParseGroup(compatibilityOperator, out command, out error);
            }

            if (IsOperator(")") || IsListOperator(_tokens[_position]) || IsOperator("|"))
            {
                error = $"unexpected operator at position {_tokens[_position].SourceStart}";
                return false;
            }

            var start = _position;
            var expressionDepth = 0;
            while (_position < _tokens.Count)
            {
                var token = _tokens[_position];
                if (expressionDepth > 0)
                {
                    _position++;
                    if (token.Kind == PwshTokenKind.Operator)
                    {
                        if (token.OperatorText == "(")
                        {
                            expressionDepth++;
                        }
                        else if (token.OperatorText == ")")
                        {
                            expressionDepth--;
                        }
                    }

                    continue;
                }

                if (token.Kind == PwshTokenKind.Operator && token.OperatorText == "(" &&
                    StartsWithInvokeExpression(CopyTokens(start, _position)))
                {
                    expressionDepth = 1;
                    _position++;
                    continue;
                }

                if (IsStructuralBoundary(token))
                {
                    break;
                }

                _position++;
            }

            if (expressionDepth != 0)
            {
                error = $"unbalanced '(' grouping at position {_source.Length}";
                return false;
            }

            var segmentTokens = CopyTokens(start, _position);
            if (segmentTokens.Count == 0)
            {
                error = $"expected a command at position {_tokens[_position].SourceStart}";
                return false;
            }

            var effectiveOptions = _options;
            var workingDirectoryUnknown = false;
            if (_attribution.HasAttribution && !_attribution.IsDynamic)
            {
                effectiveOptions = new PwshParserOptions
                {
                    HomeDirectory = _options.HomeDirectory,
                    WorkingDirectory = _attribution.ResolvedCwd,
                };
            }
            else if (_attribution.IsDynamic)
            {
                workingDirectoryUnknown = true;
            }

            var segment = new Segment
            {
                PrecedingOperator = compatibilityOperator,
                Depth = _groupDepth,
            };
            segment.Tokens.AddRange(segmentTokens);
            var built = BuildSegment(
                segment,
                _source,
                _options,
                effectiveOptions,
                workingDirectoryUnknown,
                _recursionDepth,
                _markWrapped,
                _attribution);
            if (built.Error is not null)
            {
                error = built.Error;
                return false;
            }

            if (built.Syntax is not null)
            {
                command = built.Syntax;
                return true;
            }

            if (built.Clauses.Count != 1)
            {
                error = "simple-command parser did not produce exactly one compatibility leaf";
                return false;
            }

            var clause = built.Clauses[0];
            if (!built.IsRecursion)
            {
                clause = AttachAttributionArg(clause, _attribution);
                UpdateAttribution(built.Clauses[0], _options, _attribution);
            }

            var first = segmentTokens[0];
            var last = segmentTokens[segmentTokens.Count - 1];
            command = new SimpleCommandSyntax
            {
                Clause = clause,
                SourceStart = first.SourceStart,
                SourceLength = last.SourceStart + last.SourceLength - first.SourceStart,
            };
            return true;
        }

        private bool TryParseGroup(
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            if (_groupDepth >= ShellAnalysisLimits.MaxStructuralNesting)
            {
                command = null;
                error = "PowerShell structural nesting depth exceeded (>16)";
                return false;
            }

            var open = _tokens[_position++];
            _groupDepth++;
            SkipNewlines();
            var parsed = TryParsePipeline(
                compatibilityOperator,
                out var bodyCommand,
                out error);
            _groupDepth--;
            if (!parsed)
            {
                command = null;
                return false;
            }

            SkipNewlines();
            if (_position == _tokens.Count || !IsOperator(")"))
            {
                command = null;
                error = $"unbalanced '(' grouping at position {_source.Length}";
                return false;
            }

            var close = _tokens[_position++];
            command = new GroupSyntax
            {
                GroupKind = ShellGroupKind.CurrentScope,
                Body = new ShellBlockSyntax
                {
                    Statements = bodyCommand is null
                        ? Array.Empty<ShellSyntaxNode>()
                        : new[] { bodyCommand },
                    SourceStart = open.SourceStart + open.SourceLength,
                    SourceLength = close.SourceStart - open.SourceStart - open.SourceLength,
                },
                SourceStart = open.SourceStart,
                SourceLength = close.SourceStart + close.SourceLength - open.SourceStart,
            };
            return true;
        }

        private List<PwshToken> CopyTokens(int start, int end)
        {
            var copied = new List<PwshToken>(end - start);
            for (var index = start; index < end; index++)
            {
                copied.Add(_tokens[index]);
            }

            return copied;
        }

        private bool TryReadListOperator(out CompoundOperator @operator)
        {
            @operator = CompoundOperator.None;
            if (_position == _tokens.Count)
            {
                return false;
            }

            var token = _tokens[_position];
            if (token.Kind == PwshTokenKind.Whitespace)
            {
                @operator = CompoundOperator.Sequence;
                _position++;
                return true;
            }

            if (token.Kind != PwshTokenKind.Operator)
            {
                return false;
            }

            @operator = token.OperatorText switch
            {
                "&&" => CompoundOperator.AndIf,
                "||" => CompoundOperator.OrIf,
                ";" => CompoundOperator.Sequence,
                _ => CompoundOperator.None,
            };
            if (@operator == CompoundOperator.None)
            {
                return false;
            }

            _position++;
            return true;
        }

        private void SkipNewlines()
        {
            while (_position < _tokens.Count &&
                   _tokens[_position].Kind == PwshTokenKind.Whitespace)
            {
                _position++;
            }
        }

        private bool IsOperator(string value) =>
            _position < _tokens.Count &&
            _tokens[_position].Kind == PwshTokenKind.Operator &&
            string.Equals(_tokens[_position].OperatorText, value, StringComparison.Ordinal);
    }

    private static bool IsStructuralBoundary(PwshToken token) =>
        token.Kind == PwshTokenKind.Whitespace ||
        token.Kind == PwshTokenKind.Operator && token.OperatorText is
            "(" or ")" or "&&" or "||" or ";" or "|";

    private static bool IsListOperator(PwshToken token) =>
        token.Kind == PwshTokenKind.Whitespace ||
        token.Kind == PwshTokenKind.Operator && token.OperatorText is "&&" or "||" or ";";

    private sealed class DecodedCloneState
    {
        internal CompoundOperator FirstOperator { get; init; }

        internal bool OuterSubshell { get; init; }

        internal int LeafCount { get; init; }

        internal IReadOnlyList<Redirect> WrapperRedirects { get; init; } =
            Array.Empty<Redirect>();

        internal IReadOnlyList<ClauseElement> WrapperRedirectElements { get; init; } =
            Array.Empty<ClauseElement>();

        internal int LeafIndex { get; set; }
    }

    private static bool TryBuildDecodedWrapper(
        ParsedCommand inner,
        Segment outer,
        ShellGroupKind groupKind,
        IReadOnlyList<Redirect> wrapperRedirects,
        IReadOnlyList<ClauseElement> wrapperRedirectElements,
        out GroupSyntax? wrapper)
    {
        var state = new DecodedCloneState
        {
            FirstOperator = outer.PrecedingOperator,
            OuterSubshell = outer.Depth > 0,
            LeafCount = inner.Commands.Count,
            WrapperRedirects = wrapperRedirects,
            WrapperRedirectElements = wrapperRedirectElements,
        };
        if (!TryCloneDecodedBlock(inner.Syntax, state, out var body))
        {
            wrapper = null;
            return false;
        }

        if (state.LeafCount == 0 && wrapperRedirects.Count > 0)
        {
            var redirectElements = new List<ClauseElement>(wrapperRedirectElements.Count);
            foreach (var element in wrapperRedirectElements)
            {
                redirectElements.Add(element with { PrecedingVerbElementCount = 0 });
            }

            body = new ShellBlockSyntax
            {
                Statements = new ShellSyntaxNode[]
                {
                    new SimpleCommandSyntax
                    {
                        Clause = new Clause
                        {
                            Operator = outer.PrecedingOperator,
                            Verb = new VerbChain(),
                            Args = Array.Empty<Arg>(),
                            Redirects = wrapperRedirects,
                            Elements = redirectElements,
                            IsSubshell = outer.Depth > 0,
                            IsCommandStringWrapped = true,
                        },
                    },
                },
            };
        }

        if (outer.Tokens.Count == 0)
        {
            wrapper = null;
            return false;
        }

        var first = outer.Tokens[0];
        var last = outer.Tokens[outer.Tokens.Count - 1];
        wrapper = new GroupSyntax
        {
            GroupKind = groupKind,
            Body = body,
            SourceStart = first.SourceStart,
            SourceLength = last.SourceStart + last.SourceLength - first.SourceStart,
        };
        return true;
    }

    private static bool TryCloneDecodedBlock(
        ShellBlockSyntax source,
        DecodedCloneState state,
        out ShellBlockSyntax clone)
    {
        if (source is null || source.Statements is null)
        {
            clone = new ShellBlockSyntax();
            return false;
        }

        var statements = new List<ShellSyntaxNode>(source.Statements.Count);
        foreach (var statement in source.Statements)
        {
            if (!TryCloneDecodedNode(statement, state, out var cloned))
            {
                clone = new ShellBlockSyntax();
                return false;
            }

            statements.Add(cloned!);
        }

        clone = new ShellBlockSyntax { Statements = statements };
        return true;
    }

    private static bool TryCloneDecodedNode(
        ShellSyntaxNode source,
        DecodedCloneState state,
        out ShellSyntaxNode? clone)
    {
        clone = null;
        switch (source)
        {
            case ShellBlockSyntax block:
                if (!TryCloneDecodedBlock(block, state, out var clonedBlock))
                {
                    return false;
                }

                clone = clonedBlock;
                return true;
            case SimpleCommandSyntax simple:
                var substitutions = new List<CommandSubstitutionSyntax>(simple.Substitutions.Count);
                foreach (var substitution in simple.Substitutions)
                {
                    if (!TryCloneDecodedNode(substitution, state, out var clonedSubstitution) ||
                        clonedSubstitution is not CommandSubstitutionSyntax typedSubstitution)
                    {
                        return false;
                    }

                    substitutions.Add(typedSubstitution);
                }

                var isFirst = state.LeafIndex == 0;
                var isLast = state.LeafIndex == state.LeafCount - 1;
                state.LeafIndex++;
                var redirects = new List<Redirect>(simple.Clause.Redirects.Count +
                    (isLast ? state.WrapperRedirects.Count : 0));
                redirects.AddRange(simple.Clause.Redirects);
                if (isLast)
                {
                    redirects.AddRange(state.WrapperRedirects);
                }

                var elements = new List<ClauseElement>(simple.Clause.Elements.Count +
                    (isLast ? state.WrapperRedirectElements.Count : 0));
                elements.AddRange(ClauseElementProvenance.WithoutOuterSourceSpans(
                    simple.Clause.Elements));
                if (isLast)
                {
                    foreach (var redirectElement in state.WrapperRedirectElements)
                    {
                        elements.Add(redirectElement with
                        {
                            PrecedingVerbElementCount = simple.Clause.Verb.Tokens.Count,
                        });
                    }
                }

                clone = new SimpleCommandSyntax
                {
                    Clause = simple.Clause with
                    {
                        Operator = isFirst ? state.FirstOperator : simple.Clause.Operator,
                        Redirects = redirects,
                        Elements = elements,
                        IsSubshell = state.OuterSubshell || simple.Clause.IsSubshell,
                        IsCommandStringWrapped = true,
                    },
                    Substitutions = substitutions,
                };
                return true;
            case PipelineSyntax pipeline:
                return TryCloneDecodedCollection(
                    pipeline.Stages,
                    state,
                    stages => new PipelineSyntax { Stages = stages },
                    out clone);
            case CommandListSyntax list:
                var items = new List<CommandListItemSyntax>(list.Items.Count);
                foreach (var item in list.Items)
                {
                    if (item is null ||
                        !TryCloneDecodedNode(item.Command, state, out var itemCommand))
                    {
                        return false;
                    }

                    items.Add(new CommandListItemSyntax
                    {
                        Operator = item.Operator,
                        Command = itemCommand!,
                    });
                }

                clone = new CommandListSyntax { Items = items };
                return true;
            case GroupSyntax group:
                if (!TryCloneDecodedBlock(group.Body, state, out var groupBody))
                {
                    return false;
                }

                clone = new GroupSyntax { GroupKind = group.GroupKind, Body = groupBody };
                return true;
            case ForEachSyntax forEach:
                if (!TryCloneDecodedBlock(forEach.IteratorCommands, state, out var iterator) ||
                    !TryCloneDecodedBlock(forEach.Body, state, out var forBody))
                {
                    return false;
                }

                clone = new ForEachSyntax
                {
                    Binding = new LoopBindingSyntax
                    {
                        Name = forEach.Binding.Name,
                        Source = ClearSpan(forEach.Binding.Source),
                    },
                    Iterable = ClearSpan(forEach.Iterable),
                    IteratorCommands = iterator,
                    Body = forBody,
                };
                return true;
            case ConditionLoopSyntax loop:
                if (!TryCloneDecodedBlock(loop.Condition, state, out var condition) ||
                    !TryCloneDecodedBlock(loop.Body, state, out var loopBody))
                {
                    return false;
                }

                clone = new ConditionLoopSyntax
                {
                    LoopKind = loop.LoopKind,
                    Condition = condition,
                    Body = loopBody,
                };
                return true;
            case ConditionalSyntax conditional:
                var branches = new List<ConditionalBranchSyntax>(conditional.Branches.Count);
                foreach (var branch in conditional.Branches)
                {
                    if (!TryCloneDecodedNode(branch, state, out var clonedBranch) ||
                        clonedBranch is not ConditionalBranchSyntax typedBranch)
                    {
                        return false;
                    }

                    branches.Add(typedBranch);
                }

                ShellBlockSyntax? @else = null;
                if (conditional.Else is not null &&
                    !TryCloneDecodedBlock(conditional.Else, state, out @else))
                {
                    return false;
                }

                clone = new ConditionalSyntax { Branches = branches, Else = @else };
                return true;
            case ConditionalBranchSyntax branch:
                if (!TryCloneDecodedBlock(branch.Condition, state, out var branchCondition) ||
                    !TryCloneDecodedBlock(branch.Body, state, out var branchBody))
                {
                    return false;
                }

                clone = new ConditionalBranchSyntax
                {
                    Condition = branchCondition,
                    Body = branchBody,
                };
                return true;
            case CommandSubstitutionSyntax substitution:
                if (!TryCloneDecodedBlock(substitution.Body, state, out var substitutionBody))
                {
                    return false;
                }

                clone = new CommandSubstitutionSyntax { Body = substitutionBody };
                return true;
            default:
                return false;
        }
    }

    private static bool TryCloneDecodedCollection(
        IReadOnlyList<ShellSyntaxNode> source,
        DecodedCloneState state,
        Func<IReadOnlyList<ShellSyntaxNode>, ShellSyntaxNode> factory,
        out ShellSyntaxNode? clone)
    {
        var children = new List<ShellSyntaxNode>(source.Count);
        foreach (var child in source)
        {
            if (!TryCloneDecodedNode(child, state, out var clonedChild))
            {
                clone = null;
                return false;
            }

            children.Add(clonedChild!);
        }

        clone = factory(children);
        return true;
    }

    private static ShellSourceFragment ClearSpan(ShellSourceFragment source) => new()
    {
        Raw = source.Raw,
    };

    private static bool IsStructurallyComplete(Clause clause)
    {
        if (clause.Redirects.Count > 0 ||
            clause.Verb.IsDynamic ||
            clause.Verb.Tokens.Count == 0)
        {
            return false;
        }

        if (HasUnexpandedPowerShellCommandString(clause))
        {
            return false;
        }

        foreach (var element in clause.Elements)
        {
            if (element.Kind == ArgKind.DynamicSkip &&
                (element.Raw.IndexOf("$(", StringComparison.Ordinal) >= 0 ||
                 element.Raw.IndexOf("@(", StringComparison.Ordinal) >= 0 ||
                 element.Raw.IndexOf("@{", StringComparison.Ordinal) >= 0))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUnexpandedPowerShellCommandString(Clause clause)
    {
        var verb = clause.Verb.CanonicalVerb ??
            (clause.Verb.Tokens.Count > 0 ? clause.Verb.Tokens[0] : null);
        if (verb is null)
        {
            return false;
        }

        if (IsInvokeExpressionName(verb))
        {
            return true;
        }

        if (!IsPowerShellHostName(verb))
        {
            return false;
        }

        foreach (var element in clause.Elements)
        {
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (element.Kind != ArgKind.Literal ||
                LooksLikePowerShellCommandStringOption(element.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPowerShellHostName(string verb) =>
        string.Equals(verb, "pwsh", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "powershell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "powershell.exe", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePowerShellCommandStringOption(string value)
    {
        if (value.Length < 2 || value[0] != '-')
        {
            return false;
        }

        var name = value;
        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            name = name.Substring(0, colon);
        }

        return IsCommandParameter(name) ||
            IsEncodedCommandParameter(name) ||
            string.Equals(name, "-cwa", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("-commandw", StringComparison.OrdinalIgnoreCase);
    }

    private static int? CombinedStart(IReadOnlyList<ShellSyntaxNode> nodes)
    {
        if (nodes.Count == 0 || nodes[0].SourceStart is null)
        {
            return null;
        }

        for (var index = 1; index < nodes.Count; index++)
        {
            if (nodes[index].SourceStart is null)
            {
                return null;
            }
        }

        return nodes[0].SourceStart;
    }

    private static int? CombinedLength(IReadOnlyList<ShellSyntaxNode> nodes)
    {
        var start = CombinedStart(nodes);
        var last = nodes.Count == 0 ? null : nodes[nodes.Count - 1];
        if (start is null || last?.SourceStart is null || last.SourceLength is null)
        {
            return null;
        }

        return last.SourceStart.Value + last.SourceLength.Value - start.Value;
    }
}
