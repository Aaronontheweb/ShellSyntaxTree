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
        int structuralDepth,
        bool markBashCWrapped)
    {
        var coordinator = new StructuralCoordinator(
            source,
            tokens,
            options,
            bashCDepth,
            structuralDepth,
            markBashCWrapped,
            sourceStart: 0,
            source.Length);
        if (!coordinator.TryParse(out var syntax, out var error))
        {
            return StructuralFailure(source, error, syntax);
        }

        if (!ShellSyntaxProjection.TryProject(
                syntax,
                simple => new CommandOccurrenceFacts
                {
                    IsComplete = simple.Clause.Redirects.Count == 0 &&
                        !HasUnexpandedCommandString(simple.Clause),
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
        private readonly int _structuralDepth;
        private readonly bool _markBashCWrapped;
        private readonly int _sourceStart;
        private readonly int _sourceLength;
        private readonly CdAttributionContext _attribution = new();
        private int _position;
        private int _subshellDepth;

        internal StructuralCoordinator(
            string source,
            IReadOnlyList<BashToken> tokens,
            BashParserOptions options,
            int bashCDepth,
            int structuralDepth,
            bool markBashCWrapped,
            int sourceStart,
            int sourceLength)
        {
            _source = source;
            _tokens = tokens;
            _options = options;
            _bashCDepth = bashCDepth;
            _structuralDepth = structuralDepth;
            _markBashCWrapped = markBashCWrapped;
            _sourceStart = sourceStart;
            _sourceLength = sourceLength;
        }

        internal bool TryParse(out ShellBlockSyntax syntax, out string? error)
        {
            SkipNewlines();
            if (_position == _tokens.Count)
            {
                syntax = new ShellBlockSyntax
                {
                    SourceStart = _sourceStart,
                    SourceLength = _sourceLength,
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
                SourceStart = _sourceStart,
                SourceLength = _sourceLength,
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

            if (HasAssignmentPrefix(segmentTokens))
            {
                error = "Bash assignment-prefix commands are not supported";
                return false;
            }

            if (!TryCollectCommandSubstitutions(
                    segmentTokens,
                    out var substitutionFragments,
                    out error))
            {
                return false;
            }

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
                    _structuralDepth + _subshellDepth + 1,
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
            if (!TryParseCommandSubstitutions(
                    substitutionFragments,
                    effectiveOptions,
                    out var substitutions,
                    out error))
            {
                return false;
            }

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
                Substitutions = substitutions,
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
            if (_structuralDepth + _subshellDepth >=
                ShellAnalysisLimits.MaxStructuralNesting)
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
                error = $"unbalanced parens at position {_sourceStart + _sourceLength}";
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

        private bool TryCollectCommandSubstitutions(
            IReadOnlyList<BashToken> tokens,
            out IReadOnlyList<ShellValueFragment> substitutions,
            out string? error)
        {
            var discovered = new List<ShellValueFragment>();
            var commandNameEnd = tokens[0].SourceStart + tokens[0].SourceLength;
            for (var index = 1; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (token.Kind == BashTokenKind.Operator ||
                    token.SourceStart != commandNameEnd)
                {
                    break;
                }

                commandNameEnd = token.SourceStart + token.SourceLength;
            }

            foreach (var token in tokens)
            {
                var value = token.ResolverValue;
                if (value is null)
                {
                    continue;
                }

                foreach (var fragment in value.Fragments)
                {
                    if (fragment.Kind != ShellValueFragmentKind.Opaque ||
                        fragment.OpaqueCause != ShellOpaqueCause.CommandSubstitution)
                    {
                        continue;
                    }

                    if (fragment.SourceStart is null || fragment.SourceLength is null ||
                        fragment.SourceLength < 2 ||
                        fragment.SourceStart < _sourceStart ||
                        fragment.SourceStart + fragment.SourceLength >
                            _sourceStart + _sourceLength)
                    {
                        substitutions = Array.Empty<ShellValueFragment>();
                        error = "Bash command substitution has invalid source provenance";
                        return false;
                    }

                    var raw = _source.Substring(
                        fragment.SourceStart.Value,
                        fragment.SourceLength.Value);
                    if (raw[0] == '`')
                    {
                        substitutions = Array.Empty<ShellValueFragment>();
                        error = "legacy backtick command substitution is not supported";
                        return false;
                    }

                    if (!raw.StartsWith("$(", StringComparison.Ordinal) || raw[raw.Length - 1] != ')')
                    {
                        substitutions = Array.Empty<ShellValueFragment>();
                        error = "unsupported Bash command substitution provenance";
                        return false;
                    }

                    if (fragment.SourceStart < commandNameEnd)
                    {
                        substitutions = Array.Empty<ShellValueFragment>();
                        error = "Bash command-name substitution is not supported";
                        return false;
                    }

                    discovered.Add(fragment);
                }
            }

            substitutions = discovered;
            error = null;
            return true;
        }

        private bool HasAssignmentPrefix(IReadOnlyList<BashToken> tokens)
        {
            var spelling = _source.Substring(tokens[0].SourceStart, tokens[0].SourceLength)
                .Replace("\\\r\n", string.Empty)
                .Replace("\\\n", string.Empty)
                .Replace("\\\r", string.Empty);
            var index = 0;
            if (spelling.Length == 0 || !IsBashIdentifierStart(spelling[index]))
            {
                return false;
            }

            index++;
            while (index < spelling.Length && IsBashIdentifierContinuation(spelling[index]))
            {
                index++;
            }

            if (index < spelling.Length && spelling[index] == '[')
            {
                while (index < spelling.Length && spelling[index] != ']')
                {
                    index++;
                }

                if (index >= spelling.Length)
                {
                    return false;
                }

                index++;
            }

            if (index < spelling.Length && spelling[index] == '+')
            {
                index++;
            }

            return index < spelling.Length && spelling[index] == '=';
        }

        private static bool IsBashIdentifierStart(char value) =>
            value == '_' || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

        private static bool IsBashIdentifierContinuation(char value) =>
            IsBashIdentifierStart(value) || value is >= '0' and <= '9';

        private bool TryParseCommandSubstitutions(
            IReadOnlyList<ShellValueFragment> fragments,
            BashParserOptions options,
            out IReadOnlyList<CommandSubstitutionSyntax> substitutions,
            out string? error)
        {
            if (fragments.Count == 0)
            {
                substitutions = Array.Empty<CommandSubstitutionSyntax>();
                error = null;
                return true;
            }

            if (_structuralDepth + _subshellDepth + 1 >
                ShellAnalysisLimits.MaxStructuralNesting)
            {
                substitutions = Array.Empty<CommandSubstitutionSyntax>();
                error = "Bash structural nesting depth exceeded (>16)";
                return false;
            }

            var parsed = new List<CommandSubstitutionSyntax>(fragments.Count);
            foreach (var fragment in fragments)
            {
                var sourceStart = fragment.SourceStart!.Value;
                var sourceLength = fragment.SourceLength!.Value;
                var bodyStart = sourceStart + 2;
                var bodyLength = sourceLength - 3;
                if (!TryParseSubstitutionBody(
                        bodyStart,
                        bodyLength,
                        options,
                        out var body,
                        out error))
                {
                    substitutions = Array.Empty<CommandSubstitutionSyntax>();
                    return false;
                }

                parsed.Add(new CommandSubstitutionSyntax
                {
                    Body = body,
                    SourceStart = sourceStart,
                    SourceLength = sourceLength,
                });
            }

            substitutions = parsed;
            error = null;
            return true;
        }

        private bool TryParseSubstitutionBody(
            int sourceStart,
            int sourceLength,
            BashParserOptions options,
            out ShellBlockSyntax body,
            out string? error)
        {
            var source = _source.Substring(sourceStart, sourceLength);
            var relativeTokens = BashLexer.Tokenize(source);
            for (var index = 0; index < relativeTokens.Count; index++)
            {
                if (relativeTokens[index].Kind == BashTokenKind.UnparseableSentinel)
                {
                    body = new ShellBlockSyntax();
                    error = relativeTokens[index].UnparseableReason;
                    return false;
                }
            }

            var significant = FilterSignificant(relativeTokens);
            if (TryDetectAnomaly(significant, out error))
            {
                body = new ShellBlockSyntax();
                return false;
            }

            var shifted = ShiftTokens(significant, sourceStart);
            var coordinator = new StructuralCoordinator(
                _source,
                shifted,
                options,
                _bashCDepth,
                _structuralDepth + _subshellDepth + 1,
                _markBashCWrapped,
                sourceStart,
                sourceLength);
            return coordinator.TryParse(out body, out error);
        }
    }

    private static IReadOnlyList<BashToken> ShiftTokens(
        IReadOnlyList<BashToken> tokens,
        int sourceOffset)
    {
        var shifted = new BashToken[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            shifted[index] = token with
            {
                SourceStart = token.SourceStart + sourceOffset,
                ResolverValue = ShiftValue(token.ResolverValue, sourceOffset),
            };
        }

        return shifted;
    }

    private static ShellValue? ShiftValue(ShellValue? value, int sourceOffset)
    {
        if (value is null)
        {
            return null;
        }

        var fragments = new ShellValueFragment[value.Fragments.Count];
        for (var index = 0; index < value.Fragments.Count; index++)
        {
            var fragment = value.Fragments[index];
            fragments[index] = fragment with
            {
                SourceStart = fragment.SourceStart + sourceOffset,
            };
        }

        return new ShellValue(value.Decoded, fragments);
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
