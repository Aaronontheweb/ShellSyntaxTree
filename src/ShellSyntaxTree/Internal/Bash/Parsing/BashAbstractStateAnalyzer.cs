// -----------------------------------------------------------------------
// <copyright file="BashAbstractStateAnalyzer.cs" company="Aaron Stannard">
//      Copyright (C) 2026 - 2026 Aaron Stannard <https://github.com/Aaronontheweb>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ShellSyntaxTree.Internal.Resolving;

namespace ShellSyntaxTree.Internal.Bash.Parsing;

/// <summary>
/// Computes execution-order Bash cwd facts after structural parsing. Parser
/// attribution remains a lexical construction aid; this pass owns the facts
/// exposed to security consumers.
/// </summary>
internal sealed class BashAbstractStateAnalyzer
{
    private readonly BashParserOptions _options;
    private readonly Func<SimpleCommandSyntax, CommandOccurrenceFacts> _factsFactory;
    private readonly Dictionary<Clause, BashAbstractState> _inputs =
        new(ClauseReferenceComparer.Instance);

    private BashAbstractStateAnalyzer(
        BashParserOptions options,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory)
    {
        _options = options;
        _factsFactory = factsFactory;
    }

    internal static bool TryAnalyze(
        ShellBlockSyntax syntax,
        BashParserOptions options,
        Func<SimpleCommandSyntax, CommandOccurrenceFacts> factsFactory,
        out ShellBlockSyntax analyzedSyntax,
        out Func<SimpleCommandSyntax, CommandOccurrenceFacts> analyzedFacts)
    {
        var analyzer = new BashAbstractStateAnalyzer(options, factsFactory);
        var initial = new BashAbstractState(
            options.WorkingDirectory ?? Environment.CurrentDirectory,
            hasCompatibilityAttribution: false);
        analyzer.AnalyzeBlock(syntax, initial);

        var facts = new Dictionary<Clause, CommandOccurrenceFacts>(
            ClauseReferenceComparer.Instance);
        analyzedSyntax = analyzer.RewriteBlock(syntax, facts);
        analyzedFacts = simple => facts.TryGetValue(simple.Clause, out var value)
            ? value
            : new CommandOccurrenceFacts();
        return true;
    }

    private BashFlowResult AnalyzeNode(ShellSyntaxNode node, BashAbstractState input) =>
        node switch
        {
            ShellBlockSyntax block => AnalyzeBlock(block, input),
            SimpleCommandSyntax simple => AnalyzeSimple(simple, input),
            CommandListSyntax list => AnalyzeList(list, input),
            PipelineSyntax pipeline => AnalyzePipeline(pipeline, input),
            GroupSyntax group => AnalyzeGroup(group, input),
            CommandSubstitutionSyntax substitution => AnalyzeIsolatedBody(
                substitution.Body,
                input,
                resetCompatibilityAttribution: true),
            ForEachSyntax => BashFlowResult.Both(input),
            ConditionLoopSyntax => BashFlowResult.Both(input.WithUnknownCwd()),
            ConditionalSyntax conditional => AnalyzeConditional(conditional, input),
            ConditionalBranchSyntax branch => AnalyzeBranch(branch, input),
            _ => BashFlowResult.Both(input.WithUnknownCwd()),
        };

    private BashFlowResult AnalyzeBlock(ShellBlockSyntax block, BashAbstractState input)
    {
        var flow = BashFlowResult.Both(input);
        foreach (var statement in block.Statements)
        {
            flow = AnalyzeNode(statement, flow.JoinedState);
        }

        return flow;
    }

    private BashFlowResult AnalyzeSimple(
        SimpleCommandSyntax simple,
        BashAbstractState input)
    {
        foreach (var substitution in simple.Substitutions)
        {
            AnalyzeIsolatedBody(
                substitution.Body,
                input,
                resetCompatibilityAttribution: true);
        }

        RecordInput(simple.Clause, input);
        if (!TryGetCwdTransfer(simple.Clause, input, out var success))
        {
            return BashFlowResult.Both(input);
        }

        return new BashFlowResult(success, input);
    }

