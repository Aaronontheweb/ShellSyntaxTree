// -----------------------------------------------------------------------
// <copyright file="BashStructuralCoordinator.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Bash.Lexing;
using ShellSyntaxTree.Internal.Parsing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

internal static partial class BashCommandParser
{
    private static ParsedCommand ParseStructured(
        string source,
        IReadOnlyList<BashToken> tokens,
        BashParserOptions options,
        int bashCDepth,
        bool markBashCWrapped)
    {
        var coordinator = new StructuralCoordinator(
            source,
            tokens,
            options,
            bashCDepth,
            markBashCWrapped);
        if (!coordinator.TryParse(out var syntax, out var error))
        {
            return StructuralFailure(source, error, syntax);
        }

        if (!ShellSyntaxProjection.TryProject(
                syntax,
                simple => new CommandOccurrenceFacts
                {
                    IsComplete = simple.Clause.Redirects.Count == 0 &&
                        !HasUnexpandedCommandString(simple.Clause) &&
                        !HasUndiscoveredExecutableRegion(simple.Clause),
                },
                out var projection))
        {
            return StructuralFailure(
                source,
                "Bash structural syntax exceeded limits or contained invalid parser-owned facts",
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
        private readonly IReadOnlyList<BashToken> _tokens;
        private readonly BashParserOptions _options;
        private readonly int _bashCDepth;
        private readonly bool _markBashCWrapped;
        private readonly CdAttributionContext _attribution = new();
        private int _position;
        private int _subshellDepth;

        internal StructuralCoordinator(
            string source,
            IReadOnlyList<BashToken> tokens,
            BashParserOptions options,
            int bashCDepth,
            bool markBashCWrapped)
        {
            _source = source;
            _tokens = tokens;
            _options = options;
            _bashCDepth = bashCDepth;
            _markBashCWrapped = markBashCWrapped;
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

            if (!TryParseList(
                    stopAtRightParen: false,
                    CompoundOperator.None,
                    out var command,
                    out error) ||
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
                    ? $"unbalanced parens at position {_tokens[_position].SourceStart}"
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
            bool stopAtRightParen,
            CompoundOperator firstCompatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            error = null;
            SkipNewlines();
            if (_position == _tokens.Count || stopAtRightParen && IsOperator(")"))
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
                if (stopAtRightParen && IsOperator(")"))
                {
                    break;
                }

                if (IsOperator(")"))
                {
                    error = $"unbalanced parens at position {_tokens[_position].SourceStart}";
                    return false;
                }

                if (!TryReadListOperator(out var listOperator))
                {
                    error = $"unexpected token at position {_tokens[_position].SourceStart}";
                    return false;
                }

                SkipNewlines();
                if (_position == _tokens.Count || stopAtRightParen && IsOperator(")"))
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
                return TryParseSubshell(compatibilityOperator, out command, out error);
            }

            if (IsOperator(")") || IsListOperator(_tokens[_position]) || IsOperator("|"))
            {
                error = $"unexpected operator at position {_tokens[_position].SourceStart}";
                return false;
            }

            var start = _position;
            while (_position < _tokens.Count && !IsStructuralBoundary(_tokens[_position]))
            {
                _position++;
            }

            var segmentTokens = new List<BashToken>(_position - start);
            for (var index = start; index < _position; index++)
            {
                segmentTokens.Add(_tokens[index]);
            }

            var segment = new Segment
            {
                PrecedingOperator = compatibilityOperator,
                Tokens = segmentTokens,
            };

            if (TryDetectBashCWrapper(segment, _source, out var innerCommand))
            {
                if (!IsExactStaticWrapper(segmentTokens))
                {
                    error = "bash -c wrapper has unsupported trailing arguments or redirects";
                    return false;
                }

                if (_bashCDepth + 1 > MaxBashCRecursionDepth)
                {
                    error = "bash -c recursion depth exceeded (>5)";
                    return false;
                }

                var inner = ParseInternal(
                    innerCommand!,
                    _options,
                    _bashCDepth + 1,
                    markBashCWrapped: true);
                if (inner.IsUnparseable)
                {
                    error = inner.UnparseableReason;
                    return false;
                }

                var firstLeaf = true;
                if (!TryCloneDecodedBlock(
                        inner.Syntax,
                        compatibilityOperator,
                        _subshellDepth > 0,
                        ref firstLeaf,
                        out var body))
                {
                    error = "decoded bash -c syntax could not be lifted safely";
                    return false;
                }

                var firstToken = segmentTokens[0];
                var lastToken = segmentTokens[segmentTokens.Count - 1];
                command = new GroupSyntax
                {
                    GroupKind = ShellGroupKind.IsolatedScope,
                    Body = body,
                    SourceStart = firstToken.SourceStart,
                    SourceLength = lastToken.SourceStart + lastToken.SourceLength -
                        firstToken.SourceStart,
                };
                return true;
            }

            var effectiveOptions = _options;
            var workingDirectoryUnknown = false;
            if (_attribution.HasAttribution && !_attribution.IsDynamic)
            {
                effectiveOptions = new BashParserOptions
                {
                    HomeDirectory = _options.HomeDirectory,
                    WorkingDirectory = _attribution.ResolvedCwd,
                };
            }
            else if (_attribution.IsDynamic)
            {
                workingDirectoryUnknown = true;
            }

            var parsed = ParseClauseSegment(
                segment,
                _source,
                effectiveOptions,
                workingDirectoryUnknown);
            if (parsed.Error is not null)
            {
                error = parsed.Error;
                return false;
            }

            if (parsed.Clauses.Count != 1)
            {
                error = "simple-command parser did not produce exactly one compatibility leaf";
                return false;
            }

            var clause = parsed.Clauses[0] with
            {
                IsSubshell = _subshellDepth > 0,
                IsCommandStringWrapped = _markBashCWrapped,
            };
            var emitted = AttachAttributionArg(clause, _attribution);
            var firstVerbToken = clause.Verb.Tokens.Count > 0 ? clause.Verb.Tokens[0] : null;
            if (firstVerbToken is not null &&
                (string.Equals(firstVerbToken, "cd", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(firstVerbToken, "chdir", StringComparison.OrdinalIgnoreCase)))
            {
                UpdateAttributionFromCd(clause, _attribution);
            }

            var firstSource = segmentTokens[0];
            var lastSource = segmentTokens[segmentTokens.Count - 1];
            command = new SimpleCommandSyntax
            {
                Clause = emitted,
                SourceStart = firstSource.SourceStart,
                SourceLength = lastSource.SourceStart + lastSource.SourceLength -
                    firstSource.SourceStart,
            };
            return true;
        }

        private bool TryParseSubshell(
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            if (_subshellDepth >= ShellAnalysisLimits.MaxStructuralNesting)
            {
                command = null;
                error = "Bash structural nesting depth exceeded (>16)";
                return false;
            }

            var open = _tokens[_position++];
            _attribution.PushForSubshell();
            _subshellDepth++;
            var parsed = TryParseList(
                stopAtRightParen: true,
                compatibilityOperator,
                out var bodyCommand,
                out error);
            _subshellDepth--;
            _attribution.PopForSubshell();

            if (!parsed)
            {
                command = null;
                return false;
            }

            if (_position == _tokens.Count || !IsOperator(")"))
            {
                command = null;
                error = $"unbalanced parens at position {_source.Length}";
                return false;
            }

            var close = _tokens[_position++];
            var body = new ShellBlockSyntax
            {
                Statements = bodyCommand is null
                    ? Array.Empty<ShellSyntaxNode>()
                    : new[] { bodyCommand },
                SourceStart = open.SourceStart + open.SourceLength,
                SourceLength = close.SourceStart - open.SourceStart - open.SourceLength,
            };
            command = new GroupSyntax
            {
                GroupKind = ShellGroupKind.IsolatedScope,
                Body = body,
                SourceStart = open.SourceStart,
                SourceLength = close.SourceStart + close.SourceLength - open.SourceStart,
            };
            return true;
        }

        private bool TryReadListOperator(out CompoundOperator @operator)
        {
            @operator = CompoundOperator.None;
            if (_position == _tokens.Count)
            {
                return false;
            }

            var token = _tokens[_position];
            if (token.Kind == BashTokenKind.Whitespace)
            {
                @operator = CompoundOperator.Sequence;
                _position++;
                return true;
            }

            if (token.Kind != BashTokenKind.Operator)
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
                   _tokens[_position].Kind == BashTokenKind.Whitespace)
            {
                _position++;
            }
        }

        private bool IsOperator(string value) =>
            _position < _tokens.Count &&
            _tokens[_position].Kind == BashTokenKind.Operator &&
            string.Equals(_tokens[_position].OperatorText, value, StringComparison.Ordinal);
    }

    private static bool IsStructuralBoundary(BashToken token) =>
        token.Kind == BashTokenKind.Whitespace ||
        token.Kind == BashTokenKind.Operator && token.OperatorText is
            "(" or ")" or "&&" or "||" or ";" or "|";

    private static bool IsListOperator(BashToken token) =>
        token.Kind == BashTokenKind.Whitespace ||
        token.Kind == BashTokenKind.Operator && token.OperatorText is "&&" or "||" or ";";

    private static bool IsExactStaticWrapper(IReadOnlyList<BashToken> tokens)
    {
        for (var index = 1; index + 1 < tokens.Count; index++)
        {
            if (tokens[index].Kind == BashTokenKind.Word &&
                string.Equals(tokens[index].Value, "-c", StringComparison.Ordinal))
            {
                if (index + 2 != tokens.Count ||
                    tokens[index + 1].Kind != BashTokenKind.QuotedString)
                {
                    return false;
                }

                for (var controlIndex = 0; controlIndex <= index + 1; controlIndex++)
                {
                    if (!HasExactLiteralValue(tokens[controlIndex]))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return false;
    }

    private static bool HasExactLiteralValue(BashToken token)
    {
        if (token.ResolverValue is null ||
            !string.Equals(token.ResolverValue.Decoded, token.Value, StringComparison.Ordinal))
        {
            return false;
        }

        var fragments = token.ResolverValue.Fragments;
        for (var index = 0; index < fragments.Count; index++)
        {
            var fragment = fragments[index];
            if (fragment.Kind != ShellValueFragmentKind.Literal ||
                fragment.AllowedTransforms != ShellLexicalTransform.None ||
                fragment.Expansion is not null ||
                fragment.Cardinality != ShellValueCardinality.ExactlyOne ||
                fragment.OpaqueCause != ShellOpaqueCause.None)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUnexpandedCommandString(Clause clause)
    {
        if (clause.Verb.Tokens.Count == 0 ||
            !string.Equals(clause.Verb.Tokens[0], "bash", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(clause.Verb.Tokens[0], "sh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var index = 0; index < clause.Elements.Count; index++)
        {
            var element = clause.Elements[index];
            if (element.Role != ClauseElementRole.Argument)
            {
                continue;
            }

            if (element.Kind != ArgKind.Literal)
            {
                return true;
            }

            if (string.Equals(element.Value, "--", StringComparison.Ordinal))
            {
                return false;
            }

            if (IsCommandStringOption(element.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCommandStringOption(string value)
    {
        if (string.Equals(value, "-c", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Length < 2 || value[0] != '-' || value[1] == '-')
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] == 'c')
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUndiscoveredExecutableRegion(Clause clause)
    {
        for (var index = 0; index < clause.Elements.Count; index++)
        {
            var element = clause.Elements[index];
            if (element.Kind != ArgKind.DynamicSkip)
            {
                continue;
            }

            if (element.Raw.IndexOf("$(", StringComparison.Ordinal) >= 0 ||
                element.Raw.IndexOf('`') >= 0)
            {
                return true;
            }
        }

        return false;
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

    private static bool TryCloneDecodedBlock(
        ShellBlockSyntax source,
        CompoundOperator firstOperator,
        bool outerSubshell,
        ref bool firstLeaf,
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
            if (!TryCloneDecodedNode(
                    statement,
                    firstOperator,
                    outerSubshell,
                    ref firstLeaf,
                    out var cloned))
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
        CompoundOperator firstOperator,
        bool outerSubshell,
        ref bool firstLeaf,
        out ShellSyntaxNode? clone)
    {
        clone = null;
        switch (source)
        {
            case ShellBlockSyntax block:
                if (!TryCloneDecodedBlock(
                        block,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var clonedBlock))
                {
                    return false;
                }

                clone = clonedBlock;
                return true;
            case SimpleCommandSyntax simple:
                var substitutions = new List<CommandSubstitutionSyntax>(simple.Substitutions.Count);
                foreach (var substitution in simple.Substitutions)
                {
                    if (!TryCloneDecodedNode(
                            substitution,
                            firstOperator,
                            outerSubshell,
                            ref firstLeaf,
                            out var clonedSubstitution) ||
                        clonedSubstitution is not CommandSubstitutionSyntax typedSubstitution)
                    {
                        return false;
                    }

                    substitutions.Add(typedSubstitution);
                }

                var clauseOperator = firstLeaf ? firstOperator : simple.Clause.Operator;
                firstLeaf = false;
                clone = new SimpleCommandSyntax
                {
                    Clause = simple.Clause with
                    {
                        Operator = clauseOperator,
                        IsSubshell = outerSubshell || simple.Clause.IsSubshell,
                        IsCommandStringWrapped = true,
                        Elements = ClauseElementProvenance.WithoutOuterSourceSpans(
                            simple.Clause.Elements),
                    },
                    Substitutions = substitutions,
                };
                return true;
            case PipelineSyntax pipeline:
                return TryCloneDecodedCollection(
                    pipeline.Stages,
                    firstOperator,
                    outerSubshell,
                    ref firstLeaf,
                    stages => new PipelineSyntax { Stages = stages },
                    out clone);
            case CommandListSyntax list:
                var items = new List<CommandListItemSyntax>(list.Items.Count);
                foreach (var item in list.Items)
                {
                    if (item is null || !TryCloneDecodedNode(
                            item.Command,
                            firstOperator,
                            outerSubshell,
                            ref firstLeaf,
                            out var itemCommand))
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
                if (!TryCloneDecodedBlock(
                        group.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var groupBody))
                {
                    return false;
                }

                clone = new GroupSyntax { GroupKind = group.GroupKind, Body = groupBody };
                return true;
            case ForEachSyntax forEach:
                if (!TryCloneDecodedBlock(
                        forEach.IteratorCommands,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var iterator) ||
                    !TryCloneDecodedBlock(
                        forEach.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var forBody))
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
                if (!TryCloneDecodedBlock(
                        loop.Condition,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var condition) ||
                    !TryCloneDecodedBlock(
                        loop.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var loopBody))
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
                    if (!TryCloneDecodedNode(
                            branch,
                            firstOperator,
                            outerSubshell,
                            ref firstLeaf,
                            out var clonedBranch) ||
                        clonedBranch is not ConditionalBranchSyntax typedBranch)
                    {
                        return false;
                    }

                    branches.Add(typedBranch);
                }

                ShellBlockSyntax? @else = null;
                if (conditional.Else is not null && !TryCloneDecodedBlock(
                        conditional.Else,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out @else))
                {
                    return false;
                }

                clone = new ConditionalSyntax { Branches = branches, Else = @else };
                return true;
            case ConditionalBranchSyntax branch:
                if (!TryCloneDecodedBlock(
                        branch.Condition,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var branchCondition) ||
                    !TryCloneDecodedBlock(
                        branch.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var branchBody))
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
                if (!TryCloneDecodedBlock(
                        substitution.Body,
                        firstOperator,
                        outerSubshell,
                        ref firstLeaf,
                        out var substitutionBody))
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
        CompoundOperator firstOperator,
        bool outerSubshell,
        ref bool firstLeaf,
        Func<IReadOnlyList<ShellSyntaxNode>, ShellSyntaxNode> factory,
        out ShellSyntaxNode? clone)
    {
        var children = new List<ShellSyntaxNode>(source.Count);
        foreach (var child in source)
        {
            if (!TryCloneDecodedNode(
                    child,
                    firstOperator,
                    outerSubshell,
                    ref firstLeaf,
                    out var clonedChild))
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
}
