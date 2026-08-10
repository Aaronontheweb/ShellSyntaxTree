// -----------------------------------------------------------------------
// <copyright file="PwshForEachStructuralParser.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using ShellSyntaxTree.Internal.Pwsh.Lexing;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Pwsh.Parsing;

internal static partial class PwshCommandParser
{
    private sealed partial class StructuralCoordinator
    {
        private bool IsForEachStart() =>
            _position + 1 < _tokens.Count &&
            _tokens[_position].Kind == PwshTokenKind.Word &&
            string.Equals(
                _tokens[_position].Value,
                "foreach",
                StringComparison.OrdinalIgnoreCase) &&
            _tokens[_position + 1].Kind == PwshTokenKind.Operator &&
            _tokens[_position + 1].OperatorText == "(";

        private bool TryParseForEach(
            CompoundOperator compatibilityOperator,
            out ShellSyntaxNode? command,
            out string? error)
        {
            command = null;
            if (compatibilityOperator is not CompoundOperator.None and
                not CompoundOperator.Sequence)
            {
                error = "a PowerShell foreach statement requires a statement boundary";
                return false;
            }

            if (_structuralDepth + _groupDepth >= ShellAnalysisLimits.MaxStructuralNesting)
            {
                error = "PowerShell structural nesting depth exceeded (>16)";
                return false;
            }

            var start = _tokens[_position];
            _position += 2;
            if (_position == _tokens.Count ||
                !TryReadSimpleLoopBinding(_tokens[_position], out var bindingName))
            {
                error = "PowerShell foreach requires a simple variable binding";
                return false;
            }

            var bindingToken = _tokens[_position++];
            if (_options.InitialStateMode ==
                    PwshInitialStateMode.IsolatedNonInteractiveNoProfile &&
                !PwshForEachValueAnalysis.IsEligibleBindingName(bindingName))
            {
                error = "PowerShell foreach binding collides with a reserved or stateful built-in variable";
                return false;
            }

            if (_position == _tokens.Count ||
                _tokens[_position].Kind != PwshTokenKind.Word ||
                !string.Equals(
                    _tokens[_position].Value,
                    "in",
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "PowerShell foreach binding is missing 'in'";
                return false;
            }

            _position++;
            var iterableStart = _position;
            if (!TryFindForEachHeaderClose(iterableStart, out var closePosition) ||
                closePosition == iterableStart)
            {
                error = "PowerShell foreach requires a bounded iterable expression";
                return false;
            }

            var iterableTokens = CopyTokens(iterableStart, closePosition);
            var isLiteralIterable = IsLiteralForEachExpression(iterableTokens);
            var firstIterable = iterableTokens[0];
            var lastIterable = iterableTokens[iterableTokens.Count - 1];
            var iterableSourceStart = firstIterable.SourceStart;
            var iterableSourceLength = lastIterable.SourceStart +
                lastIterable.SourceLength - iterableSourceStart;
            if (!TryParseForEachIterator(
                    iterableTokens,
                    iterableSourceStart,
                    iterableSourceLength,
                    out var iteratorCommands,
                    out error))
            {
                return false;
            }

            _position = closePosition + 1;
            if (_position == _tokens.Count ||
                _tokens[_position].Kind != PwshTokenKind.ScriptBlock)
            {
                error = "PowerShell foreach requires a script-block body";
                return false;
            }

            var bodyToken = _tokens[_position++];
            if (!TryParseForEachBody(bodyToken, out var body, out error))
            {
                return false;
            }

            if (ContainsUnsupportedForEachStateTransfer(iteratorCommands) ||
                ContainsUnsupportedForEachStateTransfer(body))
            {
                error = "PowerShell foreach state mutation is not supported in this structural slice";
                return false;
            }

            var forEach = new ForEachSyntax
            {
                Binding = new LoopBindingSyntax
                {
                    Name = bindingName,
                    Source = new ShellSourceFragment
                    {
                        Raw = _source.Substring(
                            bindingToken.SourceStart,
                            bindingToken.SourceLength),
                        SourceStart = bindingToken.SourceStart,
                        SourceLength = bindingToken.SourceLength,
                    },
                },
                Iterable = new ShellSourceFragment
                {
                    Raw = _source.Substring(iterableSourceStart, iterableSourceLength),
                    SourceStart = iterableSourceStart,
                    SourceLength = iterableSourceLength,
                },
                IteratorCommands = iteratorCommands,
                Body = body,
                SourceStart = start.SourceStart,
                SourceLength = bodyToken.SourceStart + bodyToken.SourceLength -
                    start.SourceStart,
            };
            var plan = PwshForEachValueAnalysis.CapturePlan(
                bindingName,
                iterableTokens,
                isLiteralIterable);
            _forEachPlans.Add(forEach, plan);
            if (ContainsSetLocation(iteratorCommands) ||
                plan.Cardinality != PwshIterationCardinality.Never &&
                ContainsSetLocation(body))
            {
                _attribution.SetDynamic();
            }

            command = forEach;
            error = null;
            return true;
        }

        private bool TryFindForEachHeaderClose(int start, out int closePosition)
        {
            var depth = 1;
            for (var index = start; index < _tokens.Count; index++)
            {
                var token = _tokens[index];
                if (token.Kind != PwshTokenKind.Operator)
                {
                    continue;
                }

                if (token.OperatorText == "(")
                {
                    depth++;
                }
                else if (token.OperatorText == ")" && --depth == 0)
                {
                    closePosition = index;
                    return true;
                }
            }

            closePosition = -1;
            return false;
        }

        private bool TryParseForEachIterator(
            IReadOnlyList<PwshToken> tokens,
            int sourceStart,
            int sourceLength,
            out ShellBlockSyntax iterator,
            out string? error)
        {
            if (IsLiteralForEachExpression(tokens))
            {
                iterator = new ShellBlockSyntax
                {
                    SourceStart = sourceStart,
                    SourceLength = sourceLength,
                };
                error = null;
                return true;
            }

            if (tokens.Count == 1 && tokens[0].Kind == PwshTokenKind.Subexpression &&
                tokens[0].Value.StartsWith("$(", StringComparison.Ordinal))
            {
                if (tokens[0].ResolverValue is null)
                {
                    iterator = new ShellBlockSyntax();
                    error = "PowerShell foreach subexpression lacks value provenance";
                    return false;
                }

                ShellValueFragment? fragment = null;
                foreach (var candidate in tokens[0].ResolverValue!.Fragments)
                {
                    if (candidate.SourceStart == tokens[0].SourceStart &&
                        candidate.SourceLength == tokens[0].SourceLength)
                    {
                        fragment = candidate;
                        break;
                    }
                }

                error = "PowerShell foreach subexpression lacks exact provenance";
                if (fragment is null ||
                    !TryParseStandaloneSubstitution(
                        fragment.Value,
                        CompoundOperator.None,
                        out var substitution,
                        out error))
                {
                    iterator = new ShellBlockSyntax();
                    return false;
                }

                iterator = new ShellBlockSyntax
                {
                    Statements = new[] { substitution! },
                    SourceStart = sourceStart,
                    SourceLength = sourceLength,
                };
                return true;
            }

            if (tokens[0].Kind == PwshTokenKind.Splat ||
                tokens[0].Kind == PwshTokenKind.Word &&
                tokens[0].Value.StartsWith("$", StringComparison.Ordinal) ||
                tokens[0].Kind == PwshTokenKind.Operator &&
                tokens[0].OperatorText == "&")
            {
                iterator = new ShellBlockSyntax();
                error = "dynamic PowerShell foreach iterables are not supported";
                return false;
            }

            var iteratorSource = _source.Substring(sourceStart, sourceLength);
            var firstValue = tokens[0].Value.TrimEnd(',');
            if (tokens.Count > 1 &&
                (tokens[0].Kind == PwshTokenKind.QuotedString ||
                 firstValue.Length > 0 && IsNumericExpressionWord(firstValue)))
            {
                iterator = new ShellBlockSyntax();
                error = "unsupported PowerShell foreach literal-list expression";
                return false;
            }

            if (IsUnsupportedSubstitutionBody(iteratorSource, tokens))
            {
                iterator = new ShellBlockSyntax();
                error = "unsupported PowerShell foreach expression";
                return false;
            }

            var coordinator = new StructuralCoordinator(
                _source,
                tokens,
                _options,
                _recursionDepth,
                _structuralDepth + _groupDepth + 1,
                _markWrapped,
                _attribution.Clone(),
                sourceStart,
                sourceLength,
                CompoundOperator.None,
                insideCommandSubstitution: false);
            if (!coordinator.TryParse(out iterator, out error))
            {
                return false;
            }

            MergeFacts(coordinator);
            return true;
        }

        private bool TryParseForEachBody(
            PwshToken bodyToken,
            out ShellBlockSyntax body,
            out string? error)
        {
            if (bodyToken.Value.Length < 2 ||
                bodyToken.Value[0] != '{' ||
                bodyToken.Value[bodyToken.Value.Length - 1] != '}')
            {
                body = new ShellBlockSyntax();
                error = "PowerShell foreach script-block provenance is invalid";
                return false;
            }

            var sourceStart = bodyToken.SourceStart + 1;
            var sourceLength = bodyToken.SourceLength - 2;
            var source = _source.Substring(sourceStart, sourceLength);
            var relativeTokens = PwshLexer.Tokenize(source);
            foreach (var token in relativeTokens)
            {
                if (token.Kind == PwshTokenKind.UnparseableSentinel)
                {
                    body = new ShellBlockSyntax();
                    error = token.UnparseableReason;
                    return false;
                }
            }

            var significant = FilterSignificant(relativeTokens);
            if (TryDetectAnomaly(significant, _options.Dialect, out error))
            {
                body = new ShellBlockSyntax();
                return false;
            }

            var shifted = ShiftTokens(significant, sourceStart);
            var coordinator = new StructuralCoordinator(
                _source,
                shifted,
                _options,
                _recursionDepth,
                _structuralDepth + _groupDepth + 1,
                _markWrapped,
                _attribution.Clone(),
                sourceStart,
                sourceLength,
                CompoundOperator.None,
                insideCommandSubstitution: false);
            if (!coordinator.TryParse(out body, out error))
            {
                return false;
            }

            MergeFacts(coordinator);
            return true;
        }

        private static bool TryReadSimpleLoopBinding(PwshToken token, out string name)
        {
            name = string.Empty;
            if (token.Kind != PwshTokenKind.Word || token.Value.Length < 2 ||
                token.Value[0] != '$' || !IsVariableNameStart(token.Value[1]))
            {
                return false;
            }

            for (var index = 2; index < token.Value.Length; index++)
            {
                if (!IsVariableNamePart(token.Value[index]))
                {
                    return false;
                }
            }

            name = token.Value.Substring(1);
            return true;
        }

        private static bool IsVariableNameStart(char value) =>
            value == '_' || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

        private static bool IsVariableNamePart(char value) =>
            IsVariableNameStart(value) || value is >= '0' and <= '9';

        private static bool IsLiteralForEachExpression(IReadOnlyList<PwshToken> tokens)
        {
            if (tokens.Count != 1)
            {
                return false;
            }

            var token = tokens[0];
            if (token.Kind == PwshTokenKind.QuotedString)
            {
                return !token.HasInterpolation;
            }

            if (token.Kind == PwshTokenKind.Subexpression)
            {
                return token.Value.StartsWith("@(", StringComparison.Ordinal) &&
                    IsLiteralArrayExpression(token.Value);
            }

            if (token.Kind != PwshTokenKind.Word)
            {
                return false;
            }

            return IsNumericExpressionWord(token.Value) ||
                string.Equals(token.Value, "$true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token.Value, "$false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token.Value, "$null", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsUnsupportedForEachStateTransfer(ShellSyntaxNode node)
        {
            foreach (var clause in EnumerateClauses(node))
            {
                var verb = clause.Verb.CanonicalVerb ??
                    (clause.Verb.Tokens.Count == 0 ? null : clause.Verb.Tokens[0]);
                if (verb is not null &&
                    (IsUnsupportedForEachStateVerb(verb) ||
                     IsProviderStateMutation(verb, clause)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnsupportedForEachStateVerb(string verb) =>
            PwshPersistentStateMutation.IsUnsupportedForEachStateVerb(verb);

        private static bool ContainsSetLocation(ShellSyntaxNode node)
        {
            foreach (var clause in EnumerateClauses(node))
            {
                var verb = clause.Verb.CanonicalVerb ??
                    (clause.Verb.Tokens.Count == 0 ? null : clause.Verb.Tokens[0]);
                if (verb is not null &&
                    verb.Equals("Set-Location", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsProviderStateMutation(string verb, Clause clause)
            => PwshPersistentStateMutation.IsProviderStateMutation(verb, clause);

        private static IEnumerable<Clause> EnumerateClauses(ShellSyntaxNode node)
        {
            switch (node)
            {
                case SimpleCommandSyntax simple:
                    yield return simple.Clause;
                    foreach (var substitution in simple.Substitutions)
                    {
                        foreach (var clause in EnumerateClauses(substitution))
                        {
                            yield return clause;
                        }
                    }

                    yield break;
                case ShellBlockSyntax block:
                    foreach (var statement in block.Statements)
                    {
                        foreach (var clause in EnumerateClauses(statement))
                        {
                            yield return clause;
                        }
                    }

                    yield break;
                case CommandListSyntax list:
                    foreach (var item in list.Items)
                    {
                        foreach (var clause in EnumerateClauses(item.Command))
                        {
                            yield return clause;
                        }
                    }

                    yield break;
                case PipelineSyntax pipeline:
                    foreach (var stage in pipeline.Stages)
                    {
                        foreach (var clause in EnumerateClauses(stage))
                        {
                            yield return clause;
                        }
                    }

                    yield break;
                case GroupSyntax group:
                    foreach (var clause in EnumerateClauses(group.Body))
                    {
                        yield return clause;
                    }

                    yield break;
                case ForEachSyntax forEach:
                    foreach (var clause in EnumerateClauses(forEach.IteratorCommands))
                    {
                        yield return clause;
                    }

                    foreach (var clause in EnumerateClauses(forEach.Body))
                    {
                        yield return clause;
                    }

                    yield break;
                case CommandSubstitutionSyntax substitution:
                    foreach (var clause in EnumerateClauses(substitution.Body))
                    {
                        yield return clause;
                    }

                    yield break;
            }
        }
    }
}