    private BashFlowResult AnalyzeList(CommandListSyntax list, BashAbstractState input)
    {
        var flow = AnalyzeNode(list.Items[0].Command, input);
        for (var index = 1; index < list.Items.Count; index++)
        {
            var item = list.Items[index];
            switch (item.Operator)
            {
                case CompoundOperator.AndIf:
                    {
                        var right = AnalyzeNode(item.Command, flow.OnSuccess ?? flow.JoinedState);
                        flow = new BashFlowResult(
                            right.OnSuccess,
                            BashAbstractState.JoinNullable(flow.OnFailure, right.OnFailure));
                        break;
                    }
                case CompoundOperator.OrIf:
                    {
                        var right = AnalyzeNode(item.Command, flow.OnFailure ?? flow.JoinedState);
                        flow = new BashFlowResult(
                            BashAbstractState.JoinNullable(flow.OnSuccess, right.OnSuccess),
                            right.OnFailure);
                        break;
                    }
                case CompoundOperator.Sequence:
                    flow = AnalyzeNode(item.Command, flow.JoinedState);
                    break;
                default:
                    return BashFlowResult.Both(flow.JoinedState.WithUnknownCwd());
            }
        }

        return flow;
    }

    private BashFlowResult AnalyzePipeline(
        PipelineSyntax pipeline,
        BashAbstractState input)
    {
        BashFlowResult? last = null;
        foreach (var stage in pipeline.Stages)
        {
            last = AnalyzeNode(stage, input);
        }

        // lastpipe controls whether the last stage can leak state; pipefail
        // independently controls which exit partition receives it. With no
        // proved shell options, both partitions conservatively receive the
        // join of isolated and current-scope outcomes.
        var joined = BashAbstractState.Join(input, last!.Value.JoinedState);
        return BashFlowResult.Both(joined);
    }

    private BashFlowResult AnalyzeGroup(GroupSyntax group, BashAbstractState input)
    {
        if (group.GroupKind == ShellGroupKind.CurrentScope)
        {
            return AnalyzeBlock(group.Body, input);
        }

        return AnalyzeIsolatedBody(
            group.Body,
            input,
            resetCompatibilityAttribution: IsDecodedWrapper(group.Body));
    }

    private BashFlowResult AnalyzeIsolatedBody(
        ShellBlockSyntax body,
        BashAbstractState input,
        bool resetCompatibilityAttribution)
    {
        var childInput = resetCompatibilityAttribution
            ? input.WithoutCompatibilityAttribution()
            : input;
        var inner = AnalyzeBlock(body, childInput);
        return new BashFlowResult(
            inner.OnSuccess is null ? null : input,
            inner.OnFailure is null ? null : input);
    }

    private BashFlowResult AnalyzeConditional(
        ConditionalSyntax conditional,
        BashAbstractState input)
    {
        BashAbstractState? success = null;
        BashAbstractState? failure = input;
        foreach (var branch in conditional.Branches)
        {
            var branchFlow = AnalyzeBranch(branch, failure ?? input.WithUnknownCwd());
            success = BashAbstractState.JoinNullable(success, branchFlow.OnSuccess);
            failure = BashAbstractState.JoinNullable(failure, branchFlow.OnFailure);
        }

        if (conditional.Else is not null)
        {
            var elseFlow = AnalyzeBlock(conditional.Else, failure ?? input.WithUnknownCwd());
            success = BashAbstractState.JoinNullable(success, elseFlow.OnSuccess);
            failure = elseFlow.OnFailure;
        }

        return new BashFlowResult(success, failure);
    }

    private BashFlowResult AnalyzeBranch(
        ConditionalBranchSyntax branch,
        BashAbstractState input)
    {
        var condition = AnalyzeBlock(branch.Condition, input);
        var body = AnalyzeBlock(branch.Body, condition.OnSuccess ?? condition.JoinedState);
        return new BashFlowResult(
            body.OnSuccess,
            BashAbstractState.JoinNullable(condition.OnFailure, body.OnFailure));
    }

    private void RecordInput(Clause clause, BashAbstractState input)
    {
        if (_inputs.TryGetValue(clause, out var prior))
        {
            _inputs[clause] = BashAbstractState.Join(prior, input);
        }
        else
        {
            _inputs.Add(clause, input);
        }
    }

    private bool TryGetCwdTransfer(
        Clause clause,
        BashAbstractState input,
        out BashAbstractState success)
    {
        success = input;
        var verb = FirstVerb(clause);
        if (verb is null)
        {
            return false;
        }

        if (verb is "command" or "builtin")
        {
            var dispatched = DispatchedVerb(clause);
            if (dispatched is "cd" or "chdir" or "pushd" or "popd" or
                "eval" or "." or "source" or "trap")
            {
                success = input.WithUnknownCwd();
                return true;
            }

            return false;
        }

        if (verb is "eval" or "." or "source" or "trap" or "pushd" or "popd")
        {
            success = input.WithUnknownCwd();
            return true;
        }

        if (verb is not ("cd" or "chdir"))
        {
            return false;
        }

        if (!TryGetCdOperand(
                clause,
                out _,
                out var target,
                out var resolutionMustBeUnknown))
        {
            if (resolutionMustBeUnknown)
            {
                success = input.WithUnknownCwd();
                return true;
            }

            success = string.IsNullOrEmpty(_options.HomeDirectory)
                ? input.WithUnknownCwd()
                : new BashAbstractState(_options.HomeDirectory, true);
            return true;
        }

        if (resolutionMustBeUnknown)
        {
            success = input.WithUnknownCwd();
            return true;
        }

        if (target.Kind == ArgKind.Tilde && target.Resolved is not null)
        {
            success = new BashAbstractState(target.Resolved, true);
            return true;
        }

        if (target.Kind != ArgKind.Literal)
        {
            success = input.WithUnknownCwd();
            return true;
        }

        var value = ArgumentValue(clause, target);
        var options = OptionsFor(input);
        var resolved = BashResolver.Resolve(
            value,
            treatAsPath: true,
            options,
            workingDirectoryUnknown: input.WorkingDirectory is null,
            isLiteralBytes: true);
        success = resolved.Resolved is null
            ? input.WithUnknownCwd()
            : new BashAbstractState(resolved.Resolved, true);
        return true;
    }

    private static string? DispatchedVerb(Clause clause)
    {
        var words = new List<string>(clause.Verb.Tokens.Count + clause.Args.Count);
        foreach (var token in clause.Verb.Tokens)
        {
            words.Add(token);
        }

        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution && !argument.IsFlag)
            {
                words.Add(ArgumentValue(clause, argument));
            }
        }

        var index = 0;
        while (index < words.Count && words[index] is "command" or "builtin")
        {
            index++;
        }

        return index < words.Count ? words[index] : null;
    }

    private static bool TryGetCdOperand(
        Clause clause,
        out int argumentIndex,
        out Arg target,
        out bool resolutionMustBeUnknown)
    {
        argumentIndex = -1;
        target = null!;
        resolutionMustBeUnknown = false;
        var optionsEnded = false;
        var physical = false;
        var current = 0;
        foreach (var argument in clause.Args)
        {
            if (argument.IsCwdAttribution)
            {
                continue;
            }

            var raw = argument.Raw;
            if (!optionsEnded && raw == "--")
            {
                optionsEnded = true;
                current++;
                continue;
            }

            if (!optionsEnded && raw.Length > 1 && raw[0] == '-' && raw != "-")
            {
                if (raw.IndexOf('P') >= 0 || raw.IndexOf('@') >= 0)
                {
                    physical = true;
                }

                current++;
                continue;
            }

            argumentIndex = current;
            target = argument;
            var value = ArgumentValue(clause, argument);
            resolutionMustBeUnknown = physical ||
                value == "-" ||
                target.Kind == ArgKind.Literal && IsCdPathSearchCandidate(value);
            return true;
        }

        resolutionMustBeUnknown = physical;
        return false;
    }

    private static bool IsCdPathSearchCandidate(string value)
    {
        if (value is "." or ".." ||
            value.StartsWith("./", StringComparison.Ordinal) ||
            value.StartsWith("../", StringComparison.Ordinal) ||
            BashResolver.IsRootedPath(value))
        {
            return false;
        }

        const string fileSystemPrefix = "filesystem::";
        return !value.StartsWith(fileSystemPrefix, StringComparison.Ordinal) ||
            !BashResolver.IsRootedPath(value.Substring(fileSystemPrefix.Length));
    }

    private ShellBlockSyntax RewriteBlock(
        ShellBlockSyntax block,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var statements = new ShellSyntaxNode[block.Statements.Count];
        for (var index = 0; index < statements.Length; index++)
        {
            statements[index] = RewriteNode(block.Statements[index], facts);
        }

        return block with { Statements = statements };
    }

    private ShellSyntaxNode RewriteNode(
        ShellSyntaxNode node,
        Dictionary<Clause, CommandOccurrenceFacts> facts) =>
        node switch
        {
            ShellBlockSyntax block => RewriteBlock(block, facts),
            SimpleCommandSyntax simple => RewriteSimple(simple, facts),
            PipelineSyntax pipeline => pipeline with
            {
                Stages = RewriteNodes(pipeline.Stages, facts),
            },
            CommandListSyntax list => list with
            {
                Items = RewriteItems(list.Items, facts),
            },
            GroupSyntax group => group with { Body = RewriteBlock(group.Body, facts) },
            ForEachSyntax forEach => forEach with
            {
                IteratorCommands = RewriteBlock(forEach.IteratorCommands, facts),
                Body = RewriteBlock(forEach.Body, facts),
            },
            ConditionLoopSyntax loop => loop with
            {
                Condition = RewriteBlock(loop.Condition, facts),
                Body = RewriteBlock(loop.Body, facts),
            },
            ConditionalSyntax conditional => conditional with
            {
                Branches = RewriteBranches(conditional.Branches, facts),
                Else = conditional.Else is null ? null : RewriteBlock(conditional.Else, facts),
            },
            ConditionalBranchSyntax branch => branch with
            {
                Condition = RewriteBlock(branch.Condition, facts),
                Body = RewriteBlock(branch.Body, facts),
            },
            CommandSubstitutionSyntax substitution => substitution with
            {
                Body = RewriteBlock(substitution.Body, facts),
            },
            _ => node,
        };

    private SimpleCommandSyntax RewriteSimple(
        SimpleCommandSyntax simple,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var substitutions = new CommandSubstitutionSyntax[simple.Substitutions.Count];
        for (var index = 0; index < substitutions.Length; index++)
        {
            substitutions[index] = (CommandSubstitutionSyntax)RewriteNode(
                simple.Substitutions[index],
                facts);
        }

        var sourceFacts = _factsFactory(simple);
        if (!_inputs.TryGetValue(simple.Clause, out var input))
        {
            facts.Add(simple.Clause, sourceFacts);
            return simple with { Substitutions = substitutions };
        }

        var clause = RewriteClause(simple.Clause, input, sourceFacts.CwdPathDependencies);
        facts.Add(clause, new CommandOccurrenceFacts
        {
            EffectiveArguments = sourceFacts.EffectiveArguments,
            WorkingDirectory = input.ToDomain(),
            Redirects = RewriteRedirectFacts(sourceFacts.Redirects, clause),
            CwdPathDependencies = sourceFacts.CwdPathDependencies,
            IsComplete = sourceFacts.IsComplete,
        });
        return simple with
        {
            Clause = clause,
            Substitutions = substitutions,
        };
    }

    private Clause RewriteClause(
        Clause clause,
        BashAbstractState input,
        IReadOnlyList<CwdPathDependency> dependencies)
    {
        var parseWorkingDirectory = OriginalParseWorkingDirectory(clause, input);
        var directVerb = FirstVerb(clause);
        var clearCdTargetIndex = directVerb is "cd" or "chdir" && TryGetCdOperand(
            clause,
            out var cdTargetIndex,
            out _,
            out var cdResolutionMustBeUnknown) &&
            cdResolutionMustBeUnknown
            ? cdTargetIndex
            : -1;
        var authoredArgs = new List<Arg>(clause.Args.Count);
        var authoredArgumentIndex = 0;
        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution)
            {
                var dependency = FindArgumentDependency(
                    dependencies,
                    authoredArgumentIndex);
                var clearResolution = authoredArgumentIndex == clearCdTargetIndex;
                var rebased = clearResolution
                    ? null
                    : RebaseResolution(
                        argument.Resolved,
                        argument.IsPath,
                        argument.Kind,
                        dependency?.LogicalValue ?? ArgumentValue(clause, argument),
                        dependency?.ParseWorkingDirectory ?? parseWorkingDirectory,
                        input,
                        dependency?.DependsOnWorkingDirectory);
                var promote = !clearResolution &&
                    dependency?.DependsOnWorkingDirectory == true &&
                    rebased is not null;
                authoredArgs.Add(argument with
                {
                    Kind = promote ? ArgKind.Literal : argument.Kind,
                    IsPath = promote || argument.IsPath,
                    Resolved = rebased,
                });
                authoredArgumentIndex++;
            }
        }

        var elements = new ClauseElement[clause.Elements.Count];
        var argumentElementIndex = 0;
        for (var index = 0; index < elements.Length; index++)
        {
            var element = clause.Elements[index];
            var dependency = FindDependency(dependencies, index);
            var clearResolution = element.Role == ClauseElementRole.Argument &&
                argumentElementIndex++ == clearCdTargetIndex;
            var rebased = clearResolution
                ? null
                : RebaseResolution(
                    element.Resolved,
                    element.IsPath,
                    element.Kind,
                    dependency?.LogicalValue ?? element.Value,
                    dependency?.ParseWorkingDirectory ?? parseWorkingDirectory,
                    input,
                    dependency?.DependsOnWorkingDirectory);
            var promote = !clearResolution &&
                dependency?.DependsOnWorkingDirectory == true &&
                rebased is not null;
            elements[index] = element with
            {
                Kind = promote ? ArgKind.Literal : element.Kind,
                IsPath = promote || element.IsPath,
                Resolved = rebased,
            };
        }

        var redirects = RewriteCompatibilityRedirects(
            clause,
            parseWorkingDirectory,
            input,
            dependencies);

        if (input.HasCompatibilityAttribution)
        {
            authoredArgs.Add(CreateAttribution(input));
        }

        return clause with
        {
            Args = authoredArgs.ToArray(),
            Elements = elements,
            Redirects = redirects,
        };
    }

    private IReadOnlyList<Redirect> RewriteCompatibilityRedirects(
        Clause clause,
        string? parseWorkingDirectory,
        BashAbstractState input,
        IReadOnlyList<CwdPathDependency> dependencies)
    {
        if (clause.Redirects.Count == 0)
        {
            return clause.Redirects;
        }

        var redirectElements = new List<ClauseElement>(clause.Redirects.Count);
        foreach (var element in clause.Elements)
        {
            if (element.Role == ClauseElementRole.Redirect)
            {
                redirectElements.Add(element);
            }
        }

        var redirects = new Redirect[clause.Redirects.Count];
        for (var index = 0; index < redirects.Length; index++)
        {
            var redirect = clause.Redirects[index];
            if (index >= redirectElements.Count)
            {
                redirects[index] = redirect;
                continue;
            }

            var element = redirectElements[index];
            var elementIndex = IndexOfElement(clause.Elements, element);
            var dependency = FindDependency(dependencies, elementIndex);
            if (redirect.IsDynamicSkip && dependency is null)
            {
                redirects[index] = redirect;
                continue;
            }

            var target = RebaseResolution(
                redirect.IsDynamicSkip ? null : redirect.Target,
                element.IsPath,
                element.Kind,
                dependency?.LogicalValue ?? element.Value,
                dependency?.ParseWorkingDirectory ?? parseWorkingDirectory,
                input,
                dependency?.DependsOnWorkingDirectory);
            redirects[index] = target is null
                ? redirect with
                {
                    Target = dependency?.AuthoredValue ?? element.Value,
                    IsDynamicSkip = true,
                }
                : redirect with
                {
                    Target = target,
                    IsDynamicSkip = false,
                };
        }

        return redirects;
    }

    private static IReadOnlyList<RedirectAnalysis> RewriteRedirectFacts(
        IReadOnlyList<RedirectAnalysis> source,
        Clause clause)
    {
        if (source.Count == 0)
        {
            return source;
        }

        var rewritten = new RedirectAnalysis[source.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            var fact = source[index];
            if (!fact.IsPathRelevant ||
                fact.RedirectIndex < 0 ||
                fact.RedirectIndex >= clause.Redirects.Count)
            {
                rewritten[index] = fact;
                continue;
            }

            var redirect = clause.Redirects[fact.RedirectIndex];
            rewritten[index] = fact with
            {
                Target = redirect.IsDynamicSkip
                    ? ShellValueDomain.Unknown
                    : new ShellValueDomain
                    {
                        Kind = ShellValueDomainKind.Exact,
                        Values = new[] { redirect.Target },
                    },
            };
        }

        return rewritten;
    }

    private string? RebaseResolution(
        string? resolved,
        bool isPath,
        ArgKind kind,
        string authored,
        string? parseWorkingDirectory,
        BashAbstractState input,
        bool? dependsOnWorkingDirectory)
    {
        if (dependsOnWorkingDirectory.HasValue)
        {
            if (!dependsOnWorkingDirectory.Value)
            {
                return resolved;
            }

            if (resolved is not null &&
                parseWorkingDirectory is not null &&
                string.Equals(
                    input.WorkingDirectory,
                    parseWorkingDirectory,
                    StringComparison.Ordinal))
            {
                return resolved;
            }

            if (input.WorkingDirectory is null)
            {
                return null;
            }

            return BashResolver.Resolve(
                authored,
                treatAsPath: true,
                OptionsFor(input),
                workingDirectoryUnknown: false,
                isLiteralBytes: true).Resolved;
        }

        if (!isPath || kind != ArgKind.Literal || resolved is null ||
            parseWorkingDirectory is null)
        {
            return resolved;
        }

        if (parseWorkingDirectory is not null &&
            string.Equals(
                input.WorkingDirectory,
                parseWorkingDirectory,
                StringComparison.Ordinal))
        {
            return resolved;
        }

        var suffix = string.Empty;
        var hasRelativeSuffix = parseWorkingDirectory is not null &&
            TryGetRelativeSuffix(resolved, parseWorkingDirectory, out suffix);
        if (dependsOnWorkingDirectory is null &&
            !hasRelativeSuffix &&
            IsResolverRootedLiteral(authored))
        {
            return resolved;
        }

        if (input.WorkingDirectory is null)
        {
            return null;
        }

        return BashResolver.Resolve(
            hasRelativeSuffix ? suffix : authored,
            treatAsPath: true,
            OptionsFor(input),
            workingDirectoryUnknown: false,
            isLiteralBytes: true).Resolved;
    }

    private static bool IsResolverRootedLiteral(string authored)
    {
        if (BashResolver.IsRootedPath(authored))
        {
            return true;
        }

        const string fileSystemPrefix = "filesystem::";
        return authored.StartsWith(fileSystemPrefix, StringComparison.Ordinal) &&
            BashResolver.IsRootedPath(authored.Substring(fileSystemPrefix.Length));
    }

    private string? OriginalParseWorkingDirectory(
        Clause clause,
        BashAbstractState input)
    {
        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution)
            {
                continue;
            }

            return argument.Kind == ArgKind.Literal ? argument.Resolved : null;
        }

        return clause.IsCommandStringWrapped
            ? _options.WorkingDirectory ?? Environment.CurrentDirectory
            : input.WorkingDirectory ??
                _options.WorkingDirectory ??
                Environment.CurrentDirectory;
    }

    private static CwdPathDependency? FindDependency(
        IReadOnlyList<CwdPathDependency> dependencies,
        int elementIndex)
    {
        if (elementIndex < 0)
        {
            return null;
        }

        foreach (var dependency in dependencies)
        {
            if (dependency.ClauseElementIndex == elementIndex)
            {
                return dependency;
            }
        }

        return null;
    }

    private static CwdPathDependency? FindArgumentDependency(
        IReadOnlyList<CwdPathDependency> dependencies,
        int argumentIndex)
    {
        foreach (var dependency in dependencies)
        {
            if (dependency.ClauseArgumentIndex == argumentIndex)
            {
                return dependency;
            }
        }

        return null;
    }

    private static int IndexOfElement(
        IReadOnlyList<ClauseElement> elements,
        ClauseElement expected)
    {
        for (var index = 0; index < elements.Count; index++)
        {
            if (object.ReferenceEquals(elements[index], expected))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryGetRelativeSuffix(
        string resolved,
        string workingDirectory,
        out string suffix)
    {
        var normalizedResolved = resolved.Replace('\\', '/').TrimEnd('/');
        var normalizedWorkingDirectory = workingDirectory.Replace('\\', '/').TrimEnd('/');
        if (string.Equals(
                normalizedResolved,
                normalizedWorkingDirectory,
                StringComparison.Ordinal))
        {
            suffix = ".";
            return true;
        }

        var prefix = normalizedWorkingDirectory + "/";
        if (normalizedResolved.StartsWith(prefix, StringComparison.Ordinal))
        {
            suffix = normalizedResolved.Substring(prefix.Length);
            return true;
        }

        suffix = string.Empty;
        return false;
    }

    private BashParserOptions OptionsFor(BashAbstractState state) => new()
    {
        HomeDirectory = _options.HomeDirectory,
        WorkingDirectory = state.WorkingDirectory ?? _options.WorkingDirectory,
    };

    private static Arg CreateAttribution(BashAbstractState state) =>
        state.WorkingDirectory is null
            ? new Arg
            {
                Raw = "<dynamic-cwd>",
                Kind = ArgKind.DynamicSkip,
                IsCwdAttribution = true,
            }
            : new Arg
            {
                Raw = state.WorkingDirectory,
                Resolved = state.WorkingDirectory,
                Kind = ArgKind.Literal,
                IsPath = true,
                IsCwdAttribution = true,
            };

    private static string? FirstVerb(Clause clause) =>
        clause.Verb.Tokens.Count == 0 ? null : clause.Verb.Tokens[0];

    private static string? FirstPositionalValue(Clause clause)
    {
        var argument = FirstPositionalArgument(clause);
        return argument is null ? null : ArgumentValue(clause, argument);
    }

    private static Arg? FirstPositionalArgument(Clause clause)
    {
        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution && !argument.IsFlag)
            {
                return argument;
            }
        }

        return null;
    }

    private static bool ContainsAuthoredArgument(Clause clause, string raw)
    {
        foreach (var argument in clause.Args)
        {
            if (!argument.IsCwdAttribution &&
                string.Equals(argument.Raw, raw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ArgumentValue(Clause clause, Arg expected)
    {
        var argumentIndex = 0;
        foreach (var argument in clause.Args)
        {
            if (argument.IsCwdAttribution)
            {
                continue;
            }

            if (object.ReferenceEquals(argument, expected))
            {
                var current = 0;
                foreach (var element in clause.Elements)
                {
                    if (element.Role != ClauseElementRole.Argument)
                    {
                        continue;
                    }

                    if (current == argumentIndex)
                    {
                        return element.Value;
                    }

                    current++;
                }

                return argument.Raw;
            }

            argumentIndex++;
        }

        return expected.Raw;
    }

    private static bool IsDecodedWrapper(ShellBlockSyntax body)
    {
        var stack = new Stack<ShellSyntaxNode>();
        stack.Push(body);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case SimpleCommandSyntax simple:
                    return simple.Clause.IsCommandStringWrapped;
                case ShellBlockSyntax block:
                    PushReverse(stack, block.Statements);
                    break;
                case CommandListSyntax list:
                    for (var index = list.Items.Count - 1; index >= 0; index--)
                    {
                        stack.Push(list.Items[index].Command);
                    }

                    break;
                case PipelineSyntax pipeline:
                    PushReverse(stack, pipeline.Stages);
                    break;
                case GroupSyntax group:
                    stack.Push(group.Body);
                    break;
                case ForEachSyntax forEach:
                    stack.Push(forEach.Body);
                    stack.Push(forEach.IteratorCommands);
                    break;
                case CommandSubstitutionSyntax substitution:
                    stack.Push(substitution.Body);
                    break;
            }
        }

        return false;
    }

    private static void PushReverse(
        Stack<ShellSyntaxNode> stack,
        IReadOnlyList<ShellSyntaxNode> nodes)
    {
        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            stack.Push(nodes[index]);
        }
    }

    private IReadOnlyList<ShellSyntaxNode> RewriteNodes(
        IReadOnlyList<ShellSyntaxNode> nodes,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new ShellSyntaxNode[nodes.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = RewriteNode(nodes[index], facts);
        }

        return rewritten;
    }

    private IReadOnlyList<CommandListItemSyntax> RewriteItems(
        IReadOnlyList<CommandListItemSyntax> items,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new CommandListItemSyntax[items.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = items[index] with
            {
                Command = RewriteNode(items[index].Command, facts),
            };
        }

        return rewritten;
    }

    private IReadOnlyList<ConditionalBranchSyntax> RewriteBranches(
        IReadOnlyList<ConditionalBranchSyntax> branches,
        Dictionary<Clause, CommandOccurrenceFacts> facts)
    {
        var rewritten = new ConditionalBranchSyntax[branches.Count];
        for (var index = 0; index < rewritten.Length; index++)
        {
            rewritten[index] = (ConditionalBranchSyntax)RewriteNode(branches[index], facts);
        }

        return rewritten;
    }

    private readonly struct BashFlowResult
    {
        internal BashFlowResult(BashAbstractState? onSuccess, BashAbstractState? onFailure)
        {
            OnSuccess = onSuccess;
            OnFailure = onFailure;
        }

        internal BashAbstractState? OnSuccess { get; }

        internal BashAbstractState? OnFailure { get; }

        internal BashAbstractState JoinedState =>
            BashAbstractState.JoinNullable(OnSuccess, OnFailure) ??
            new BashAbstractState(null, true);

        internal static BashFlowResult Both(BashAbstractState state) => new(state, state);
    }

    private readonly struct BashAbstractState
    {
        internal BashAbstractState(
            string? workingDirectory,
            bool hasCompatibilityAttribution)
        {
            WorkingDirectory = workingDirectory;
            HasCompatibilityAttribution = hasCompatibilityAttribution;
        }

        internal string? WorkingDirectory { get; }

        internal bool HasCompatibilityAttribution { get; }

        internal BashAbstractState WithUnknownCwd() => new(null, true);

        internal BashAbstractState WithoutCompatibilityAttribution() =>
            new(WorkingDirectory, WorkingDirectory is null);

        internal ShellValueDomain ToDomain() =>
            WorkingDirectory is null
                ? ShellValueDomain.Unknown
                : new ShellValueDomain
                {
                    Kind = ShellValueDomainKind.Exact,
                    Values = new[] { WorkingDirectory },
                };

        internal static BashAbstractState Join(
            BashAbstractState left,
            BashAbstractState right) => new(
                string.Equals(
                    left.WorkingDirectory,
                    right.WorkingDirectory,
                    StringComparison.Ordinal)
                    ? left.WorkingDirectory
                    : null,
                left.HasCompatibilityAttribution || right.HasCompatibilityAttribution);

        internal static BashAbstractState? JoinNullable(
            BashAbstractState? left,
            BashAbstractState? right)
        {
            if (left is null)
            {
                return right;
            }

            if (right is null)
            {
                return left;
            }

            return Join(left.Value, right.Value);
        }
    }

    private sealed class ClauseReferenceComparer : IEqualityComparer<Clause>
    {
        internal static ClauseReferenceComparer Instance { get; } = new();

        public bool Equals(Clause? x, Clause? y) => object.ReferenceEquals(x, y);

        public int GetHashCode(Clause obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
